using System;
using System.Collections.Generic;
using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Paths;

namespace Theseus.Services.Run;

/// <summary>Where the executor is.</summary>
public enum ExecutorStatus
{
    Idle,
    Running,

    /// <summary>Ran off the end of the path — the route is done.</summary>
    Finished,

    /// <summary>Stopped with a reason. Never advances on its own from here.</summary>
    Faulted,
}

/// <summary>
/// Walks a <see cref="ThreadPath"/>, one step per frame.
///
/// <para>
/// All game contact goes through <see cref="IStepWorld"/>, so what lives here is only the parts
/// that are decisions: when a step is finished, when combat takes precedence over walking, where a
/// branch goes, and when to give up. Those are the parts worth testing, and they are tested.
/// </para>
///
/// <para>
/// <b>Combat is not a step.</b> Routes do not contain "kill this pack" instructions — they contain
/// <see cref="StepVerb.StopForCombat"/> toggles, and while that is on and the player is in combat
/// the executor simply stops walking. The rotation plugin does the killing because
/// <c>Theseus.IsBusy</c> is true throughout. That is the whole trash-fighting design.
/// </para>
/// </summary>
public sealed class StepExecutor
{
    /// <summary>Re-path attempts before settling for a position inside the arrival slack.</summary>
    private const int MaxArrivalAttempts = 3;

    /// <summary>
    /// Consecutive empty pathfinds before a destination is declared unreachable.
    ///
    /// <para>
    /// More than one because a pathfind is asynchronous and a single empty read can be a race
    /// rather than a verdict. Three is enough to be sure and still fails in seconds, where the
    /// progress watchdog would take three minutes to reach the same conclusion less usefully.
    /// </para>
    /// </summary>
    private const int MaxFailedPathfinds = 3;

    /// <summary>
    /// Consecutive move requests that leave the character exactly where it was before the waypoint
    /// is declared unwalkable.
    ///
    /// <para>
    /// A separate failure from an empty pathfind, and the one that hurts more, because every signal
    /// says success: vnavmesh returns a full path, reports the pathfind complete, and the character
    /// does not take a single step. Mistwake produced it six yalms from a waypoint reached from the
    /// wrong side — three waypoints of path, three minutes of standing still, and nothing in the log
    /// but the same move-to being queued once a second. The navmesh believing a route exists is not
    /// evidence the character can walk it.
    /// </para>
    /// </summary>
    private const int MaxStuckMoves = 8;

    /// <summary>
    /// Stuck moves before trying to jump free rather than just asking again.
    ///
    /// <para>
    /// A character wedged in scenery is the common case behind "vnavmesh has a path but nothing
    /// moves", and it is usually a tank standing wherever a merged pull happened to end. The
    /// navmesh is built from the geometry the pathfinder knows about, so it will happily route out
    /// of a spot the character cannot physically leave, and re-issuing the same move forever
    /// changes nothing. A jump clears most of them; the fault stays as the backstop for the rest.
    /// </para>
    /// </summary>
    private const int UnstuckAtMoves = MaxStuckMoves / 2;

    /// <summary>
    /// Distance at which a waypoint that will not close is taken as reached.
    ///
    /// <para>
    /// Above <see cref="StepTuning.ArrivalSlack"/> on purpose: slack is what we settle for while
    /// vnavmesh is still trying, and this is what we settle for once it has demonstrably stopped
    /// trying. Deliberately not larger — past this the character really is somewhere it should not
    /// be, and quietly skipping would walk a route that never reached its own waypoints.
    /// </para>
    /// </summary>
    private const float CloseEnoughWhenStuck = 8f;

    /// <summary>
    /// What we ask vnavmesh to treat as arrival. Well under
    /// <see cref="StepTuning.ArrivalTolerance"/> so it always overshoots our requirement rather
    /// than stopping short of it — the two tolerances have to agree in that direction or waypoints
    /// become unreachable by construction.
    /// </summary>
    private const float MoveTolerance = 0.25f;

    /// <summary>Search radius when a route names an object but its position is only approximate.</summary>
    private const float InteractSearchRadius = 10f;

    private static readonly TimeSpan DefaultStepTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How far the character must move to count as still making progress. Comfortably above
    /// standing-still jitter and far below a real path leg.
    /// </summary>
    private const float ProgressEpsilon = 2f;

    /// <summary>Bosses legitimately take a long time; everything else that does is stuck.</summary>
    private static readonly TimeSpan BossStepTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How often a blocked destination is re-attempted. Slow enough that a shut door costs one
    /// pathfind a second rather than one a frame, fast enough to move on promptly once it opens.
    /// </summary>
    private static readonly TimeSpan MoveRetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Retry interval when already within the arrival slack.
    ///
    /// <para>
    /// A near miss and a blocked door are different problems wearing the same shape. A door needs
    /// patience — one pathfind a second until it opens. Being half a yalm short needs the opposite:
    /// the answer arrives immediately, and waiting a second between attempts left the character
    /// standing still for three seconds at every waypoint it did not land on exactly. Across thirty
    /// waypoints that is most of a minute spent doing nothing.
    /// </para>
    /// </summary>
    private static readonly TimeSpan NearMissRetryInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a boss step waits for a pull before deciding the marker has no module.</summary>
    private static readonly TimeSpan BossPullGrace = TimeSpan.FromSeconds(20);

    /// <summary>Boss markers are approximate — stopping on top of one is neither needed nor wanted.</summary>
    private const float BossApproachTolerance = 9f;

    /// <summary>
    /// How far from the marker to look for the boss itself. Wider than the approach tolerance
    /// because the marker is the doorway to the arena, not the boss's own position.
    /// </summary>
    private const float BossPullRadius = 30f;

