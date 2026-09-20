using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Ipc;

namespace Theseus.Tests;

/// <summary>
/// The Ariadne wrapper's failure contract, which the migration depends on more than its success
/// path.
///
/// <para>
/// An absent Ariadne has to read as "nothing happens", reported once — never as a crash inside a
/// run loop and never as a plausible-looking success. The defaults are picked so that a caller who
/// ignores them fails safe: false is "not connected", -1 is "unknown", NaN is not "idle", and an
/// unanswerable grid says so in its result rather than presenting empty arrays as "no walkable
/// ground here".
/// </para>
///
/// <para>
/// The gate names are re-stated here as literals on purpose: a typo in the wrapper then fails these
/// tests instead of silently subscribing to nothing.
/// </para>
/// </summary>
public class AriadneIpcTests
{
    private static readonly Vector3 From = new(1f, 2f, 3f);
    private static readonly Vector3 To = new(4f, 5f, 6f);

    private static Mock<IDalamudPluginInterface> Absent() => new(MockBehavior.Strict);

    [Fact]
    public void An_absent_ariadne_reads_as_nothing_at_all()
    {
        // Strict: every gate call throws, which is what a plugin that is not loaded looks like from
        // this side.
        var warnings = new List<string>();
        var ipc = new AriadneIpc(Absent().Object, warnings.Add);

        Assert.False(ipc.IsConnected);
        Assert.Equal(string.Empty, ipc.CurrentCacheKey);
        Assert.Equal(-1, ipc.ZoneStatus);
        Assert.False(ipc.NavReady);
        Assert.False(ipc.IsPathRunning);
        Assert.False(ipc.IsRunning);
        Assert.Equal(-1, ipc.WaypointCount);
        Assert.Equal(-1, ipc.StallCount);
        Assert.True(float.IsNaN(ipc.RemainingDistance));
        Assert.False(ipc.MoveTo([To]));
        Assert.False(ipc.MoveToWithTolerance([To], fly: false, tolerance: 0.5f));

        // The debug window calls this every frame.
        Assert.NotNull(ipc.Describe());

        ipc.Stop();
        ipc.SetTolerance(0.5f);

        Assert.Single(warnings);
    }

    [Fact]
    public async Task An_absent_ariadne_answers_the_task_shaped_gates_without_throwing()
    {
        var ipc = new AriadneIpc(Absent().Object);

        Assert.Empty(await ipc.FindPath(From, To));

        var detailed = await ipc.PathfindDetailed(From, To);
        Assert.Equal("serviceUnavailable", detailed.Result);
        Assert.Empty(detailed.Waypoints);
        Assert.Null(detailed.Nearest);

        var cells = await ipc.ReachableCells(From, 60f, 3f, float.NaN, float.NaN);
        Assert.Equal("serviceUnavailable", cells.Result);
        Assert.Equal(From, cells.Start);
        Assert.Equal(3f, cells.CellSize);
        Assert.Equal(0, cells.Width);
        Assert.Empty(cells.States);

        Assert.False(await ipc.ReportTraversal(From, To, "direct", true));
    }

    [Fact]
    public void A_gate_that_throws_reads_as_absent_and_warns_once()
    {
        // The other shape of missing: the subscriber resolves and the call itself is what fails
        // (the provider unregistered in between). Same contract — it must not escape, and the log
        // must not fill with one line per poll.
        var gate = new Mock<ICallGateSubscriber<bool>>();
        gate.Setup(s => s.InvokeFunc()).Throws(new InvalidOperationException("no function registered"));

        var pi = new Mock<IDalamudPluginInterface>();
        pi.Setup(p => p.GetIpcSubscriber<bool>("Ariadne.IsConnected")).Returns(gate.Object);

        var warnings = new List<string>();
        var ipc = new AriadneIpc(pi.Object, warnings.Add);

        Assert.False(ipc.IsConnected);
        Assert.False(ipc.IsConnected);
        Assert.False(ipc.IsConnected);

        Assert.Single(warnings);
        Assert.Contains("Ariadne", warnings[0]);
    }

