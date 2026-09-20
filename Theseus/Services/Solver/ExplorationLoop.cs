using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>
/// Walks the run into the parts of the map nobody has looked at yet, in the order §2.4 gives.
///
/// <para>
/// Two of its six steps are its own: an open gate is the shortest path into the unexplored half of
/// the dungeon and outranks any amount of walking, and the nearest unexplored reachable ground is
/// the next best thing. The rest — a zone override's authored waypoint, the nearest unvisited map
/// landmark, and the fallback ladder below that — belong to the caller, which already had them
/// before the solver existed. When the grid cannot be had at all (an older Ariadne, or vnavmesh
/// alone) this loop bids nothing, exploration degrades to those two, and the run window says so.
/// </para>
///
/// <para>
/// The destination survives an interrupt. Combat takes the grant away and gives it back, and a loop
/// that re-picks on every grant walks a zigzag across the room: the pick only changes when the
/// thing it was picked for changes — the gate opened or was passed, the ground was visited, or the
/// world grew a better answer.
/// </para>
/// </summary>
public sealed class ExplorationLoop
{
    /// <summary>Close enough to call a destination reached. The grid's own cells are 2 y.</summary>
    public const float ArrivalRadius = 2f;

    /// <summary>Two picks this close together are the same place, and the old destination stands.</summary>
    private const float SamePlace = 3f;

    private readonly Func<IReadOnlyList<Gate>> _gates;

    private Vector3? _destination;
    private string _key = string.Empty;
    private string _reason = string.Empty;

    public ExplorationLoop(Func<IReadOnlyList<Gate>> gates) => _gates = gates;

    /// <summary>Where it is walking to and why, for the debug window.</summary>
    public string Describe => _destination is { } destination
        ? $"→ ({destination.X:0.#}, {destination.Y:0.#}, {destination.Z:0.#}) {_reason}"
        : "nowhere picked";

    /// <summary>The exploration loop's own pick, for the driver: overrides and landmarks are the caller's.</summary>
    public Vector3? Destination => _destination;

    public LoopBid? Bid(WorldModel.Snapshot world)
    {
        var pick = Pick(world);

        if (pick is not { } candidate)
        {
            _destination = null;
            _key = string.Empty;
            _reason = string.Empty;
            return null;
        }

        // Same place, same reason: keep the destination that was already being walked. An interrupt
        // is not a reason to re-decide.
        if (Keep(candidate))
            return new LoopBid(_reason);

        _destination = candidate.Position;
        _key = candidate.Key;
        _reason = candidate.Reason;
        return new LoopBid(candidate.Reason);
    }

    public void Run(WorldModel.Snapshot world, IStepWorld game)
    {
        if (_destination is not { } destination)
        {
            game.StopMoving();
            return;
        }

        if (Vector3.Distance(world.Position, destination) <= ArrivalRadius)
        {
            // Arrived. Whether the ground counts as explored is the grid's answer, not the loop's —
            // a destination reached is a pick that is finished either way.
            _destination = null;
            _key = string.Empty;
            _reason = string.Empty;
            game.StopMoving();
            return;
        }

        // Movement goes through the seam like every other move, so whichever source is answering
        // does the routing — including off the edge, if that is what the ground does.
        game.MoveTo(destination);
    }

    /// <summary>
    /// §2.4's own two steps. A pick carries a key naming what it was picked for, so "the same
    /// place" is a comparison of keys rather than of floating-point coordinates.
    /// </summary>
    private (Vector3 Position, string Reason, string Key)? Pick(WorldModel.Snapshot world)
    {
        var open = _gates().FirstOrDefault(g => g.State == GateState.Open);

        if (open is not null)
        {
            var beyond = open.Beyond;
            return (beyond,
                $"the way through at ({open.Frontier.X:0.#}, {open.Frontier.Z:0.#}) is open",
                $"gate:{open.Id}");
        }

        if (world.Unexplored is not { } unexplored)
            return null;

        var distance = Vector3.Distance(world.Position, unexplored);
        return (unexplored, $"unexplored ground {distance:0}y away", $"region:{unexplored.X:0.#},{unexplored.Z:0.#}");
    }

    private bool Keep((Vector3 Position, string Reason, string Key) candidate)
    {
        if (_destination is not { } held)
            return false;

        if (candidate.Key == _key)
            return true;

        // A different thing, but the same place: the ground we are walking to is still the ground.
        return candidate.Key.StartsWith("region:", StringComparison.Ordinal)
            && _key.StartsWith("region:", StringComparison.Ordinal)
            && Vector3.Distance(held, candidate.Position) <= SamePlace;
    }
}