    /// <summary>
    /// How long to keep looking for a chest that has not appeared yet.
    ///
    /// <para>
    /// A boss's coffer lands a moment after the kill rather than with it. Measured in Mistwake:
    /// 0.3 seconds for Amdusias, 4.4 for the Thundergust Griffin. Ten leaves generous headroom
    /// without idling in a room that was never going to produce one.
    ///
    /// <para>
    /// An earlier forty seconds here was compensating for a different bug — a loot pass that opened
    /// in the wrong room entirely, because trash fought on the approach counted as the encounter.
    /// Padding a timeout to cover a wrong location is worth remembering as a smell: the numbers
    /// only became sane once the pass ran where the boss actually died.
    /// </para>
    /// </summary>
    private static readonly TimeSpan BossLootSpawnGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How much longer to look once something has already been taken. A room that gave up one
    /// chest has probably finished; waiting the full spawn grace again would idle after every boss.
    /// </summary>
    private static readonly TimeSpan ExtraCofferGrace = TimeSpan.FromSeconds(3);

    /// <summary>How far to look for a chest once a boss is down. Room-sized, not dungeon-sized.</summary>
    private const float BossLootRadius = 40f;

    /// <summary>
    /// How long the post-boss loot pass may take before the run moves on regardless. A chest is
    /// worth a few seconds and never worth a stalled farm loop.
    /// </summary>
    private static readonly TimeSpan BossLootTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often to re-attempt the pull while nothing has engaged.</summary>
    private static readonly TimeSpan PullRetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a boss that is standing right there gets to engage before the run gives up on it.
    ///
    /// <para>
    /// This is the "bosses sometimes never engage and a ninety-minute run expires" failure, and
    /// the cost of getting it wrong is asymmetric. Walking past a live boss loses the run silently;
    /// waiting forever loses it slowly. Stopping with a reason loses a minute and tells you which
    /// step to look at.
    /// </para>
    /// </summary>
    private static readonly TimeSpan BossEngageTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long a boss module may stay down, with the party alive and the objective unchanged,
    /// before the fight is taken as finished anyway.
    ///
    /// <para>
    /// A multi-phase boss is several modules in one encounter, and between them nothing says the
    /// fight is still on: the module is gone, the combat flag drops, and no chest appears. The
    /// Ultima Weapon brings its second module up almost exactly ten seconds after the first ends,
    /// which raced the ten-second loot grace — three runs in nine lost, and the step walked off to
    /// its next waypoint as phase two began, leaving the character on a navmesh path the boss
    /// handler would not interrupt. The objective is what settles it: a boss that died advanced
    /// one, and a module that ended without that is a phase break, not a kill. This bound exists
    /// for bosses whose objective never ticks, so the old behaviour is a fallback rather than gone.
    /// </para>
    /// </summary>
    private static readonly TimeSpan PhaseGrace = TimeSpan.FromSeconds(60);

    private readonly IStepWorld _world;
    private readonly Func<bool> _wallToWall;
    private readonly Func<StepTuning> _tuning;
    private readonly Func<bool> _lootBossChests;
    private readonly Func<bool> _lootRouteChests;
    private readonly Func<DutyObjectiveSnapshot> _objectives;

    private ThreadPath? _path;
    private int _index;
    private bool _stepBegun;
    private DateTime _stepStartedUtc;
    private DateTime _waitUntilUtc;
    private bool _stopForCombat = true;
    private bool _bossAiRequested;
    private bool _bossApproached;
    private bool _bossEngaged;
    private DutyObjectiveSnapshot _objectivesAtPull = DutyObjectiveSnapshot.Unavailable;
    private DateTime _moduleEndedUtc = DateTime.MinValue;
    private bool _phaseWaitLogged;
    private bool _forwardMoving;
    private Vector3 _lastProgressPosition;
    private DateTime _lastMoveRequestUtc = DateTime.MinValue;
    private DateTime _lastPullAttemptUtc = DateTime.MinValue;
    private bool _pullTargetFound;
    private int _arrivalAttempts;
    private bool _approachSatisfied;
    private DateTime _bossLootStartedUtc = DateTime.MinValue;
    private ulong _lootTargetId;
    private bool _lootReported;
    private int _cofferstakenHere;
    private bool _chestSightingLogged;
    private bool _moveIssued;
    private int _failedPathfinds;
    private int _stuckMoves;
    private bool _unstuckTried;
    private Vector3 _positionAtLastMove;

    /// <summary>
    /// Coffers already dealt with this run. A looted chest lingers in the object table, so without
    /// remembering it the loot pass would re-open the same one until its timeout.
    /// </summary>
    private readonly HashSet<ulong> _openedCoffers = [];

    /// <param name="wallToWall">
    /// Whether this character is pulling wall to wall — in practice, whether it is the tank.
    /// Decides if <c>W2W</c>-tagged steps run. A function rather than a value because role and
    /// settings are read fresh for each run.
    /// </param>
    /// <param name="tuning">Field-tuned movement constants; see <see cref="StepTuning"/>.</param>
    /// <param name="lootBossChests">Whether to take the chest a boss drops.</param>
    /// <param name="lootRouteChests">Whether to take the route's own treasure detours.</param>
    /// <param name="objectives">
    /// Live duty objectives, for telling a dead boss from one between phases. Absent, a boss step
    /// falls back to timing alone.
    /// </param>
    public StepExecutor(
        IStepWorld world,
        Func<bool>? wallToWall = null,
        Func<StepTuning>? tuning = null,
        Func<bool>? lootBossChests = null,
        Func<bool>? lootRouteChests = null,
        Func<DutyObjectiveSnapshot>? objectives = null)
    {
        _world = world;
        _wallToWall = wallToWall ?? (() => false);
        _tuning = tuning ?? (() => StepTuning.Default);
        _lootBossChests = lootBossChests ?? (() => true);
        _lootRouteChests = lootRouteChests ?? (() => true);
        _objectives = objectives ?? (() => DutyObjectiveSnapshot.Unavailable);
    }

    public ExecutorStatus Status { get; private set; } = ExecutorStatus.Idle;

    /// <summary>Index of the step being executed, or -1 when idle.</summary>
    public int CurrentStepIndex => Status == ExecutorStatus.Running ? _index : -1;

    /// <summary>Why the run stopped, when <see cref="Status"/> is <see cref="ExecutorStatus.Faulted"/>.</summary>
    public string FaultReason { get; private set; } = string.Empty;

    /// <summary>Whether vnavmesh accepted the last move request. Diagnostic.</summary>
    public bool LastMoveAccepted { get; private set; } = true;

    /// <summary>Consecutive move requests that have not moved the character. Diagnostic.</summary>
    public int StuckMoves => _stuckMoves;

