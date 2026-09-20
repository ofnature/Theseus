using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Duty;
using Theseus.Services.Frontier;
using Theseus.Services.Ipc;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The shadow watches and writes; it never drives. These pin the two kinds of learning it can do on
/// its own — an object that leaves the scan where it stood, and a gate that opens — and that it
/// leaves the character exactly where it found it.
/// </summary>
public class ShadowObserverTests
{
    private const uint Lever = 2001234;

    private sealed class Harness : IDisposable
    {
        private readonly Mock<IDalamudPluginInterface> _pi = new();
        private readonly Mock<ICallGateSubscriber<Vector3, float, float, float, float,
            Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>> _cells = new();
        private readonly Mock<ICallGateSubscriber<Vector3, Vector3, bool, Task<(string, List<Vector3>, Vector3?, bool)>>> _pathfind = new();

        public FakeStepWorld World { get; } = new();

        public int Stage;

        public bool InDuty = true;

        public readonly List<string> Log = [];

        public readonly string GapPath =
            Path.Combine(Path.GetTempPath(), $"theseus-shadow-gaps-{Guid.NewGuid():N}.jsonl");

        public readonly string TaxonomyPath =
            Path.Combine(Path.GetTempPath(), $"theseus-shadow-taxonomy-{Guid.NewGuid():N}.json");

        public (string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth,
            int[] Columns, float[] Heights, byte[] States, bool ReachableOutside) Cells =
            ("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4, [5, 6], [0f, 0f], [1, 2], false);

        public (string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial) Answer =
            ("ok", [new Vector3(5f, 0f, 3f)], null, false);

        public Taxonomy Taxonomy { get; } = new();

        public GateLedger Ledger { get; } = new();

        public GapLog Gaps { get; }

        public ShadowObserver Shadow { get; }

        public Harness()
        {
            Gaps = new GapLog(GapPath);

            _pi.Setup(p => p.GetIpcSubscriber<Vector3, float, float, float, float,
                    Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>(
                    "Ariadne.Query.Mesh.ReachableCells"))
                .Returns(_cells.Object);
            _cells.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<float>(), It.IsAny<float>(),
                    It.IsAny<float>(), It.IsAny<float>()))
                .Returns(() => Task.FromResult(Cells));