    [Fact]
    public async Task Answers_pass_through_unchanged()
    {
        var connected = new Mock<ICallGateSubscriber<bool>>();
        connected.Setup(s => s.InvokeFunc()).Returns(true);

        var status = new Mock<ICallGateSubscriber<int>>();
        status.Setup(s => s.InvokeFunc()).Returns(3);

        var key = new Mock<ICallGateSubscriber<string>>();
        key.Setup(s => s.InvokeFunc()).Returns("(1042) Mistwake");

        var waypoints = new List<Vector3> { new(10f, 5f, 20f), new(30f, 5f, 40f) };
        var detailed =
            new Mock<ICallGateSubscriber<Vector3, Vector3, bool, Task<(string, List<Vector3>, Vector3?, bool)>>>();
        detailed.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<Vector3>(), It.IsAny<bool>()))
            .ReturnsAsync(("partial", waypoints, new Vector3(50f, 60f, 70f), true));

        var pi = new Mock<IDalamudPluginInterface>();
        pi.Setup(p => p.GetIpcSubscriber<bool>("Ariadne.IsConnected")).Returns(connected.Object);
        pi.Setup(p => p.GetIpcSubscriber<int>("Ariadne.ZoneStatus")).Returns(status.Object);
        pi.Setup(p => p.GetIpcSubscriber<string>("Ariadne.CurrentCacheKey")).Returns(key.Object);
        pi.Setup(p => p.GetIpcSubscriber<Vector3, Vector3, bool, Task<(string, List<Vector3>, Vector3?, bool)>>(
                "Ariadne.Nav.PathfindDetailed"))
            .Returns(detailed.Object);

        var ipc = new AriadneIpc(pi.Object);

        Assert.True(ipc.IsConnected);
        Assert.Equal(3, ipc.ZoneStatus);
        Assert.Equal("(1042) Mistwake", ipc.CurrentCacheKey);

        var answer = await ipc.PathfindDetailed(From, To);
        Assert.Equal("partial", answer.Result);
        Assert.Equal(waypoints, answer.Waypoints);
        Assert.Equal(new Vector3(50f, 60f, 70f), answer.Nearest);
        Assert.True(answer.Partial);
    }

    [Fact]
    public async Task A_reachable_grid_passes_through_with_its_arrays_intact()
    {
        // The ten-element tuple is the one shape that cannot be checked by eye: Dalamud's IPC
        // serializer sees it as nested, so it is pinned here field by field.
        var answer = ("ok", new Vector3(1f, 2f, 3f), new Vector2(-100f, -100f), 3f, 2, 3,
            new[] { 0, 1, 2 }, new[] { 1.5f, 2.5f, 3.5f }, new byte[] { 1, 2, 1 }, false);

        var gate = new Mock<ICallGateSubscriber<Vector3, float, float, float, float,
            Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>>();
        gate.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<float>(), It.IsAny<float>(),
                It.IsAny<float>(), It.IsAny<float>()))
            .ReturnsAsync(answer);

        var pi = new Mock<IDalamudPluginInterface>();
        pi.Setup(p => p.GetIpcSubscriber<Vector3, float, float, float, float,
                Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>(
                "Ariadne.Query.Mesh.ReachableCells"))
            .Returns(gate.Object);

        var cells = await new AriadneIpc(pi.Object).ReachableCells(From, 60f, 3f, float.NaN, float.NaN);

        Assert.Equal("ok", cells.Result);
        Assert.Equal(new Vector3(1f, 2f, 3f), cells.Start);
        Assert.Equal(2, cells.Width);
        Assert.Equal(3, cells.Depth);
        Assert.Equal(new[] { 0, 1, 2 }, cells.Columns);
        Assert.Equal(new byte[] { 1, 2, 1 }, cells.States);
        Assert.False(cells.ReachableOutside);
    }

    [Fact]
    public void The_shared_flags_are_read_by_their_published_names()
    {
        // Names and casing are Ariadne's contract; the setup only matches the exact strings, so a
        // drift here shows up as a false rather than as a silent mismatch in game.
        var pi = new Mock<IDalamudPluginInterface>();
        pi.Setup(p => p.GetOrCreateData<bool[]>("ariadne.NavReady", It.IsAny<Func<bool[]>>()))
            .Returns(new[] { true });
        pi.Setup(p => p.GetOrCreateData<bool[]>("ariadne.PathIsRunning", It.IsAny<Func<bool[]>>()))
            .Returns(new[] { false });

        var ipc = new AriadneIpc(pi.Object);

        Assert.True(ipc.NavReady);
        Assert.False(ipc.IsPathRunning);
    }

    [Fact]
    public void An_unreadable_or_oddly_shaped_flag_reads_as_false()
    {
        // Two shapes worth surviving: an entry created with no elements (never "ready"), and an
        // interface that cannot answer at all.
        var empty = new Mock<IDalamudPluginInterface>();
        empty.Setup(p => p.GetOrCreateData<bool[]>(It.IsAny<string>(), It.IsAny<Func<bool[]>>()))
            .Returns(Array.Empty<bool>());
        Assert.False(new AriadneIpc(empty.Object).NavReady);

        Assert.False(new AriadneIpc(Absent().Object).NavReady);
    }

    [Fact]
    public async Task A_faulted_pathfind_reads_as_no_path()
    {
        // The nav side's gates never throw, but a provider that goes away between the subscribe and
        // the call faults the task instead — and an unobserved fault inside a run loop is not
        // something to find in the field.
        var find = new Mock<ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>>();
        find.Setup(s => s.InvokeFunc(It.IsAny<Vector3>(), It.IsAny<Vector3>(), It.IsAny<bool>()))
            .Returns(Task.FromException<List<Vector3>>(new InvalidOperationException("provider gone")));

        var pi = new Mock<IDalamudPluginInterface>();
        pi.Setup(p => p.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("Ariadne.FindPath"))
            .Returns(find.Object);

        var warnings = new List<string>();
        var ipc = new AriadneIpc(pi.Object, warnings.Add);

        Assert.Empty(await ipc.FindPath(From, To));
        Assert.Single(warnings);
    }
}
