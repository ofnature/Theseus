using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Theseus.Services.Ipc;

namespace Theseus.Services.Run;

/// <summary>
/// Ariadne as the mover: a route computed out of process by Mnemosyne, then driven as a path
/// Theseus owns.
///
/// <para>
/// <b>The asymmetry with vnavmesh is that Ariadne's routes arrive late.</b> Its pathfinding gates
/// are Task-shaped, so a move request starts here and finishes a frame or more later; everything
/// the executor reads (<see cref="IsBusy"/>, <see cref="WaypointCount"/>) finishes that work in
/// passing, because the world seam has no per-frame entry point of its own and a completion
/// callback would be running outside the frame that asked for the move.
/// </para>
///
/// <para>
/// <b>Owning the path is the point.</b> The waypoints are handed to Ariadne with a per-path
/// tolerance, and a path a consumer supplied is never mesh-re-pathed by Ariadne: on a stall it
/// keeps following and raises its stall count instead. That is the shape the transit rule and the
/// solver need — the answer is ours to interpret, not something that disappears inside a plugin.
/// </para>
///
/// <para>
/// <b>A move never dies of its source.</b> An empty answer falls back to vnavmesh, which is
/// normally installed alongside, with one line in the log. The one exception is
/// <c>meshNotReady</c>, which the nav side defines as "wait and retry" rather than an answer, and
/// is retried on a short budget — the same order of patience Ariadne's own SimpleMove gates keep —
/// before the fallback.
/// </para>
/// </summary>
public sealed class AriadneMover
{
    /// <summary>
    /// The <c>meshNotReady</c> retry budget. The nav side waits ~5 s for a zone's flight volume
    /// itself; a consumer using the raw gates is documented as having to retry on its own, and this
    /// is that, at a cadence the executor's own one-per-second throttle is happy with.
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private const int MaxRetries = 5;

    /// <summary>
    /// Waypoint-pass tolerance before the executor sets its own. Matches Ariadne's default, so a
    /// move that happens before the run configures anything is not quietly looser.
    /// </summary>
    private const float DefaultTolerance = 0.25f;

    private readonly AriadneIpc _ariadne;
    private readonly VnavIpc _vnav;
    private readonly Func<Vector3> _position;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;