            _pi.Setup(p => p.GetIpcSubscriber<Vector3, Vector3, bool, Task<(string, List<Vector3>, Vector3?, bool)>>(
                    "Ariadne.Nav.PathfindDetailed"))
                .Returns(_pathfind.Object);
            _pathfind.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<Vector3>(), It.IsAny<bool>()))
                .Returns(() => Task.FromResult(Answer));

            var key = new Mock<ICallGateSubscriber<string>>();
            key.Setup(s => s.InvokeFunc()).Returns("(1314) Mistwake");
            _pi.Setup(p => p.GetIpcSubscriber<string>("Ariadne.CurrentCacheKey")).Returns(key.Object);

            var ariadne = new AriadneIpc(_pi.Object);
            var frontier = new ReachableFrontier(ariadne, () => World.PlayerPosition);
            var reader = new Reader(() => Stage);

            Shadow = new ShadowObserver(World, reader, Taxonomy, TaxonomyPath, new GhostCache(), frontier,
                Ledger, Gaps, ariadne, () => InDuty, () => "1314:103:0", Log.Add);
        }

        public void Dispose()
        {
            foreach (var path in new[] { GapPath, TaxonomyPath })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private sealed class Reader(Func<int> stage) : IObjectiveReader
        {
            public int CurrentIndex => stage();

            public DutyObjectiveSnapshot Read() => new([], stage(), 0, Available: true);
        }
    }

    private static WorldObject At(WorldObjectKind kind, uint dataId, Vector3 position, ulong id = 1)
        => new(id, dataId, "a thing", position, kind, true);

    [Fact]
    public void An_interactable_that_leaves_the_scan_where_it_stood_is_learned_as_a_pickup()
    {
        using var h = new Harness();
        h.World.PlayerPosition = Vector3.Zero;
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f)));

        h.Shadow.Tick();
        Assert.Equal(0, h.Taxonomy.Confirmations(Lever));

        // Taken: gone from the scan, with the character still standing next to where it was.
        h.World.Nearby.Clear();
        h.Shadow.Tick();

        Assert.Equal(1, h.Taxonomy.Confirmations(Lever));
        Assert.Equal(BehaviourClass.Unknown, h.Taxonomy.Classify(Lever)); // one look is still a hypothesis
        Assert.Contains(h.Log, line => line.Contains("learned as a pickup"));
    }

    [Fact]
    public void Walking_away_is_not_a_pickup()
    {
        using var h = new Harness();
        h.World.PlayerPosition = Vector3.Zero;
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f)));
        h.Shadow.Tick();

        // Out of the scan because the character left the room, not because anything was taken.
        h.World.PlayerPosition = new Vector3(300f, 0f, 0f);
        h.Shadow.Tick();

        Assert.Equal(0, h.Taxonomy.Confirmations(Lever));
    }

    [Fact]
    public void An_object_nothing_can_name_goes_into_the_gap_log_once()
    {
        using var h = new Harness();
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f)));

        h.Shadow.Tick();
        h.Shadow.Tick();
        h.Shadow.Tick();

        Assert.Single(File.ReadAllLines(h.GapPath));
        Assert.Contains("UnknownInteractable", File.ReadAllLines(h.GapPath)[0]);
    }

    [Fact]
    public void A_gate_from_the_grid_is_probed_when_its_condition_moves_and_opens_the_way()
    {
        using var h = new Harness();
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(6f, 0f, 3f)));
        h.Stage = 0;

        // Two ticks: the first asks the frontier for a grid, the second sees it, and the cut-off
        // surface beside reachable ground becomes a gate.
        h.Shadow.Tick();
        h.Shadow.Tick();

        var gate = Assert.Single(h.Shadow.Gates);
        Assert.Equal(new Vector3(5f, 0f, 3f), gate.Beyond);
        Assert.Empty(h.Ledger.DueForProbe()); // shut by the mesh's own word: nothing to ask yet

        // The condition that holds is the named object resolving — which is what the gate then
        // teaches, and the only thing a passive run can be taught from.
        h.Ledger.Resolved(Lever);
        h.Shadow.Tick(); // the probe goes out
        h.Shadow.Tick(); // and its answer lands

        Assert.Equal(GateState.Open, gate.State);
        Assert.Equal(1, h.Taxonomy.Confirmations(Lever));
    }

    [Fact]
    public void A_gate_that_stays_shut_is_not_probed_again_until_something_moves()
    {
        using var h = new Harness();
        h.Answer = ("noRouteOnMesh", [], null, false);
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(6f, 0f, 3f)));

        h.Shadow.Tick(); // asks the frontier for a grid
        h.Shadow.Tick(); // sees it: the cut-off beside reachable ground is a gate
        var gate = Assert.Single(h.Shadow.Gates);

        h.Ledger.Resolved(Lever); // the condition moves
        h.Shadow.Tick(); // the probe goes out
        h.Shadow.Tick(); // and comes back still shut

        Assert.Equal(GateState.Locked, gate.State);
        Assert.Equal(1, gate.Probes);

        // Nothing has moved since: the edge is not asked about again for free.
        h.Shadow.Tick();
        h.Shadow.Tick();
        Assert.Equal(1, gate.Probes);
    }

    [Fact]
    public void The_shadow_never_moves_the_character()
    {
        using var h = new Harness();
        h.World.PlayerPosition = Vector3.Zero;
        h.World.Nearby.Add(At(WorldObjectKind.Interactable, Lever, new Vector3(4f, 0f, 0f)));

        for (var i = 0; i < 5; i++)
            h.Shadow.Tick();

        Assert.Empty(h.World.MoveRequests);
        Assert.Empty(h.World.InteractedObjects);
        Assert.Empty(h.World.ForwardMovement);
        Assert.Equal(0, h.World.LeaveDutyCalls);
    }

    [Fact]
    public void Leaving_the_duty_writes_what_was_learned_and_stops_watching()
    {
        using var h = new Harness();

        h.Shadow.Tick();
        h.Shadow.Tick();

        h.InDuty = false;
        h.Shadow.Tick();

        Assert.True(File.Exists(h.TaxonomyPath)); // saved on the way out
        Assert.Contains("idle", h.Shadow.Describe());
    }
}