    /// <summary>Where the current step is trying to get to, or null when it is not walking.</summary>
    public Vector3? CurrentDestination
        => Status == ExecutorStatus.Running && _path is not null && _index < _path.Steps.Count
           && _path.Steps[_index].Position.IsSet
            ? _path.Steps[_index].Position.ToVector3()
            : null;

    /// <summary>Raised as each step begins. This is what teaches <c>ObjectiveMapper</c>.</summary>
    public event Action<int>? StepStarted;

    /// <summary>Starts a route, optionally partway in — which is how the Thread resumes one.</summary>
    public void Start(ThreadPath path, int fromStep = 0)
    {
        if (!path.IsRunnable)
        {
            Fault(path.Blockers.Count > 0 ? path.Blockers[0] : "Path has no steps.");
            return;
        }

        _path = path;
        _index = Math.Clamp(fromStep, 0, path.Steps.Count - 1);
        _stepBegun = false;
        _stopForCombat = true;
        _bossAiRequested = false;
        _openedCoffers.Clear();
        FaultReason = string.Empty;
        Status = ExecutorStatus.Running;

        // Comfortably inside ArrivalTolerance, so the navigator always gets closer than we ask for.
        // Whatever it was configured with before is not ours to assume: a six-yalm setting left it
        // stopping short of every waypoint in the route, and we re-pathed at each one forever.
        _world.SetMoveTolerance(MoveTolerance);

        RestoreModeSetBefore(_index);
        EnsureBossModAi();
    }

    /// <summary>
    /// Replays the mode-setting steps ahead of a resume point.
    ///
    /// <para>
    /// Some verbs are settings, not actions: they say what mode the route expects to be in from
    /// there on, and every later step is written assuming it. Starting partway in used to drop all
    /// of them, so a resume began in default mode no matter what the route had asked for.
    /// </para>
    ///
    /// <para>
    /// Wall-to-wall routes are where it shows. Mistwake opens by turning combat stops <i>off</i> at
    /// step 0 and only back on at step 2 — that gap is the chain pull. Resume at step 1, which is
    /// what auto-start does on arrival, and step 0 never runs: combat stops stay on and the
    /// character fights each pack where it stands. The route looked like it was ignoring its own
    /// wall-to-wall tags when in fact it had never been switched into the mode they belong to.
    /// </para>
    ///
    /// <para>
    /// Only the resolved final value is applied, so replaying costs one call rather than one per
    /// step, and skipped steps stay skipped — a route's tank-only toggles must not take effect for
    /// a character that would not have run them.
    /// </para>
    /// </summary>
    private void RestoreModeSetBefore(int fromStep)
    {
        if (_path is null || fromStep <= 0)
            return;

        bool? rotation = null;

        for (var i = 0; i < fromStep && i < _path.Steps.Count; i++)
        {
            var step = _path.Steps[i];
            if (ShouldSkip(step))
                continue;

            switch (step.Verb)
            {
                case StepVerb.StopForCombat:
                    _stopForCombat = StepArguments.ParseBool(First(step)) ?? _stopForCombat;
                    break;

                case StepVerb.Rotation:
                    rotation = StepArguments.ParseBool(First(step)) ?? true;
                    break;
            }
        }

        if (rotation is { } enabled)
            _world.SetRotationEnabled(enabled);
    }

    /// <summary>Stops and releases anything the run was holding.</summary>
    public void Stop()
    {
        ReleaseHolds();
        _path = null;
        Status = ExecutorStatus.Idle;
    }

    /// <summary>One frame of progress. Cheap and non-blocking; safe to call every tick.</summary>
    public void Tick()
    {
        if (Status != ExecutorStatus.Running || _path is null)
            return;

        // Filtered steps are consumed in one pass rather than one per frame — Mistwake skips three
        // in a row repeatedly, and a frame each would have the character standing still between
        // every pack.
        while (_index < _path.Steps.Count && ShouldSkip(_path.Steps[_index]))
            Advance();

        if (Status != ExecutorStatus.Running)
            return; // the route ended while skipping

        if (_index >= _path.Steps.Count)
        {
            ReleaseHolds();
            Status = ExecutorStatus.Finished;
            return;
        }

        var step = _path.Steps[_index];

        if (!_stepBegun)
        {
            _stepBegun = true;
            _stepStartedUtc = _world.UtcNow;
            _waitUntilUtc = DateTime.MinValue;
            _bossApproached = false;
            _bossEngaged = false;
            _objectivesAtPull = DutyObjectiveSnapshot.Unavailable;
            _moduleEndedUtc = DateTime.MinValue;
            _phaseWaitLogged = false;
            _lastProgressPosition = _world.PlayerPosition;
            _lastMoveRequestUtc = DateTime.MinValue; // throttle retries, never the first attempt
            _lastPullAttemptUtc = DateTime.MinValue;
            _pullTargetFound = false;
            _arrivalAttempts = 0;
            _approachSatisfied = false;
            _bossLootStartedUtc = DateTime.MinValue;
            _lootReported = false;
            _cofferstakenHere = 0;
            _chestSightingLogged = false;
            _moveIssued = false;
            _failedPathfinds = 0;
            _stuckMoves = 0;
            _unstuckTried = false;
            _positionAtLastMove = _world.PlayerPosition;
            StepStarted?.Invoke(_index);

            // One line per step, unconditionally. Every stall this project has had was diagnosed by
            // working out which step the run was sitting on, and without a trail that question can
            // only be answered by guessing — which has been wrong more often than it has been right.
            // Thirty-odd lines a run is a cheap price for never having to guess again.
            _world.Log(
                $"Step {_index}/{_path.Steps.Count} {step.Verb}" +
                (step.Arguments.Length > 0 ? $" [{string.Join(", ", step.Arguments)}]" : string.Empty) +
                (step.Position.IsSet ? $" → ({step.Position})" : string.Empty));
        }

        // Trash gate. Walking away from a pack the rotation is mid-fight with is how a run pulls
        // the whole wing, so travel holds until combat ends.
        //
        // Keyed on whether this step still has ground to cover, not on its verb: a StopForCombat
        // step is itself gateable now that positions are honoured everywhere, and gating it by verb
        // deadlocked the run — a route could never switch combat stops OFF while in combat, which
        // is exactly when a wall-to-wall pull does it.
        if (_stopForCombat && _world.InCombat && HasGroundToCover(step))
        {
            _world.StopMoving();
            StopForwardMovement();
            _stepStartedUtc = _world.UtcNow; // combat time is not stuck time
            return;
        }

        // The watchdog measures time without progress, not time on the step. Routes are not evenly
        // spaced — Mistwake crosses the whole dungeon in four MoveTo legs — so a single leg can
        // legitimately run for minutes, and a fixed per-step deadline would kill a run that is
        // walking along perfectly well. A character that is still covering ground is not stuck.
        var position = _world.PlayerPosition;
        if (Vector3.Distance(position, _lastProgressPosition) > ProgressEpsilon)
        {
            _lastProgressPosition = position;
            _stepStartedUtc = _world.UtcNow;
        }

        if (_world.UtcNow - _stepStartedUtc > TimeoutFor(step.Verb))
        {
            Fault($"Step {_index} ({step.Verb}) made no progress for {TimeoutFor(step.Verb).TotalMinutes:0} minutes.");
            return;
        }

        if (Execute(step))
            Advance();
    }

