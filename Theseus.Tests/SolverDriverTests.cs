using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The driver is the only thing that moves a solver-driven run, and the only things it may do are
/// the four the arbiter grants. What these pin is the middle: it hands the run back rather than
/// standing in it, and it stops moving the moment its map goes away.
/// </summary>
public sealed class SolverDriverTests
{
    private sealed class FakePerception : IPerception
    {
        public WorldModel.Snapshot? LastSnapshot { get; set; }
        public int Widened { get; private set; }
        public void Widen() => Widened++;
    }

    private sealed class Fixture
    {
        public readonly FakeStepWorld Game = new();
        public readonly FakePerception Perception = new();
        public readonly List<Gate> Gates = [];
        public readonly List<string> Log = [];
        public readonly InteractableLoop Interactables;
        public readonly Arbiter Arbiter;
        public readonly SolverDriver Driver;

        private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public Fixture()
        {
            var gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-driver-{Guid.NewGuid():N}.jsonl"));
            Interactables = new InteractableLoop(new InteractableContext(
                new Taxonomy(), new GhostCache(), gaps, () => "run", () => "key"));
            Arbiter = new Arbiter(new CombatLoop(), Interactables, new ExplorationLoop(() => Gates), () => Game.Transit);
            Driver = new SolverDriver(Perception, Arbiter, Game, () => Usable, ladder: null, Log.Add);
            Driver.Start();
        }

        public bool Usable { get; set; } = true;

        public WorldModel.Snapshot World(
            Vector3? unexplored = null,
            bool inCombat = false,
            bool bossModule = false,
            params WorldObject[] objects)
            => new(_now, Vector3.Zero, (1314, 1, 103), 0, 0, 1, true, inCombat, bossModule,
                [.. objects.Select(o => new WorldModel.Recognised(o, null, false, o.Position.Length()))],
                [], unexplored, false);

        public void Advance(double seconds, Vector3? unexplored = null, bool inCombat = false)
        {
            _now = _now.AddSeconds(seconds);
            Perception.LastSnapshot = World(unexplored, inCombat);
        }

        public WorldModel.Snapshot Fresh(Vector3? unexplored = null, bool inCombat = false, bool bossModule = false)
        {
            Perception.LastSnapshot = World(unexplored, inCombat, bossModule);
            return Perception.LastSnapshot;
        }
    }

    [Fact]
    public void Exploration_moves_the_character_through_the_seam()
    {
        var f = new Fixture();
        f.Fresh(unexplored: new Vector3(40f, 0f, 0f));

        f.Driver.Tick();

        Assert.Single(f.Game.MoveRequests);
        Assert.Equal(SolverStatus.Driving, f.Driver.Status);
        Assert.Contains("Exploration", f.Driver.Detail);
    }

    [Fact]
    public void Combat_stands_and_targets_instead_of_walking_on()
    {
        var f = new Fixture();
        f.Fresh(inCombat: true);

        f.Driver.Tick();

        Assert.Equal(1, f.Game.StopMovingCalls);
        Assert.Empty(f.Game.MoveRequests);
    }

    [Fact]
    public void A_transit_moves_nothing_at_all()
    {
        var f = new Fixture();
        f.Game.Transit = TransitPhase.Pushing;
        f.Fresh(inCombat: true, unexplored: new Vector3(40f, 0f, 0f));

        f.Driver.Tick();

        // Mid-ride, a fight in progress and ground to explore: every one of these is a reason to do
        // something, and every one of them is wrong while the game is carrying the character.
        Assert.Empty(f.Game.MoveRequests);
        Assert.Equal(0, f.Game.StopMovingCalls);
        Assert.Equal(SolverStatus.Driving, f.Driver.Status);
    }

    [Fact]
    public void A_boss_module_gets_the_fight_and_the_solver_stands_still()
    {
        var f = new Fixture();
        f.Fresh(bossModule: true);

        f.Driver.Tick();

        Assert.Equal(1, f.Game.StopMovingCalls);
        Assert.Empty(f.Game.Attacked);
    }

