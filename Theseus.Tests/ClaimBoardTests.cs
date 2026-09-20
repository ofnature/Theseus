using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Moq;
using Theseus.Services.Fleet;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The claim board is the only fleet coordination the solver has, and its whole job is that two
/// boxes do not interact with one lever. What these pin is that, plus the two ways it must never
/// matter: solo, and a missing relay.
/// </summary>
public sealed class ClaimBoardTests
{
    private const uint Lever = 2001234;

    private sealed class Box : IDisposable
    {
        private readonly Mock<IDalamudPluginInterface> _pi = new();
        private readonly Mock<ICallGateSubscriber<string, string, object?>> _publish = new();
        private readonly Mock<ICallGateSubscriber<string, string, object?>> _message = new();

        public readonly List<(string Channel, string Json)> Published = [];
        public readonly FakeStepWorld World = new();
        public readonly List<string> Log = [];

        private Action<string, string>? _inbound;

        public DateTime Now { get; set; } = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public string RunId { get; set; } = "run-1";

        public RelayClient Relay { get; }

        public ClaimBoard Board { get; }

        public Box(int mySlot, int boxes = 2, bool withRelay = true)
        {
            for (var i = 0; i < boxes; i++)
            {
                World.Party.Add(new FleetMember(
                    (ulong)(900 + i), $"toon{i}", i, Vector3.Zero, IsSelf: i == mySlot, IsPlayer: true));
            }

            _publish.Setup(s => s.InvokeAction(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((channel, json) => Published.Add((channel, json)));

            _message.Setup(s => s.Subscribe(It.IsAny<Action<string, string>>()))
                .Callback<Action<string, string>>(handler => _inbound = handler);

            if (!withRelay)
            {
                // Daedalus is absent: every gate lookup throws, which is the shape the relay client
                // has to survive at construction time.
                _pi.Setup(p => p.GetIpcSubscriber<string, string, object?>(It.IsAny<string>()))
                    .Throws(new Exception("Daedalus is not loaded"));
            }
            else
            {
                _pi.Setup(p => p.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Publish"))
                    .Returns(_publish.Object);
                _pi.Setup(p => p.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Message"))
                    .Returns(_message.Object);
            }

            Relay = new RelayClient(_pi.Object, Mock.Of<Dalamud.Plugin.Services.IPluginLog>());
            Board = new ClaimBoard(new FleetRoster(World), Relay, () => RunId, () => Now, Log.Add);
            Relay.OnMessage += Board.NoteMessage;
        }

        /// <summary>Delivers this box's published frames to another, as the relay would.</summary>
        public void DeliverTo(Box other)
        {
            foreach (var (channel, json) in Published)
                other.Board.NoteMessage(channel, json);
        }

        public void Dispose() => Relay.Dispose();

        public static WorldObject Object()
            => new(900 + Lever, Lever, "lever", new Vector3(10f, 0f, 4f), WorldObjectKind.Interactable, true);
    }

    [Fact]
    public void Solo_every_claim_is_granted_locally_and_nothing_is_published()
    {
        using var box = new Box(mySlot: 0, boxes: 1);

        Assert.True(box.Board.Claim(Box.Object()));
        Assert.Empty(box.Published);
        Assert.False(box.Board.Active);
    }

    [Fact]
    public void Without_a_relay_the_board_still_grants_and_still_publishes_nothing()
    {
        using var box = new Box(mySlot: 0, withRelay: false);

        Assert.False(box.Relay.Available);
        Assert.True(box.Board.Claim(Box.Object()));
        Assert.Empty(box.Published);
    }

    [Fact]
    public void Two_boxes_on_one_lever_resolve_by_slot_without_a_leader()
    {
        using var first = new Box(mySlot: 0);
        using var second = new Box(mySlot: 1);

        Assert.True(first.Board.Claim(Box.Object()));
        Assert.True(second.Board.Claim(Box.Object()));

        // Each box publishes its claim, and each hears the other's.
        first.DeliverTo(second);
        second.DeliverTo(first);

        // The lower slot keeps it; the higher one defers. Both reached that alone.
        Assert.Equal(ClaimState.Mine, first.Board.StateOf(Box.Object()));
        Assert.Equal(ClaimState.Deferred, second.Board.StateOf(Box.Object()));
        Assert.False(second.Board.Claim(Box.Object()));
    }

    [Fact]
    public void A_claim_from_a_higher_slot_is_not_worth_deferring_to()
    {
        using var first = new Box(mySlot: 0);
        using var second = new Box(mySlot: 1);

        second.Board.Claim(Box.Object());
        second.DeliverTo(first);

        // The other box's claim does not stop this one, because this one wins the tie.
        Assert.Equal(ClaimState.Free, first.Board.StateOf(Box.Object()));
    }

    [Fact]
    public void A_claim_expires_after_twenty_seconds_and_the_object_comes_back()
    {
        using var winner = new Box(mySlot: 0);
        using var loser = new Box(mySlot: 1);

        winner.Board.Claim(Box.Object());
        winner.DeliverTo(loser);
        Assert.Equal(ClaimState.Deferred, loser.Board.StateOf(Box.Object()));

        // Twenty seconds with no done: whatever happened to the other box, this run does not wait.
        loser.Now = loser.Now.AddSeconds(ClaimBoard.ExpirySeconds + 1);

        Assert.Equal(ClaimState.Free, loser.Board.StateOf(Box.Object()));
        Assert.Contains(loser.Log, line => line.Contains("expired"));
    }

    [Fact]
    public void A_done_frame_frees_the_object_and_tells_the_solver_it_is_finished()
    {
        using var winner = new Box(mySlot: 0);
        using var loser = new Box(mySlot: 1);

        winner.Board.Claim(Box.Object());
        winner.DeliverTo(loser);
        Assert.Equal(ClaimState.Deferred, loser.Board.StateOf(Box.Object()));

        uint? done = null;
        loser.Board.PeerDone += dataId => done = dataId;

        winner.Board.Done(Box.Object(), "resolved");
        winner.DeliverTo(loser);

        Assert.Equal(Lever, done);
        Assert.Equal(ClaimState.Free, loser.Board.StateOf(Box.Object()));
    }

    [Fact]
    public void A_frame_from_another_run_is_not_this_run_business()
    {
        using var winner = new Box(mySlot: 0);
        using var loser = new Box(mySlot: 1);

        winner.Board.Claim(Box.Object());
        winner.DeliverTo(loser);
        Assert.Equal(ClaimState.Deferred, loser.Board.StateOf(Box.Object()));

        // A re-queue mints a new nonce, and a claim from the run before it is about a run that is
        // over — including the ones this box made itself.
        loser.RunId = "run-2";
        loser.Board.Claim(Box.Object());
        winner.DeliverTo(loser);

        Assert.Equal(ClaimState.Mine, loser.Board.StateOf(Box.Object()));
        Assert.Equal(0, loser.Board.PeerClaims);
    }

    [Fact]
    public void A_beyond_frame_opens_the_matching_gate_by_id_or_by_where_it_leads()
    {
        var ledger = new GateLedger();
        var frontier = new Vector3(3f, 0f, 3f);
        var beyond = new Vector3(5f, 0f, 3f);
        var world = new WorldModel.Snapshot(
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), Vector3.Zero, (1314, 1, 103),
            0, 0, 1, true, false, false, [], [new GateCandidate(frontier, beyond)], null, false);

        ledger.Discover(world.Gates, world, 1314);
        var gate = Assert.Single(ledger.Gates);

        var parsed = ClaimRelay.ParseGateKey(ClaimRelay.GateKeyFor(gate.Id, gate.Beyond));
        Assert.NotNull(parsed);
        Assert.True(ledger.PeerBeyondNear(parsed!.Value.GateId, parsed.Value.Beyond));
        Assert.Equal(Theseus.Services.Solver.GateState.Open, gate.State);
        Assert.Contains("peer", gate.Evidence);

        // Two yalms of drift is the same doorway; ten is a different one, and a gate nobody here can
        // find is a note about a peer rather than a gate to open.
        Assert.False(ledger.PeerBeyondNear("nobody", new Vector3(15f, 0f, 3f)));
    }

    [Fact]
    public void Keys_round_trip_and_are_rounded_to_a_yalm()
    {
        var target = new WorldObject(1, Lever, "lever", new Vector3(10.2f, 0.4f, 4.1f),
            WorldObjectKind.Interactable, true);

        var key = ClaimRelay.KeyFor(target);

        Assert.Equal($"{Lever}@10:0:4", key);
        Assert.Equal(Lever, ClaimRelay.DataIdOf(key));
        Assert.Equal(0u, ClaimRelay.DataIdOf("garbage"));
    }

    [Fact]
    public void Malformed_frames_are_ignored_rather_than_thrown()
    {
        using var box = new Box(mySlot: 0);

        box.Board.NoteMessage(RelayClient.ClaimChannel, "{ not json");
        box.Board.NoteMessage(RelayClient.ClaimChannel, "{}");
        box.Board.NoteMessage("charon.follow", "{\"kind\":\"claim\",\"key\":\"1@0:0:0\",\"runId\":\"run-1\"}");
        box.Board.NoteMessage(RelayClient.ClaimChannel, ClaimRelay.Serialize(new ClaimMessage
        {
            RunId = "run-1", Kind = ClaimRelay.ActClaim, Key = "1@0:0:0", Slot = 0,
        }));

        Assert.Equal(ClaimState.Free, box.Board.StateOf(Box.Object()));
    }

    [Fact]
    public void A_peer_held_object_is_not_chosen()
    {
        // The loop's own view of a claim: a deferred object is still seen, and not this box's to walk
        // to — choosing it anyway is the duplicate interaction the board exists to prevent.
        var gapLog = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-claim-{Guid.NewGuid():N}.jsonl"));
        var loop = new InteractableLoop(new InteractableContext(
            new Taxonomy(), new GhostCache(), gapLog, () => "run", () => "key",
            IsFree: _ => false));

        var world = new WorldModel.Snapshot(
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), Vector3.Zero, (1314, 1, 103),
            0, 0, 1, true, false, false,
            [new WorldModel.Recognised(Box.Object(), null, false, 3f)], [], null, false);

        Assert.Null(loop.Bid(world));

        // And with nobody holding it, the same object is chosen: the filter is the claim, not the
        // object.
        var free = new InteractableLoop(new InteractableContext(
            new Taxonomy(), new GhostCache(), gapLog, () => "run", () => "key"));

        Assert.NotNull(free.Bid(world));
    }
}
