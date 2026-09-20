using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Frontier;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>
/// One tick's answer to "what is around me, and what is left to do" — the snapshot every loop
/// reads instead of touching the game.
///
/// <para>
/// There is no cache here and no history beyond what the snapshot carries: a loop that is not
/// granted does nothing and recomputes when it is next granted, which is what keeps a fight or an
/// interaction from leaving half-finished state behind. The memory that does exist — what is done,
/// what is known, what has been seen — lives in the ghost cache, the taxonomy and the grid.
/// </para>
/// </summary>
public static class WorldModel
{
    /// <summary>
    /// How far the character is taken to have seen. A room, not a wing: the point of the visited
    /// set is to stop the exploration loop walking back through ground it has already crossed.
    /// </summary>
    public const float SightRadius = 30f;

    /// <summary>
    /// One nearby object with everything the loops need to bid on it: what kind of thing it is,
    /// what it has been learned to be, and whether the run is already done with it.
    /// </summary>
    /// <param name="Class">
    /// The taxonomy's answer for an interactable, or null for a hostile or a coffer — those are
    /// kinds of <see cref="WorldObject"/>, not behaviour classes, and nothing probes them.
    /// </param>
    public readonly record struct Recognised(
        WorldObject Object,
        BehaviourClass? Class,
        bool Done,
        float Distance);

    public sealed record Snapshot(
        DateTime UtcNow,
        Vector3 Position,
        (uint Territory, uint Map, uint ContentId) Scope,
        int Stage,
        int ObjectivesDone,
        int ObjectivesTotal,
        bool ObjectivesReadable,
        bool InCombat,
        IReadOnlyList<Recognised> Objects,
        IReadOnlyList<GateCandidate> Gates,
        Vector3? Unexplored,
        bool Exhausted)
    {
        /// <summary>Interactables the run has not finished with, nearest first.</summary>
        public IEnumerable<Recognised> Interactables => InReach(WorldObjectKind.Interactable);

        /// <summary>Hostiles worth engaging, nearest first.</summary>
        public IEnumerable<Recognised> Hostiles => InReach(WorldObjectKind.Hostile);

        /// <summary>Coffers, nearest first. Loot is not progression, and never bids on its own.</summary>
        public IEnumerable<Recognised> Loot => InReach(WorldObjectKind.Treasure);

        /// <summary>Nothing reachable is left unseen and nothing reachable lies beyond the window.</summary>
        public bool Explored => Unexplored is null && Exhausted;

        private IEnumerable<Recognised> InReach(WorldObjectKind kind)
            => Objects.Where(o => o.Object.Kind == kind && !o.Done).OrderBy(o => o.Distance);
    }

    /// <summary>
    /// Takes the snapshot.
    ///
    /// <para>
    /// The grid is passed in rather than fetched: it is the one input that costs a pipe round trip,
    /// re-asked only when a gate changes, a transit lands, or the character has moved half its
    /// radius. Everything else is local, and this stays a pure function of its arguments so the
    /// loops can be tested against a written-down world.
    /// </para>
    /// </summary>
    /// <param name="entrance">
    /// Where the duty started. The exploration tiebreaker pulls toward ground farther from it, so
    /// two equally close regions resolve forward rather than back toward the door.
    /// </param>
    public static Snapshot Observe(
        IStepWorld world,
        DutyObjectiveSnapshot objectives,
        Taxonomy taxonomy,
        GhostCache done,
        ReachableGrid? grid = null,
        Vector3? entrance = null,
        float scanRadius = 40f,
        float sightRadius = SightRadius)
    {
        var position = world.PlayerPosition;
        var scope = world.Scope;

        // A new duty, a new territory or a new map within one: nothing remembered about the old
        // ground means anything on this one.
        done.SyncScope(scope.Territory, scope.Map, scope.ContentId);

        // Seeing a place is standing near it, which is why this happens here and not in the loop:
        // every tick's position is a place the character has been.
        grid?.MarkExplored(position, sightRadius);

        var objects = new List<Recognised>();
        foreach (var candidate in world.ScanNearby(scanRadius))
        {
            BehaviourClass? cls = candidate.Kind == WorldObjectKind.Interactable
                ? taxonomy.Classify(candidate.DataId)
                : null;
            objects.Add(new Recognised(candidate, cls, done.IsGhost(candidate), Vector3.Distance(position, candidate.Position)));
        }

        return new Snapshot(
            world.UtcNow,
            position,
            scope,
            objectives.Stage,
            objectives.CompletedCount,
            objectives.TotalCount,
            objectives.Available,
            world.InCombat,
            objects,
            grid?.GateCandidates(position) ?? [],
            grid?.NearestUnexplored(position, entrance),
            grid?.Exhausted ?? false);
    }
}
