using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Theseus.Services.Frontier;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>Where the loop is with its one object.</summary>
public enum InteractPhase
{
    /// <summary>Nothing in hand.</summary>
    None,

    /// <summary>Walking over. The claim is held; the walk is not combat.</summary>
    Approaching,

    /// <summary>It has been touched. Watching for an outcome inside the window.</summary>
    Interacting,
}

/// <summary>
/// What the loop needs from everything around it. Grouped so the loop's own signature stays about
/// its job rather than about the plugin's object graph.
/// </summary>
public sealed record InteractableContext(
    Taxonomy Taxonomy,
    GhostCache Ghosts,
    GapLog Gaps,
    Func<string> RunId,
    Func<string> CacheKey,
    Func<uint, bool>? WantedByAGate = null,
    Action<uint>? Resolved = null,
    Action<string>? Log = null);

/// <summary>
/// One object at a time, in the four steps §1.5 gives it: choose, claim, touch, watch.
///
/// <para>
/// Its only intelligence is the claim check before walking over and the outcome observation after
/// touching — no object is ever touched twice on faith, and nothing is ever marked done without an
/// outcome. "Interacted" is not an outcome: the interaction succeeded if the world changed, and
/// what changed is what teaches the taxonomy. Get that loop wrong and it either retries a switch
/// forever or marks the run complete with the dungeon still ahead.
/// </para>
///
/// <para>
/// Objects inside the opportunistic radius are picked up as the character passes; an object named
/// by a locked gate is wanted regardless of distance, because a lever behind a pack is the frontier
/// rather than an errand (§2.4). Anything that does nothing twice is inert: ghosted, learned, and
/// written to the gap log, so the next run does not spend a fourth interaction on it.
/// </para>
/// </summary>
public sealed class InteractableLoop
{
    /// <summary>How close an object has to be to be worth a detour on the way somewhere else.</summary>
    public const float OpportunisticRadius = 25f;

    /// <summary>How close is close enough to touch.</summary>
    public const float InteractRange = 2.5f;

    /// <summary>How many interactions may do nothing before the object is called inert.</summary>
    public const int FailuresBeforeInert = 2;

    /// <summary>How far the character must move for a transit to have taken over the interaction.</summary>
    private const float TransitJump = 15f;

    /// <summary>How far the character may wander and still count as standing where it interacted.</summary>
    private const float StillThere = 6f;

    private static readonly TimeSpan OutcomeWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransitWindow = TimeSpan.FromSeconds(30);

    private readonly InteractableContext _context;
    private readonly Dictionary<uint, int> _failures = [];
    private readonly HashSet<uint> _inert = [];

    private WorldModel.Recognised _target;
    private InteractPhase _phase = InteractPhase.None;
    private DateTime _deadlineUtc;
    private int _stageAtTouch;
    private Vector3 _positionAtTouch;

    public InteractableLoop(InteractableContext context) => _context = context;

    /// <summary>What the loop is on, for the debug window.</summary>
    public string Describe => _phase switch
    {
        InteractPhase.Approaching => $"approaching {_target.Object.Name}",
        InteractPhase.Interacting => $"watching {_target.Object.Name}",
        _ => "idle",
    };

    public LoopBid? Bid(WorldModel.Snapshot world)
    {
        // A watched interaction has the grant until it resolves: the world is mid-change and the
        // outcome is the only thing that matters.
        if (_phase == InteractPhase.Interacting)
            return new LoopBid($"watching {_target.Object.Name}");

        // Interactables never preempt combat. A claim can be granted during a fight so a damage
        // dealer knows it is theirs, but the walk over waits.
        if (world.InCombat)
            return null;

        var candidate = Choose(world);
        return candidate is { } pick
            ? new LoopBid($"{pick.Object.Name} is {pick.Distance:0}y away")
            : null;
    }

    public void Run(WorldModel.Snapshot world, IStepWorld game)
    {
        if (_phase == InteractPhase.Interacting)
        {
            Watch(world, game);
            return;
        }

        if (Choose(world) is not { } target)
        {
            _phase = InteractPhase.None;
            return;
        }

        _target = target;

        if (target.Distance > InteractRange)
        {
            _phase = InteractPhase.Approaching;
            game.MoveCloseTo(target.Object.Position, InteractRange);
            return;
        }

        // In range, stopped, touching. The claim is the loop's own state, so the same object is not
        // chosen twice by a second bidder in the same tick.
        game.StopMoving();
        _phase = InteractPhase.Interacting;
        _stageAtTouch = world.Stage;
        _positionAtTouch = world.Position;
        _deadlineUtc = world.UtcNow + WindowFor(target.Class);

        game.InteractWithObject(target.Object.Id);
        _context.Log?.Invoke($"Touching {target.Object.Name} ({(target.Class?.ToString() ?? "unknown")}) …");
    }

