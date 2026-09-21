using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The ladder is what happens when nothing bids: look further, ask the fleet, doubt the failures,
/// take the authored way out, and give the run back. What these pin is the order, that each rung
/// gets exactly one attempt, and that a rung which unblocks a loop ends the climb.
/// </summary>
public sealed class SolverLadderTests
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
        public readonly List<string> Log = [];
        public readonly List<string> Asked = [];
        public readonly Arbiter Arbiter;
        public readonly SolverDriver Driver;

        private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        private int _fleetOpened;
        private int _retried;
        private Vector3? _escape;

        public Fixture()
        {
            var gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-ladder-{Guid.NewGuid():N}.jsonl"));
            var interactables = new InteractableLoop(new InteractableContext(
                new Taxonomy(), new GhostCache(), gaps, () => "run", () => "key"));
            Arbiter = new Arbiter(new CombatLoop(), interactables, new ExplorationLoop(() => []), () => Game.Transit);

            var ladder = new LadderContext(
                FleetSweep: () => { Asked.Add("fleet"); return _fleetOpened; },
                RetryInteractables: () => { Asked.Add("retry"); return _retried; },
                OverrideWaypoint: () => { Asked.Add("override"); return _escape; });

            Driver = new SolverDriver(Perception, Arbiter, Game, () => true, ladder, Log.Add);
            Driver.Start();
        }

        public void FleetOpens(int gates) => _fleetOpened = gates;

        public void Retries(int objects) => _retried = objects;

        public void EscapeAt(Vector3? waypoint) => _escape = waypoint;

        /// <summary>Idles the run for a while: the snapshot moves with the clock, nothing bids.</summary>
        public void Idle(double seconds, Vector3? at = null)
        {
            _now = _now.AddSeconds(seconds);
            Perception.LastSnapshot = new WorldModel.Snapshot(
                _now, at ?? Vector3.Zero, (1314, 1, 103), 0, 0, 1, true, false, false, [], [], null, false);
            Driver.Tick();
        }

        /// <summary>Something to do again: the lobby for the next climb's reset assertions.</summary>
        public void Busy(Vector3? unexplored = null)
        {
            Perception.LastSnapshot = new WorldModel.Snapshot(
                _now, Vector3.Zero, (1314, 1, 103), 0, 0, 1, true, false, false, [], [],
                unexplored ?? new Vector3(40f, 0f, 0f), false);
            Driver.Tick();
        }
    }

    [Fact]
    public void The_ladder_climbs_in_order_with_one_attempt_per_rung()
    {
        var f = new Fixture();

        f.Idle(0);
        Assert.Equal(LadderRung.None, f.Driver.Rung);

        f.Idle(11);                                             // rung 1
        Assert.Equal(LadderRung.Widen, f.Driver.Rung);
        Assert.Equal(1, f.Perception.Widened);

        f.Idle(20);                                             // rung 2
        Assert.Equal(LadderRung.Fleet, f.Driver.Rung);

        f.Idle(31);                                             // rung 3
        Assert.Equal(LadderRung.Retry, f.Driver.Rung);

        f.Idle(31);                                             // rung 4
        Assert.Equal(LadderRung.Override, f.Driver.Rung);

        f.Idle(31);                                             // rung 5
        Assert.Equal(LadderRung.HandBack, f.Driver.Rung);
        Assert.Equal(SolverStatus.HandedOff, f.Driver.Status);

        // Each rung was asked exactly once, in order.
        Assert.Equal(["fleet", "retry", "override"], f.Asked);
    }

    [Fact]
    public void A_rung_that_finds_nothing_moves_the_climb_on()
    {
        var f = new Fixture();

        f.Idle(0);
        f.Idle(11);
        f.Idle(20);

        // Nothing from the fleet: the climb carries on rather than restarting the wait.
        Assert.Contains("nothing from the fleet yet", f.Driver.Detail);

        f.Idle(31);
        Assert.Equal(LadderRung.Retry, f.Driver.Rung);
        Assert.Contains(f.Log, line => line.Contains("nothing worth retrying"));
    }

    [Fact]
    public void A_peer_past_an_edge_is_worth_following()
    {
        var f = new Fixture();
        f.FleetOpens(2);

        f.Idle(0);
        f.Idle(11);
        f.Idle(20);

        Assert.Equal(LadderRung.Fleet, f.Driver.Rung);
        Assert.Contains(f.Log, line => line.Contains("already past 2 edge(s)"));
    }

    [Fact]
    public void An_authored_way_out_is_walked_and_then_abandoned_if_it_leads_nowhere()
    {
        var f = new Fixture();
        var escape = new Vector3(50f, 0f, 50f);
        f.EscapeAt(escape);

        f.Idle(0);
        f.Idle(11);
        f.Idle(20);
        f.Idle(31);
        f.Idle(31);                                             // rung 4 finds the escape

        Assert.Equal(LadderRung.Override, f.Driver.Rung);
        Assert.Contains(f.Log, line => line.Contains("authored way out"));

        // Walking it: the next idle tick moves the character instead of climbing further.
        f.Idle(1);
        Assert.Equal([escape], f.Game.MoveRequests);

        // Arrived, and still nothing to do: the climb finishes and the run goes back. The ladder's
        // whole budget is two minutes, and the escape was walked inside it.
        f.Idle(30, at: escape);
        Assert.Equal(LadderRung.HandBack, f.Driver.Rung);
    }

    [Fact]
    public void A_rung_that_unblocks_a_loop_ends_the_climb()
    {
        var f = new Fixture();

        f.Idle(0);
        f.Idle(11);                                             // widened
        Assert.Equal(1, f.Perception.Widened);

        // The widened look found something: the arbiter now has a destination, so the ladder resets
        // — and the next climb gets a fresh widen rather than inheriting this one.
        f.Busy(unexplored: new Vector3(40f, 0f, 0f));
        Assert.Equal(SolverStatus.Driving, f.Driver.Status);
        Assert.Contains("Exploration", f.Driver.Detail);

        f.Idle(0);
        f.Idle(11);

        Assert.Equal(2, f.Perception.Widened);
    }
}
