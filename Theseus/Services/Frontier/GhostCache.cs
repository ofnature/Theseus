using System;
using System.Collections.Generic;
using System.Numerics;

namespace Theseus.Services.Frontier;

/// <summary>
/// Remembers what has already been dealt with, so the run moves on instead of circling.
///
/// <para>
/// Without this the navigator has no notion of progress at all: a corpse can linger in the object
/// table, a lever stays where it was after being pulled, and "nearest thing to do" keeps pointing
/// at the same spot forever. A ghost is that memory — the object is still there, but it is done.
/// </para>
///
/// <para>
/// Keyed by instance id <i>and</i> position together, because neither alone survives the field.
/// Instance ids are recycled within a zone, so an id alone eventually ghosts something that was
/// never touched; position alone would ghost a respawn standing where its predecessor died. The
/// pair is stable for as long as it needs to be, and the whole cache is dropped when the ground
/// changes underneath it — a new duty, a new territory, a new map within a territory.
/// </para>
/// </summary>
public sealed class GhostCache
{
    /// <summary>
    /// How far an object may be from the remembered position and still count as the same one.
    /// Loose enough for a body that slid on death, tight enough not to swallow its neighbour.
    /// </summary>
    private const float SamePlace = 5f;

    private readonly record struct Ghost(ulong Id, Vector3 Position);

    private readonly List<Ghost> _ghosts = [];

    /// <summary>The scope the cache belongs to: forgetting is keyed on this changing.</summary>
    public (uint Territory, uint Map, uint ContentId) Scope { get; private set; }

    public int Count => _ghosts.Count;

    /// <summary>
    /// Drops everything when the scope changes. Cheap to call every tick — it only acts on a real
    /// change, and a stale cache across a zone boundary is worse than none: it would ghost objects
    /// in the new area purely because an id was reused.
    /// </summary>
    public void SyncScope(uint territory, uint map, uint contentId)
    {
        var scope = (territory, map, contentId);
        if (Scope == scope)
            return;

        Scope = scope;
        _ghosts.Clear();
    }

    public void Forget() => _ghosts.Clear();

    public void Remember(WorldObject target)
        => _ghosts.Add(new Ghost(target.Id, target.Position));

    public bool IsGhost(WorldObject candidate)
    {
        foreach (var ghost in _ghosts)
        {
            if (ghost.Id == candidate.Id && Vector3.Distance(ghost.Position, candidate.Position) <= SamePlace)
                return true;
        }

        return false;
    }
}
