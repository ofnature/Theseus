using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The ledger's job is not to guess well but to ask rarely and be right about why: a gate is a
/// hypothesis about what opens it, a probe is only spent when that hypothesis's inputs move, and
/// the condition that holds when it finally opens is what teaches the objects involved.
/// </summary>
public class GateLedgerTests
{
    private static readonly Vector3 Frontier = new(3f, 0f, 3f);
    private static readonly Vector3 Beyond = new(5f, 0f, 3f);

    private static WorldModel.Snapshot World(
        int stage = 0,
        Vector3? position = null,
        IReadOnlyList<WorldModel.Recognised>? objects = null,
        IReadOnlyList<GateCandidate>? gates = null)
        => new(
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc),
            position ?? Vector3.Zero,
            (1314, 1, 103),
            stage,
            stage,
            1,
            true,
            false,
            objects ?? [],
            gates ?? [new GateCandidate(Frontier, Beyond)],
            null,
            false);

    private static WorldModel.Recognised At(WorldObjectKind kind, uint dataId, Vector3 position, bool done = false,
        BehaviourClass? cls = null)
        => new(new WorldObject(1, dataId, "thing", position, kind, true), cls, done, Vector3.Distance(position, Vector3.Zero));

    private static Gate Discovered(GateLedger ledger, WorldModel.Snapshot world)
    {
        ledger.Discover(world.Gates, world, world.Scope.Territory);
        ledger.Note(world);
        return Assert.Single(ledger.Gates);
    }

    [Fact]
    public void A_gate_beside_a_pack_is_hypothesised_as_a_combat_gate_and_waits_for_the_pack()
    {
        var ledger = new GateLedger();
        var world = World(objects: [At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f))]);

        var gate = Discovered(ledger, world);

        Assert.Equal(GateKind.Combat, gate.Kind);
        Assert.IsType<GateCondition.HostilesCleared>(gate.Unlock);
        Assert.Equal(GateState.Locked, gate.State);

        // The mesh already says this edge is shut, so nothing is asked about it yet.
        Assert.Empty(ledger.DueForProbe());
    }

    [Fact]
    public void An_idle_gate_is_not_asked_about_again()
    {
        var ledger = new GateLedger();
        var pack = At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f));
        var world = World(objects: [pack]);
        var gate = Discovered(ledger, world);

        // The pack dies: that is the condition's input moving, and it is worth one pathfind.
        ledger.Note(World(objects: [At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f), done: true)]));
        Assert.Single(ledger.DueForProbe());

        ledger.ProbeFailed(gate, "noRouteOnMesh");
        Assert.Empty(ledger.DueForProbe());

        // Nothing has changed since: a dozen frames of the same world cost nothing.
        ledger.Note(World(objects: [At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f), done: true)]));
        ledger.Note(World(objects: [At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f), done: true)]));
        Assert.Empty(ledger.DueForProbe());
    }

    [Fact]
    public void A_gate_with_nothing_around_it_is_hypothesised_against_the_objective_or_the_nearest_unknown()
    {
        var ledger = new GateLedger();
        var world = World(stage: 2, objects:
            [At(WorldObjectKind.Interactable, 2001234, Beyond + new Vector3(2f, 0f, 0f), cls: BehaviourClass.Unknown)]);

        var gate = Discovered(ledger, world);

        Assert.Equal(GateKind.Unknown, gate.Kind);

        var any = Assert.IsType<GateCondition.AnyOf>(gate.Unlock);
        Assert.Contains(any.Alternatives, c => c is GateCondition.ObjectiveReached { Stage: 3 });
        Assert.Contains(any.Alternatives, c => c is GateCondition.Triggered { DataId: 2001234 });
    }

    [Fact]
    public void Opening_names_the_condition_that_holds()
    {
        var ledger = new GateLedger();
        var pack = At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f));
        var gate = Discovered(ledger, World(objects: [pack]));

        // Everything in its region is dead: the hypothesis holds.
        ledger.Note(World(objects: [At(WorldObjectKind.Hostile, 99, Beyond + new Vector3(3f, 0f, 0f), done: true)]));
        var reason = ledger.Opened(gate, "a route exists now (ok).");

        Assert.IsType<GateCondition.HostilesCleared>(reason);
        Assert.Equal(GateKind.Combat, gate.Kind);
        Assert.Equal(GateState.Open, gate.State);
        Assert.Contains("1 of 1 hostiles in its region are ghosts", gate.Evidence);
    }

    [Fact]
    public void A_gate_that_opens_with_no_condition_holding_teaches_nothing()
    {
        var ledger = new GateLedger();
        var gate = Discovered(ledger, World(stage: 0, objects: []));

        var reason = ledger.Opened(gate, "a route exists now (ok).");

        Assert.Null(reason); // nothing the run understands opened it, so nothing is learned from it
        Assert.Equal(GateState.Open, gate.State);
    }

    [Fact]
    public void A_resolved_object_satisfies_the_trigger_and_names_itself_as_the_reason()
    {
        var ledger = new GateLedger();
        var lever = At(WorldObjectKind.Interactable, 2001234, Beyond + new Vector3(2f, 0f, 0f),
            cls: BehaviourClass.Unknown);
        var gate = Discovered(ledger, World(stage: 0, objects: [lever]));

        ledger.Resolved(2001234);
        Assert.Single(ledger.DueForProbe()); // the named object resolved: worth a probe

        var reason = ledger.Opened(gate, "a route exists now (ok).");

        Assert.Equal(GateKind.Trigger, gate.Kind);
        Assert.Equal([2001234u], GateLedger.ObjectsNamedBy(reason!)); // and this is what gets learned
    }

    [Fact]
    public void Standing_beyond_a_gate_passes_it_without_probing()
    {
        var ledger = new GateLedger();
        var gate = Discovered(ledger, World(position: Vector3.Zero));

        ledger.Note(World(position: Beyond + new Vector3(1f, 0f, 0f)));

        Assert.Equal(GateState.Passed, gate.State);
        Assert.Equal(1, ledger.PassedCount);
        Assert.Empty(ledger.DueForProbe());
    }

    [Fact]
    public void A_fleet_peer_beyond_it_opens_the_gate_here()
    {
        var ledger = new GateLedger();
        var gate = Discovered(ledger, World());

        ledger.PeerBeyond(gate.Id);

        Assert.Equal(GateState.Open, gate.State);
        Assert.Contains("peer", gate.Evidence);
        Assert.Empty(ledger.DueForProbe());
    }

    [Fact]
    public void The_same_edge_seen_again_is_the_same_gate()
    {
        var ledger = new GateLedger();

        ledger.Discover(World().Gates, World(), 1314);
        ledger.Discover(World().Gates, World(), 1314);

        Assert.Single(ledger.Gates);
    }

    [Fact]
    public void A_new_run_starts_with_no_gates()
    {
        var ledger = new GateLedger();
        Discovered(ledger, World());

        ledger.Reset();

        Assert.Empty(ledger.Gates);
        Assert.Equal(0, ledger.OpenCount);
    }
}
