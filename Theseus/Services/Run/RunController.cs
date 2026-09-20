using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Theseus.Config;
using Theseus.Services.Duty;
using Theseus.Services.Fleet;
using Theseus.Services.Frontier;
using Theseus.Services.Paths;
using Solver = Theseus.Services.Solver;
using Theseus.Services.Thread;

namespace Theseus.Services.Run;

/// <summary>
/// Drives one dungeon run: picks the route, ticks the executor, and learns the objective map while
/// it goes.
///
/// <para>
/// The learning is the part worth being careful about. Objective tags are written back only when
/// the duty actually completes — a run that faulted, wiped out or was stopped halfway saw a
/// sequence of steps that does not describe a successful route, and baking that in would teach the
/// path something wrong permanently. One clean run is the price of a tagged path; nothing less
/// counts.
/// </para>
/// </summary>
public sealed class RunController : IDisposable
{
    /// <summary>
    /// Breathing room between the duty completing and walking out, so loot rolls and the last
    /// chest have a moment to resolve rather than being abandoned on the floor.
    /// </summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(8);

    /// <summary>How close a peer counts as gathered. A room, roughly.</summary>
    private const float FleetRallyRadius = 30f;

    private readonly TheseusConfig _config;
    private readonly DutyLifecycle _lifecycle;
    private readonly IObjectiveReader _objectives;
    private readonly PathStore _paths;
    private readonly StepExecutor _executor;
    private readonly DutyEntry _entry;
    private readonly IStepWorld _world;
    private readonly IFramework _framework;
    private readonly Action<string> _log;

    /// <summary>Territories auto-start may run in — dungeons, in practice. See TryAutoStart.</summary>
    private readonly Func<uint, bool> _autoRunnableTerritory;

    private readonly ObjectiveMapper _mapper = new();
    private readonly PathRecorder _recorder;
    private readonly FrontierNavigator? _frontier;
    private readonly Solver.SolverWiring? _solver;

    /// <summary>
    /// True while the run is being driven by the frontier navigator rather than a recorded route.
    ///
    /// <para>
    /// The two never run together. A territory with a route uses the route — the step executor,
    /// the variant tags, the objective-matched resume, all of it unchanged — and the frontier mode
    /// only picks up territories that have nothing, so unconverted content is slow rather than
    /// dead.
    /// </para>
    /// </summary>
    private bool _exploring;

    private readonly FleetRoster _fleet;
    private readonly FleetGate _gate;

    /// <summary>Objective the fleet gate last opened on, so a boundary is only gated once.</summary>
    private int _gatedObjective = -1;

    /// <summary>
    /// The route being taught, kept separately from <see cref="ActivePath"/>.
    ///
    /// <para>
    /// Learning has to outlive the run controller's own idea of "running". A route that takes
    /// three stop/start cycles to get through still produced real observations on every one of
    /// them, and throwing them away each time meant a dungeon that could not be completed in a
    /// single unbroken run could never be tagged at all.
    /// </para>
    /// </summary>
    private ThreadPath? _learningPath;
    private DutyRunKey _learningKey;
    private DateTime _leaveAtUtc = DateTime.MaxValue;
    private int _singleStepIndex = -1;

    /// <summary>
    /// This run was asked for by name, so begin it on arrival whether or not auto-start is on.
    /// Someone who pressed "Enter and run" has already said what they want.
    /// </summary>
    private bool _runOnArrival;

