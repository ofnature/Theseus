namespace Theseus.Services.Solver;

/// <summary>
/// What an interactable is, in the five shapes that change what a solver does with it.
///
/// <para>
/// <see cref="Unknown"/> is a real class rather than a failure, and <see cref="Inert"/> is learned:
/// the two bookkeeping values every classifier needs. A hostile is never one of these — it belongs
/// to the combat loop — and a coffer is not either, because loot is not progression.
/// </para>
///
/// <para>
/// <c>CombatGate</c> from the architecture's table is absent on purpose: it is not an object, it is
/// a region whose hostiles must die, and the gate ledger's conditions are where that lives.
/// </para>
/// </summary>
public enum BehaviourClass
{
    /// <summary>Take it: the object leaves the scan and something is held.</summary>
    PickupHold,

    /// <summary>Hand in a held item: the item is consumed and a gate opens or an objective advances.</summary>
    TurnIn,

    /// <summary>Pull it: a gate opens or an objective advances.</summary>
    DirectTrigger,

    /// <summary>A lift, a rail, a teleporter: everyone boards, and the character's position moves.</summary>
    Discontinuity,

    /// <summary>Nothing is known about it yet, so it is probed.</summary>
    Unknown,

    /// <summary>Learned: interacting did nothing, twice. Never probed again.</summary>
    Inert,
}
