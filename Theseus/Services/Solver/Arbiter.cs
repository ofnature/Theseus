using System;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>Which loop is running. The order here is the arbitration order.</summary>
public enum LoopKind
{
    /// <summary>A discontinuity is in flight: nobody moves, the mover owns the character.</summary>
    Transit,

    /// <summary>The boss handler has the fight, or should.</summary>
    BossHandoff,

    /// <summary>Something is alive and worth killing.</summary>
    Combat,

    /// <summary>An object the run can act on is within reach.</summary>
    Interactable,

    /// <summary>Ground nobody has looked at, or a gate that has opened.</summary>
    Exploration,

    /// <summary>Nothing bids. The ladder, or the person.</summary>
    Idle,
}

/// <summary>A loop saying it wants to run, and why — the reason is what the debug window prints.</summary>
public sealed record LoopBid(string Reason);

/// <summary>What the arbiter granted this tick, and the sentence explaining it.</summary>
public sealed record LoopDecision(LoopKind Kind, string Reason)
{
    public static LoopDecision Nothing { get; } = new(LoopKind.Idle, "nothing to do");
}

/// <summary>
/// The arbiter: one loop per tick, granted to the highest bidder, with the preemption rules that
/// exist because each of them has been a failure at least once.
///
/// <para>
/// The rules it enforces, in the order they matter:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>A transit freezes everything.</b> The mover is mid-ride or mid-fall, and the one thing that
/// must not happen is a loop deciding the character is stuck and driving it somewhere.
/// </item>
/// <item>
/// <b>Combat beats everything else, and exploration never resumes mid-fight.</b> Walking away from
/// a pack the rotation is fighting is how a run pulls the whole wing; wall-to-wall for a tank is a
/// later, explicit mode that raises a pull budget rather than removing this rule.
/// </item>
/// <item>
/// <b>Interactables never preempt combat.</b> A claim can be granted during a fight so a damage
/// dealer knows it is theirs, but the walk over waits — positioning belongs to the rotation, and
/// walking to a lever through a fight is how a healer dies.
/// </item>
/// <item>
/// <b>A fight that just ended holds the grant for a beat.</b> A straggler shows up inside that
/// second, and it is the difference between a clean pull and a run that walks off mid-pack.
/// </item>
/// </list>
/// </summary>
public sealed class Arbiter
{
    /// <summary>How long combat holds the grant after the last hostile is gone or dead.</summary>
    private static readonly TimeSpan CombatSettle = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a hostile has to stand off, with nowhere left to walk, before the fight stops being
    /// the solver's. The architecture inherits the step executor's engage timeout here: ninety
    /// seconds is long enough for a pack to come to you and for a walk to be attempted, and short
    /// enough that a run does not spend a farm's worth of time in an arena's corner.
    /// </summary>
    public static readonly TimeSpan StandoffBefore = TimeSpan.FromSeconds(90);

    private readonly CombatLoop _combat;
    private readonly InteractableLoop _interactables;
    private readonly ExplorationLoop _exploration;
    private readonly Func<TransitPhase> _transit;
    private readonly Func<bool> _openGateWaiting;

    private DateTime _combatEndedUtc = DateTime.MinValue;
    private DateTime? _standoffSinceUtc;

    public Arbiter(
        CombatLoop combat,
        InteractableLoop interactables,
        ExplorationLoop exploration,
        Func<TransitPhase> transit,
        Func<bool>? openGateWaiting = null)
    {
        _combat = combat;
        _interactables = interactables;
        _exploration = exploration;
        _transit = transit;
        _openGateWaiting = openGateWaiting ?? (() => false);
    }

    /// <summary>The loops, for whoever needs to report or drive them.</summary>
    public CombatLoop Combat => _combat;

    public InteractableLoop Interactables => _interactables;

    public ExplorationLoop Exploration => _exploration;

    /// <summary>
    /// Decides who runs this tick. The clock is the snapshot's own: every settle time is measured
    /// against the world the caller just read, so the tests can advance a fake clock and see the
    /// settle expire.
    /// </summary>
    public LoopDecision Decide(WorldModel.Snapshot world)
    {
        if (_transit() != TransitPhase.None)
            return new LoopDecision(LoopKind.Transit, "a transit is in flight — the mover has the character");

        if (world.BossModuleActive)
        {
            _combatEndedUtc = world.UtcNow;
            return new LoopDecision(LoopKind.BossHandoff, "a boss module is up");
        }

        // §2.5's other trigger: a hostile that will not come to us, with nothing left to explore and
        // nothing waiting on a lever. Walking to an arena boss is the combat loop's job and it does
        // it while the hostile is inside aggro range; this is the case where it cannot.
        if (BossStandoff(world))
        {
            _combatEndedUtc = world.UtcNow;
            return new LoopDecision(LoopKind.BossHandoff,
                "a hostile is standing off with nowhere left to walk");
        }

        if (_combat.Bid(world) is { } combat)
        {
            _combatEndedUtc = world.UtcNow;
            return new LoopDecision(LoopKind.Combat, combat.Reason);
        }

        // Combat has stopped bidding. Hold the grant for a beat in case a straggler is still coming.
        if (world.UtcNow - _combatEndedUtc < CombatSettle)
            return new LoopDecision(LoopKind.Combat, "holding a beat after combat");

        if (_interactables.Bid(world) is { } interactable)
            return new LoopDecision(LoopKind.Interactable, interactable.Reason);

        if (_exploration.Bid(world) is { } exploration)
            return new LoopDecision(LoopKind.Exploration, exploration.Reason);

        // Nothing to fight, nothing to touch, nowhere left to walk. The ladder's first rung and the
        // boss trigger both start here.
        return new LoopDecision(LoopKind.Idle, "nothing bids");
    }

    /// <summary>Wakes the settle timer — called when the boss module hands back to the solver.</summary>
    public void NoteCombatOver(DateTime utcNow) => _combatEndedUtc = utcNow;

    /// <summary>
    /// Whether the fight is standing off: exploration has nothing left, a hostile is there, no edge
    /// is waiting to be taken, and none of that has changed for the engage timeout.
    ///
    /// <para>
    /// Every clause is load-bearing. A dungeon with somewhere left to walk is not a standoff, it is a
    /// walk — and an open unpassed gate is the one thing that outranks a boss, because the way on may
    /// be through it rather than past the thing in the way. Combat resets the clock: a pack that
    /// engages is a pack the combat loop can fight.
    /// </para>
    /// </summary>
    private bool BossStandoff(WorldModel.Snapshot world)
    {
        if (!world.Explored || world.InCombat || _openGateWaiting())
        {
            _standoffSinceUtc = null;
            return false;
        }

        if (world.Nearest(o => o.Object.Kind == Services.Frontier.WorldObjectKind.Hostile) is null)
        {
            _standoffSinceUtc = null;
            return false;
        }

        _standoffSinceUtc ??= world.UtcNow;
        return world.UtcNow - _standoffSinceUtc.Value >= StandoffBefore;
    }
}