    public RunController(
        TheseusConfig config,
        DutyLifecycle lifecycle,
        IObjectiveReader objectives,
        PathStore paths,
        StepExecutor executor,
        DutyEntry entry,
        IStepWorld world,
        IFramework framework,
        Action<string> log,
        Func<uint, bool>? autoRunnableTerritory = null,
        FrontierNavigator? frontier = null,
        Solver.SolverWiring? solver = null)
    {
        _config = config;
        _lifecycle = lifecycle;
        _objectives = objectives;
        _paths = paths;
        _executor = executor;
        _entry = entry;
        _world = world;
        _recorder = new PathRecorder(world, objectives);
        _framework = framework;
        _log = log;
        _autoRunnableTerritory = autoRunnableTerritory ?? (_ => true);
        _frontier = frontier;
        _solver = solver;
        _fleet = new FleetRoster(world);
        _gate = new FleetGate(world, _fleet, () => _config.PeerStaleSeconds);

        _executor.StepStarted += OnStepStarted;
        _lifecycle.DutyCompleted += OnDutyCompleted;
        _lifecycle.DutyStarted += TryAutoStart;
        _lifecycle.RunEntered += OnRunEntered;
        _lifecycle.RunLeft += OnRunLeft;
        _framework.Update += OnUpdate;
    }

    public RunState State { get; private set; } = RunState.Idle;

    /// <summary>One line explaining the current state, shown on the run window's status row.</summary>
    public string Status { get; private set; } = "Idle.";

    /// <summary>The route being run, or null.</summary>
    public ThreadPath? ActivePath { get; private set; }

    public int CurrentStepIndex => _executor.CurrentStepIndex;

    /// <summary>The run is exploring an unmapped zone rather than following a recorded route.</summary>
    public bool IsExploring => _exploring;

    /// <summary>The fleet as this box sees it, for the run window.</summary>
    public (int Players, int MySlot, int Waiting) FleetState
        => (_fleet.Players.Count, _fleet.MySlot, _gate.Waiting);

    /// <summary>Frontier progress, for the run window: landmarks reached and objects dealt with.</summary>
    public (int Landmarks, int Objects) ExplorationProgress
        => _frontier is null ? (0, 0) : (_frontier.VisitedLandmarks, _frontier.GhostCount);

    /// <summary>
    /// What the solver's perception is doing, for the debug window. Diagnostic only: nothing here
    /// drives the character.
    /// </summary>
    public string DescribeShadow() => _solver?.Perception.Describe() ?? "off";

    /// <summary>
    /// Who is driving, and what the solver would be doing if it were. Read together with
    /// <see cref="DescribeShadow"/>: the driver decision is per territory and made from the record,
    /// not from a setting.
    /// </summary>
    public string DescribeSolver()
    {
        if (_solver is not { } solver)
            return "off";

        var driver = solver.Driver;
        var territory = _lifecycle.RunKey.TerritoryId;
        var record = solver.Records.For(territory);

        return $"{(driver.Status == Solver.SolverStatus.Idle ? "not driving" : driver.Status.ToString())} · " +
               $"{driver.Detail} · driving {driver.RunsDriven}, given back {driver.RunsHandedBack} · " +
               $"territory {territory}: {(record.Promoted ? "promoted" : "not promoted")}, " +
               $"{record.ShadowAgreements}/{Solver.SolverRecordStore.AgreementsToPromote} agreements, " +
               $"{record.SolverRuns} solver run(s), {record.SolverFallbacks} fallback(s)";
    }

    /// <summary>
    /// What the executor is doing about movement right now, for the debug window. Diagnostic only.
    /// </summary>
    public string DescribeMovement()
    {
        if (_executor.CurrentDestination is not { } destination)
            return $"{State} · executor {_executor.Status} · no destination";

        var distance = System.Numerics.Vector3.Distance(_world.PlayerPosition, destination);
        return $"{State} · step {_executor.CurrentStepIndex} → ({destination.X:0.##}, {destination.Y:0.##}, " +
               $"{destination.Z:0.##}) · {distance:0.0}y away · move accepted {_executor.LastMoveAccepted} · " +
               $"stuck {_executor.StuckMoves}";
    }

