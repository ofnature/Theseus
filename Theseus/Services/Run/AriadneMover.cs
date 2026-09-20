using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Theseus.Services.Ipc;

namespace Theseus.Services.Run;

/// <summary>
/// What the mover is doing about a route that stopped short.
/// </summary>
public enum TransitPhase
{
    /// <summary>No transit in hand.</summary>
    None,

    /// <summary>Walking to the reachable point nearest the goal — the edge of the drop or the rail.</summary>
    Approaching,

    /// <summary>At the edge, holding forward: the game carries the character from here.</summary>
    Pushing,

    /// <summary>Holding nothing, watching the position until it lands or settles.</summary>
    Settling,
}

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
/// keeps following and raises its stall count instead. That is the shape the transit rule below and
/// the solver need — the answer is ours to interpret, not something that disappears inside a
/// plugin.
/// </para>
///
/// <para>
/// <b>A move never dies of its source.</b> An empty answer falls back to vnavmesh, which is
/// normally installed alongside, with one line in the log. The one exception is
/// <c>meshNotReady</c>, which the nav side defines as "wait and retry" rather than an answer, and
/// is retried on a short budget — the same order of patience Ariadne's own SimpleMove gates keep —
/// before the fallback.
/// </para>
///
/// <para>
/// <b>A route that ends short of a drop is a transit, not a failure.</b> The mesh will never walk
/// off a ledge, along a rail, or through a one-way drop, so "no route" is the correct answer to a
/// question whose answer is "walk off the edge". The shape is recognisable: a partial or empty
/// route whose nearest reachable point sits within a few yalms of the goal and at or above it.
/// Theseus walks to that edge, holds forward for about a second and a half — AutoDuty's own macro
/// is literally a move followed by a two-second automove — then lets the game land the character
/// and re-paths from wherever it ended up. Landing on the far side is reported to Ariadne as a
/// traversal, so the zone's evidence can accumulate what the mesh cannot express. Nothing is
/// marked walkable on this side: a rail needs an interaction and only a person can promote it.
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

    /// <summary>
    /// How long the character is pushed forward at the edge. AutoDuty uses two seconds of automove
    /// after the move that stops at the lip; checked in the field against the same rails.
    /// </summary>
    private static readonly TimeSpan PushDuration = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long a push may go without the character moving at all before it is abandoned. A hold
    /// that produces no displacement is the character facing a wall or facing the wrong way, and
    /// the remaining second of it is worth nobody's time.
    /// </summary>
    private static readonly TimeSpan PushNoMovementGrace = TimeSpan.FromMilliseconds(700);

    /// <summary>How long the character is watched after the push before the run carries on regardless.</summary>
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Stillness that counts as landed.</summary>
    private static readonly TimeSpan SettleStill = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// How far from the goal the reachable edge may be for this to look like a drop or a rail
    /// rather than a different problem.
    ///
    /// <para>
    /// Measured against the field cases rather than guessed: the landing under a lip sits within a
    /// yalm or two of it (Xelphatol's 30 m drop: 0.7 y), the rail's near platform a couple (2.0 y),
    /// while the migration note's own rail measurement is 15 m of span. So the window has to hold
    /// a rail's length, and an off-mesh goal — a point in the void, 13.7 y away in the r2d3 probe —
    /// is excluded by its answer (<c>targetOffMesh</c>) rather than by distance.
    /// </para>
    /// </summary>
    private const float TransitReach = 18f;

    /// <summary>
    /// How far above the reachable edge the goal may sit. Forward movement cannot climb: a goal
    /// more than a step above the nearest reachable ground is a wall, a ledge, or a locked door,
    /// and pushing at it would only waste the attempt.
    /// </summary>
    private const float TransitStepUp = 1.5f;

    /// <summary>How close to the edge the character has to be for the push to mean anything.</summary>
    private const float EdgeSlack = 3f;

    /// <summary>
    /// Movement that counts as the transit having done something — the game carrying the character,
    /// or a fall. Below this, the push produced nothing and the route is simply unreachable.
    /// </summary>
    private const float TransitMovement = 1f;

    /// <summary>
    /// Transits attempted for one move before giving the destination up as unreachable. Two: a
    /// first attempt walks to the nearest edge and steps off, and a second covers a route that
    /// landed on ground the mesh still refuses. A third would be the run arguing with the mesh.
    /// </summary>
    private const int MaxTransitAttempts = 2;

    private readonly AriadneIpc _ariadne;
    private readonly VnavIpc _vnav;
    private readonly Func<Vector3> _position;
    private readonly Action<bool> _setForwardMovement;
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

    private TransitPhase _transit = TransitPhase.None;

    /// <summary>The reachable point nearest the goal — where a drop or a rail starts.</summary>
    private Vector3 _transitEdge;

    private Vector3 _transitWatchPosition;
    private DateTime _transitMovedUtc = DateTime.MinValue;
    private DateTime _transitUntilUtc = DateTime.MinValue;
    private DateTime _pushStartedUtc = DateTime.MinValue;
    private bool _forwardHeld;

    /// <summary>
    /// Transit attempts already spent on this move. Survives the transit itself, so the route the
    /// run re-paths into is measured against the budget rather than starting a fresh one.
    /// </summary>
    private int _transitAttempts;

    private string _lastAnswer = "nothing asked";

    /// <param name="position">The character's position now — a retry paths from where it stands.</param>
    /// <param name="setForwardMovement">
    /// The game's own auto-run, which is how a character is moved somewhere the mesh refuses to
    /// route. Held for the push at the edge and always released again, on every exit path.
    /// </param>
    public AriadneMover(
        AriadneIpc ariadne,
        VnavIpc vnav,
        Func<Vector3> position,
        Action<bool> setForwardMovement,
        Func<DateTime>? clock = null,
        Action<string>? log = null)
    {
        _ariadne = ariadne;
        _vnav = vnav;
        _position = position;
        _setForwardMovement = setForwardMovement;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    /// <summary>What the mover is doing about a route that stopped short, if anything.</summary>
    public TransitPhase Transit => _transit;

    /// <summary>
    /// Whether a move is in hand: a route being computed, a retry pending, a path being followed, a
    /// transit mid-flight, or a fallback move on vnavmesh.
    ///
    /// <para>
    /// A transit counts, and that is what freezes the executor's stuck detector across it — the one
    /// thing a rail or a drop must not be mistaken for is a character that will not move.
    /// </para>
    /// </summary>
    public bool IsBusy
    {
        get
        {
            Pump();

            if (_pending is not null || _retryAtUtc != DateTime.MinValue || _transit != TransitPhase.None)
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

            if (_pending is not null || _retryAtUtc != DateTime.MinValue || _transit != TransitPhase.None)
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
        _transit = TransitPhase.None;
        _transitAttempts = 0;
        ReleaseForward();

        if (!_ariadne.NavReady)
            // Not connected, or no mesh for this zone yet: vnavmesh takes the move, and the log
            // says so once. The run is working — just not the way the config asked for.
            return FallBack("this zone cannot be routed on Ariadne");

        _vnavOwnsTheMove = false;
        _retryAtUtc = DateTime.MinValue;
        _retries = 0;
        _lastAnswer = "computing";

        TakeOverFromVnav();

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
    /// Drops whatever is in hand, forward movement included. A route still being computed is
    /// forgotten rather than awaited: its answer would drive a character that has just been told to
    /// stop.
    /// </summary>
    public void Stop()
    {
        _pending = null;
        _retryAtUtc = DateTime.MinValue;
        _vnavOwnsTheMove = false;
        _transit = TransitPhase.None;
        _lastAnswer = "stopped";
        ReleaseForward();
        _ariadne.Stop();
        _vnav.Stop();
    }

    /// <summary>One line for the debug window: what the last move did, and what is in hand now.</summary>
    public string Describe()
    {
        Pump();

        var state = _transit switch
        {
            TransitPhase.Approaching => $"transit to the edge ({_transitAttempts + 1}/{MaxTransitAttempts})",
            TransitPhase.Pushing => $"transit: pushing off the edge ({_transitAttempts + 1}/{MaxTransitAttempts})",
            TransitPhase.Settling => "transit: waiting to land",
            _ => _pending is not null ? "computing"
                : _retryAtUtc != DateTime.MinValue ? "retrying"
                : _vnavOwnsTheMove ? "vnavmesh (fell back)"
                : _ariadne.IsPathRunning ? "following"
                : "idle",
        };

        return $"{state} · last {_lastAnswer}";
    }

    /// <summary>
    /// Advances the move in hand: a transit step that has come round, a retry that has come round,
    /// and an answer that has arrived. Called from the reads rather than from a callback, so that
    /// everything happens inside the frame that asked, on the framework thread.
    /// </summary>
    private void Pump()
    {
        AdvanceTransit();

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
        var (result, waypoints, nearest, partial) = pending.Result;

        if (waypoints.Count > 0 && !partial)
        {
            _fallbackWarned = false;
            _retries = 0;
            _lastAnswer = $"{result}, {waypoints.Count} waypoints";
            _ariadne.MoveToWithTolerance(waypoints, fly: false, tolerance: _tolerance);
            return;
        }

        if (IsTransitCandidate(result, partial, nearest))
        {
            _transit = TransitPhase.Approaching;
            _transitEdge = nearest!.Value;
            _lastAnswer = $"{result} — trying a transit to ({_transitEdge.X:0.#}, {_transitEdge.Y:0.#}, " +
                          $"{_transitEdge.Z:0.#}) ({_transitAttempts + 1}/{MaxTransitAttempts})";

            if (waypoints.Count > 0)
            {
                // A partial route already walks to where the mesh stops: follow it to the edge.
                _ariadne.MoveToWithTolerance(waypoints, fly: false, tolerance: _tolerance);
            }
            else
            {
                // Nothing to follow, so the edge is asked for directly. It is by definition the
                // nearest reachable ground to the goal, which is exactly what a drop or a rail
                // starts from.
                _pending = _ariadne.PathfindDetailed(_position(), _transitEdge, fly: false);
            }

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
    /// Whether a route that stopped short is worth one transit attempt: the nav side found
    /// reachable ground within a few yalms of the goal, and the goal is not above it.
    ///
    /// <para>
    /// This is the interim rule the migration note calls for. The nav side is adding a classified
    /// transition answer, at which point "there is a drop between here and there" becomes a fact
    /// rather than an inference from geometry — and this becomes the fallback for the answers that
    /// predate it.
    /// </para>
    /// </summary>
    private bool IsTransitCandidate(string result, bool partial, Vector3? nearest)
    {
        // One transit at a time: the route the run re-paths into is what gets measured against the
        // budget, so an edge that turns out to be unreachable itself cannot start a cascade.
        if (_transit != TransitPhase.None || _transitAttempts >= MaxTransitAttempts)
            return false;

        // Two shapes that look similar and are not this. `targetOffMesh` means the goal is not on
        // the mesh at all — there is nothing to land on — and `startOffMesh` means the character is
        // the one off the mesh, which is a different recovery (get back on, or wait to be carried),
        // not a push away from it. A drop or a rail answers `noRouteOnMesh`: the goal is meshed
        // ground that cannot be walked to from here. Both field cases produced exactly that.
        if (!partial && result is not ("noRouteOnMesh" or "unreachable"))
            return false;

        if (nearest is not { } edge)
            return false;

        var toGoal = _destination - edge;
        var horizontal = new Vector2(toGoal.X, toGoal.Z).Length();

        return horizontal <= TransitReach && _destination.Y <= edge.Y + TransitStepUp;
    }

    /// <summary>
    /// Moves the transit along. Everything here is time- and position-based, and every exit path
    /// releases forward movement — a hold nobody releases is a character walking into a wall until
    /// someone notices.
    /// </summary>
    private void AdvanceTransit()
    {
        var now = _clock();
        var position = _position();

        switch (_transit)
        {
            case TransitPhase.Approaching:
                // The approach is over when nothing is in hand: no answer pending and no path being
                // followed. That is also the moment the character is standing at the edge.
                if (_pending is not null || _ariadne.IsPathRunning)
                    return;

                var toEdge = position - _transitEdge;
                if (new Vector2(toEdge.X, toEdge.Z).Length() > EdgeSlack)
                {
                    // The walk to the edge failed too, so there is nothing to push off. A move that
                    // already fell back on its own is not asked again.
                    _transit = TransitPhase.None;
                    _transitAttempts++;

                    if (!_vnavOwnsTheMove)
                    {
                        _lastAnswer = "the edge could not be reached";
                        FallBack("the edge could not be reached");
                    }

                    return;
                }

                _transit = TransitPhase.Pushing;
                _pushStartedUtc = now;
                _transitUntilUtc = now + PushDuration;
                _transitWatchPosition = position;

                var shortBy = Vector3.Distance(_transitEdge, _destination);
                _log?.Invoke($"Route ends {shortBy:0.#}y short of the goal at " +
                             $"({_transitEdge.X:0.#}, {_transitEdge.Y:0.#}, {_transitEdge.Z:0.#}) — " +
                             $"walking off the edge (attempt {_transitAttempts + 1}/{MaxTransitAttempts}).");

                HoldForward();
                return;

            case TransitPhase.Pushing:
            {
                var moved = Vector3.Distance(position, _transitWatchPosition) > 0.25f;

                // Nothing has shifted since the hold began, and the grace for that is spent: the
                // character is against something, and the rest of the push is wasted.
                if (!moved && now - _pushStartedUtc >= PushNoMovementGrace)
                    _transitUntilUtc = now;

                if (now < _transitUntilUtc)
                    return;

                ReleaseForward();
                _transit = TransitPhase.Settling;
                _transitWatchPosition = position;
                _transitMovedUtc = now;
                _transitUntilUtc = now + SettleTimeout;
                return;
            }

            case TransitPhase.Settling:
            {
                if (Vector3.Distance(position, _transitWatchPosition) > 0.25f)
                {
                    _transitWatchPosition = position;
                    _transitMovedUtc = now;
                }

                var landed = now - _transitMovedUtc >= SettleStill; // still: landed, or never moved
                if (!landed && now < _transitUntilUtc)
                    return;

                var from = _transitEdge;
                var travelled = Vector3.Distance(position, _transitEdge);
                _transit = TransitPhase.None;
                _transitAttempts++;

                if (travelled > TransitMovement)
                {
                    // Landed on the far side of something the mesh cannot cross. Reported, not
                    // recorded as walkable: a rail needs an interaction and only a person can
                    // promote it into the zone's evidence.
                    _lastAnswer = $"landed {travelled:0.#}y from the edge after a transit";
                    _log?.Invoke($"Transit landed {travelled:0.#}y from ({from.X:0.#}, {from.Y:0.#}, " +
                                 $"{from.Z:0.#}) — re-pathing from ({position.X:0.#}, {position.Y:0.#}, " +
                                 $"{position.Z:0.#}).");
                    _ = _ariadne.ReportTraversal(from, position, "direct", true);
                }
                else
                {
                    _lastAnswer = "the transit went nowhere";
                    _log?.Invoke("The transit moved nothing — the goal is not across an edge after all.");
                }

                // Wherever that ended up, ask again from there.
                _pending = _ariadne.PathfindDetailed(position, _destination, fly: false);
                return;
            }
        }
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

    /// <summary>
    /// The source can be flipped mid-run, and a vnavmesh path already being followed would keep
    /// driving the character alongside this one — two movers writing input on the same frame.
    /// Taking the move over means taking it over.
    /// </summary>
    private void TakeOverFromVnav() => _vnav.Stop();

    private void HoldForward()
    {
        _forwardHeld = true;
        _setForwardMovement(true);
    }

    private void ReleaseForward()
    {
        if (!_forwardHeld)
            return;

        _forwardHeld = false;
        _setForwardMovement(false);
    }
}