    [Fact]
    public void Nothing_to_do_widens_perception_once_and_then_hands_the_run_back()
    {
        var f = new Fixture();
        f.Advance(0);
        f.Driver.Tick();

        Assert.Equal(SolverStatus.Driving, f.Driver.Status);
        Assert.Equal(0, f.Perception.Widened);

        // Ten seconds of nothing: look further. This is the ladder's first rung, and with no ladder
        // context supplied there is nothing after it but the hand-back.
        f.Advance(11);
        f.Driver.Tick();

        Assert.Equal(1, f.Perception.Widened);
        Assert.Equal(SolverStatus.Driving, f.Driver.Status);

        // Two minutes of nothing, widened included: this is the caller's run again.
        f.Advance(110);
        f.Driver.Tick();

        Assert.Equal(SolverStatus.HandedOff, f.Driver.Status);
        Assert.Equal(1, f.Driver.RunsHandedBack);
        Assert.Contains(f.Log, line => line.Contains("handing this run back"));
        Assert.Equal(1, f.Game.StopMovingCalls);
    }

    [Fact]
    public void Doing_something_again_clears_the_idle_timer()
    {
        var f = new Fixture();
        f.Advance(0);
        f.Driver.Tick();

        f.Advance(11, unexplored: new Vector3(40f, 0f, 0f));
        f.Driver.Tick();

        // Something else to do, and back to idling: the widen happens on the new idle, not never.
        f.Advance(0);
        f.Driver.Tick();
        f.Advance(11);
        f.Driver.Tick();

        Assert.Equal(1, f.Perception.Widened);
        Assert.Equal(SolverStatus.Driving, f.Driver.Status);
    }

    [Fact]
    public void A_map_that_stops_answering_hands_the_run_back_at_once()
    {
        var f = new Fixture();
        f.Fresh(unexplored: new Vector3(40f, 0f, 0f));
        f.Usable = false;

        f.Driver.Tick();

        Assert.Equal(SolverStatus.HandedOff, f.Driver.Status);
        Assert.Empty(f.Game.MoveRequests);
        Assert.Contains(f.Log, line => line.Contains("its map stopped answering"));
    }

    [Fact]
    public void A_hand_back_stops_the_movement_it_was_doing()
    {
        var f = new Fixture();
        f.Fresh(unexplored: new Vector3(40f, 0f, 0f));
        f.Driver.Tick();
        Assert.Equal(0, f.Game.StopMovingCalls); // walking somewhere is a move, not an arrival

        f.Usable = false;
        f.Driver.Tick();

        // Release first, hand back second. A hand-back that leaves the character walking is a
        // hand-back into a wall.
        Assert.Equal(1, f.Game.StopMovingCalls);
        Assert.Equal(SolverStatus.HandedOff, f.Driver.Status);
    }

    [Fact]
    public void It_does_not_drive_before_it_is_started_and_not_after_it_stops()
    {
        var f = new Fixture();
        f.Driver.Stop("done");
        f.Fresh(unexplored: new Vector3(40f, 0f, 0f));

        f.Driver.Tick();

        Assert.Empty(f.Game.MoveRequests);
        Assert.Equal(SolverStatus.Idle, f.Driver.Status);
    }

    [Fact]
    public void The_hand_back_names_where_it_happened()
    {
        var f = new Fixture();
        f.Advance(0);
        f.Driver.Tick();

        // One widen first — that is the ladder's first rung, and it has to fail before rung 5.
        f.Advance(11);
        f.Driver.Tick();
        f.Advance(110);
        f.Driver.Tick();

        Assert.Equal(SolverStatus.HandedOff, f.Driver.Status);
        Assert.NotNull(f.Driver.LastStuckPosition);
        Assert.Contains("(", f.Driver.Detail);
    }
}
