using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Ipc;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The frontier is a pipe round trip wearing a property, so its contract is about how rarely it
/// asks, what it does with an answer it cannot use, and that re-asking does not lose what the run
/// has already seen.
/// </summary>
public class ReachableFrontierTests
{
    private sealed class Harness
    {
        private readonly Mock<IDalamudPluginInterface> _pi = new();
        private readonly Mock<ICallGateSubscriber<Vector3, float, float, float, float,
            Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>> _gate = new();

        public Vector3 Position = Vector3.Zero;
        public int Calls;
        public readonly List<string> Log = [];
        public readonly List<(Vector3 From, float Radius, float CellSize)> Asked = [];

        public (string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth,
            int[] Columns, float[] Heights, byte[] States, bool ReachableOutside) Answer =
            ("ok", Vector3.Zero, Vector2.Zero, 2f, 4, 4, [5], [0f], [1], false);

        public ReachableFrontier Frontier { get; }

        public Harness()
        {
            _pi.Setup(p => p.GetIpcSubscriber<Vector3, float, float, float, float,
                    Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>(
                    "Ariadne.Query.Mesh.ReachableCells"))
                .Returns(_gate.Object);

            _gate.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<float>(), It.IsAny<float>(),
                    It.IsAny<float>(), It.IsAny<float>()))
                .Callback<Vector3, float, float, float, float>((from, radius, cellSize, _, _) =>
                {
                    Calls++;
                    Asked.Add((from, radius, cellSize));
                })
                .Returns(() => Task.FromResult(Answer));

            Frontier = new ReachableFrontier(new AriadneIpc(_pi.Object), () => Position, Log.Add);
        }
    }

    /// <summary>Two refreshes: one to ask, one to pick the answer up — the model asks on the frame it needs it.</summary>
    private static ReachableGrid? Settle(Harness h)
    {
        h.Frontier.Refresh();
        return h.Frontier.Refresh();
    }

    [Fact]
    public void The_first_refresh_asks_once_and_walking_a_little_does_not_ask_again()
    {
        var h = new Harness();

        var grid = Settle(h);

        Assert.NotNull(grid);
        Assert.Equal("ok", h.Frontier.LastResult);
        Assert.Equal(1, h.Calls);

        // A room's worth of walking: still inside the window's middle, so nothing to re-ask.
        h.Position = new Vector3(10f, 0f, 10f);
        h.Frontier.Refresh();
        Assert.Equal(1, h.Calls);

        // Half the radius crossed: the window has to be re-centred.
        h.Position = new Vector3(70f, 0f, 0f);
        h.Frontier.Refresh();
        h.Frontier.Refresh();
        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public void The_query_carries_the_radius_and_cell_size_the_solver_plans_around()
    {
        var h = new Harness();

        Settle(h);

        var asked = Assert.Single(h.Asked);
        Assert.Equal(ReachableFrontier.Radius, asked.Radius);
        Assert.Equal(ReachableFrontier.CellSize, asked.CellSize);
        Assert.Equal(Vector3.Zero, asked.From);
    }

    [Fact]
    public void A_world_that_changed_under_the_grid_asks_again_without_walking()
    {
        var h = new Harness();
        Settle(h);
        Assert.Equal(1, h.Calls);

        h.Frontier.Invalidate();
        h.Frontier.Refresh();
        h.Frontier.Refresh();

        Assert.Equal(2, h.Calls);
    }

    [Fact]
    public void An_answer_it_cannot_use_keeps_the_last_good_grid_and_says_so_once()
    {
        var h = new Harness();
        var first = Settle(h);
        Assert.NotNull(first);

        // The zone starts loading its volume, or the service goes away mid-run.
        h.Answer = ("meshNotReady", Vector3.Zero, Vector2.Zero, 2f, 4, 4, [], [], [], false);
        h.Position = new Vector3(70f, 0f, 0f);
        Settle(h);

        Assert.Same(first, h.Frontier.Grid); // an unanswerable query is not "no ground here"
        Assert.Equal("meshNotReady", h.Frontier.LastResult);
        Assert.Single(h.Log);

        // And it keeps not asking twice about it.
        h.Position = new Vector3(150f, 0f, 0f);
        Settle(h);
        Assert.Single(h.Log);
    }

    [Fact]
    public void An_absent_ariadne_leaves_no_frontier_at_all()
    {
        var pi = new Mock<IDalamudPluginInterface>(MockBehavior.Strict);
        var log = new List<string>();
        var frontier = new ReachableFrontier(new AriadneIpc(pi.Object), () => Vector3.Zero, log.Add);

        frontier.Refresh();
        Assert.Null(frontier.Refresh());

        Assert.Equal("serviceUnavailable", frontier.LastResult);
        Assert.Single(log);
    }

    [Fact]
    public void What_has_been_seen_survives_a_re_ask_from_somewhere_else()
    {
        var h = new Harness();
        var first = Settle(h)!;

        // Standing on the one surface in the grid: seen.
        first.MarkExplored(new Vector3(3f, 0f, 3f), sightRadius: 1f);
        Assert.Equal(0, first.UnexploredCount);

        // Ask again from a window whose origin is four yalms away, so the same world surface lands
        // in a different column of a differently-shaped answer: origin (-4, -4) puts (3, 0, 3) at
        // column 15 rather than 5.
        h.Position = new Vector3(70f, 0f, 0f);
        h.Answer = ("ok", h.Position, new Vector2(-4f, -4f), 2f, 4, 4, [15], [0f], [1], false);
        var second = Settle(h)!;

        Assert.NotSame(first, second);
        Assert.Equal(1, second.SurfaceCount);
        Assert.Equal(0, second.UnexploredCount); // the same ground, seen, whatever column it is in
    }
}
