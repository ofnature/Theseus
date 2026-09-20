using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// One loop per tick, and the preemption rules that exist because each of them has been a failure
/// at least once: never decide anything is wrong while a transit is in flight, never walk away from
/// a fight, never leave a fight mid-pack the moment it looks over.
/// </summary>
public sealed class ArbiterTests
{
    private const uint Lever = 2001234;

    private sealed class Fixture
    {
        public readonly FakeStepWorld Game = new();
        public readonly List<Gate> Gates = [];
        public readonly ExplorationLoop Exploration;
        public readonly InteractableLoop Interactables;
        public readonly Arbiter Arbiter;

        public Fixture()
        {
            var gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-arbiter-{Guid.NewGuid():N}.jsonl"));
            Interactables = new InteractableLoop(new InteractableContext(
                new Taxonomy(), new GhostCache(), gaps, () => "run", () => "key"));
            Exploration = new ExplorationLoop(() => Gates);
            Arbiter = new Arbiter(new CombatLoop(), Interactables, Exploration, () => Game.Transit);
        }

        public WorldModel.Snapshot World(
            bool inCombat = false,
            bool bossModule = false,
            DateTime? at = null,
            Vector3? position = null,
            Vector3? unexplored = null,
            params WorldObject[] objects)
        {
            var where = position ?? Vector3.Zero;
            return new WorldModel.Snapshot(
                at ?? new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
                where, (1314, 1, 103), 0, 0, 1, true, inCombat, bossModule,
                [.. objects.Select(o => new WorldModel.Recognised(
                    o, null, false, Vector3.Distance(o.Position, where)))],
                [], unexplored, false);
        }
    }

    private static WorldObject Mob(Vector3 at, bool targetable = true)
        => new(99, 99, "wolf", at, WorldObjectKind.Hostile, targetable);

    private static WorldObject Lever100(uint at = Lever) => new(
        900 + at, at, "lever", new Vector3(10f, 0f, 0f), WorldObjectKind.Interactable, true);

    [Fact]
    public void A_transit_in_flight_freezes_every_loop()
    {
        var f = new Fixture();
        f.Game.Transit = TransitPhase.Pushing;

        // Everything at once: a fight in progress, a lever in reach, ground to explore. The mover
        // is mid-ride, and the one thing that must not happen is a loop declaring the character stuck.
        var decision = f.Arbiter.Decide(f.World(inCombat: true, objects: [Mob(new Vector3(2f, 0f, 0f)), Lever100()]));

        Assert.Equal(LoopKind.Transit, decision.Kind);
    }

    [Fact]
    public void A_boss_module_takes_the_fight_from_the_solver()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World(bossModule: true, objects: [Mob(new Vector3(2f, 0f, 0f))]));

        Assert.Equal(LoopKind.BossHandoff, decision.Kind);
    }

    [Fact]
    public void Combat_beats_both_an_object_and_a_destination()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World(
            inCombat: true,
            objects: [Mob(new Vector3(2f, 0f, 0f)), Lever100()],
            unexplored: new Vector3(40f, 0f, 0f)));

        Assert.Equal(LoopKind.Combat, decision.Kind);
    }

    [Fact]
    public void An_object_in_reach_beats_walking_onwards()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World(
            objects: [Lever100()],
            unexplored: new Vector3(40f, 0f, 0f)));

        Assert.Equal(LoopKind.Interactable, decision.Kind);
        Assert.Contains("lever", decision.Reason);
    }

    [Fact]
    public void Exploration_gets_the_grant_when_nothing_else_bids()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World(unexplored: new Vector3(40f, 0f, 0f)));

        Assert.Equal(LoopKind.Exploration, decision.Kind);
        Assert.Contains("unexplored ground", decision.Reason);
    }

    [Fact]
    public void Nothing_at_all_bids_nothing()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World());

        Assert.Equal(LoopKind.Idle, decision.Kind);
    }

    [Fact]
    public void A_fight_that_just_ended_holds_the_grant_for_a_beat()
    {
        var f = new Fixture();
        var start = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var ground = new Vector3(40f, 0f, 0f);

        // Mid-fight.
        var fighting = f.Arbiter.Decide(f.World(inCombat: true, at: start, unexplored: ground));
        Assert.Equal(LoopKind.Combat, fighting.Kind);

        // The pack is dead and the fight is over, a quarter second ago.
        var justAfter = f.Arbiter.Decide(f.World(at: start.AddSeconds(0.25), unexplored: ground));
        Assert.Equal(LoopKind.Combat, justAfter.Kind);
        Assert.Contains("holding a beat", justAfter.Reason);

        // Two seconds on, and the walk resumes.
        var later = f.Arbiter.Decide(f.World(at: start.AddSeconds(2), unexplored: ground));
        Assert.Equal(LoopKind.Exploration, later.Kind);
    }

    [Fact]
    public void A_hostile_that_cannot_be_targeted_yet_still_holds_the_fight()
    {
        var f = new Fixture();

        // Untargetable inside aggro range: not engaged, but not walking away either — the pack is
        // still the thing happening here.
        var decision = f.Arbiter.Decide(f.World(objects: [Mob(new Vector3(2f, 0f, 0f), targetable: false)]));

        Assert.Equal(LoopKind.Combat, decision.Kind);
    }
}
