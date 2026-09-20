using System;
using System.Collections.Generic;
using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Services.Fleet;

public enum GateState
{
    /// <summary>No gate in force; the run proceeds.</summary>
    Open,

    /// <summary>Waiting for the fleet to gather.</summary>
    Holding,

    /// <summary>Everyone who was coming has arrived, or been given up on.</summary>
    Released,
}

/// <summary>
/// Holds a box at a rally point until the rest of the fleet arrives.
///
/// <para>
/// The primitive the coordinated patterns are built from: whoever reaches a boundary first waits
/// for the others, so a wall-to-wall tank does not drag a pack into three characters still walking
/// the previous corridor, and so the boxes waiting at a locked door are all present when it opens.
/// </para>
///
/// <para>
/// Two properties keep it from being a liability. It is <b>inert solo</b> — with no real peers it
/// never holds, so a single-box run cannot be frozen by a fleet feature. And it <b>gives up on the
/// dead</b>: a peer whose position has not changed for the stale timeout and who is not at the
/// rally has either crashed or is stuck on scenery, and one dead box must not strand the other
/// three indefinitely.
/// </para>
/// </summary>
public sealed class FleetGate
{
    private readonly IStepWorld _world;
    private readonly FleetRoster _roster;
    private readonly Func<float> _staleSeconds;

    /// <summary>Last place each peer was seen, and when it last differed.</summary>
    private readonly Dictionary<ulong, (Vector3 Position, DateTime Moved)> _seen = [];

    private Vector3 _rally;
    private float _radius;

    public FleetGate(IStepWorld world, FleetRoster roster, Func<float>? staleSeconds = null)
    {
        _world = world;
        _roster = roster;
        _staleSeconds = staleSeconds ?? (() => 10f);
    }

    public GateState State { get; private set; } = GateState.Open;

    /// <summary>One line for the run window.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>Peers still being waited on, for the status line.</summary>
    public int Waiting { get; private set; }

    /// <summary>Begins holding at a point. Returns false when there is nobody to wait for.</summary>
    public bool Hold(Vector3 rally, float radius)
    {
        if (_roster.IsSolo)
        {
            State = GateState.Open;
            return false;
        }

        _rally = rally;
        _radius = radius;
        State = GateState.Holding;
        Detail = "Waiting for the fleet.";
        return true;
    }

    public void Release()
    {
        State = GateState.Open;
        Waiting = 0;
        Detail = string.Empty;
        _seen.Clear();
    }

    public GateState Tick()
    {
        if (State != GateState.Holding)
            return State;

        // The fleet can dissolve while a gate is held — someone disconnects, or the party empties
        // on the way out of a duty. Nobody left to wait for means the gate has done its job.
        if (_roster.IsSolo)
        {
            Detail = "Alone — nothing to wait for.";
            State = GateState.Released;
            return State;
        }

        var now = _world.UtcNow;
        var stale = TimeSpan.FromSeconds(Math.Max(1f, _staleSeconds()));
        var outstanding = 0;

        foreach (var peer in _roster.Players)
        {
            if (peer.IsSelf)
                continue;

            var arrived = Vector3.Distance(peer.Position, _rally) <= _radius;
            TrackMovement(peer, now);

            if (arrived)
                continue;

            // Not here, and not moving: crashed, stuck, or otherwise not coming. Waiting longer
            // costs the whole fleet the run.
            if (_seen.TryGetValue(peer.ContentId, out var seen) && now - seen.Moved > stale)
                continue;

            outstanding++;
        }

        Waiting = outstanding;

        if (outstanding > 0)
        {
            Detail = $"Waiting for {outstanding} of the fleet.";
            _world.StopMoving();
            return State;
        }

        Detail = "Fleet gathered.";
        State = GateState.Released;
        return State;
    }

    /// <summary>
    /// Remembers when a peer last actually moved, which is the only liveness signal available
    /// without anything passing between the boxes.
    /// </summary>
    private void TrackMovement(FleetMember peer, DateTime now)
    {
        if (!_seen.TryGetValue(peer.ContentId, out var seen))
        {
            _seen[peer.ContentId] = (peer.Position, now);
            return;
        }

        // A small threshold, so standing-still jitter does not read as walking.
        if (Vector3.Distance(seen.Position, peer.Position) > 1f)
            _seen[peer.ContentId] = (peer.Position, now);
    }
}
