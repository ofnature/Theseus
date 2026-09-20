using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>
/// Ariadne, wrapped — the second pathfinding source, answered out of process by Mnemosyne.
///
/// <para>
/// The reason to prefer it over <see cref="VnavIpc"/> is timing. vnavmesh has to build a mesh in
/// game before it can route anywhere (7–15 s, once per zone); Mnemosyne keeps meshes in its own
/// store, so "this zone can be routed" turns true something like a tenth of a second after
/// zone-in. <see cref="NavReady"/> is that answer, and it has no <c>Nav.IsReady</c> twin — the
/// gate-shaped readiness check is <see cref="ZoneStatus"/> 2 or 3, which the flag mirrors.
/// </para>
///
/// <para>
/// Every call fails open, exactly as <see cref="VnavIpc"/> does: an absent Ariadne reads as
/// "nothing happens", reported once, never thrown inside a run loop. The two shared-data flags are
/// array reads rather than gate calls, which is what makes them cheap enough to poll every frame.
/// </para>
///
/// <para>
/// Names and shapes mirror Ariadne's own registration; the contract is that repo's
/// <c>docs/consumer-ipc.md</c>. Nothing is reshaped here to carry extra data — a gate that needs to
/// answer something new is extended on the nav side first, and the call site follows the doc.
/// </para>
/// </summary>
public sealed class AriadneIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<bool>? _isConnected;
    private ICallGateSubscriber<string>? _currentCacheKey;
    private ICallGateSubscriber<int>? _zoneStatus;
    private ICallGateSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>? _findPath;
    private ICallGateSubscriber<Vector3, Vector3, bool,
        Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)>>? _pathfindDetailed;
    private ICallGateSubscriber<Vector3, float, float, float, float,
        Task<(string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth,
            int[] Columns, float[] Heights, byte[] States, bool ReachableOutside)>>? _reachableCells;
    private ICallGateSubscriber<Vector3, Vector3, string, bool, Task<bool>>? _reportTraversal;
    private ICallGateSubscriber<List<Vector3>, bool, object>? _moveTo;
    private ICallGateSubscriber<List<Vector3>, bool, float, object>? _moveToWithTolerance;
    private ICallGateSubscriber<object>? _stop;
    private ICallGateSubscriber<bool>? _isRunning;
    private ICallGateSubscriber<int>? _numWaypoints;
    private ICallGateSubscriber<int>? _stallCount;
    private ICallGateSubscriber<float, object>? _setTolerance;
    private ICallGateSubscriber<float>? _remainingDistance;

    private bool _warned;

    public AriadneIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    // ── Presence and readiness ──

    /// <summary>The pipe to Mnemosyne is up. The cheapest presence probe there is.</summary>
    public bool IsConnected => Try(() =>
        (_isConnected ??= _pluginInterface.GetIpcSubscriber<bool>("Ariadne.IsConnected")).InvokeFunc());

    /// <summary>This zone's mesh identity, or empty while the layout is still loading.</summary>
    public string CurrentCacheKey => Try(
        () => (_currentCacheKey ??= _pluginInterface.GetIpcSubscriber<string>("Ariadne.CurrentCacheKey")).InvokeFunc(),
        string.Empty);

    /// <summary>
    /// 0 NotReady · 1 MnemosyneUnavailable · 2 LocalCurrent · 3 MnemosyneCached · 4 Missing — or
    /// -1, which is this wrapper's own "nothing answered" and never a value the gate produces.
    ///
    /// <para>First entry into a zone nobody has visited yet is the slow case: 4 → a capture → 3.</para>
    /// </summary>
    public int ZoneStatus => Try(
        () => (_zoneStatus ??= _pluginInterface.GetIpcSubscriber<int>("Ariadne.ZoneStatus")).InvokeFunc(),
        -1);

    /// <summary>
    /// This zone can be routed on right now, per the flag Ariadne publishes every frame (it mirrors
    /// <see cref="IsConnected"/> and a <see cref="ZoneStatus"/> of 2 or 3).
    ///
    /// <para>
    /// Read this rather than polling the gates: it is a plain array read, and it is the readiness
    /// theseus's run loop should gate on — not vnavmesh's build.
    /// </para>
    /// </summary>
    public bool NavReady => Flag("ariadne.NavReady");

    /// <summary>
    /// Someone is driving the character, per the shared-data flag.
    ///
    /// <para>
    /// Prefer this over the gate-shaped <c>vnav.PathIsRunning</c> while vnavmesh is loaded:
    /// vnavmesh writes false into its own tag every idle frame, so that one is only trustworthy
    /// when nothing else publishes it. Plain array read, safe every frame.
    /// </para>
    /// </summary>
    public bool IsPathRunning => Flag("ariadne.PathIsRunning");

    // ── Pathfinding ──

    /// <summary>
    /// A route to <paramref name="to"/>, or empty — which covers both "no route" and "could not
    /// ask", the same way the gate itself does. Use <see cref="PathfindDetailed"/> when the
    /// difference matters.
    /// </summary>
    public Task<List<Vector3>> FindPath(Vector3 from, Vector3 to, bool fly = false)
        => TryAsync(
            () => (_findPath ??=
                    _pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, Task<List<Vector3>>>("Ariadne.FindPath"))
                .InvokeFunc(from, to, fly),
            []);

    /// <summary>
    /// The classified answer: <c>ok</c>, <c>targetOffMesh</c>, <c>startOffMesh</c>,
    /// <c>noRouteOnMesh</c>, <c>budgetExhausted</c>, <c>meshNotReady</c>,
    /// <c>serviceUnavailable</c> or <c>failed</c> — with the waypoints, the nearest reachable point
    /// when the route stops short, and whether it did.
    ///
    /// <para>
    /// This is the gate to prefer for anything that has to tell "unreachable" from "not yet":
    /// <c>meshNotReady</c> is a wait, a <c>nearest</c> a few yalms from the goal with a drop
    /// between is a transit rather than a failure, and a route that ends short of an L-shaped
    /// corridor is not the same as a route that ends at a wall. An absent or faulted Ariadne
    /// answers <c>serviceUnavailable</c>, which is the nav side's own word for "nothing answered
    /// the pipe".
    /// </para>
    /// </summary>
    public Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)> PathfindDetailed(
        Vector3 from, Vector3 to, bool fly = false)
        => TryAsync(
            () => (_pathfindDetailed ??= _pluginInterface.GetIpcSubscriber<Vector3, Vector3, bool,
                        Task<(string, List<Vector3>, Vector3?, bool)>>("Ariadne.Nav.PathfindDetailed"))
                .InvokeFunc(from, to, fly),
            ("serviceUnavailable", [], null, false));

    /// <summary>
    /// Which walkable ground is reachable from <paramref name="from"/>, as a world-aligned grid of
    /// stacked surfaces — the exploration query, and the reason a solver can tell "there is more of
    /// this room past that corner" from "there is not".
    ///
    /// <para>
    /// The ten-element tuple is the documented shape and is passed through untouched: Dalamud's IPC
    /// serializer sees it as nested, the nav side owns any extension of it, and reshaping it here
    /// would be the first suspect every time a call misbehaves rather than answers. Result uses the
    /// same vocabulary as <see cref="PathfindDetailed"/>, and the arrays are empty unless it is
    /// <c>ok</c> — an empty grid must never be read as "no walkable ground here", which is why the
    /// absent answer keeps them empty and says so in the result instead.
    /// </para>
    ///
    /// <para>
    /// Keep the visited set on this side: cells line up across queries at the same
    /// <paramref name="cellSize"/>, so one set can span many of them.
    /// </para>
    /// </summary>
    public Task<(string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth,
        int[] Columns, float[] Heights, byte[] States, bool ReachableOutside)> ReachableCells(
        Vector3 from, float radius, float cellSize, float minY, float maxY)
        => TryAsync(
            () => (_reachableCells ??= _pluginInterface.GetIpcSubscriber<Vector3, float, float, float, float,
                        Task<(string, Vector3, Vector2, float, int, int, int[], float[], byte[], bool)>>(
                        "Ariadne.Query.Mesh.ReachableCells"))
                .InvokeFunc(from, radius, cellSize, minY, maxY),
            ("serviceUnavailable", from, default, cellSize, 0, 0, [], [], [], false));

    /// <summary>
    /// Feeds the mesh-learning channel: <c>"direct"</c> with true is "I drove through where the
    /// mesh said no" — off-mesh-link evidence, the Yedlihmad doorways — and false is "a planned
    /// route failed there". Best-effort: false means it was not recorded.
    /// </summary>
    public Task<bool> ReportTraversal(Vector3 from, Vector3 to, string mode, bool success)
        => TryAsync(
            () => (_reportTraversal ??= _pluginInterface
                    .GetIpcSubscriber<Vector3, Vector3, string, bool, Task<bool>>("Ariadne.ReportTraversal"))
                .InvokeFunc(from, to, mode, success),
            false);

    // ── Movement ──

    /// <summary>
    /// Follows waypoints Theseus computed itself.
    ///
    /// <para>
    /// A path handed to Ariadne is ours: it never mesh-re-paths supplied waypoints, because those
    /// may encode knowledge the mesh lacks. On a stall it keeps following and raises
    /// <see cref="StallCount"/> — re-planning is the caller's job. When mesh recovery is what is
    /// wanted instead, the <c>SimpleMove.*</c> gates are the ones that have it.
    /// </para>
    /// </summary>
    public bool MoveTo(List<Vector3> waypoints, bool fly = false) => Try(() =>
    {
        (_moveTo ??= _pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("Ariadne.Path.MoveTo"))
            .InvokeAction(waypoints, fly);
        return true;
    });

    /// <summary>Same, with a per-path waypoint tolerance that leaves the global setting alone.</summary>
    public bool MoveToWithTolerance(List<Vector3> waypoints, bool fly, float tolerance) => Try(() =>
    {
        (_moveToWithTolerance ??= _pluginInterface
                .GetIpcSubscriber<List<Vector3>, bool, float, object>("Ariadne.Path.MoveToWithTolerance"))
            .InvokeAction(waypoints, fly, tolerance);
        return true;
    });

    /// <summary>Drops the path and any pending pathfind.</summary>
    public void Stop() => Try(() =>
    {
        (_stop ??= _pluginInterface.GetIpcSubscriber<object>("Ariadne.Path.Stop")).InvokeAction();
        return true;
    });

    /// <summary>This gate's own answer to "is someone driving" — the same truth as the flag above.</summary>
    public bool IsRunning => Try(() =>
        (_isRunning ??= _pluginInterface.GetIpcSubscriber<bool>("Ariadne.Path.IsRunning")).InvokeFunc());

    /// <summary>Waypoints left in the current path; -1 when the gate cannot be read.</summary>
    public int WaypointCount => Try(
        () => (_numWaypoints ??= _pluginInterface.GetIpcSubscriber<int>("Ariadne.Path.NumWaypoints")).InvokeFunc(),
        -1);

    /// <summary>
    /// Stalls detected on the current path — a rise on a path we supplied means re-plan it. -1 when
    /// the gate cannot be read, and note that 0 is a meaningful answer here.
    /// </summary>
    public int StallCount => Try(
        () => (_stallCount ??= _pluginInterface.GetIpcSubscriber<int>("Ariadne.Path.StallCount")).InvokeFunc(),
        -1);

    /// <summary>
    /// Waypoint-pass tolerance for paths Ariadne follows. A failed call leaves the nav side's own
    /// default in place rather than setting something wrong.
    /// </summary>
    public void SetTolerance(float tolerance) => Try(() =>
    {
        (_setTolerance ??= _pluginInterface.GetIpcSubscriber<float, object>("Ariadne.Path.SetTolerance"))
            .InvokeAction(tolerance);
        return true;
    });

    /// <summary>
    /// Yalms left along the current path, -1 while idle — or NaN here when the gate cannot be read,
    /// which is a different claim from "nothing is being followed".
    /// </summary>
    public float RemainingDistance => Try(
        () => (_remainingDistance ??= _pluginInterface.GetIpcSubscriber<float>("Ariadne.Path.RemainingDistance"))
            .InvokeFunc(),
        float.NaN);

    // ── Diagnostics ──

    /// <summary>
    /// One line for the debug window and the log: everything Ariadne answers without being asked to
    /// find anything. In a duty this is also the migration's own check — <c>zone</c> reading 2 or 3
    /// with <c>ready</c> true is the mesh being serviceable.
    /// </summary>
    public string Describe()
        => $"connected {IsConnected} · zone {ZoneStatus} · ready {NavReady} · cache \"{CurrentCacheKey}\"";

    // ── Plumbing ──

    private bool Try(Func<bool> call)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            Warn(ex);
            return false;
        }
    }

    private T Try<T>(Func<T> call, T fallback)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            Warn(ex);
            return fallback;
        }
    }

    /// <summary>
    /// The Task-shaped gates never throw on the nav side, but a provider that unregistered between
    /// the subscribe and the call faults the task instead — and a faulted task that nobody awaited
    /// inside a try/catch is an unobserved exception in the middle of a run.
    /// </summary>
    private async Task<T> TryAsync<T>(Func<Task<T>> call, T fallback)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            Warn(ex);
            return fallback;
        }
    }

    /// <summary>
    /// Reads a shared-data flag. These are published as one-element arrays because a shared-data
    /// entry has to be a reference type the owner can write into; an entry created by us before
    /// Ariadne ever published reads as the default, which is false.
    /// </summary>
    private bool Flag(string name)
    {
        try
        {
            var data = _pluginInterface.GetOrCreateData<bool[]>(name, () => [false]);
            return data is { Length: > 0 } && data[0];
        }
        catch
        {
            return false;
        }
    }

    private void Warn(Exception ex)
    {
        if (_warned)
            return;

        _warned = true;
        _log?.Invoke($"Ariadne unavailable ({ex.GetType().Name}) — pathfinding stays on vnavmesh.");
    }
}
