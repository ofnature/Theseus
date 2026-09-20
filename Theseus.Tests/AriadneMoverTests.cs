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
        public readonly List<string> Log = [];

        /// <summary>The fallback warnings — what a run is owed when its source could not route a move.</summary>
        public IEnumerable<string> Fallbacks => Log.Where(line => line.Contains("using vnavmesh"));

        /// <summary>The transit's own narration: where the route stopped, and where it landed.</summary>
        public IEnumerable<string> Transits => Log.Where(line => line.Contains("walking off the edge") || line.Contains("Transit landed"));
        public readonly List<float> VnavTolerances = [];
        public readonly List<bool> ForwardHolds = [];
        public readonly List<(Vector3 From, Vector3 To, string Mode, bool Success)> Reported = [];

        public Vector3 Position = PlayerPosition;
        public int PathfindCalls;
        public int VnavStops;
        public bool NavReady = true;
        public bool AriadnePathRunning;
        public bool VnavPathfinding;
        public int AriadneWaypointCount = 2;
        public DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public Func<Task<(string Result, List<Vector3> Waypoints, Vector3?, bool Partial)>> Answer =
            () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("ok", [], null, false));

        private readonly Queue<Func<Task<(string Result, List<Vector3> Waypoints, Vector3?, bool Partial)>>> _scripted = [];

        /// <summary>
        /// Answers handed out in order, before <see cref="Answer"/> takes over. A transit asks more
        /// than once — the goal, then the edge — and the two answers are rarely the same shape.
        /// </summary>
        public void Script(params Func<Task<(string Result, List<Vector3> Waypoints, Vector3?, bool Partial)>>[] answers)
        {
            foreach (var answer in answers)
                _scripted.Enqueue(answer);
        }

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
                    return _scripted.Count > 0 ? _scripted.Dequeue()() : Answer();
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

            var report = new Mock<ICallGateSubscriber<Vector3, Vector3, string, bool, Task<bool>>>();
            report.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<Vector3>(), It.IsAny<string>(), It.IsAny<bool>()))
                .Callback<Vector3, Vector3, string, bool>((from, to, mode, success) => Reported.Add((from, to, mode, success)))
                .ReturnsAsync(true);
            _pi.Setup(p => p.GetIpcSubscriber<Vector3, Vector3, string, bool, Task<bool>>("Ariadne.ReportTraversal"))
                .Returns(report.Object);

            Mover = new AriadneMover(
                new AriadneIpc(_pi.Object),
                new VnavIpc(_pi.Object),
                () => Position,
                held => ForwardHolds.Add(held),
                () => Now,
                Log.Add);
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
        Assert.Empty(h.Fallbacks);

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
        Assert.Single(h.Fallbacks);                // one line, not one per move
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
        Assert.Single(h.Fallbacks);
        Assert.Contains("noRouteOnMesh", h.Fallbacks.First());
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
        Assert.Single(h.Fallbacks);
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

    // ── Transits: a route that ends at a drop or a rail ──

    /// <summary>
    /// The Xelphatol one-way drop, exactly as the mesh answers it (probed 2026-09-20 through
    /// Mnemosyne's CLI): <c>noRouteOnMesh partial waypoints=2</c> with the nearest reachable ground
    /// 30 m above the landing and 0.7 y across from it.
    /// </summary>
    private static readonly Vector3 XelphatolLip = new(183.5f, 86.8f, -68.2f);
    private static readonly Vector3 XelphatolLanding = new(182.8f, 56.8f, -68.0f);

    private static Func<Task<(string Result, List<Vector3> Waypoints, Vector3?, bool Partial)>> EndsShortAt(
        Vector3 edge, params Vector3[] waypoints)
        => () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("noRouteOnMesh", [.. waypoints], edge, true));

    [Fact]
    public void A_route_that_ends_at_a_drop_is_walked_to_the_edge_and_pushed()
    {
        var h = new Harness { Position = XelphatolLip };
        h.Answer = EndsShortAt(XelphatolLip, XelphatolLip);

        Assert.True(h.Mover.Begin(XelphatolLanding));
        Assert.True(h.Mover.IsBusy); // the partial route is being followed to the edge
        Assert.Equal(TransitPhase.Approaching, h.Mover.Transit);
        Assert.Single(h.AriadneMoveRequests);
        Assert.Empty(h.ForwardHolds); // nothing pushed while there is still ground to walk

        // The path stops where the mesh stops: the character is standing at the lip.
        h.AriadnePathRunning = false;
        Assert.True(h.Mover.IsBusy);
        Assert.Equal(TransitPhase.Pushing, h.Mover.Transit);
        Assert.Equal([true], h.ForwardHolds);
        Assert.Empty(h.Fallbacks); // Ariadne could not route it, and that is not a warning — it is the rule
        Assert.Single(h.Transits); // and it says so in the log, with where the route stopped
    }

    [Fact]
    public void An_empty_answer_at_a_drop_walks_to_the_edge_before_pushing()
    {
        var h = new Harness { Position = new Vector3(190f, 86.8f, -68.2f) }; // 6.5 y from the lip
        h.Script(
            () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("noRouteOnMesh", [], XelphatolLip, false)),
            () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(("ok", [XelphatolLip], null, false)));

        h.Mover.Begin(XelphatolLanding);
        Assert.True(h.Mover.IsBusy); // the goal's answer: nothing to follow, so the edge is asked for
        Assert.True(h.Mover.IsBusy); // the edge's own answer: followed

        Assert.Equal(2, h.PathfindCalls); // the goal, then the edge itself
        Assert.Equal(TransitPhase.Approaching, h.Mover.Transit);

        var approach = Assert.Single(h.AriadneMoveRequests);
        Assert.Equal(1, approach.Waypoints); // the route to the edge is what is being followed

        // Walking to the edge, then the path ends there.
        h.Position = XelphatolLip;
        h.AriadnePathRunning = false;
        Assert.True(h.Mover.IsBusy);

        Assert.Equal([true], h.ForwardHolds);
    }

    [Fact]
    public void The_push_lands_and_the_run_re_paths_from_the_far_side()
    {
        var h = new Harness { Position = XelphatolLip };
        h.Answer = EndsShortAt(XelphatolLip, XelphatolLip);

        h.Mover.Begin(XelphatolLanding);
        _ = h.Mover.IsBusy;
        h.AriadnePathRunning = false;
        _ = h.Mover.IsBusy; // the push begins

        // The game carries the character down and off while the hold is on.
        h.Position = XelphatolLanding;
        h.Now = h.Now.AddSeconds(2);
        Assert.True(h.Mover.IsBusy); // push over, waiting to land
        Assert.Equal([true, false], h.ForwardHolds); // released — never left held

        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("ok", [XelphatolLanding + new Vector3(0f, 0f, 4f)], null, false));
        h.Now = h.Now.AddSeconds(0.6);
        Assert.True(h.Mover.IsBusy); // landed, re-pathed, following the new route

        Assert.Equal(TransitPhase.None, h.Mover.Transit);
        Assert.Equal(2, h.AriadneMoveRequests.Count); // the partial route, then the re-path's

        var reported = Assert.Single(h.Reported);
        Assert.Equal(("direct", true), (reported.Mode, reported.Success));
        Assert.Equal(XelphatolLip, reported.From);
        Assert.Equal(XelphatolLanding, reported.To);
    }

    [Fact]
    public void An_off_mesh_goal_is_not_a_transit()
    {
        // The r2d3 probe's own answer for a goal in the void: nothing to land on. Walking to the
        // nearest point and stepping off would be opening the run to a fall it cannot recover from.
        var h = new Harness { Position = XelphatolLip };
        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("targetOffMesh", [], new Vector3(253.8f, 66.5f, -52.2f), false));

        Assert.True(h.Mover.Begin(new Vector3(260f, 60f, -40f)));
        Assert.True(h.Mover.IsBusy);

        Assert.Equal(TransitPhase.None, h.Mover.Transit);
        Assert.Equal([new Vector3(260f, 60f, -40f)], h.VnavMoveRequests);
        Assert.Empty(h.ForwardHolds);
    }

    [Fact]
    public void A_goal_above_the_reachable_edge_is_not_a_transit()
    {
        // Forward movement cannot climb. A goal more than a step above the nearest reachable ground
        // is a ledge, a wall or a locked door — not a drop, and not worth an attempt.
        var h = new Harness { Position = XelphatolLip };
        h.Answer = () => Task.FromResult<(string, List<Vector3>, Vector3?, bool)>(
            ("noRouteOnMesh", [], XelphatolLip, false));

        h.Mover.Begin(XelphatolLip + new Vector3(0.5f, 6f, 0f));
        _ = h.Mover.IsBusy;

        Assert.Equal(TransitPhase.None, h.Mover.Transit);
        Assert.Empty(h.ForwardHolds);
        Assert.Single(h.VnavMoveRequests);
    }

    [Fact]
    public void The_rail_the_migration_note_measured_is_recognised()
    {
        // Mistwake x6d9, probed the same day: from the boarding platform, the landing is 4.6 m below
        // the nearest reachable ground and two yalms across from it.
        var boarding = new Vector3(104.18f, 38.06f, 275.93f);
        var edge = new Vector3(93.2f, 42.1f, 283.8f);
        var landing = new Vector3(91.2f, 37.5f, 283.8f);

        var h = new Harness { Position = boarding };
        h.Answer = EndsShortAt(edge, edge);

        h.Mover.Begin(landing);
        _ = h.Mover.IsBusy;

        Assert.Equal(TransitPhase.Approaching, h.Mover.Transit);

        // Off the platform, along the rail's approach, and at the lip.
        h.Position = edge;
        h.AriadnePathRunning = false;
        _ = h.Mover.IsBusy;

        Assert.Equal(TransitPhase.Pushing, h.Mover.Transit);
        Assert.Equal([true], h.ForwardHolds);
    }

    [Fact]
    public void Transits_are_bounded_and_then_the_destination_is_given_up()
    {
        // A route that keeps answering the same way: two attempts — the drop, then a second look
        // from wherever it ended — and then the other source. Never a run that steps off forever.
        var h = new Harness { Position = XelphatolLip };
        h.Answer = EndsShortAt(XelphatolLip, XelphatolLip);

        h.Mover.Begin(XelphatolLanding);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            _ = h.Mover.IsBusy; // following the partial route
            h.AriadnePathRunning = false;
            _ = h.Mover.IsBusy; // pushing
            h.Now = h.Now.AddSeconds(2);
            _ = h.Mover.IsBusy; // released, settling
            h.Now = h.Now.AddSeconds(0.6);
            _ = h.Mover.IsBusy; // landed where it started, re-pathing
        }

        Assert.Equal([true, false, true, false], h.ForwardHolds);
        Assert.Equal(TransitPhase.None, h.Mover.Transit);
        Assert.Equal([XelphatolLanding], h.VnavMoveRequests); // the fallback, once
        Assert.Single(h.Fallbacks);
        Assert.Empty(h.Reported); // nothing was crossed, so nothing is claimed
    }

    [Fact]
    public void A_push_that_moves_nothing_is_abandoned_early()
    {
        var h = new Harness { Position = XelphatolLip };
        h.Answer = EndsShortAt(XelphatolLip, XelphatolLip);

        h.Mover.Begin(XelphatolLanding);
        _ = h.Mover.IsBusy;
        h.AriadnePathRunning = false;
        _ = h.Mover.IsBusy; // pushing, forward held

        // Past the no-movement grace but well inside the push: a hold against a wall is released
        // rather than spent.
        h.Now = h.Now.AddSeconds(0.8);
        _ = h.Mover.IsBusy;

        Assert.Equal([true, false], h.ForwardHolds);
    }

    [Fact]
    public void Stopping_releases_the_push()
    {
        var h = new Harness { Position = XelphatolLip };
        h.Answer = EndsShortAt(XelphatolLip, XelphatolLip);

        h.Mover.Begin(XelphatolLanding);
        _ = h.Mover.IsBusy;
        h.AriadnePathRunning = false;
        _ = h.Mover.IsBusy; // pushing

        h.Mover.Stop();

        Assert.Equal([true, false], h.ForwardHolds);
        Assert.Equal(TransitPhase.None, h.Mover.Transit);
        Assert.False(h.Mover.IsBusy);
    }
}
