using System;
using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>Where the solver is with a run it is driving.</summary>
public enum SolverStatus
{
    /// <summary>Not driving. The executor or the frontier navigator has it.</summary>
    Idle,

    /// <summary>Driving: a loop has the grant.</summary>
    Driving,

    /// <summary>Nothing bids and the ladder is done with what it can do alone — the caller's turn.</summary>
    HandedOff,

    /// <summary>Something is wrong that a person should look at.</summary>
    Faulted,
}

/// <summary>
/// The solver as a driver: one arbiter decision per tick, and the granted loop runs.
///
/// <para>
/// It owns no perception of its own. It acts on the snapshot the shadow just built, so the thing
/// that decided and the thing that wrote the evidence down are looking at the same frame — which is
/// also why it can only ever be as good as what perception saw, and why the two are wired in that
/// order.
/// </para>
///
/// <para>
/// <b>It hands back rather than improvising.</b> When nothing bids — nothing to fight, nothing to
/// touch, nothing left to walk to — it widens perception once and then, if that finds nothing,
/// gives the run back to the caller with the frontier navigator's landmarks and overrides waiting
/// behind it. Being wrong about a dungeon is recoverable; being stuck in it is not.
/// </para>
/// </summary>
public sealed class SolverDriver
{
    // The ladder's budget: about two minutes in total, each rung given its own moment, so a run
    // that is genuinely stuck is handed back inside a sane window instead of standing about.
    private static readonly TimeSpan WidenAfter = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FleetAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OverrideAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HandBackAfter = TimeSpan.FromSeconds(120);

    /// <summary>Close enough to an authored escape to call it taken.</summary>
    private const float EscapeArrival = 3f;

    private readonly IPerception _perception;
    private readonly Arbiter _arbiter;
    private readonly IStepWorld _game;
    private readonly Func<bool> _usable;
    private readonly LadderContext? _ladder;
    private readonly Action<string>? _log;

    private DateTime? _idleSinceUtc;
    private bool _widened;
    private bool _fleetAsked;
    private bool _retried;
    private bool _overrideTried;
    private Vector3? _overrideTarget;

    public SolverDriver(
        IPerception perception,
        Arbiter arbiter,
        IStepWorld game,
        Func<bool> usable,
        LadderContext? ladder = null,
        Action<string>? log = null)
    {
        _perception = perception;
        _arbiter = arbiter;
        _game = game;
        _usable = usable;
        _ladder = ladder;
        _log = log;
    }

    /// <summary>Which rung of the ladder it is on, for the run window and the debug row.</summary>
    public LadderRung Rung { get; private set; }

    public SolverStatus Status { get; private set; } = SolverStatus.Idle;

    /// <summary>The last decision, for the run window and the log line that explains a hand-back.</summary>
    public LoopDecision Decision { get; private set; } = LoopDecision.Nothing;

    /// <summary>One line for the run window: what it is doing and why.</summary>
    public string Detail { get; private set; } = "not driving";

    /// <summary>Where it was standing when it last ran out of ideas — what a hand-back names.</summary>
    public Vector3? LastStuckPosition { get; private set; }

    /// <summary>How many runs it has driven since the plugin started, and how many it gave back.</summary>
    public int RunsDriven { get; private set; }

    public int RunsHandedBack { get; private set; }

    public void Start()
    {
        Status = SolverStatus.Driving;
        _idleSinceUtc = null;
        _widened = false;
        LastStuckPosition = null;
        Detail = "starting";
        _log?.Invoke("Solver: driving this run — no route for it.");
    }

    /// <summary>Stops driving and says why, leaving the character where it is.</summary>
    public void Stop(string why)
    {
        if (Status == SolverStatus.Idle)
            return;

        if (Status == SolverStatus.Driving)
            _game.StopMoving();

        Status = SolverStatus.Idle;
        Detail = why;
    }

    /// <summary>
    /// Hands the run to a person rather than pretending: the solver keeps driving nothing, the
    /// caller sees <see cref="SolverStatus.Faulted"/> and stops the run with the reason on screen.
    /// </summary>
    private void Fault(string why)
    {
        _game.StopMoving();
        Status = SolverStatus.Faulted;
        Detail = why;
        _log?.Invoke($"Solver: {why}");
    }

    public void Tick()
    {
        if (Status != SolverStatus.Driving)
            return;
        // The solver without a map is not a solver: if Ariadne stopped answering, everything below
        // would be guessing. Handing back is the whole of the recovery.
        if (!_usable())
        {
            HandBack("its map stopped answering");
            return;
        }

        if (_perception.LastSnapshot is not { } world)
        {
            Detail = "waiting on perception";
            return;
        }

        Decision = _arbiter.Decide(world);
        Detail = $"{Decision.Kind}: {Decision.Reason}";

        switch (Decision.Kind)
        {
            case LoopKind.Transit:
                // The mover is mid-ride. Touching anything here is how a lift ride becomes a fall.
                return;

            case LoopKind.BossHandoff:
                // The boss module has the fight. Standing still is the whole contribution — unless
                // nobody has it: a hostile standing off with no module to hand it to is the case
                // §2.5 names, and ninety seconds of nothing happening is what a person needs to see.
                _game.StopMoving();
                NoteBusy();

                if (!world.BossModuleActive && world.Nearest(o => o.Object.Kind == Services.Frontier.WorldObjectKind.Hostile) is { } stuck)
                {
                    Fault($"\"{stuck.Object.Name}\" is standing off {stuck.Distance:0}y away at " +
                          $"({stuck.Object.Position.X:0.#}, {stuck.Object.Position.Y:0.#}, {stuck.Object.Position.Z:0.#}) " +
                          "and no boss handler has a module for it — a person should look.");
                }

                return;

            case LoopKind.Combat:
                _arbiter.Combat.Run(world, _game);
                NoteBusy();
                return;

            case LoopKind.Interactable:
                _arbiter.Interactables.Run(world, _game);
                NoteBusy();
                return;

            case LoopKind.Exploration:
                _arbiter.Exploration.Run(world, _game);
                NoteBusy();
                return;

            default:
                NoteIdle(world);
                return;
        }
    }