    public void Dispose()
    {
        _framework.Update -= OnUpdate;
        _executor.StepStarted -= OnStepStarted;
        _lifecycle.DutyCompleted -= OnDutyCompleted;
        _lifecycle.DutyStarted -= TryAutoStart;
        _lifecycle.RunEntered -= OnRunEntered;
        _lifecycle.RunLeft -= OnRunLeft;
        _solver?.Perception.Save();
        _solver?.Records.Save(_solver.RecordsPath);
        Stop("Plugin unloading.");
    }

    /// <summary>
    /// Picks up the current territory's route from wherever the character is standing.
    ///
    /// <para>
    /// This is the default because restarting is usually wrong. Anything that interrupts a run —
    /// a stop, a reload, a crash — leaves the character partway through the dungeon, and replaying
    /// from step 0 means walking back through content that is already cleared. Past a one-way
    /// transition it means not arriving at all.
    /// </para>
    /// </summary>
    public void Start() => Start(fromBeginning: false);

    /// <summary>Runs the route from step 0, ignoring where the character is.</summary>
    public void Restart() => Start(fromBeginning: true);

    /// <summary>Entering is available: outside a duty, with nothing already in flight.</summary>
    public bool CanEnterDuty => !_lifecycle.IsInDuty && State is RunState.Idle or RunState.Faulted;

    /// <summary>The selected duty lets you choose Trust companions rather than fixing them.</summary>
    public bool CanChooseCompanions(bool trust) => _entry.CanChooseCompanions(trust);

    /// <summary>Companions the game is currently offering, or empty when the window is not open.</summary>
    public IReadOnlyList<TrustCompanion> ReadCompanions(uint contentFinderConditionId, bool trust)
        => _entry.ReadCompanions(contentFinderConditionId, trust);

    /// <summary>Points an open companion window at a duty, so the picker reads its roster.</summary>
    public bool SelectDuty(uint contentFinderConditionId, bool trust)
        => _entry.SelectDuty(contentFinderConditionId, trust);

    /// <summary>Raw companion arrays, for the debug window.</summary>
    public string DescribeCompanionData(bool trust) => _entry.DescribeCompanionData(trust);

    /// <summary>Opens the Trust window so its roster can be read into the picker.</summary>
    public void OpenCompanionWindow(uint contentFinderConditionId, bool trust)
        => _entry.OpenCompanionWindow(contentFinderConditionId, trust);

    /// <summary>
    /// Walks into a Duty Support dungeon and, by default, runs it on arrival.
    ///
    /// <para>
    /// Strictly additive: it refuses while inside a duty rather than restarting one. Resuming from
    /// wherever the character is standing is the older and more load-bearing path, and entering
    /// must not be able to interrupt it.
    /// </para>
    /// </summary>
    public void EnterDuty(uint contentFinderConditionId, bool trust, bool runOnArrival = true)
    {
        if (!_config.Enabled)
        {
            Fail("Theseus is disabled in settings.");
            return;
        }

        if (_lifecycle.IsInDuty)
        {
            Fail("Already in a duty — use Resume.");
            return;
        }

        if (!_entry.Begin(contentFinderConditionId, trust, out var reason))
        {
            Fail(reason);
            return;
        }

        _runOnArrival = runOnArrival;
        State = RunState.Entering;
        Status = reason;
        _log($"Entering duty {contentFinderConditionId}.");
    }

    /// <summary>This box is grouped with other real players, so a duty is entered as a party.</summary>
    public bool InGroup => !_fleet.IsSolo;

    /// <summary>This box leads its party — the one that queues.</summary>
    public bool IsPartyLeader => _fleet.IsLeader;

    /// <summary>
    /// Enters a duty the way the party calls for: through the Duty Finder when grouped, through
    /// Trust or Duty Support when alone.
    ///
    /// <para>
    /// The group decides, not the duty. Companions only exist for a character on its own — a party
    /// cannot bring Trust — and a lone character has nobody to queue with. Real players only: Trust
    /// companions sit in the party list too, but they never make a group.
    /// </para>
    /// </summary>
    public void Enter(DutyListing duty, bool trust, bool runOnArrival = true)
        => Enter(duty.ContentFinderConditionId, duty.Name, trust, runOnArrival);