    /// <summary>The route being computed, if one is. Null once its answer has been acted on.</summary>
    private Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)>? _pending;

    /// <summary>
    /// Where the move in hand is going. Kept because the answer — including the empty one that
    /// triggers the fallback — arrives after the request, and vnavmesh needs a destination.
    /// </summary>
    private Vector3 _destination;

    private float _tolerance = DefaultTolerance;
    private DateTime _retryAtUtc = DateTime.MinValue;
    private int _retries;
    private bool _fallbackWarned;

    /// <summary>
    /// True while the move in hand was handed to vnavmesh instead. Its state is then the one worth
    /// reporting: a fallback move is a real move, and the executor's stuck detector has to see it.
    /// </summary>
    private bool _vnavOwnsTheMove;

    private string _lastAnswer = "nothing asked";

    /// <param name="position">The character's position now — a retry paths from where it stands.</param>
    public AriadneMover(
        AriadneIpc ariadne,
        VnavIpc vnav,
        Func<Vector3> position,
        Func<DateTime>? clock = null,
        Action<string>? log = null)
    {
        _ariadne = ariadne;
        _vnav = vnav;
        _position = position;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    /// <summary>
    /// Whether a move is in hand: a route being computed, a retry pending, a path being followed,
    /// or a fallback move on vnavmesh.
    /// </summary>
    public bool IsBusy
    {
        get
        {
            Pump();

            if (_pending is not null || _retryAtUtc != DateTime.MinValue)
                return true;

            return _vnavOwnsTheMove ? _vnav.IsBusy : _ariadne.IsPathRunning;
        }
    }

    /// <summary>
    /// Waypoints left in the path being driven — or -1 while a route is still being computed or
    /// retried, which is not the same answer as "no path" and must not be read as one.
    ///
    /// <para>
    /// Zero after a finished pathfind means the destination could not be reached, and that is the
    /// executor's own contract with the seam: it counts those and stops the run after three.
    /// </para>
    /// </summary>
    public int WaypointCount
    {
        get
        {
            Pump();

            if (_pending is not null || _retryAtUtc != DateTime.MinValue)
                return -1;

            return _vnavOwnsTheMove ? _vnav.WaypointCount : _ariadne.WaypointCount;
        }
    }

    /// <summary>
    /// Starts a move; true when it was issued.
    ///
    /// <para>
    /// Ariadne computing the route is an accepted move rather than a refused one — the executor's
    /// side of it is "a move is in hand" — and so is a fallback: the one thing this cannot return
    /// is "nothing happened" while the character stands still waiting on a source that is not
    /// coming.
    /// </para>
    /// </summary>
    public bool Begin(Vector3 destination)
    {
        _destination = destination;

        if (!_ariadne.NavReady)
            // Not connected, or no mesh for this zone yet: vnavmesh takes the move, and the log
            // says so once. The run is working — just not the way the config asked for.
            return FallBack("this zone cannot be routed on Ariadne");

        _vnavOwnsTheMove = false;
        _retryAtUtc = DateTime.MinValue;
        _retries = 0;
        _lastAnswer = "computing";

        // The source can be flipped mid-run, and a vnavmesh path already being followed would keep
        // driving the character alongside this one — two movers writing input on the same frame.
        // Taking the move over means taking it over.
        _vnav.Stop();

        _pending = _ariadne.PathfindDetailed(_position(), destination, fly: false);
        return true;
    }

    /// <summary>
    /// How close the navigator walks before declaring a waypoint reached. Set by the run rather
    /// than inherited, and passed on to vnavmesh as well: a fallback move mid-run executes there,
    /// and the two have to agree on which side of the executor's own tolerance they stop.
    /// </summary>
    public void SetTolerance(float tolerance)
    {
        _tolerance = tolerance;
        _vnav.SetTolerance(tolerance);
    }

    /// <summary>
    /// Drops whatever is in hand. A route still being computed is forgotten rather than awaited:
    /// its answer would drive a character that has just been told to stop.
    /// </summary>
    public void Stop()
    {
        _pending = null;
        _retryAtUtc = DateTime.MinValue;
        _vnavOwnsTheMove = false;
        _lastAnswer = "stopped";
        _ariadne.Stop();
        _vnav.Stop();
    }

    /// <summary>One line for the debug window: what the last move did, and what is in hand now.</summary>
    public string Describe()
    {
        Pump();

        var state = _pending is not null ? "computing"
            : _retryAtUtc != DateTime.MinValue ? "retrying"
            : _vnavOwnsTheMove ? "vnavmesh (fell back)"
            : _ariadne.IsPathRunning ? "following"
            : "idle";

        return $"{state} · last {_lastAnswer}";
    }

    /// <summary>
    /// Advances the move in hand: re-issues a retry that has come round, and acts on an answer that
    /// has arrived. Called from the reads rather than from a callback, so that everything happens
    /// inside the frame that asked, on the framework thread.
    /// </summary>
    private void Pump()
    {
        if (_retryAtUtc != DateTime.MinValue && _clock() >= _retryAtUtc)
        {
            _retryAtUtc = DateTime.MinValue;
            _lastAnswer = "retrying (mesh not ready)";
            _pending = _ariadne.PathfindDetailed(_position(), _destination, fly: false);
        }

        var pending = _pending;
        if (pending is null || !pending.IsCompleted)
            return;

        _pending = null;

        // A faulted task is not supposed to exist — the wrapper turns an unreachable Ariadne into
        // serviceUnavailable — but an IPC serializer that cannot carry a shape is exactly how one
        // would appear, and an unhandled one here would take the run down.
        if (pending.IsFaulted)
        {
            _lastAnswer = "the pathfind failed";
            FallBack("the pathfind failed");
            return;
        }

        // Safe without await: a completed task whose faults were just handled carries an answer.
        var (result, waypoints, _, _) = pending.Result;

        if (waypoints.Count > 0)
        {
            _fallbackWarned = false;
            _retries = 0;
            _lastAnswer = $"{result}, {waypoints.Count} waypoints";
            _ariadne.MoveToWithTolerance(waypoints, fly: false, tolerance: _tolerance);
            return;
        }

        _lastAnswer = result;

        if (result == "meshNotReady" && _retries < MaxRetries)
        {
            _retries++;
            _retryAtUtc = _clock() + RetryInterval;
            return;
        }

        FallBack(result);
    }

    /// <summary>
    /// Hands the move to vnavmesh. Once per outage rather than once per move: a zone Ariadne cannot
    /// route is a state that lasts a run, and a warning per waypoint would bury the run's own log.
    /// </summary>
    private bool FallBack(string reason)
    {
        if (!_fallbackWarned)
        {
            _fallbackWarned = true;
            _log?.Invoke($"Ariadne could not route this move ({reason}) — using vnavmesh.");
        }

        _vnavOwnsTheMove = true;
        return _vnav.MoveTo(_destination);
    }
}