    /// <summary>Stops whatever is in flight — a transit, a boss handoff, a person taking over.</summary>
    public void Release(string why)
    {
        if (_phase == InteractPhase.None)
            return;

        _phase = InteractPhase.None;
        _context.Log?.Invoke($"Dropping {_target.Object.Name}: {why}.");
    }

    /// <summary>An object resolved outside the loop — a fleet peer took it, or a route did.</summary>
    public void NoteResolved(uint dataId)
    {
        _inert.Remove(dataId);
        if (_phase != InteractPhase.None && _target.Object.DataId == dataId)
            _phase = InteractPhase.None;
    }

    private WorldModel.Recognised? Choose(WorldModel.Snapshot world)
        => world.Nearest(o =>
            o.Object.Kind == WorldObjectKind.Interactable
            && !_inert.Contains(o.Object.DataId)
            && _context.Taxonomy.Classify(o.Object.DataId) != BehaviourClass.Inert
            && !_context.Ghosts.IsGhost(o.Object)
            && (o.Distance <= OpportunisticRadius || (_context.WantedByAGate?.Invoke(o.Object.DataId) ?? false)));

    /// <summary>
    /// Watches for the outcome of a touch. Nothing is marked done here without one: a despawn, an
    /// advanced objective, or nothing at all — and nothing at all is the one that turns into an
    /// inert object rather than into a retry forever.
    /// </summary>
    private void Watch(WorldModel.Snapshot world, IStepWorld game)
    {
        var moved = Vector3.Distance(world.Position, _positionAtTouch);

        // A discontinuity does not answer in an outcome window: it hands the character to the
        // mover, which is a different state machine with its own accounting (§1.5).
        if (_target.Class == BehaviourClass.Discontinuity && moved > TransitJump)
        {
            _context.Log?.Invoke($"Dropping {_target.Object.Name}: a transit took over.");
            _phase = InteractPhase.None;
            return;
        }

        var stillThere = world.Objects.Any(o => o.Object.Id == _target.Object.Id);

        if (!stillThere && moved <= StillThere)
        {
            Resolve(BehaviourClass.PickupHold, "it is gone and the character never left");
            return;
        }

        if (world.Stage > _stageAtTouch)
        {
            Resolve(_target.Class ?? BehaviourClass.DirectTrigger, "the objective moved");
            return;
        }

        if (world.UtcNow >= _deadlineUtc)
            Fail(world, "nothing changed inside the outcome window");
    }

    private void Resolve(BehaviourClass observed, string why)
    {
        _context.Ghosts.Remember(_target.Object);
        _context.Taxonomy.Observe(_target.Object.DataId, observed);
        _context.Resolved?.Invoke(_target.Object.DataId);
        _context.Log?.Invoke($"{_target.Object.Name}: {why} — learned as {observed}.");
        _phase = InteractPhase.None;
    }

    private void Fail(WorldModel.Snapshot world, string why)
    {
        var dataId = _target.Object.DataId;
        var failures = _failures.GetValueOrDefault(dataId) + 1;
        _failures[dataId] = failures;

        if (failures < FailuresBeforeInert)
        {
            _context.Log?.Invoke($"{_target.Object.Name}: {why} (attempt {failures}/{FailuresBeforeInert}).");
            _phase = InteractPhase.None;
            return;
        }

        // Twice with nothing to show for it: this object is scenery, and the run should stop
        // spending interactions on it.
        _inert.Add(dataId);
        _context.Taxonomy.Observe(dataId, BehaviourClass.Inert);
        _context.Ghosts.Remember(_target.Object);
        _context.Gaps.Append(
            GapKind.Inert,
            _context.RunId(),
            world.Scope.Territory,
            _context.CacheKey(),
            world.Stage,
            _target.Object.Position,
            dataId,
            _target.Object.Name,
            detail: why);

        _context.Log?.Invoke($"{_target.Object.Name}: inert after {failures} attempts — {why}.");
        _phase = InteractPhase.None;
    }

    private static TimeSpan WindowFor(BehaviourClass? cls)
        => cls == BehaviourClass.Discontinuity ? TransitWindow : OutcomeWindow;
}