    /// <inheritdoc cref="Enter(DutyListing, bool, bool)"/>
    public void Enter(uint contentFinderConditionId, string dutyName, bool trust, bool runOnArrival = true)
    {
        // Alone, companions are the door when the duty has them. Most trials do not — and the Duty
        // Finder takes a single player just as well, matching the rest of the party from the queue.
        if (!InGroup && _entry.SupportsDutySupport(contentFinderConditionId))
        {
            EnterDuty(contentFinderConditionId, trust, runOnArrival);
            return;
        }

        if (!_config.Enabled)
        {
            Fail("Theseus is disabled in settings.");
            return;
        }

        if (_lifecycle.IsInDuty)
        {
            Fail("Already in a duty — use Resume.");
            return;
        }

        if (!_entry.BeginDutyFinder(contentFinderConditionId, dutyName, IsPartyLeader, out var reason))
        {
            Fail(reason);
            return;
        }

        _runOnArrival = runOnArrival;
        State = RunState.Entering;
        Status = reason;
        _log($"Queueing duty {contentFinderConditionId} through the Duty Finder " +
             $"({(IsPartyLeader ? "leader" : "member")}).");
    }

    /// <summary>
    /// Runs exactly one step, then stops.
    ///
    /// <para>
    /// For checking a repaired step without committing to a whole run. Editing a waypoint by hand
    /// is guesswork until something walks it, and the alternative — restart the route and wait to
    /// reach that step again — is slow enough that it discourages fixing things.
    /// </para>
    /// </summary>
    public void StepOnce(int stepIndex)
    {
        if (!TryBeginRoute(out var path))
            return;

        _executor.Start(path, stepIndex);
        if (_executor.Status == ExecutorStatus.Faulted)
        {
            Fail(_executor.FaultReason);
            return;
        }

        _singleStepIndex = _executor.CurrentStepIndex;
        State = RunState.Running;
        Status = $"Running step {_singleStepIndex} on its own.";
        _log(Status);
    }

    /// <summary>Shared setup for a run and a single step: pick the route and prime the mapper.</summary>
    private bool TryBeginRoute(out ThreadPath path)
    {
        path = null!;

        if (!_lifecycle.IsInDuty)
        {
            Fail("Not in a duty.");
            return false;
        }

        var territoryId = _lifecycle.RunKey.TerritoryId;
        var resolved = _paths.Resolve(
            territoryId,
            _config.PreferredRoutes.GetValueOrDefault(territoryId),
            wallToWall: _config.MatchRouteToRole,
            isTank: _world.IsTank);

        if (resolved is null)
        {
            // Nothing recorded here. This is the case the solver exists for: it drives the dungeon
            // itself, with the frontier navigator behind it as the fallback for what it cannot do
            // yet. A territory with a route never reaches this branch at all.
            var kind = _solver is { } solver
                ? solver.Records.Decide(territoryId, hasRoute: false, solverReady: solver.Usable())
                : Solver.DriverKind.Route;

            if (_solver is not null && kind == Solver.DriverKind.Solver && _config.ExploreUnmappedZones)
            {
                _exploring = true;
                ActivePath = null;
                State = RunState.Running;
                _solver.Driver.Start();
                Status = $"No route for territory {territoryId} — the solver is driving.";
                _log(Status);
                return false;
            }

            // Nothing recorded here. Explore instead of refusing — that is the whole point of the
            // fallback, and it leaves the recorded-route path untouched for zones that have one.
            if (_frontier is not null && _config.ExploreUnmappedZones)
            {
                StartFrontier();
                ActivePath = null;
                State = RunState.Running;
                Status = $"No route for territory {territoryId} — exploring.";
                _log(Status);
                return false;
            }

            Fail($"No path for territory {territoryId}. Import from AutoDuty in settings.");
            return false;
        }

        if (!resolved.IsRunnable)
        {
            Fail(resolved.Blockers.Count > 0 ? resolved.Blockers[0] : "Path is not runnable.");
            return false;
        }

        if (_learningKey != _lifecycle.RunKey || _learningPath?.Key != resolved.Key)
        {
            _mapper.Reset();
            _learningKey = _lifecycle.RunKey;
        }

        _learningPath = resolved;
        ActivePath = resolved;
        _leaveAtUtc = DateTime.MaxValue;
        _singleStepIndex = -1;
        path = resolved;
        return true;
    }

