using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Ipc;
using Theseus.Services.Run;

namespace Theseus.Tests;

/// <summary>
/// The mover's contract with a run: a route Ariadne can serve becomes a path Theseus owns and
/// drives; a route it cannot serve still ends in a move, on vnavmesh, with one line in the log; and
/// a route still being computed never reads as "no path" — which is the reading that would stop a
/// run for a destination that was never unreachable.
/// </summary>
public class AriadneMoverTests
{
    private static readonly Vector3 PlayerPosition = new(1f, 0f, 2f);
    private static readonly Vector3 Destination = new(10f, 0f, 20f);

    /// <summary>
    /// The IPC gates are async by contract, so an answer that arrives from a real one lands on a
    /// continuation rather than on the line after the call. Everything else here is deterministic;
    /// these are the two places a test has to wait for that continuation to be picked up.
    /// </summary>
    private static void WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            Thread.Sleep(5);
        }

        Assert.Fail("the route answer never landed");
    }

    /// <summary>
    /// Both plugins' gates, faked behind one interface. The callbacks mirror what the real ones do
    /// to the world: accepting a path makes a navigator report that it is running one.
    /// </summary>
    private sealed class Harness
    {
        private readonly Mock<IDalamudPluginInterface> _pi = new();
        private readonly Mock<ICallGateSubscriber<Vector3, Vector3, bool, Task<(string, List<Vector3>, Vector3?, bool)>>> _pathfind = new();
        private readonly Mock<ICallGateSubscriber<List<Vector3>, bool, float, object>> _ariadneMove = new();
        private readonly Mock<ICallGateSubscriber<bool>> _ariadneRunning = new();
        private readonly Mock<ICallGateSubscriber<int>> _ariadneWaypoints = new();
        private readonly Mock<ICallGateSubscriber<Vector3, bool, bool>> _vnavMove = new();
        private readonly Mock<ICallGateSubscriber<bool>> _vnavPathfinding = new();
        private readonly Mock<ICallGateSubscriber<float, object>> _vnavTolerance = new();

        public readonly List<Vector3> VnavMoveRequests = [];
        public readonly List<(int Waypoints, float Tolerance)> AriadneMoveRequests = [];
        public readonly List<string> Warnings = [];
        public readonly List<float> VnavTolerances = [];

        public int PathfindCalls;
        public int VnavStops;
        public bool NavReady = true;
        public bool AriadnePathRunning;
        public bool VnavPathfinding;
        public int AriadneWaypointCount = 2;
        public DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public Func<Task<(string Result, List<Vector3> Waypoints, Vector3?, bool Partial)>> Answer =
            () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("ok", [], null, false));

        public AriadneMover Mover { get; }

        public Harness()
        {
            _pi.Setup(p => p.GetOrCreateData<bool[]>("ariadne.NavReady", It.IsAny<Func<bool[]>>()))
                .Returns(() => new[] { NavReady });

            _pi.Setup(p => p.GetIpcSubscriber<Vector3, Vector3, bool,
                    Task<(string, List<Vector3>, Vector3?, bool)>>("Ariadne.Nav.PathfindDetailed"))
                .Returns(_pathfind.Object);
            _pathfind.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<Vector3>(), It.IsAny<bool>()))
                .Returns(() =>
                {
                    PathfindCalls++;
                    return Answer();
                });

            _pi.Setup(p => p.GetIpcSubscriber<List<Vector3>, bool, float, object>("Ariadne.Path.MoveToWithTolerance"))
                .Returns(_ariadneMove.Object);
            _ariadneMove.Setup(s => s.InvokeAction(It.IsAny<List<Vector3>>(), It.IsAny<bool>(), It.IsAny<float>()))
                .Callback<List<Vector3>, bool, float>((waypoints, _, tolerance) =>
                {
                    AriadneMoveRequests.Add((waypoints.Count, tolerance));
                    AriadnePathRunning = true;
                });

            _pi.Setup(p => p.GetIpcSubscriber<bool>("Ariadne.Path.IsRunning")).Returns(_ariadneRunning.Object);
            _ariadneRunning.Setup(s => s.InvokeFunc()).Returns(() => AriadnePathRunning);

            // What the mover actually reads: the shared-data flag Ariadne publishes every frame.
            _pi.Setup(p => p.GetOrCreateData<bool[]>("ariadne.PathIsRunning", It.IsAny<Func<bool[]>>()))
                .Returns(() => new[] { AriadnePathRunning });

            _pi.Setup(p => p.GetIpcSubscriber<int>("Ariadne.Path.NumWaypoints")).Returns(_ariadneWaypoints.Object);
            _ariadneWaypoints.Setup(s => s.InvokeFunc()).Returns(() => AriadneWaypointCount);

            _pi.Setup(p => p.GetIpcSubscriber<object>("Ariadne.Path.Stop")).Returns(new Mock<ICallGateSubscriber<object>>().Object);

            _pi.Setup(p => p.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo"))
                .Returns(_vnavMove.Object);
            _vnavMove.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<bool>()))
                .Callback<Vector3, bool>((destination, _) =>
                {
                    VnavMoveRequests.Add(destination);
                    VnavPathfinding = true;
                })
                .Returns(true);

            _pi.Setup(p => p.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress"))
                .Returns(_vnavPathfinding.Object);
            _vnavPathfinding.Setup(s => s.InvokeFunc()).Returns(() => VnavPathfinding);

            _pi.Setup(p => p.GetIpcSubscriber<float, object>("vnavmesh.Path.SetTolerance"))
                .Returns(_vnavTolerance.Object);
            _vnavTolerance.Setup(s => s.InvokeAction(It.IsAny<float>())).Callback<float>(VnavTolerances.Add);

            var vnavStop = new Mock<ICallGateSubscriber<object>>();
            vnavStop.Setup(s => s.InvokeAction()).Callback(() => VnavStops++);
            _pi.Setup(p => p.GetIpcSubscriber<object>("vnavmesh.Path.Stop")).Returns(vnavStop.Object);

            Mover = new AriadneMover(
                new AriadneIpc(_pi.Object),
                new VnavIpc(_pi.Object),
                () => PlayerPosition,
                () => Now,
                Warnings.Add);
        }
    }

    [Fact]
    public void A_computed_route_is_driven_by_ariadne()
    {
        var h = new Harness();
        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("ok", new List<Vector3> { new(5f, 0f, 5f), new(9f, 0f, 18f) }, null, false));

        Assert.True(h.Mover.Begin(Destination));

        // The answer is acted on by the read that asks for movement state — there is no callback.
        Assert.True(h.Mover.IsBusy);

        var move = Assert.Single(h.AriadneMoveRequests);
        Assert.Equal(2, move.Waypoints);
        Assert.Empty(h.VnavMoveRequests);
        Assert.Empty(h.Warnings);

        // Taking a move over from a source that might still be following a path: two movers writing
        // input on one frame is the thing this prevents.
        Assert.Equal(1, h.VnavStops);
    }

    [Fact]
    public void A_route_still_being_computed_reads_as_unknown_not_as_no_path()
    {
        var h = new Harness();
        var answer = new TaskCompletionSource<(string, List<Vector3>, Vector3?, bool)>();
        h.Answer = () => answer.Task;

        h.Mover.Begin(Destination);

        Assert.True(h.Mover.IsBusy);
        Assert.Equal(-1, h.Mover.WaypointCount); // "not yet" must never be counted as unreachable

        answer.SetResult(("ok", new List<Vector3> { new(5f, 0f, 5f) }, null, false));

        WaitFor(() => h.Mover.WaypointCount >= 0);
        Assert.Equal(h.AriadneWaypointCount, h.Mover.WaypointCount); // Ariadne's own count now
    }

    [Fact]
    public void A_zone_ariadne_cannot_route_moves_on_vnavmesh_and_says_so_once()
    {
        var h = new Harness { NavReady = false };

        Assert.True(h.Mover.Begin(Destination));
        Assert.True(h.Mover.Begin(Destination));
        Assert.True(h.Mover.Begin(Destination));

        Assert.Equal(3, h.VnavMoveRequests.Count); // every move still happened
        Assert.Single(h.Warnings);                 // one line, not one per move
        Assert.Equal(0, h.PathfindCalls);          // Ariadne was not even asked
    }

    [Fact]
    public void An_empty_route_falls_back_to_vnavmesh_for_that_move()
    {
        var h = new Harness();
        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("noRouteOnMesh", [], null, false));

        h.Mover.Begin(Destination);
        Assert.True(h.Mover.IsBusy); // pumps the answer; the move is vnavmesh's now

        Assert.Equal([Destination], h.VnavMoveRequests);
        Assert.Empty(h.AriadneMoveRequests);
        Assert.Single(h.Warnings);
        Assert.Contains("noRouteOnMesh", h.Warnings[0]);
    }

    [Fact]
    public void A_mesh_that_is_not_ready_is_retried_before_the_fallback()
    {
        var h = new Harness();
        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("meshNotReady", [], null, false));

        h.Mover.Begin(Destination);
        Assert.True(h.Mover.IsBusy); // a retry is scheduled: this is a wait, not an answer
        Assert.Equal(1, h.PathfindCalls);
        Assert.Empty(h.VnavMoveRequests);

        h.Now = h.Now.AddSeconds(2);
        Assert.True(h.Mover.IsBusy);
        Assert.Equal(2, h.PathfindCalls);
        Assert.Empty(h.VnavMoveRequests);

        for (var i = 0; i < 6; i++)
        {
            h.Now = h.Now.AddSeconds(2);
            _ = h.Mover.IsBusy;
        }

        Assert.Equal(1 + 1 + 4, h.PathfindCalls); // the initial ask, then the retry budget, then no more asking
        Assert.Equal([Destination], h.VnavMoveRequests);
        Assert.Single(h.Warnings);
    }

    [Fact]
    public void Stopping_forgets_a_route_that_is_still_in_flight()
    {
        var h = new Harness();
        var answer = new TaskCompletionSource<(string, List<Vector3>, Vector3?, bool)>();
        h.Answer = () => answer.Task;

        h.Mover.Begin(Destination);
        h.Mover.Stop();
        answer.SetResult(("ok", new List<Vector3> { new(5f, 0f, 5f) }, null, false));

        // Keep checking across the window the continuation would arrive in: the answer is dropped,
        // not merely not-yet-read.
        var deadline = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < deadline)
        {
            Assert.False(h.Mover.IsBusy);
            Thread.Sleep(5);
        }

        Assert.Empty(h.AriadneMoveRequests); // the late answer drove nothing
    }

    [Fact]
    public void The_runs_tolerance_reaches_whichever_source_drives()
    {
        var h = new Harness();
        h.Mover.SetTolerance(0.4f);

        Assert.Equal([0.4f], h.VnavTolerances); // a fallback move executes there

        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("ok", new List<Vector3> { new(1f, 0f, 1f) }, null, false));
        h.Mover.Begin(Destination);
        _ = h.Mover.IsBusy;

        Assert.Equal(0.4f, Assert.Single(h.AriadneMoveRequests).Tolerance);
    }
}
