using System;
using System.Collections.Generic;
using System.Linq;
using Theseus.Services.Frontier;
using Theseus.Services.Solver;

namespace Theseus.Services.Fleet;

/// <summary>What a claim is doing.</summary>
public enum ClaimState
{
    /// <summary>Nobody is on it.</summary>
    Free,

    /// <summary>This box has it.</summary>
    Mine,

    /// <summary>A peer has it, and is still within its expiry.</summary>
    Deferred,
}

/// <summary>
/// Who is taking which object, over the fleet relay (§5.4).
///
/// <para>
/// Arbitration is deterministic and needs no leader: <b>the lowest party-list slot among the boxes
/// that want it wins</b>, and every box reaches that answer from the claims it has heard plus its
/// own. That is why the slot is in the frame at all — not to elect anybody, but so that two boxes
/// that both see the same lever can decide between themselves without a message beyond the two
/// claims they already sent.
/// </para>
///
/// <para>
/// A claim expires 20 s after it is made unless the claimant says it is done, and the object goes
/// back to <see cref="ClaimState.Free"/> everywhere. That is the whole failure story: a box that
/// crashes mid-interaction costs one object one twenty-second wait, never a run.
/// </para>
///
/// <para>
/// Solo, the board is inert: every claim is granted locally and nothing is published. The same is
/// true when the relay is missing — which is why every method here can be called on any box and
/// none of them can hold a run up.
/// </para>
/// </summary>
public sealed class ClaimBoard
{
    /// <summary>How long a claim lives without a done. The architecture's number, not a guess.</summary>
    public const int ExpirySeconds = 20;

    private readonly RelayClient? _relay;
    private readonly FleetRoster? _roster;
    private readonly Func<string> _runId;
    private readonly Action<string>? _log;

    private readonly Dictionary<string, Peer> _peers = [];
    private readonly HashSet<string> _mine = [];
    private string _runIdAtClaim = string.Empty;

    private readonly Func<DateTime> _clock;