    private void Start(bool fromBeginning)
    {
        if (!TryBeginRoute(out var path))
            return;

        var resume = fromBeginning
            ? null
            : PathRelocalizer.Find(path, _world.PlayerPosition, _objectives.Read());

        _executor.Start(path, resume?.StepIndex ?? 0);

        if (_executor.Status == ExecutorStatus.Faulted)
        {
            Fail(_executor.FaultReason);
            return;
        }

        State = RunState.Running;
        Status = resume is null
            ? $"Running \"{path.Name}\" [{path.Variant}] from the start ({path.Steps.Count} steps)."
            : $"Resuming \"{path.Name}\" [{path.Variant}] at step {resume.Value.StepIndex}/{path.Steps.Count} " +
              $"({resume.Value.Distance:0} yalms away" +
              $"{(resume.Value.UsedObjectives ? ", objective-matched" : string.Empty)})" +
              (resume.Value.IsConfident ? "." : " — nothing close by, so this is a guess.");

        _log(Status);
    }

    /// <summary>Hands the run to the frontier navigator: landmarks, overrides, and its own ladder.</summary>
    private void StartFrontier()
    {
        _exploring = true;
        _frontier?.Start();
    }

    /// <summary>Stops the run and releases everything it was holding.</summary>
    public void Stop(string reason = "Stopped.")
    {
        _singleStepIndex = -1;
        _runOnArrival = false;

        if (_exploring)
        {
            _exploring = false;
            _frontier?.Stop();
        }

        // A solver run that ends is a data point for the promotion decision — driven through, or
        // given back. Counted here rather than mid-run, because a run is only a run once it is over.
        if (_solver is { } solver && solver.Driver.Status is Solver.SolverStatus.Driving or Solver.SolverStatus.HandedOff)
        {
            var fellBack = solver.Driver.Status == Solver.SolverStatus.HandedOff;
            solver.Driver.Stop(reason);
            solver.NoteRun(_lifecycle.RunKey.TerritoryId, fellBack, _world.UtcNow);
        }

        _entry.Cancel(reason);
        _executor.Stop();
        ActivePath = null;
        State = RunState.Idle;
        Status = reason;
    }

    /// <summary>Watches a manual run and writes it out as a route. See <see cref="PathRecorder"/>.</summary>
    public bool IsRecording => _recorder.IsRecording;

    public int RecordedSamples => _recorder.SampleCount;

    /// <summary>A paused recording is waiting to be saved or discarded.</summary>
    public bool HasPendingRecording => !_recorder.IsRecording && _recorder.SampleCount > 0;

    public void DiscardRecording()
    {
        _recorder.Discard();
        Status = "Recording discarded.";
    }

    public void StartRecording()
    {
        if (!_lifecycle.IsInDuty)
        {
            Status = "Not in a duty — nothing to record.";
            return;
        }

        _recorder.Start(_lifecycle.RunKey.TerritoryId);
        Status = "Recording. Run the dungeon, then stop to save the route.";
        _log(Status);
    }