    /// <summary>
    /// Runs one step, returning whether it is finished. Steps that take time — walking, waiting,
    /// a boss — return false and are re-entered next frame.
    /// </summary>
    private bool Execute(ThreadStep step)
    {
        // A position on a step is a waypoint, whatever the verb is. This is the library's central
        // convention and it is easy to miss: only a third of positioned steps are MoveTo. Mistwake
        // walks its whole first wing on positions attached to StopForCombat and Wait steps —
        //
        //     StopForCombat false  (174,47,598)
        //     Wait          2500   (174,47,558)      <- walk here, then wait for the pull
        //     StopForCombat true   (207,46,468)
        //
        // which is the wall-to-wall pattern written entirely without MoveTo. Ignoring those
        // positions does not stall the run, which is what makes it dangerous: it silently walks
        // the straight line to the next waypoint it does recognise, over cliffs the navmesh
        // believes are walkable and into doors that have not opened yet.
        // Approach once per step, then never again. A waypoint is somewhere to get to, not
        // somewhere to be held: AutoMoveFor exists precisely to walk away from its own position —
        // off a ledge, onto a slide — and re-checking distance afterwards sends the run straight
        // back up. The same applies to anything else that moves the character mid-step, from a
        // knockback to the boss AI repositioning.
        if (!_approachSatisfied && step.Position.IsSet && RequiresApproach(step.Verb))
        {
            if (!MoveToStep(step.Position.ToVector3(), _tuning().ArrivalTolerance))
                return false;

            _approachSatisfied = true;
        }

        switch (step.Verb)
        {
            // Annotations and verbs whose effect is not ours to produce.
            case StepVerb.Comment:
            case StepVerb.Unknown:
                return true;

            case StepVerb.MoveTo:
                return true; // the approach above is the whole step

            case StepVerb.Boss:
                return BossStep(step);

            case StepVerb.TreasureCoffer:
                return TreasureStep(step);

            case StepVerb.Interactable:
                return InteractStep(step);

            case StepVerb.KillInRange:
                return KillInRangeStep(step);

            case StepVerb.AutoMoveFor:
                return AutoMoveStep(step);

            case StepVerb.Wait:
                return WaitStep(step);

            case StepVerb.WaitFor:
                return WaitForStep(step);

            case StepVerb.StopForCombat:
                _stopForCombat = StepArguments.ParseBool(First(step)) ?? _stopForCombat;
                return true;

            case StepVerb.Rotation:
                _world.SetRotationEnabled(StepArguments.ParseBool(First(step)) ?? true);
                return true;

            case StepVerb.BossMod:
                // Routes carry "BossMod off" steps; they are ignored on purpose. See EnsureBossModAi.
                if (StepArguments.ParseBool(First(step)) ?? true)
                    EnsureBossModAi();
                return true;

            case StepVerb.ForceAttack:
                _world.AttackNearestWithin(InteractSearchRadius * 3f);
                return true;

            case StepVerb.Target:
                if (uint.TryParse(First(step), out var targetId))
                    _world.TryTargetDataId(targetId);
                return true;

            case StepVerb.Jump:
                _world.Jump();
                return true;

            case StepVerb.JumpTo:
                return JumpToStep(step);

            case StepVerb.ChatCommand:
                if (First(step) is { Length: > 0 } command)
                    _world.SendChatCommand(command);
                return true;

            case StepVerb.SelectString:
                if (StepArguments.ParseInt(First(step)) is { } stringIndex)
                    _world.SelectStringIndex(stringIndex);
                return true;

            case StepVerb.SelectYesno:
                _world.SelectYesNo(StepArguments.ParseBool(First(step)) ?? true);
                return true;

            case StepVerb.SelectJournalResult:
                _world.SelectYesNo(StepArguments.ParseBool(First(step)) ?? true);
                return true;

            case StepVerb.Revival:
                return RevivalStep();

            case StepVerb.ModifyIndex:
                return ModifyIndexStep(step);

            case StepVerb.ConditionAction:
                return ConditionActionStep(step);

            // Recognised but not acted on: each appears a handful of times in the whole library,
            // and doing nothing is the correct behaviour for all of them here — Pandora and BLU
            // loading are other plugins' business, camera facing does not affect automation, and
            // variant votes and module disabling belong to content P1 does not target.
            case StepVerb.PausePandora:
            case StepVerb.BLULoad:
            case StepVerb.CameraFacing:
            case StepVerb.VariantVote:
            case StepVerb.DisableBMModule:
            case StepVerb.Action:
                return true;

            case StepVerb.DutySpecificCode:
                // Import blocks these, so reaching one means a path was hand-edited.
                Fault($"Step {_index} needs AutoDuty's own hardcoded logic and cannot be run.");
                return false;

            default:
                return true;
        }
    }

    // ── Step kinds ──

