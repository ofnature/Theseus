using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// §2.5's other boss trigger: a hostile that will not come to us, with nothing left to explore. The
/// clauses are what these pin — every one of them is the difference between handing a fight over and
/// abandoning a dungeon that still had somewhere to walk.
/// </summary>
public sealed class BossHandoffTests
{
    private sealed class Fixture
    {
        public readonly FakeStepWorld Game = new();
        public bool OpenGate;
        public readonly Arbiter Arbiter;

        private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public Fixture()
        {
            var gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-boss-{Guid.NewGuid():N}.jsonl"));
            var interactables = new InteractableLoop(new InteractableContext(
                new Taxonomy(), new GhostCache(), gaps, () => "run", () => "key"));

            Arbiter = new Arbiter(
                new CombatLoop(),
                interactables,
                new ExplorationLoop(() => []),
                () => Game.Transit,
                openGateWaiting: () => OpenGate);
        }

        public WorldModel.Snapshot World(
            bool inCombat = false,
            bool bossModule = false,
            bool exhausted = true,
            Vector3? unexplored = null,
            params WorldObject[] objects)
            => new(_now, Vector3.Zero, (1314, 1, 103), 0, 0, 3, true, inCombat, bossModule,
                [.. objects.Select(o => new WorldModel.Recognised(o, null, false, o.Position.Length()))],
                [], unexplored, exhausted);

        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);
    }

    private static WorldObject Boss(Vector3 at)
        => new(1, 4242, "arena boss", at, WorldObjectKind.Hostile, true);

    [Fact]
    public void A_hostile_standing_off_hands_over_only_after_the_engage_timeout()
    {
        var f = new Fixture();
        var far = Boss(new Vector3(60f, 0f, 0f));   // outside aggro range: it is not coming to us

        Assert.Equal(LoopKind.Idle, f.Arbiter.Decide(f.World(objects: far)).Kind);

        f.Advance(Arbiter.StandoffBefore.TotalSeconds - 1);
        Assert.Equal(LoopKind.Idle, f.Arbiter.Decide(f.World(objects: far)).Kind);

        f.Advance(1);
        var decision = f.Arbiter.Decide(f.World(objects: far));

        Assert.Equal(LoopKind.BossHandoff, decision.Kind);
        Assert.Contains("standing off", decision.Reason);
    }

    [Fact]
    public void Somewhere_left_to_walk_is_not_a_standoff()
    {
        var f = new Fixture();
        var far = Boss(new Vector3(60f, 0f, 0f));

        // Two minutes of nothing but a distant hostile — and an unexplored region. That is a walk,
        // not a fight, and walking is what the solver is for.
        f.Advance(200);

        var decision = f.Arbiter.Decide(f.World(exhausted: false, unexplored: new Vector3(80f, 0f, 0f), objects: far));

        Assert.Equal(LoopKind.Exploration, decision.Kind);
    }

    [Fact]
    public void Fighting_resets_the_standoff_clock()
    {
        var f = new Fixture();
        var far = Boss(new Vector3(60f, 0f, 0f));

        f.Advance(Arbiter.StandoffBefore.TotalSeconds + 10);
        Assert.Equal(LoopKind.Combat, f.Arbiter.Decide(f.World(inCombat: true, objects: far)).Kind);

        // The pack engaged, so the clock starts again: a fight that came to us is not a standoff.
        f.Advance(1);
        Assert.Equal(LoopKind.Combat, f.Arbiter.Decide(f.World(inCombat: true, objects: far)).Kind);

        f.Advance(Arbiter.StandoffBefore.TotalSeconds - 5);
        Assert.Equal(LoopKind.Idle, f.Arbiter.Decide(f.World(objects: far)).Kind);
    }

    [Fact]
    public void An_open_edge_outranks_a_boss()
    {
        var f = new Fixture();
        var far = Boss(new Vector3(60f, 0f, 0f));

        f.Advance(Arbiter.StandoffBefore.TotalSeconds + 10);
        Assert.Equal(LoopKind.Idle, f.Arbiter.Decide(f.World(objects: far)).Kind);

        // A way through is open: whatever is standing there, the dungeon goes on through the door.
        f.OpenGate = true;

        Assert.Equal(LoopKind.Idle, f.Arbiter.Decide(f.World(objects: far)).Kind);
    }

    [Fact]
    public void A_module_takes_the_fight_immediately()
    {
        var f = new Fixture();

        var decision = f.Arbiter.Decide(f.World(bossModule: true, exhausted: false, objects: Boss(new Vector3(10f, 0f, 0f))));

        Assert.Equal(LoopKind.BossHandoff, decision.Kind);
        Assert.Contains("module", decision.Reason);
    }

    [Fact]
    public void A_standoff_with_nobody_to_hand_it_to_releases_the_run_named()
    {
        // The driver's side of §2.5: the handoff happened, no module took it, and the answer is a
        // person — so the solver stops the character and says exactly where and what.
        var perception = new Perception();
        var game = new FakeStepWorld();
        var gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-boss-{Guid.NewGuid():N}.jsonl"));
        var interactables = new InteractableLoop(new InteractableContext(
            new Taxonomy(), new GhostCache(), gaps, () => "run", () => "key"));
        var arbiter = new Arbiter(new CombatLoop(), interactables, new ExplorationLoop(() => []), () => game.Transit);

        var logs = new List<string>();
        var driver = new SolverDriver(perception, arbiter, game, () => true, ladder: null, logs.Add);
        driver.Start();

        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var boss = new WorldObject(1, 4242, "arena boss", new Vector3(60f, 0f, 0f), WorldObjectKind.Hostile, true);

        WorldModel.Snapshot Snapshot(DateTime at) => new(
            at, Vector3.Zero, (1314, 1, 103), 0, 0, 3, true, false, false,
            [new WorldModel.Recognised(boss, null, false, 60f)], [], null, true);

        perception.LastSnapshot = Snapshot(now);
        driver.Tick();
        Assert.Equal(SolverStatus.Driving, driver.Status);

        perception.LastSnapshot = Snapshot(now.AddSeconds(Arbiter.StandoffBefore.TotalSeconds + 1));
        driver.Tick();

        Assert.Equal(SolverStatus.Faulted, driver.Status);
        Assert.Contains("standing off", driver.Detail);
        Assert.Contains("(60, 0, 0)", driver.Detail);
        Assert.Contains(logs, line => line.Contains("a person should look"));
        Assert.True(game.StopMovingCalls > 0);
    }

    private sealed class Perception : IPerception
    {
        public WorldModel.Snapshot? LastSnapshot { get; set; }

        public void Widen()
        {
        }
    }
}