    /// <summary>Marks the run as finished, so the record's counts mean something.</summary>
    public void NoteRunFinished(TimeSpan drove)
    {
        if (Status is SolverStatus.Driving or SolverStatus.HandedOff)
            RunsDriven++;

        Stop("run finished");
    }

    private void NoteBusy()
    {
        Status = SolverStatus.Driving;
        _idleSinceUtc = null;
    }

    /// <summary>
    /// Nothing bid. §6.2's ladder, in order and bounded: look further, ask the fleet, give the
    /// objects that failed another go, take the authored way out if there is one — and then give the
    /// run back rather than stand in it. A rung that unblocks a loop ends the climb by itself: the
    /// next tick has something to do, and the idle clock resets from there.
    /// </summary>
    private void NoteIdle(WorldModel.Snapshot world)
    {
        if (_idleSinceUtc is null)
        {
            _idleSinceUtc = world.UtcNow;
            LastStuckPosition = world.Position;
            Rung = LadderRung.None;
            _widened = false;
            _fleetAsked = false;
            _retried = false;
            _overrideTried = false;
            _overrideTarget = null;
        }

        // An authored escape is walked before anything else is decided: somebody wrote it down for
        // exactly this spot, and the ladder's remaining budget bounds the detour.
        if (_overrideTarget is { } escape)
        {
            if (Vector3.Distance(world.Position, escape) <= EscapeArrival)
            {
                _overrideTarget = null;
            }
            else
            {
                _game.MoveTo(escape);
                Detail = $"taking the authored way out to ({escape.X:0.#}, {escape.Z:0.#})";
                return;
            }
        }

        var idle = world.UtcNow - _idleSinceUtc.Value;

        if (idle >= HandBackAfter)
        {
            Rung = LadderRung.HandBack;
            HandBack("nothing left to fight, touch or reach");
            return;
        }

        if (idle >= OverrideAfter && !_overrideTried && _ladder is not null)
        {
            _overrideTried = true;
            Rung = LadderRung.Override;
            _overrideTarget = _ladder.OverrideWaypoint();
            Detail = _overrideTarget is { } waypoint
                ? $"nothing bids — taking the authored way out to ({waypoint.X:0.#}, {waypoint.Z:0.#})"
                : "nothing bids — no authored way out here";
            _log?.Invoke(Detail);
            return;
        }

        if (idle >= RetryAfter && !_retried && _ladder is not null)
        {
            _retried = true;
            Rung = LadderRung.Retry;
            var again = _ladder.RetryInteractables();
            Detail = again > 0 ? $"giving {again} object(s) another go" : "nothing worth retrying";
            _log?.Invoke($"Solver: {Detail}.");
            return;
        }

        if (idle >= FleetAfter && !_fleetAsked && _ladder is not null)
        {
            _fleetAsked = true;
            Rung = LadderRung.Fleet;
            var opened = _ladder.FleetSweep();
            Detail = opened > 0 ? $"a peer is past {opened} edge(s) — following" : "nothing from the fleet yet";
            _log?.Invoke(opened > 0
                ? $"Solver: a fleet peer is already past {opened} edge(s) — marking them open."
                : "Solver: asking the fleet — nobody is past anything yet.");
            return;
        }

        if (idle >= WidenAfter && !_widened)
        {
            _widened = true;
            Rung = LadderRung.Widen;
            _perception.Widen();
            Detail = $"nothing bids — looking {(int)ShadowObserver.WidenedScanRadius}y and re-probing every edge";
            _log?.Invoke("Solver: nothing to fight, touch or walk to — widening perception.");
        }
    }

    /// <summary>
    /// Gives the run back, naming where. This is §6.2's rung 5, minus the naming of an unknown gate
    /// — the caller has the frontier navigator's landmarks and overrides behind this, and whatever
    /// it makes of it is better than a solver standing still.
    /// </summary>
    private void HandBack(string why)
    {
        var where = LastStuckPosition is { } position
            ? $"({position.X:0.#}, {position.Y:0.#}, {position.Z:0.#})"
            : "an unknown spot";

        _game.StopMoving();

        RunsHandedBack++;
        Status = SolverStatus.HandedOff;
        Detail = $"handed back: {why} at {where}";
        _log?.Invoke($"Solver: handing this run back — {why} at {where}.");
    }
}
