using System.Numerics;

namespace Theseus.Services.Frontier;

/// <summary>What the frontier navigator can be asked to do with a nearby object.</summary>
public enum WorldObjectKind
{
    /// <summary>Something to kill.</summary>
    Hostile,

    /// <summary>Something to interact with — a lever, a door mechanism, a quest object.</summary>
    Interactable,

    /// <summary>A treasure coffer.</summary>
    Treasure,
}

/// <summary>
/// One nearby object, as the frontier navigator sees it.
///
/// <para>
/// Identity is the instance id rather than the data id: the data id names a <i>kind</i> of thing,
/// and a room with four of the same mob would collapse into one entry, so a run that killed one
/// would believe it had killed them all.
/// </para>
/// </summary>
/// <param name="InCombat">
/// A hostile that is already fighting. What separates a pack that has been pulled — and must be
/// closed on — from one that is standing around and must not be.
/// </param>
public readonly record struct WorldObject(
    ulong Id,
    uint DataId,
    string Name,
    Vector3 Position,
    WorldObjectKind Kind,
    bool IsTargetable,
    bool InCombat = false);