    /// <summary>Ends the recording and saves it, unless it saw nothing worth keeping.</summary>
    public ThreadPath? StopRecording(string name)
    {
        // A paused recording — sampling stopped when the duty was left — still saves. The marks
        // are the run; whether sampling was live at the moment of saving is irrelevant.
        if (!_recorder.IsRecording && _recorder.SampleCount == 0)
            return null;

        _recorder.Stop();

        var path = _recorder.Build(name);
        if (path.Steps.Count == 0)
        {
            Status = "Recording had no steps — nothing saved.";
            _log(Status);
            return null;
        }

        _paths.Save(path);
        _recorder.Discard(); // saved; holding the marks would offer the same save twice
        Status = $"Saved \"{path.Name}\" — {path.Steps.Count} steps.";
        _log(Status);
        return path;
    }

    /// <summary>
    /// Leaving pauses a live recording rather than letting it run on.
    ///
    /// <para>
    /// The recorder cannot tell a duty from the town outside it — it samples whoever is moving.
    /// Left running across the exit, it appends the walk around the overworld to the route, and
    /// the first field recording only avoided that because the user beat the auto-leave to the
    /// save button. The marks are kept; the run window offers to save or discard them.
    /// </para>
    /// </summary>
    private void OnRunLeft(DutyRunKey key)
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _log($"Recording paused — left the duty ({_recorder.SampleCount} marks held).");
        }

        Stop("Left the duty.");
    }

    private void OnUpdate(IFramework framework)
    {
        // Recording runs alongside everything else and outlives a stopped run, because the point is
        // to watch a person play rather than to watch Theseus.
        _recorder.Tick();

        // The solver's perception does the same, and for the same reason: a run that was going to
        // happen anyway is what teaches it the dungeon. It issues no movement of its own — whatever
        // moves the character below moves it through the arbiter.
        _solver?.Perception.Tick();

        if (State is RunState.Idle or RunState.Faulted)
            return;

        if (!_config.Enabled)
        {
            Stop("Theseus is disabled in settings.");
            return;
        }

        if (_exploring)
        {
            // The solver first, when this territory is its to drive, and the frontier navigator
            // underneath it: a hand-back is not a failure, it is the next rung of the ladder.
            if (_solver is { } solver && solver.Driver.Status == Solver.SolverStatus.Driving)
            {
                // The duty is over. Whatever is left is the exit, and the frontier navigator — which
                // knows about the end-of-dungeon coffer and the leave — is better at that than a
                // solver looking for something to do. No idle timers on the way out.
                if (State == RunState.Exiting)
                {
                    solver.Driver.Stop("the duty is complete");
                    _log("Duty complete — the solver is done driving this one.");
                    StartFrontier();
                    return;
                }

                solver.Driver.Tick();
                Status = solver.Driver.Detail;

                switch (solver.Driver.Status)
                {
                    case Solver.SolverStatus.HandedOff:
                        solver.Driver.Stop("handed back");
                        _log($"Solver gave the run back: {Status}. Exploring from here.");
                        StartFrontier();
                        return;

                    case Solver.SolverStatus.Faulted:
                        Fail(Status);
                        return;

                    default:
                        State = ClassifyRunningState();
                        return;
                }
            }

            if (_frontier is null)
            {
                Fail("Nothing to drive with: no solver and no frontier navigation.");
                return;
            }

            _frontier.Tick();
            Status = _frontier.Detail;

            switch (_frontier.Status)
            {
                case FrontierStatus.Exhausted:
                    _exploring = false;
                    State = RunState.Exiting;
                    _leaveAtUtc = _world.UtcNow + LeaveDelay;
                    break;

                case FrontierStatus.Faulted:
                    _exploring = false;
                    Fail(_frontier.Detail);
                    break;

                default:
                    State = ClassifyRunningState();
                    break;
            }

            return;
        }

        if (State == RunState.Entering)
        {
            _entry.Tick();

            if (_entry.Status == DutyEntryStatus.Faulted)
            {
                Fail(_entry.Detail);
                return;
            }

            if (_entry.IsActive)
            {
                Status = _entry.Detail;
                return;
            }

            // Arrived, or cancelled out from under us. Either way entering is over; the lifecycle's
            // duty-started event is what begins the run, so there is nothing to hand off here.
            State = RunState.Idle;
            Status = _entry.Detail;
            return;
        }

        if (State == RunState.Exiting)
        {
            // A cleared duty does not mean a finished route. Killing the last boss completes the
            // duty and starts its chest spawning in the same moment, so cutting the executor off
            // here made the end-of-dungeon coffer the one chest that could never be taken.
            if (_executor.Status == ExecutorStatus.Running)
            {
                _executor.Tick();

                // Hold the exit open for as long as the route is still doing something. Its own
                // watchdogs bound this, so it cannot keep the party inside indefinitely.
                _leaveAtUtc = _world.UtcNow + LeaveDelay;
                return;
            }

            TryLeave();
            return;
        }

        if (HoldingForFleet())
            return;

        _executor.Tick();

        // Single-step mode: the moment the executor moves off the step, we are finished. Checked
        // before the status switch so a step that completes the route still stops here rather than
        // falling into the leave-the-duty path.
        if (_singleStepIndex >= 0 && _executor.CurrentStepIndex != _singleStepIndex)
        {
            Stop($"Step {_singleStepIndex} finished.");
            return;
        }

        switch (_executor.Status)
        {
            case ExecutorStatus.Running:
                State = ClassifyRunningState();
                break;

            case ExecutorStatus.Finished:
                State = RunState.Exiting;
                Status = "Route finished.";
                break;

            case ExecutorStatus.Faulted:
                Fail(_executor.FaultReason);
                break;
        }
    }

    /// <summary>
    /// Holds at an objective boundary until the fleet gathers.
    ///
    /// <para>
    /// The boundary is the natural place to regroup: the objective changing is the game's own
    /// statement that a section is finished, and it is the one moment where waiting costs nothing
    /// and arriving apart costs a great deal — a wall-to-wall tank moving on alone drags the next
    /// pack into three characters still walking the last corridor.
    /// </para>
    ///
    /// <para>
    /// Inert solo, by construction: the gate refuses to hold when there are no real peers, so a
    /// single-box run never waits for anyone. Single-stepping is exempt too — that is someone
    /// testing one step by hand, and holding it for the fleet would be baffling.
    /// </para>
    /// </summary>
    private bool HoldingForFleet()
    {
        if (!_config.EnableFleetGates || _singleStepIndex >= 0 || _fleet.IsSolo)
            return false;

        var objective = _objectives.Read().CurrentIndex;

        if (objective != _gatedObjective && objective >= 0)
        {
            _gatedObjective = objective;
            _gate.Hold(_world.PlayerPosition, FleetRallyRadius);
        }

        if (_gate.Tick() != GateState.Holding)
            return false;

        Status = _gate.Detail;
        return true;
    }

    /// <summary>
    /// Leaves once the duty is done and the loot rolls have had a moment to resolve.
    ///
    /// <para>
    /// Gated on the duty actually completing, never on the route running out of steps. A route
    /// that ends early — a bad waypoint, an objective the path does not cover — must not walk the
    /// party out of a dungeon they have not cleared.
    /// </para>
    /// </summary>
    private void TryLeave()
    {
        if (!_config.LeaveWhenComplete || _world.UtcNow < _leaveAtUtc)
            return;

        if (!_world.CanLeaveDuty)
            return;

        _leaveAtUtc = DateTime.MaxValue;
        _world.LeaveDuty();
        Status = "Leaving the duty.";
        _log(Status);
    }

    /// <summary>
    /// Which flavour of "running" this is. Cosmetic — the executor does not branch on it — but it
    /// is what makes the run window legible when a box stops moving, so it should not lie.
    /// </summary>
    private RunState ClassifyRunningState()
    {
        if (_world.BossModuleActive)
            return RunState.BossHandoff;

        return _world.InCombat ? RunState.Combat : RunState.Running;
    }

    /// <summary>
    /// Reloading or zoning into a duty that is already under way still counts as arriving, and the
    /// barrier event has long since fired.
    /// </summary>
    private void OnRunEntered(DutyRunKey key)
    {
        if (_lifecycle.Phase == DutyPhase.InProgress)
            TryAutoStart(key);
    }

    /// <summary>
    /// Begins a run on arrival, when asked to.
    ///
    /// <para>
    /// A territory with no usable route leaves the run idle rather than faulting. A roulette can
    /// drop you into anything, and content Theseus cannot run is an ordinary outcome — faulting
    /// would turn it into an error to dismiss, and would block a manual start afterwards.
    /// </para>
    /// </summary>
    private void TryAutoStart(DutyRunKey key)
    {
        // Entering counts as idle here. The lifecycle ticks ahead of this controller, so on the
        // frame a run we asked for finishes zoning, this fires before the entering branch has had
        // a chance to settle back to Idle — rejecting it would drop the start it was asked for.
        if (!_config.Enabled || State is not (RunState.Idle or RunState.Entering))
            return;

        if (!_config.AutoStartInDuty && !_runOnArrival)
            return;

        var explicitlyAsked = _runOnArrival;
        _runOnArrival = false;
        _entry.Cancel("Entered the duty.");
        State = RunState.Idle;

        // Auto-start is for content a lone box can politely run. The path library carries routes
        // for trials, raids and alliance raids too, so "a route exists" is no gate at all — and a
        // runner that starts recording waypoints and driving movement in a 24-man full of real
        // people is exactly the bad neighbour the master switch warns about. Entering by name
        // through the picker is different: that was an explicit ask, and the picker only offers
        // dungeons anyway.
        if (!explicitlyAsked && !_autoRunnableTerritory(key.TerritoryId))
        {
            Status = $"Territory {key.TerritoryId} is not a dungeon — auto-start held.";
            _log(Status);
            return;
        }

        var path = _paths.Resolve(
            key.TerritoryId,
            _config.PreferredRoutes.GetValueOrDefault(key.TerritoryId),
            wallToWall: _config.MatchRouteToRole,
            isTank: _world.IsTank);

        if (path is null && (_frontier is null || !_config.ExploreUnmappedZones))
        {
            Status = $"No route for territory {key.TerritoryId} — waiting.";
            _log(Status);
            return;
        }

        if (path is not null && !path.IsRunnable)
        {
            Status = $"\"{path.Name}\" needs a recorded path — waiting.";
            _log(Status);
            return;
        }

        Start();
    }

    private void OnStepStarted(int stepIndex) => _mapper.Observe(stepIndex, _objectives.Read());

    private void OnDutyCompleted(DutyRunKey key)
    {
        // Deliberately keyed off the duty completing rather than the executor finishing. The duty
        // is what the objective list describes, and a route can be completed by hand after Theseus
        // stopped — those observations are still true and still worth keeping.
        var path = _learningPath;
        if (path is not null && _mapper.Apply(path))
        {
            _paths.Save(path);
            _log($"Learned objective tags for \"{path.Name}\" " +
                 $"({_mapper.ObservedStepCount} steps" +
                 $"{(_mapper.ConflictCount > 0 ? $", {_mapper.ConflictCount} ambiguous" : string.Empty)})" +
                 $"{(path.IsObjectiveTagged ? " — fully tagged." : ", still incomplete.")}");
        }

        State = RunState.Exiting;
        Status = path?.IsObjectiveTagged == true
            ? "Duty complete — route is objective-tagged."
            : "Duty complete.";

        _leaveAtUtc = _world.UtcNow + LeaveDelay;
    }

    private void Fail(string reason)
    {
        State = RunState.Faulted;
        Status = reason;
        ActivePath = null;
        _log(reason);
    }
}
