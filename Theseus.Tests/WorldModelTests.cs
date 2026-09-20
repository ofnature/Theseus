using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Frontier;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The snapshot is what the loops read instead of the game, so these pin that it says what a loop
/// needs — what is around, what is done with it, where is left to go — and that it stays a pure
/// function of what it was handed.
/// </summary>
public class WorldModelTests
{
    private const uint Lever = 2001234;
    private const uint Grindstone = 2005678;

    private static DutyObjectiveSnapshot Objectives(params bool[] complete)
    {
        var slots = complete
            .Select((done, index) => new DutyObjective(index, done ? 1 : 0, 1, false, $"objective {index}", done))
            .ToList();

        return DutyObjectiveSnapshot.From(slots, ObjectiveSource.Director);
    }

    private static Theseus.Services.Frontier.WorldObject At(
        WorldObjectKind kind, uint dataId, Vector3 position, ulong id = 1)
        => new(id, dataId, "something", position, kind, true);

    [Fact]
    public void What_is_nearby_is_recognised_by_kind_and_by_what_is_known_of_it()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f)));
        world.Nearby.Add(At(WorldObjectKind.Hostile, Grindstone, new Vector3(6f, 0f, 0f), id: 2));
        world.Nearby.Add(At(WorldObjectKind.Treasure, 2009999, new Vector3(8f, 0f, 0f), id: 3));

        var taxonomy = new Taxonomy();
        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);
        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);

        var snapshot = WorldModel.Observe(world, Objectives(false), taxonomy, new GhostCache());

        var interactable = Assert.Single(snapshot.Interactables);
        Assert.Equal(BehaviourClass.DirectTrigger, interactable.Class);
        Assert.Equal(4f, interactable.Distance, 2);

        // A hostile and a coffer are kinds of object, not behaviour classes: nothing probes them.
        Assert.Null(Assert.Single(snapshot.Hostiles).Class);
        Assert.Null(Assert.Single(snapshot.Loot).Class);
    }

    [Fact]
    public void An_object_the_run_is_done_with_is_still_seen_but_does_not_bid()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var lever = At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f));
        world.Nearby.Add(lever);

        var done = new GhostCache();

        // The first observation sets the cache's scope; remembering before it would be remembered
        // into the world that gets forgotten.
        WorldModel.Observe(world, Objectives(false), new Taxonomy(), done);
        done.Remember(lever);

        var snapshot = WorldModel.Observe(world, Objectives(false), new Taxonomy(), done);

        Assert.Single(snapshot.Objects);
        Assert.Empty(snapshot.Interactables); // ghosted: remembered, not still to do
    }

    [Fact]
    public void The_stage_and_the_counts_come_from_the_game_s_own_objectives()
    {
        var world = new FakeStepWorld();

        var snapshot = WorldModel.Observe(world, Objectives(true, false, false), new Taxonomy(), new GhostCache());

        Assert.Equal(1, snapshot.Stage);        // the first incomplete slot, counted, never backwards
        Assert.Equal(1, snapshot.ObjectivesDone);
        Assert.Equal(3, snapshot.ObjectivesTotal);
        Assert.True(snapshot.ObjectivesReadable);
    }

    [Fact]
    public void Unreadable_objectives_are_marked_unreadable_rather_than_empty()
    {
        // The reader's rule: "nothing could be read" must never look like "nothing is complete".
        var world = new FakeStepWorld();

        var snapshot = WorldModel.Observe(world, DutyObjectiveSnapshot.Unavailable, new Taxonomy(), new GhostCache());

        Assert.False(snapshot.ObjectivesReadable);
        Assert.Equal(0, snapshot.ObjectivesTotal);
    }

    [Fact]
    public void The_grid_supplies_what_is_left_to_explore_and_where_the_edges_are()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };

        // A 4×4 grid of 2 y cells: (3,0,3) reachable, (5,0,3) cut off beside it at the same height,
        // (3,0,1) reachable and close enough to have been seen, (3,0,5) reachable and not.
        var grid = ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4,
            [5, 6, 1, 9], [0f, 0f, 0f, 0f], [1, 2, 1, 1], false)!;

        var snapshot = WorldModel.Observe(world, Objectives(false), new Taxonomy(), new GhostCache(),
            grid, entrance: Vector3.Zero, sightRadius: 5f);

        var gate = Assert.Single(snapshot.Gates);
        Assert.Equal(new Vector3(5f, 0f, 3f), gate.Beyond);

        Assert.Equal(new Vector3(3f, 0f, 5f), snapshot.Unexplored);
        Assert.False(snapshot.Explored);
    }

    [Fact]
    public void Standing_on_the_last_unseen_ground_is_explored()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var grid = ReachableGrid.TryFrom("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4,
            [5], [0f], [1], false)!;

        var snapshot = WorldModel.Observe(world, Objectives(true), new Taxonomy(), new GhostCache(), grid);

        Assert.Null(snapshot.Unexplored);
        Assert.True(snapshot.Explored);
    }

    [Fact]
    public void A_scope_change_forgets_what_was_done_in_the_last_one()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, Scope = (1314, 1, 103) };
        var lever = At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f));
        world.Nearby.Add(lever);

        var done = new GhostCache();
        WorldModel.Observe(world, Objectives(false), new Taxonomy(), done);
        done.Remember(lever);
        Assert.Empty(WorldModel.Observe(world, Objectives(false), new Taxonomy(), done).Interactables);

        // A different duty: the same ground is not the same ground.
        world.Scope = (1113, 2, 105);

        Assert.Single(WorldModel.Observe(world, Objectives(false), new Taxonomy(), done).Interactables);
    }

    [Fact]
    public void Objects_outside_the_scan_are_not_in_the_snapshot()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(200f, 0f, 0f)));

        var snapshot = WorldModel.Observe(world, Objectives(false), new Taxonomy(), new GhostCache(), scanRadius: 40f);

        Assert.Empty(snapshot.Objects);
    }
}
