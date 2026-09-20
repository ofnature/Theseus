using System.Numerics;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The grid is the only piece of the solver that reads a shape from another process, so most of
/// these are about the shapes that must be refused: an answer that cannot be indexed safely must
/// come back as nothing rather than as a smaller world than the one that exists.
/// </summary>
public class ReachableGridTests
{
    /// <summary>
    /// A 4×4 grid of 2 y cells whose origin is the world origin. Column 5 is (xi 1, zi 1), so its
    /// centre is (3, y, 3) — cell centres, not corners, which is the convention the protocol fixes.
    /// </summary>
    private static ReachableGrid Grid(
        int[] columns, float[] heights, byte[] states, bool reachableOutside = false)
        => ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4, columns, heights, states,
               reachableOutside)
           ?? throw new InvalidOperationException("the fixture grid should index");

    [Fact]
    public void A_cell_centre_is_the_corner_plus_half_a_cell()
    {
        var grid = Grid([5], [7f], [1]);

        grid.MarkExplored(new Vector3(3f, 7f, 3f), sightRadius: 1f);

        Assert.Equal(0, grid.UnexploredCount); // the one surface was at (3, 7, 3) and was seen
    }

    [Fact]
    public void Anything_that_is_not_ok_becomes_no_grid_at_all()
    {
        // "Could not answer" and "no walkable ground here" are different claims, and only the second
        // one is safe to act on. Every degraded result keeps them apart.
        foreach (var result in new[] { "meshNotReady", "serviceUnavailable", "failed", "startOffMesh", "" })
        {
            Assert.Null(ReachableGrid.TryFrom(result, Vector3.Zero, Vector2.Zero, 2f, 4, 4,
                [0], [0f], [1], false));
        }
    }

    [Fact]
    public void A_grid_that_cannot_be_indexed_is_refused_rather_than_trimmed()
    {
        // Mismatched arrays, a column outside the declared grid, an impossible cell size and an
        // absurd extent: each is a shape that would silently understate the world.
        Assert.Null(ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4, [0, 1], [0f], [1], false));
        Assert.Null(ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4, [99], [0f], [1], false));
        Assert.Null(ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 0f, 4, 4, [0], [0f], [1], false));
        Assert.Null(ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 400, 400, [0], [0f], [1], false));
    }

    [Fact]
    public void The_nearest_unexplored_pick_pulls_forward_not_back()
    {
        // Two surfaces the same distance from the character — (1,0,7) and (7,0,1) — with the
        // entrance over by the second one. The progression term is what makes the run prefer the
        // ground further from the door when everything else is equal.
        var grid = Grid([12, 3], [0f, 0f], [1, 1]);

        var pick = grid.NearestUnexplored(new Vector3(1f, 0f, 1f), entrance: new Vector3(7f, 0f, 1f));

        Assert.Equal(new Vector3(1f, 0f, 7f), pick);
    }

    [Fact]
    public void Explored_ground_is_not_a_destination()
    {
        var grid = Grid([5, 6], [0f, 0f], [1, 1]);

        grid.MarkExplored(new Vector3(3f, 0f, 3f), sightRadius: 1f); // only the first

        var pick = grid.NearestUnexplored(new Vector3(3f, 0f, 3f));
        Assert.Equal(new Vector3(5f, 0f, 3f), pick);
        Assert.Equal(1, grid.UnexploredCount);
    }

    [Fact]
    public void Exhausted_needs_both_the_grid_seen_and_the_world_beyond_it_seen()
    {
        var seen = Grid([5], [0f], [1]);
        seen.MarkExplored(Vector3.Zero, 100f);

        Assert.True(seen.Exhausted);

        // The same grid with reachable ground outside the window is not exhausted — the loop has
        // somewhere to go, it just cannot see it from here.
        var more = Grid([5], [0f], [1], reachableOutside: true);
        more.MarkExplored(Vector3.Zero, 100f);

        Assert.False(more.Exhausted);
        Assert.True(more.UnexploredCount == 0);
    }

    [Fact]
    public void A_cut_off_surface_beside_reachable_ground_at_the_same_level_is_a_gate()
    {
        // Column 5 (3, 0, 3) is reachable; column 6 (5, 0, 3) is cut off at the same height. That is
        // a door, a lip or a seam.
        var grid = Grid([5, 6], [0f, 0f], [1, 2]);

        var gate = Assert.Single(grid.GateCandidates(new Vector3(3f, 0f, 3f)));

        Assert.Equal(new Vector3(3f, 0f, 3f), gate.Frontier);
        Assert.Equal(new Vector3(5f, 0f, 3f), gate.Beyond);
    }

    [Fact]
    public void A_cut_off_surface_on_another_storey_is_not_a_gate()
    {
        // The same neighbours, thirty yalms apart vertically: that is the far end of a lift shaft,
        // and treating it as a gate would have the run probing its own ceiling forever.
        var grid = Grid([5, 6], [0f, 30f], [1, 2]);

        Assert.Empty(grid.GateCandidates(new Vector3(3f, 0f, 3f)));
    }

    [Fact]
    public void One_doorway_is_one_gate_even_when_it_spans_several_cells()
    {
        // A doorway is wider than a cell, so the same edge shows up from both sides and from every
        // cell along it. The ledger wants one entry per edge, not nine.
        var grid = Grid([5, 6, 7, 9, 10, 11], [0f, 0f, 0f, 0f, 0f, 0f], [1, 1, 1, 2, 2, 2]);

        var gates = grid.GateCandidates(new Vector3(3f, 0f, 3f));

        Assert.Equal(3, gates.Count); // three distinct cut-off surfaces, not the nine pairs above them
    }

    [Fact]
    public void Gates_are_ordered_by_how_far_away_they_are()
    {
        // (3,0,3) reachable, with a cut-off surface beside it at (5,0,3) and a diagonal one at
        // (1,0,5): the nearer edge is offered first.
        var grid = Grid([5, 6, 8], [0f, 0f, 0f], [1, 2, 2]);

        var gates = grid.GateCandidates(new Vector3(3f, 0f, 3f));

        Assert.Equal(new Vector3(5f, 0f, 3f), gates[0].Beyond);
        Assert.Equal(new Vector3(1f, 0f, 5f), gates[1].Beyond);
    }
}