    public ClaimBoard(
        FleetRoster? roster,
        RelayClient? relay,
        Func<string> runId,
        Func<DateTime>? clock = null,
        Action<string>? log = null)
    {
        _roster = roster;
        _relay = relay;
        _runId = runId;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    private sealed record Peer(string Key, int Slot, string From, DateTime AtUtc);

    /// <summary>Objects a peer is on right now — for the debug window.</summary>
    public int PeerClaims => Live().Count;

    /// <summary>One line for the debug window: whether the board is doing anything, and what it holds.</summary>
    public string Describe()
        => _relay is { Available: false }
            ? "fleet offline (no Daedalus relay) — claims are local"
            : _roster is not { IsSolo: false }
                ? "solo — every claim granted locally, nothing published"
                : $"slot {_roster.MySlot} · my claims {_mine.Count} · peer claims {Live().Count}";

    /// <summary>Whether the board is doing anything at all: false solo, and false with no relay.</summary>
    public bool Active => _relay is { Available: true } && _roster is { IsSolo: false };

    /// <summary>
    /// States the object from this box's point of view. Deferring is not refusing: a deferred object
    /// is still seen and still tracked, it just is not this box's to walk to while the peer holds it.
    /// </summary>
    public ClaimState StateOf(WorldObject target)
    {
        EnsureRun();
        var key = ClaimRelay.KeyFor(target);

        if (!Active)
            return _mine.Contains(key) ? ClaimState.Mine : ClaimState.Free;

        if (Live().Any(p => p.Key == key))
        {
            // Somebody who wins the arbitration is on it. This box's own claim, if it made one, is
            // withdrawn here rather than raced: both boxes see both claims, and only one of them can
            // be right about who goes.
            _mine.Remove(key);
            return ClaimState.Deferred;
        }

        return _mine.Contains(key) ? ClaimState.Mine : ClaimState.Free;
    }

    /// <summary>
    /// Claims the object for this box, broadcasting if there is anybody to tell. Returns false when a
    /// peer holds it — the caller should not walk over.
    /// </summary>
    public bool Claim(WorldObject target)
    {
        EnsureRun();
        var key = ClaimRelay.KeyFor(target);

        if (StateOf(target) == ClaimState.Deferred)
            return false;

        if (!_mine.Add(key))
            return true; // already ours, already announced

        Publish(key, ClaimRelay.ActClaim, dataId: target.DataId);
        return true;
    }

    /// <summary>The object is finished with. One-shot, repeated, and idempotent on the far side.</summary>
    public void Done(WorldObject target, string outcome)
    {
        EnsureRun();
        var key = ClaimRelay.KeyFor(target);
        _mine.Remove(key);
        _peers.Remove(key);
        Publish(key, ClaimRelay.ActDone, target.DataId, outcome: outcome);
    }

    /// <summary>This box is past a gate: the fleet marks it open without spending a probe each.</summary>
    public void Beyond(Gate gate)
    {
        Publish(ClaimRelay.GateKeyFor(gate.Id, gate.Beyond), ClaimRelay.ActBeyond, 0);
    }

    /// <summary>Raised when a peer reports it is finished with an object, by DataId.</summary>
    public event Action<uint>? PeerDone;

    /// <summary>
    /// A frame from a peer. Anything malformed, from a run that is not this one, or about an object
    /// this box cannot see is dropped — the local scan stays the authority on what exists.
    /// </summary>
    public void NoteMessage(string channel, string json)
    {
        if (channel != RelayClient.ClaimChannel)
            return;

        if (ClaimRelay.Parse(json) is not { } message)
            return;

        EnsureRun();

        if (message.RunId != _runId())
            return; // a claim from a run that is over

        switch (message.Kind)
        {
            case ClaimRelay.ActClaim:
                // Lowest slot wins, and both boxes reach that answer from the two claims involved —
                // so only a claim from a box that beats this one is worth deferring to. An unknown
                // slot defers to everyone: one extra twenty-second wait is cheaper than two boxes on
                // one lever.
                var mine = _roster is { IsSolo: false } ? _roster.MySlot : -1;
                if (mine < 0 || message.Slot < mine)
                    _peers[message.Key] = new Peer(message.Key, message.Slot, message.From, _clock());

                break;

            case ClaimRelay.ActDone:
                _peers.Remove(message.Key);

                if (ClaimRelay.DataIdOf(message.Key) is var dataId and not 0)
                    PeerDone?.Invoke(dataId);

                break;

            case ClaimRelay.ActBeyond:
                PeerBeyond?.Invoke(message.Key);
                break;

            default:
                // held, and anything a newer box invents: ignored on purpose, never an error.
                break;
        }
    }

    /// <summary>Raised when a peer reports it is past a gate. The key carries the gate's id.</summary>
    public event Action<string>? PeerBeyond;

    /// <summary>A new run: nothing claimed before it is about this one.</summary>
    public void ResetRun()
    {
        _peers.Clear();
        _mine.Clear();
    }

    /// <summary>
    /// Clears the board when the run changes. A re-queue mints a new nonce and the same lever
    /// respawns in the same place, so a box that kept its old claims would silently never announce
    /// the one it is walking to this time.
    /// </summary>
    private void EnsureRun()
    {
        var runId = _runId();

        if (runId == _runIdAtClaim)
            return;

        _runIdAtClaim = runId;
        ResetRun();
    }

    /// <summary>Live peer claims, expired ones dropped as they are noticed.</summary>
    private List<Peer> Live()
    {
        var now = _clock();
        var expired = _peers.Values.Where(p => (now - p.AtUtc).TotalSeconds > ExpirySeconds).ToList();

        foreach (var peer in expired)
        {
            _peers.Remove(peer.Key);
            _log?.Invoke($"Fleet claim on {peer.Key} expired — the object is free again.");
        }

        return [.. _peers.Values];
    }

    private void Publish(string key, string kind, uint dataId, string outcome = "", string itemKind = "")
    {
        if (!Active || _relay is not { } relay)
            return;

        var message = new ClaimMessage
        {
            From = Self(),
            RunId = _runId(),
            Kind = kind,
            Key = key,
            Slot = _roster?.MySlot ?? -1,
            At = new DateTimeOffset(_clock()).ToUnixTimeMilliseconds(),
            Outcome = outcome,
            ItemKind = itemKind,
        };

        var json = ClaimRelay.Serialize(message);

        // Three identical frames: the relay's dedup ring makes the repeats idempotent, so a lost
        // packet costs a repeat rather than a duplicated interaction.
        for (var i = 0; i < ClaimRelay.Repeats; i++)
            relay.Publish(RelayClient.ClaimChannel, json);
    }

    private string Self()
        => _roster?.Players.FirstOrDefault(m => m.IsSelf).Name ?? string.Empty;
}
