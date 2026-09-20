using System;
using System.Linq;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>
/// Fights the pack that is in the way, without fighting it.
///
/// <para>
/// The loop's whole contribution is target selection and feet: walk into engage range, target the
/// nearest hostile, stand still. The damage comes from the rotation, which keeps running because
/// Theseus reports itself busy for as long as the loop keeps bidding — so the loop is also what
/// tells the rotation "this is ours". It re-bids while anything is alive inside the aggro radius,
/// which is what makes a pack resolve rather than a single mob.
/// </para>
///
/// <para>
/// It deliberately does not chase. A hostile that walks away is the rotation's problem; a loop that
/// chases is a loop that walks the character through a boss's cleave.
/// </para>
/// </summary>
public sealed class CombatLoop
{
    /// <summary>
    /// How far away a hostile is still our fight. A room, not a zone: the difference between this
    /// and the aggro radius of the mobs themselves is what keeps a run from pulling a wing it was
    /// meant to walk past.
    /// </summary>
    public const float AggroRange = 25f;

    /// <summary>How close the character stands to fight. Inside this, the rotation has its range.</summary>
    public const float EngageRange = 3f;

    /// <summary>Bids while something is alive and close enough to be this run's fight.</summary>
    public LoopBid? Bid(WorldModel.Snapshot world)
    {
        if (world.InCombat)
            return new LoopBid("in combat");

        var hostile = world.Nearest(o => o.Object.Kind == Services.Frontier.WorldObjectKind.Hostile);

        if (hostile is not { } target || target.Distance > AggroRange)
            return null;

        return target.Object.IsTargetable
            ? new LoopBid($"a hostile {target.Distance:0}y away")
            : new LoopBid($"waiting on a hostile {target.Distance:0}y away");
    }

    /// <summary>
    /// Closes to engage range and targets, or holds. Runs every tick it is granted: the loop has no
    /// memory, because "what is alive" is a fact about the world and not about the loop.
    /// </summary>
    public void Run(WorldModel.Snapshot world, IStepWorld game)
    {
        var hostile = world.Nearest(o => o.Object.Kind == Services.Frontier.WorldObjectKind.Hostile);

        if (hostile is not { } target)
        {
            game.StopMoving();
            return;
        }

        if (target.Distance > EngageRange)
        {
            game.MoveCloseTo(target.Object.Position, EngageRange);
            return;
        }

        // In range: stop, then target. Targeting goes through Theseus' target service, which records
        // the external write with Daedalus first — without that, a movement hold silently eats it.
        game.StopMoving();
        game.AttackObject(target.Object.Id);
    }
}