    /// <param name="faultIfUnreachable">
    /// Whether an unreachable destination should stop the run. False for optional excursions like
    /// a chest, where the right answer is to shrug and carry on rather than end the dungeon.
    /// </param>
    private bool MoveToStep(Vector3 destination, float tolerance, bool faultIfUnreachable = true)
    {
        var distance = Vector3.Distance(_world.PlayerPosition, destination);
        if (distance <= tolerance)
        {
            _world.StopMoving();
            return true;
        }

        if (_world.IsMoving)
        {
            _failedPathfinds = 0;
            return false;
        }

        if (!_world.NavmeshReady)
            return false; // still building; the watchdog is the backstop

        // Throttle. A destination that cannot be reached — a boss door that has not opened yet is
        // the common one — makes the pathfind fail immediately, so an unthrottled retry asks
        // vnavmesh for a full path every single frame for as long as the door stays shut. Closing
        // a last half-yalm is not that problem and must not pay that price.
        var nearMiss = distance <= _tuning().ArrivalSlack;
        if (_world.UtcNow - _lastMoveRequestUtc < (nearMiss ? NearMissRetryInterval : MoveRetryInterval))
            return false;

        // Movement has stopped short of the waypoint. Try again a few times, then take what we
        // have if it is close: some waypoints simply cannot be stood on exactly, and a run that
        // re-paths forever at a doorway is worse than one that rounds a corner.
        if (nearMiss && ++_arrivalAttempts >= MaxArrivalAttempts)
        {
            _world.StopMoving();
            return true;
        }

        // A finished pathfind holding no waypoints means there is no route to the destination —
        // usually a waypoint recorded slightly off the navmesh, which "use my position" produces
        // easily from a ledge or a step. vnavmesh knows this instantly, so there is no reason to
        // spend three minutes standing still before saying so.
        if (_moveIssued && _world.PathWaypointCount == 0 && ++_failedPathfinds >= MaxFailedPathfinds)
        {
            // Optional excursions get "give up on this destination"; the route itself gets a stop.
            // Reporting the step as complete would advance past something we never performed, and
            // would overwrite the fault with a finished route.
            if (!faultIfUnreachable)
                return true;

            Fault($"Step {_index}: no path to ({destination.X:0.##}, {destination.Y:0.##}, " +
                  $"{destination.Z:0.##}) — the waypoint may be off the navmesh.");
            return false;
        }

        // Did the previous request actually move us? A path that vnavmesh is happy with but the
        // character cannot walk leaves this at zero for as long as we keep asking, and every other
        // signal reads as success — so this is the only place the difference is visible.
        // Combat is exempt. A fight pins the character where it stands — a stun, a knockback, or
        // simply killing a pack on a StopForCombat-false leg that keeps issuing moves throughout —
        // and movement being prevented by a fight is not movement that is impossible.
        if (_world.InCombat)
            _stuckMoves = 0;
        else if (_moveIssued && Vector3.Distance(_world.PlayerPosition, _positionAtLastMove) <= ProgressEpsilon)
            _stuckMoves++;
        else
            _stuckMoves = 0;

        if (_stuckMoves >= UnstuckAtMoves)
        {
            // A hop too short for vnavmesh to bother with. It treats itself as already arrived,
            // stops without taking a step, and no amount of asking again changes that — while we go
            // on insisting on a tolerance it will never close. Mistwake's steps 2 and 3 sit 1.85
            // yalms apart and stall every time, while the twenty-yalm hops either side of them walk
            // fine. Being within a couple of yalms of a waypoint is being at it; take it and move on.
            if (distance <= CloseEnoughWhenStuck)
            {
                _world.Log($"Step {_index}: {distance:0.0}y short and vnavmesh will not close it — taking it.");
                _world.StopMoving();
                return true;
            }

            // Far enough out that being physically wedged is worth one attempt to shake off.
            if (!_unstuckTried)
            {
                _unstuckTried = true;
                _world.StopMoving();
                _world.Jump();
                _world.Log($"Step {_index}: not moving with {distance:0.0}y to go — jumping to break free.");
                return false;
            }
        }

        if (_stuckMoves >= MaxStuckMoves)
        {
            if (!faultIfUnreachable)
                return true;

            // The state at the moment it gives up, in the message itself. Catching this live in a
            // debug window means noticing the stall inside a few seconds and having the window
            // already open; carrying it in the fault means the answer is in the log every time.
            var here = _world.PlayerPosition;
            Fault($"Step {_index}: {MaxStuckMoves} moves to ({destination.X:0.##}, {destination.Y:0.##}, " +
                  $"{destination.Z:0.##}) and the character has not moved, jump included. " +
                  $"At ({here.X:0.##}, {here.Y:0.##}, {here.Z:0.##}), {distance:0.0}y out · " +
                  $"vnav accepted {LastMoveAccepted}, moving {_world.IsMoving}, " +
                  $"waypoints {_world.PathWaypointCount} · combat {_world.InCombat}, " +
                  $"ready {_world.IsReady}, occupied {_world.IsOccupied}, casting {_world.IsCasting}.");
            return false;
        }

        _lastMoveRequestUtc = _world.UtcNow;
        _moveIssued = true;
        _positionAtLastMove = _world.PlayerPosition;

        // The return value used to be discarded. A refused move and an accepted-then-abandoned one
        // are indistinguishable from the outside, and telling them apart is the whole question when
        // a run pulses, so it is worth one field.
        LastMoveAccepted = _world.MoveTo(destination);
        return false;
    }

