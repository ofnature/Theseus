using System.Numerics;
using Theseus.Services.Fleet;

namespace Theseus.Tests;

public class FleetTests
{
    private static FleetMember Player(ulong id, int slot, Vector3 at, bool self = false)
        => new(id, $"box{id}", slot, at, self, IsPlayer: true);

    private static FleetMember Npc(ulong id, int slot, Vector3 at)
        => new(id, $"npc{id}", slot, at, IsSelf: false, IsPlayer: false);

    private static (FleetRoster Roster, FleetGate Gate) Fleet(FakeStepWorld world, float stale = 10f)
    {
        var roster = new FleetRoster(world);
        return (roster, new FleetGate(world, roster, () => stale));
    }

    // ── Roster ──

    [Fact]
    public void A_trust_party_is_not_a_fleet()
    {
        // Trust companions sit in the same party list as real characters. Counting them would make
        // every gate wait for allies that follow you by themselves.
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Npc(2, 1, Vector3.Zero));
        world.Party.Add(Npc(3, 2, Vector3.Zero));

        var (roster, _) = Fleet(world);

        Assert.True(roster.IsSolo);
        Assert.Single(roster.Players);
    }

    [Fact]
    public void Duties_come_from_the_slot_so_every_box_agrees()
    {
        // The whole point: four clients reach the same answer with nothing passing between them.
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero));
        world.Party.Add(Player(2, 1, Vector3.Zero, self: true));
        world.Party.Add(Player(3, 2, Vector3.Zero));

        var (roster, _) = Fleet(world);

        Assert.Equal(1, roster.MySlot);
        Assert.True(roster.Holds(1));
        Assert.False(roster.Holds(0));
    }

    [Fact]
    public void Only_the_party_leader_leads()
    {
        var world = new FakeStepWorld();
        world.Party.Add(new FleetMember(1, "lead", 0, Vector3.Zero, false, true, IsLeader: true));
        world.Party.Add(new FleetMember(2, "me", 1, Vector3.Zero, true, true));

        var (roster, _) = Fleet(world);

        Assert.False(roster.IsLeader);
    }

    [Fact]
    public void Solo_counts_as_leading()
    {
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));

        var (roster, _) = Fleet(world);

        Assert.True(roster.IsLeader);
    }

    [Fact]
    public void Solo_holds_every_duty()
    {
        // There is nobody else to hold it, so a fleet-only job must still get done.
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));

        var (roster, _) = Fleet(world);

        Assert.True(roster.Holds(0));
        Assert.True(roster.Holds(3));
    }

    // ── Gate ──

    [Fact]
    public void A_gate_never_holds_a_solo_run()
    {
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));

        var (_, gate) = Fleet(world);

        Assert.False(gate.Hold(Vector3.Zero, 30f));
        Assert.Equal(GateState.Open, gate.State);
    }

    [Fact]
    public void Holds_until_the_stragglers_arrive()
    {
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Player(2, 1, new Vector3(200, 0, 0)));

        var (_, gate) = Fleet(world);
        gate.Hold(Vector3.Zero, 30f);

        Assert.Equal(GateState.Holding, gate.Tick());
        Assert.Equal(1, gate.Waiting);

        world.Party[1] = Player(2, 1, new Vector3(5, 0, 0));

        Assert.Equal(GateState.Released, gate.Tick());
    }

    [Fact]
    public void One_dead_box_does_not_strand_the_others()
    {
        // A peer that has stopped moving and is not here has crashed or is stuck on scenery.
        // Waiting forever costs the whole fleet the run.
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Player(2, 1, new Vector3(300, 0, 0)));

        var (_, gate) = Fleet(world, stale: 10f);
        gate.Hold(Vector3.Zero, 30f);

        Assert.Equal(GateState.Holding, gate.Tick());

        world.Advance(30);

        Assert.Equal(GateState.Released, gate.Tick());
    }

    [Fact]
    public void A_peer_still_walking_is_waited_for()
    {
        // Movement is the only liveness signal available without anything passing between boxes,
        // so a peer covering ground must never be mistaken for a dead one.
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Player(2, 1, new Vector3(300, 0, 0)));

        var (_, gate) = Fleet(world, stale: 10f);
        gate.Hold(Vector3.Zero, 30f);

        for (var i = 0; i < 12; i++)
        {
            gate.Tick();
            world.Party[1] = Player(2, 1, new Vector3(300 - (i * 10), 0, 0));
            world.Advance(5);
        }

        Assert.Equal(GateState.Holding, gate.State);
    }

    [Fact]
    public void A_fleet_that_dissolves_releases_the_gate()
    {
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Player(2, 1, new Vector3(300, 0, 0)));

        var (_, gate) = Fleet(world);
        gate.Hold(Vector3.Zero, 30f);
        Assert.Equal(GateState.Holding, gate.Tick());

        world.Party.RemoveAt(1);

        Assert.Equal(GateState.Released, gate.Tick());
    }

    [Fact]
    public void Holding_stops_the_character_moving()
    {
        var world = new FakeStepWorld();
        world.Party.Add(Player(1, 0, Vector3.Zero, self: true));
        world.Party.Add(Player(2, 1, new Vector3(300, 0, 0)));

        var (_, gate) = Fleet(world);
        gate.Hold(Vector3.Zero, 30f);
        gate.Tick();

        Assert.True(world.StopMovingCalls > 0);
    }
}
