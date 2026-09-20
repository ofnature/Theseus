using System.Numerics;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// Exploration walks into the parts of the map nobody has looked at, in §2.4's order: the way
/// through a gate the dungeon just opened, then the nearest reachable ground nobody has seen. What
/// these pin beyond the order is that a destination survives being interrupted — a loop that
/// re-picks on every grant walks a zigzag across the room.
/// </summary>
public sealed class ExplorationLoopTests
{
    private sealed class Fixture
    {
        public readonly List<Gate> Gates = [];
        public readonly FakeStepWorld Game = new();
        public readonly ExplorationLoop Loop;

        public Fixture() => Loop = new ExplorationLoop(() => Gates);

        public WorldModel.Snapshot World(
            Vector3? at = null,
            Vector3? unexplored = null,
            bool exhausted = false)
            => new(
                new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
                at ?? Vector3.Zero, (1314, 1, 103), 0, 0, 1, true, false, false,
                [], [], unexplored, exhausted);
    }

    [Fact]
    public void An_open_gate_outranks_any_amount_of_walking()
    {
        var f = new Fixture();
        f.Gates.Add(Gate(open: true, beyond: new Vector3(60f, 0f, 0f)));

        var bid = f.Loop.Bid(f.World(unexplored: new Vector3(10f, 0f, 0f)));

        Assert.NotNull(bid);
        Assert.Contains("is open", bid!.Reason);
        Assert.Equal(new Vector3(60f, 0f, 0f), f.Loop.Destination);
    }

    [Fact]
    public void A_gate_still_shut_is_not_a_destination()
    {
        var f = new Fixture();
        f.Gates.Add(Gate(open: false, beyond: new Vector3(60f, 0f, 0f)));

        var bid = f.Loop.Bid(f.World(unexplored: new Vector3(10f, 0f, 0f)));

        Assert.NotNull(bid);
        Assert.Contains("unexplored ground", bid!.Reason);
    }

    [Fact]
    public void The_nearest_unexplored_ground_is_the_pick()
    {
        var f = new Fixture();

        var bid = f.Loop.Bid(f.World(at: Vector3.Zero, unexplored: new Vector3(12f, 0f, 0f)));

        Assert.NotNull(bid);
        Assert.Contains("12y away", bid!.Reason);
    }

    [Fact]
    public void The_destination_survives_being_interrupted()
    {
        var f = new Fixture();
        var ground = new Vector3(12f, 0f, 0f);

        f.Loop.Bid(f.World(unexplored: ground));

        // Combat takes the grant and gives it back. Nothing about the ground changed, so neither
        // does the answer: the loop is still walking to the same place it was.
        f.Loop.Bid(f.World(unexplored: ground));

        Assert.Equal(ground, f.Loop.Destination);
    }

    [Fact]
    public void A_pick_moves_when_the_grid_offers_a_different_place()
    {
        var f = new Fixture();
        f.Loop.Bid(f.World(unexplored: new Vector3(12f, 0f, 0f)));

        var bid = f.Loop.Bid(f.World(unexplored: new Vector3(30f, 0f, 0f)));

        Assert.Equal(new Vector3(30f, 0f, 0f), f.Loop.Destination);
        Assert.Contains("30y", bid!.Reason);
    }

    [Fact]
    public void An_open_gate_still_wins_over_a_region_already_being_walked()
    {
        // §2.4's order is not a suggestion: a way through the dungeon outranks more walking, even
        // when the walking has already started.
        var f = new Fixture();
        f.Loop.Bid(f.World(unexplored: new Vector3(12f, 0f, 0f)));
        f.Gates.Add(Gate(open: true, beyond: new Vector3(90f, 0f, 0f)));

        var bid = f.Loop.Bid(f.World(unexplored: new Vector3(12f, 0f, 0f)));

        Assert.Equal(new Vector3(90f, 0f, 0f), f.Loop.Destination);
        Assert.Contains("is open", bid!.Reason);
    }

    [Fact]
    public void Arriving_clears_the_pick_so_the_next_tick_can_choose_again()
    {
        var f = new Fixture();
        var destination = new Vector3(12f, 0f, 0f);
        f.Loop.Bid(f.World(unexplored: destination));

        f.Loop.Run(f.World(at: destination, unexplored: destination), f.Game);

        Assert.Equal(1, f.Game.StopMovingCalls);
        Assert.Empty(f.Game.MoveRequests);
        Assert.Null(f.Loop.Destination);
    }

    [Fact]
    public void Nowhere_left_and_no_gate_bids_nothing()
    {
        var f = new Fixture();

        // No grid answer at all: the loop stays out of it and the caller's landmarks and overrides
        // do the walking, exactly as they did before the solver existed.
        Assert.Null(f.Loop.Bid(f.World(exhausted: true)));
        Assert.Null(f.Loop.Bid(f.World()));
    }

    [Fact]
    public void Walking_goes_through_the_seam()
    {
        var f = new Fixture();
        var destination = new Vector3(12f, 0f, 0f);
        f.Loop.Bid(f.World(unexplored: destination));

        f.Loop.Run(f.World(unexplored: destination), f.Game);

        Assert.Equal([destination], f.Game.MoveRequests);
    }

    private static Gate Gate(bool open, Vector3 beyond)
    {
        var gate = new Gate(
            $"gate{beyond.X}",
            new Vector3(beyond.X, 0f, beyond.Z - 2f),
            beyond,
            GateKind.Unknown,
            new GateCondition.ObjectiveReached(1),
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));

        gate.State = open ? GateState.Open : GateState.Locked;
        return gate;
    }
}