    /// <summary>
    /// Walks to the boss, hands the fight to BossMod, and waits for the encounter to finish.
    ///
    /// <para>
    /// The subtlety is the beginning, not the end. A boss step is reached <i>before</i> the pull,
    /// so "no module is active" is the normal state on arrival and cannot mean the fight is over —
    /// treating it that way walks the run straight past an undefeated boss. The step therefore
    /// waits for the module to appear, and only concludes there is no boss here at all if nothing
    /// has started within the pull grace.
    /// </para>
    /// </summary>
    private bool BossStep(ThreadStep step)
    {
        // Approach first. BossMod suppresses its own movement while a navmesh path is running, so
        // walking in and the AI wanting to reposition cannot fight each other.
        var destination = step.Position.IsSet
            ? step.Position.ToVector3()
            : StepArguments.ParsePoint(First(step))?.ToVector3();

        // The module is unambiguous: if it is up, this is the fight, wherever we happen to be.
        if (_world.BossModuleActive)
        {
            if (!_bossEngaged)
                _objectivesAtPull = _objectives();

            _bossApproached = true;
            _bossEngaged = true;

            // A module coming back up is the next phase. Whatever the gap between phases started
            // — a phase clock, a loot pass — belongs to the fight that is still going.
            _moduleEndedUtc = DateTime.MinValue;
            _bossLootStartedUtc = DateTime.MinValue;
            return false;
        }

        // Combat only counts as the boss once we have actually reached the boss. Trash fought on
        // the way in is still combat, and treating it as the encounter meant the step declared the
        // fight over the moment that trash died — opening the loot pass in an empty room, waiting
        // out its grace, and then advancing past a boss that had never been pulled.
        if (!_bossApproached)
        {
            if (_world.InCombat)
                return false;

            if (destination is not null
                && Vector3.Distance(_world.PlayerPosition, destination.Value) > BossApproachTolerance)
            {
                MoveToStep(destination.Value, BossApproachTolerance);
                return false;
            }

            _bossApproached = true;
        }

        if (_world.InCombat)
        {
            if (!_bossEngaged)
                _objectivesAtPull = _objectives();

            _bossEngaged = true;
            return false;
        }

        // Fought and finished — or between phases, which looks identical from here.
        if (_bossEngaged)
            return EncounterOver() && LootBossRoom();

        // Nothing pulls a boss on its own. Trash aggroes on proximity and the rotation fights back,
        // but a boss sits there — and so does the run, waiting for a module that cannot start.
        // Field-confirmed: giving the rotation a target is the whole pull. It engages immediately,
        // so there is no attack to send, only something to point at.
        if (_world.UtcNow - _lastPullAttemptUtc >= PullRetryInterval)
        {
            _lastPullAttemptUtc = _world.UtcNow;
            _pullTargetFound |= _world.AttackNearestWithin(BossPullRadius);
        }

        var waited = _world.UtcNow - _stepStartedUtc;
        if (waited < BossPullGrace)
            return false;

        // Nothing to fight here. Plenty of Boss markers have no encounter behind them, so this is
        // an ordinary outcome rather than a problem.
        if (!_pullTargetFound)
            return true;

        // Something IS standing there and will not engage. Walking on past a live boss is how a run
        // quietly becomes unrecoverable, so stop and name the step instead.
        if (waited > BossEngageTimeout)
            Fault($"Step {_index}: a boss is present but would not engage after {BossEngageTimeout.TotalSeconds:0} seconds.");

        return false;
    }

    /// <summary>
    /// Whether the fight is really over, rather than paused between phases.
    ///
    /// <para>
    /// The module ending and combat dropping are both true between phases, so neither can say. The
    /// duty objective can: killing the boss advances it, a phase break does not. Unreadable
    /// objectives fall back to the old timing-only behaviour, and an objective that never ticks is
    /// bounded by <see cref="PhaseGrace"/> so a boss the game does not count cannot stall the run.
    /// </para>
    /// </summary>
    private bool EncounterOver()
    {
        switch (EncounterClassifier.Classify(_objectivesAtPull, _objectives()))
        {
            case EncounterOutcome.Cleared:
            case EncounterOutcome.Unknown:
                return true;
        }

        // Nothing gained. A wipe reads exactly like a phase break, and a dead character must not
        // run the phase clock down while waiting for a raise — the fight resumes when it resumes.
        if (_world.IsDead)
        {
            _moduleEndedUtc = DateTime.MinValue;
            return false;
        }

        if (_moduleEndedUtc == DateTime.MinValue)
            _moduleEndedUtc = _world.UtcNow;

        if (_world.UtcNow - _moduleEndedUtc < PhaseGrace)
        {
            if (!_phaseWaitLogged)
            {
                _phaseWaitLogged = true;
                _world.Log($"Step {_index}: boss module ended with the objective unchanged — " +
                           "waiting for the next phase.");
            }

            return false;
        }

        // Past the bound with nothing to show for it. Forgetting the pull snapshot makes every
        // later read Unknown, so the loot pass proceeds without re-deciding — and re-logging — this
        // every tick.
        _objectivesAtPull = DutyObjectiveSnapshot.Unavailable;
        _world.Log($"Step {_index}: no module and no objective progress for " +
                   $"{PhaseGrace.TotalSeconds:0}s — treating the fight as over.");
        return true;
    }

