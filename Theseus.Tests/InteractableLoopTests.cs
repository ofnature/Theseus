using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The interactable loop's contract is the four steps: choose, claim, touch, watch. What these pin
/// is the watching — because "interacted" is not an outcome, and a loop that believes otherwise
/// either retries a dead switch forever or calls a dungeon complete with the boss still up.
/// </summary>
public sealed class InteractableLoopTests
{
    private const uint Lever = 2001234;

    private sealed class Fixture
    {
        public readonly FakeStepWorld Game = new();
        public readonly Taxonomy Taxonomy = new();
        public readonly GhostCache Ghosts = new();
        public readonly List<string> Log = [];
        public readonly GapLog Gaps;
        public readonly Func<uint, bool> Wanted;
        public readonly InteractableLoop Loop;

        public Fixture(bool wanted = false)
        {
            Gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-loops-{Guid.NewGuid():N}.jsonl"));
            Wanted = _ => wanted;
            Loop = new InteractableLoop(new InteractableContext(
                Taxonomy, Ghosts, Gaps, () => "run", () => "key", Wanted, Log: Log.Add));
        }

        public WorldModel.Snapshot World(
            int stage = 0,
            Vector3? at = null,
            params WorldObject[] objects)
            => Snapshot(stage, at, objects);

        private static WorldModel.Snapshot Snapshot(int stage, Vector3? at, WorldObject[] objects)
        {
            var position = at ?? Vector3.Zero;
            return new WorldModel.Snapshot(
                new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
                position, (1314, 1, 103), stage, stage, 1, true, false, false,
                [.. objects.Select(o => new WorldModel.Recognised(
                    o, null, false, Vector3.Distance(o.Position, position)))],
                [], null, false);
        }
    }

    private static WorldObject Object(float x, float z, uint dataId = Lever, string name = "lever",
        BehaviourClass? cls = null)
        => new(900 + dataId, dataId, name, new Vector3(x, 0f, z), WorldObjectKind.Interactable, true);

    [Fact]
    public void An_object_in_reach_is_walked_to_and_then_touched()
    {
        var f = new Fixture();
        var world = f.World(at: Vector3.Zero, objects: [Object(10f, 0f)]);

        f.Loop.Run(world, f.Game);

        Assert.Single(f.Game.MoveRequests);
        Assert.Empty(f.Game.InteractedObjects);

        // Second tick, standing next to it: touch.
        var near = f.World(at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]);
        f.Loop.Run(near, f.Game);

        Assert.Single(f.Game.InteractedObjects);
        Assert.Contains(f.Log, line => line.Contains("Touching lever"));
    }

    [Fact]
    public void An_object_that_despawns_where_it_stood_is_a_pickup()
    {
        var f = new Fixture();
        f.Loop.Run(f.World(at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]), f.Game);

        // Next tick it is gone, and the character has not wandered.
        f.Loop.Run(f.World(at: new Vector3(2f, 0f, 0f)), f.Game);

        // One observation is a claim, not a reclassification: the taxonomy needs two before a
        // learned class outranks what it already believes.
        Assert.Equal(1, f.Taxonomy.Confirmations(Lever));
        Assert.True(f.Ghosts.Count > 0);
        Assert.Contains(f.Log, line => line.Contains("learned as PickupHold"));
        Assert.Empty(f.Game.Attacked);
    }

    [Fact]
    public void An_object_that_despawns_while_walking_away_is_not_a_pickup()
    {
        var f = new Fixture();
        f.Loop.Run(f.World(at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]), f.Game);

        // Twenty yalms later it is out of view, not taken.
        f.Loop.Run(f.World(at: new Vector3(20f, 0f, 0f)), f.Game);

        Assert.Equal(BehaviourClass.Unknown, f.Taxonomy.Classify(Lever));
    }

    [Fact]
    public void The_objective_moving_is_an_outcome()
    {
        var f = new Fixture();
        f.Loop.Run(f.World(stage: 0, at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]), f.Game);

        // Still there, but the run's stage advanced: whatever it did, it did something.
        f.Loop.Run(f.World(stage: 1, at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]), f.Game);

        Assert.Equal(1, f.Taxonomy.Confirmations(Lever));
        Assert.Contains(f.Log, line => line.Contains("the objective moved"));
    }

    [Fact]
    public void Nothing_in_the_window_is_a_failure_and_two_of_them_make_it_inert()
    {
        var f = new Fixture();
        var world = f.World(at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]);

        f.Loop.Run(world, f.Game);
        var late = Later(world, 6);
        f.Loop.Run(late, f.Game);

        Assert.Contains(f.Log, line => line.Contains("attempt 1/2"));
        Assert.Equal(0, f.Taxonomy.Confirmations(Lever));

        // A second chance, pressed again, and nothing again.
        f.Loop.Run(late, f.Game);
        f.Loop.Run(Later(late, 6), f.Game);

        Assert.Equal(1, f.Taxonomy.Confirmations(Lever));
        Assert.Contains(f.Log, line => line.Contains("inert after 2 attempts"));
        Assert.True(f.Gaps.Written > 0);
        Assert.True(f.Ghosts.Count > 0);

        // And now nothing bids on it: the loop will not spend a third interaction on scenery.
        Assert.Null(f.Loop.Bid(late));
    }

    [Fact]
    public void A_wanted_object_is_picked_up_from_across_the_zone()
    {
        var near = new Fixture();
        Assert.Null(near.Loop.Bid(near.World(objects: [Object(80f, 0f)])));

        // The same object, named by a locked gate: the lever is the frontier, not an errand.
        var wanted = new Fixture(wanted: true);
        Assert.NotNull(wanted.Loop.Bid(wanted.World(objects: [Object(80f, 0f)])));
    }

    [Fact]
    public void Combat_suspends_the_loop_but_an_unfinished_watch_still_bids()
    {
        var f = new Fixture();
        var world = f.World(at: new Vector3(2f, 0f, 0f), objects: [Object(1f, 0f)]);
        f.Loop.Run(world, f.Game);

        // Mid-watch, a fight breaks out: the claim holds, because the world is mid-change.
        Assert.NotNull(f.Loop.Bid(InCombat(world)));

        // But nothing new is started while the pack is alive.
        f.Loop.Release("combat");
        Assert.Null(f.Loop.Bid(InCombat(world)));
    }

    private static WorldModel.Snapshot Later(WorldModel.Snapshot world, double seconds)
        => world with { UtcNow = world.UtcNow.AddSeconds(seconds) };

    private static WorldModel.Snapshot InCombat(WorldModel.Snapshot world)
        => world with { InCombat = true };
}