    /// <summary>
    /// Clears the chests in a boss room, returning true when there is nothing left to take.
    ///
    /// <para>
    /// Routes do not cover these. Measured across the library: two hundred and fifteen of three
    /// hundred and nine routes end on a <see cref="StepVerb.Boss"/> step and a hundred and
    /// fifty-three contain no coffer step at all, so the chest that appears when a boss dies —
    /// including the one at the end of the dungeon — is never in the path data. Triggering on the
    /// encounter ending rather than on the duty completing means it works whether or not the run
    /// leaves automatically, and it fires in the one place we know a chest to be.
    /// </para>
    /// </summary>
    private bool LootBossRoom()
    {
        if (!_lootBossChests())
            return true;

        if (_bossLootStartedUtc == DateTime.MinValue)
        {
            _bossLootStartedUtc = _world.UtcNow;

            // Dump what is present the instant the pass opens, not just when it gives up. The
            // failure and the success disagree about whether the coffer is there at the moment the
            // boss dies, and only a reading taken then can settle it.
            _world.Log($"Step {_index}: encounter over, looking for a chest within {BossLootRadius:0} yalms.");
        }

        var elapsed = _world.UtcNow - _bossLootStartedUtc;
        if (elapsed > BossLootTimeout)
            return true;

        var coffer = _world.NearestCoffer(BossLootRadius, _openedCoffers);
        if (coffer is null)
        {
            var patience = _cofferstakenHere > 0 ? ExtraCofferGrace : BossLootSpawnGrace;

            // Only worth reporting when the room gave up nothing at all. After a successful open
            // this is just the check for a second chest coming back empty, which is the ordinary
            // case and reads alarmingly like a failure.
            if (!_lootReported && _cofferstakenHere == 0 && elapsed >= patience)
            {
                _lootReported = true;
                var anywhere = _world.NearestCoffer(float.MaxValue, _openedCoffers);
                _world.Log(anywhere is null
                    ? $"Step {_index}: no treasure object visible anywhere after {elapsed.TotalSeconds:0}s. " +
                      $"Nearby: {_world.DescribeNearby(BossLootRadius)}"
                    : $"Step {_index}: nearest treasure is " +
                      $"{Vector3.Distance(_world.PlayerPosition, anywhere.Value.Position):0} yalms away, " +
                      $"outside the {BossLootRadius:0} yalm search.");
            }

            return elapsed >= patience;
        }

        // Worth recording how long it took to show up — the right spawn grace is a measurement, not
        // a guess, and this is the only place it can be taken. Once, not once per frame while
        // walking over to it.
        if (!_chestSightingLogged)
        {
            _chestSightingLogged = true;
            _world.Log($"Step {_index}: chest appeared after {elapsed.TotalSeconds:0.0}s.");
        }

        // A new target gets a fresh approach budget; otherwise the previous chest's failed
        // attempts would count against this one.
        if (coffer.Value.Id != _lootTargetId)
        {
            _lootTargetId = coffer.Value.Id;
            _arrivalAttempts = 0;
            _lastMoveRequestUtc = DateTime.MinValue;
        }

        if (!MoveToStep(coffer.Value.Position, _tuning().ArrivalTolerance, faultIfUnreachable: false))
            return false;

        // Either we arrived or the chest turned out to be unreachable. Marking it done covers both:
        // a chest is never worth ending a dungeon over.
        _failedPathfinds = 0;
        _world.OpenCoffer(coffer.Value.Id);
        _cofferstakenHere++;
        _world.Log($"Step {_index}: opened a coffer the route did not know about.");
        _openedCoffers.Add(coffer.Value.Id);
        _lootTargetId = 0;
        return false; // look again next tick in case the room holds more than one
    }

    private bool TreasureStep(ThreadStep step)
    {
        _world.TryOpenCofferNear(step.Position.ToVector3(), InteractSearchRadius);
        return true;
    }

    private bool InteractStep(ThreadStep step)
    {
        if (!uint.TryParse(First(step), out var dataId))
            return true;

        _world.TryInteractWithDataId(dataId, InteractSearchRadius);
        return true;
    }

    private bool KillInRangeStep(ThreadStep step)
    {
        var radius = StepArguments.ParseInt(First(step)) ?? 15;

        if (step.Position.IsSet)
        {
            var target = step.Position.ToVector3();
            if (Vector3.Distance(_world.PlayerPosition, target) > _tuning().ArrivalTolerance)
            {
                MoveToStep(target, _tuning().ArrivalTolerance);
                return false;
            }
        }

        // Pull, then let the rotation finish. The step ends when nothing is left in range and we
        // are out of combat, so a pack pulled in two halves still resolves.
        if (_world.AttackNearestWithin(radius))
            return false;

        return !_world.InCombat;
    }

    private bool AutoMoveStep(ThreadStep step)
    {
        var duration = StepArguments.ParseInt(First(step)) ?? 0;
        if (duration <= 0)
            return true;

        if (_waitUntilUtc == DateTime.MinValue)
        {
            _waitUntilUtc = _world.UtcNow.AddMilliseconds(duration);
            _world.SetForwardMovement(true);
            _forwardMoving = true;
        }

        if (_world.UtcNow < _waitUntilUtc)
            return false;

        StopForwardMovement();
        return true;
    }

    private bool WaitStep(ThreadStep step)
    {
        var duration = StepArguments.ParseInt(First(step)) ?? 0;
        if (duration <= 0)
            return true;

        if (_waitUntilUtc == DateTime.MinValue)
            _waitUntilUtc = _world.UtcNow.AddMilliseconds(duration);

        return _world.UtcNow >= _waitUntilUtc;
    }

    private bool WaitForStep(ThreadStep step)
    {
        var argument = First(step);
        if (string.IsNullOrEmpty(argument))
            return true;

        var parts = argument.Split(';', StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "isready":
            case "ready":
                return _world.IsReady;

            case "combat":
                return _world.InCombat;

            case "ooc":
                return !_world.InCombat;

            // Spelled this way in the library. Matching the source's typo is not an endorsement.
            case "isocupied":
            case "isoccupied":
                return _world.IsOccupied;

            case "conditionflag" when parts.Length == 3:
            {
                var expected = StepArguments.ParseBool(parts[2]) ?? true;
                return _world.HasConditionFlag(parts[1]) == expected;
            }

            case "addon" when parts.Length == 2:
                return _world.IsAddonVisible(parts[1]);

            default:
                // A bare number is a duration; a point and a radius waits for arrival.
                if (StepArguments.ParseInt(parts[0]) is { } milliseconds && parts.Length == 1)
                {
                    if (_waitUntilUtc == DateTime.MinValue)
                        _waitUntilUtc = _world.UtcNow.AddMilliseconds(milliseconds);
                    return _world.UtcNow >= _waitUntilUtc;
                }

                if (parts.Length == 2
                    && StepArguments.ParsePoint(parts[0]) is { } point
                    && StepArguments.ParseInt(parts[1]) is { } radius)
                {
                    return Vector3.Distance(_world.PlayerPosition, point.ToVector3()) <= radius;
                }

                return true;
        }
    }

    private bool JumpToStep(ThreadStep step)
    {
        var destination = StepArguments.ParsePoint(First(step))
                          ?? (step.Position.IsSet ? step.Position : null);
        if (destination is null)
        {
            _world.Jump();
            return true;
        }

        if (Vector3.Distance(_world.PlayerPosition, destination.Value.ToVector3()) <= _tuning().ArrivalTolerance)
            return true;

        _world.Jump();
        return false;
    }

    private bool RevivalStep()
    {
        // A revival marker is a no-op while alive; dead, it holds until something raises us.
        // Daedalus owns raising, so waiting here is the whole behaviour.
        return !_world.IsDead;
    }

    private bool ModifyIndexStep(ThreadStep step)
    {
        var delta = StepArguments.ParseInt(First(step));
        if (delta is null)
            return true;

        return !TryJump(delta.Value);
    }

    private bool ConditionActionStep(ThreadStep step)
    {
        var branch = StepArguments.ParseConditionAction(step.Arguments);
        if (branch is null)
            return true;

        if (!ConditionEvaluator.Evaluate(branch.Value.Predicate, _world, out var recognised))
        {
            if (!recognised)
                _world.Log($"Step {_index}: condition \"{branch.Value.Predicate}\" is not a form Theseus knows.");
            return true;
        }

        return !TryJump(branch.Value.IndexDelta);
    }

    // ── Mechanics ──

    /// <summary>
    /// Applies a relative jump. A jump outside the path faults rather than clamping — the real
    /// library contains one, and a route that silently lands on the wrong step is worse than one
    /// that stops and says where.
    /// </summary>
    private bool TryJump(int delta)
    {
        var target = _index + delta;
        if (_path is null || target < 0 || target >= _path.Steps.Count)
        {
            Fault($"Step {_index}: jump of {delta:+#;-#;0} lands outside the path (step {target}).");
            return true;
        }

        _index = target;
        _stepBegun = false;
        return true;
    }

    /// <summary>
    /// Moves to the next step, finishing the run if that was the last one. Finishing here rather
    /// than on the following tick matters: a caller that stops ticking the moment a route reports
    /// done would otherwise never see it report done.
    /// </summary>
    private void Advance()
    {
        _index++;
        _stepBegun = false;

        if (_path is not null && _index >= _path.Steps.Count)
        {
            ReleaseHolds();
            Status = ExecutorStatus.Finished;
        }
    }

    /// <summary>
    /// Whether a step should walk to its position before running its verb.
    ///
    /// <para>
    /// Nearly everything should. The exclusions are the verbs where a position means something
    /// other than "stand here": <see cref="StepVerb.Boss"/> and <see cref="StepVerb.KillInRange"/>
    /// approach on their own terms and must not be dragged back to a marker while the fight moves
    /// them around, <see cref="StepVerb.JumpTo"/> names somewhere you cannot walk to,
    /// <see cref="StepVerb.CameraFacing"/> names something to look at, and the flow-control verbs
    /// are decisions rather than places.
    /// </para>
    /// </summary>
    private static bool RequiresApproach(StepVerb verb) => verb switch
    {
        StepVerb.Boss or StepVerb.KillInRange or StepVerb.JumpTo or StepVerb.CameraFacing
            or StepVerb.ModifyIndex or StepVerb.ConditionAction
            or StepVerb.Comment or StepVerb.Unknown or StepVerb.DutySpecificCode => false,
        _ => true,
    };

    /// <summary>
    /// Whether a step belongs to this character at all.
    ///
    /// <para>
    /// The <c>W2W</c> tag is a role filter, and it is how a single route serves both roles. Those
    /// steps are the chain-pull machinery — combat stops off, walk onto the pack, wait for the AoE
    /// to land, combat stops back on — which only works with someone holding aggro. Skipping them
    /// leaves the plain waypoints, so the character kills each pack where it stands. Mistwake tags
    /// eighteen of its thirty steps this way, and that one filter is the whole difference between
    /// how a tank and a damage dealer run it.
    /// </para>
    /// </summary>
    private bool ShouldSkip(ThreadStep step)
    {
        if (step.Tag.HasFlag(StepTag.W2W) && !_wallToWall())
            return true;

        // The Treasure tag covers the whole excursion — the walk over, whatever guards the chest,
        // the dialog — so skipping it removes the detour rather than leaving the character standing
        // at an unopened coffer. Every coffer step in the library carries it.
        //
        // Skipping never renumbers anything, so relative jumps elsewhere still land where they
        // meant to; the few flow-control steps that are themselves tagged belong to the chest loop
        // being skipped.
        return step.Tag.HasFlag(StepTag.Treasure) && !_lootRouteChests();
    }

    /// <summary>Whether this step still has to travel to reach its waypoint.</summary>
    private bool HasGroundToCover(ThreadStep step)
        => !_approachSatisfied
           && step.Position.IsSet
           && RequiresApproach(step.Verb)
           && Vector3.Distance(_world.PlayerPosition, step.Position.ToVector3()) > _tuning().ArrivalTolerance;

    private static TimeSpan TimeoutFor(StepVerb verb)
        => verb == StepVerb.Boss ? BossStepTimeout : DefaultStepTimeout;

    /// <summary>
    /// Switches BossMod's AI on, once per run, and never switches it off.
    ///
    /// <para>
    /// The fleet runs the AI on permanently, and it costs nothing to leave on: BossMod suppresses
    /// its own movement for as long as a navmesh path is running, so it cannot fight Theseus for
    /// control of the character. Toggling it per boss was strictly worse — the character spent
    /// every stretch of trash and every approach with mechanics handling switched off, and
    /// stopping the plugin left the user's AI disabled behind it.
    /// </para>
    ///
    /// <para>
    /// This is also why <c>BossMod off</c> steps in imported routes are ignored rather than obeyed.
    /// </para>
    /// </summary>
    private void EnsureBossModAi()
    {
        if (_bossAiRequested)
            return;

        _bossAiRequested = true;
        _world.SetBossModAi(true);
    }

    private void StopForwardMovement()
    {
        if (!_forwardMoving)
            return;

        _forwardMoving = false;
        _world.SetForwardMovement(false);
    }

    /// <summary>
    /// Drops everything the run was actively doing — movement, and nothing else.
    ///
    /// <para>
    /// BossMod's AI is deliberately not released. It is the fleet's normal operating state rather
    /// than something Theseus borrows, so switching it off on stop would disable mechanics handling
    /// for a user who has just taken manual control and most needs it.
    /// </para>
    /// </summary>
    private void ReleaseHolds()
    {
        _world.StopMoving();
        StopForwardMovement();
    }

    private void Fault(string reason)
    {
        FaultReason = reason;
        ReleaseHolds();
        Status = ExecutorStatus.Faulted;
        _world.Log(reason);
    }

    private static string First(ThreadStep step) => step.Arguments.Length > 0 ? step.Arguments[0] : string.Empty;
}
