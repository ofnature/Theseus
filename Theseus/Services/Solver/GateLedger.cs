using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Frontier;

namespace Theseus.Services.Solver;

/// <summary>What a gate turned out to be. The kind is a hypothesis until the gate opens.</summary>
public enum GateKind
{
    Unknown,
    Combat,
    Trigger,
    TurnIn,
    Objective,
}

public enum GateState
{
    /// <summary>Seen, and the mesh says it cannot be walked to from here.</summary>
    Locked,

    /// <summary>A probe answered after its condition held: the way through exists now.</summary>
    Open,

    /// <summary>The character has been beyond it.</summary>
    Passed,
}

/// <summary>
/// What has to become true for a gate to open — a hypothesis attached at discovery, confirmed by
/// opening, and the thing that teaches the taxonomy when it does.
/// </summary>
public abstract record GateCondition
{
    /// <summary>Every hostile first seen within the radius is a ghost.</summary>
    public sealed record HostilesCleared(Vector3 Centre, float Radius) : GateCondition;

    /// <summary>The duty's objective stage has reached this.</summary>
    public sealed record ObjectiveReached(int Stage) : GateCondition;

    /// <summary>That object's interaction resolved.</summary>
    public sealed record Triggered(uint DataId) : GateCondition;

    /// <summary>Any of these — the honest default while the real one is unknown.</summary>
    public sealed record AnyOf(IReadOnlyList<GateCondition> Alternatives) : GateCondition;
}

/// <summary>An edge the mesh says is cut off right now, with everything the run believes about it.</summary>
public sealed class Gate
{
    internal Gate(string id, Vector3 frontier, Vector3 beyond, GateKind kind, GateCondition unlock, DateTime discoveredUtc)
    {
        Id = id;
        Frontier = frontier;
        Beyond = beyond;
        Kind = kind;
        Unlock = unlock;
        DiscoveredUtc = discoveredUtc;
    }

    /// <summary>Territory and the rounded frontier point — stable within a run, which is all it needs to be.</summary>
    public string Id { get; }

    /// <summary>The reachable ground on our side: where the walk stopped.</summary>
    public Vector3 Frontier { get; }

    /// <summary>What opens up when it unlocks.</summary>
    public Vector3 Beyond { get; }

    public GateKind Kind { get; internal set; }

    public GateCondition Unlock { get; internal set; }

    public GateState State { get; internal set; } = GateState.Locked;

    /// <summary>Which observation set the kind and the condition, and when.</summary>
    public string Evidence { get; internal set; } =
        "the reachable grid: walkable ground beside this point cannot be walked to from here.";

    public DateTime DiscoveredUtc { get; }

    public DateTime? CheckedUtc { get; internal set; }

    /// <summary>How many times the edge has been probed.</summary>
    public int Probes { get; internal set; }

    /// <summary>Set while the condition's inputs have moved since the last probe — when asking is worth it.</summary>
    internal bool Due { get; set; }

    /// <summary>The condition's inputs as of the last look, so a change is what triggers the next probe.</summary>
    internal int Signature { get; set; } = int.MinValue;
}

/// <summary>
/// Every locked edge seen this run, with its unlock condition and state.
///
/// <para>
/// A gate is <b>discovered, not declared</b>: an edge the mesh refuses right now — walkable ground
/// beside reachable ground that cannot be walked to. The ledger holds it, watches the things that
/// could open it, and asks again only when one of them actually moves. A dungeon holds a dozen of
/// them, and an idle gate costs nothing at all.
/// </para>
///
/// <para>
/// Unlock conditions are hypotheses. A gate first seen beside a pack gets "every hostile in its
/// region is a ghost"; one with nothing else around gets "the objective has advanced, or the
/// nearest unknown object resolved". When the way through exists, whichever condition holds at that
/// moment is written down as the reason — and that reason is what teaches the taxonomy against the
/// objects involved. Gates teach the objects; that is the learning mechanism.
/// </para>
/// </summary>
public sealed class GateLedger
{
    /// <summary>How close a newly seen edge has to be to an existing gate to be the same one.</summary>
    private const float SameGate = 5f;

    /// <summary>How near a hostile has to be for a gate to look like a combat gate.</summary>
    private const float NearbyHostile = 15f;

    /// <summary>The radius a combat gate's condition cares about.</summary>
    private const float HostileRegion = 30f;

    /// <summary>How close, horizontally, counts as being past a gate.</summary>
    private const float PassedSlack = 4f;

    private readonly List<Gate> _gates = [];

    /// <summary>Hostiles as first seen, because the condition is about the pack that was there.</summary>
    private readonly List<(ulong Id, Vector3 Position)> _hostiles = [];

    private readonly HashSet<ulong> _ghosted = [];
    private readonly HashSet<uint> _resolved = [];
    private readonly Func<DateTime> _clock;

    private int _stage;

    public GateLedger(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public IReadOnlyList<Gate> Gates => _gates;

    public int OpenCount => _gates.Count(g => g.State is GateState.Open or GateState.Passed);

    public int PassedCount => _gates.Count(g => g.State == GateState.Passed);

    /// <summary>Edges the grid has shown us that the ledger has not seen before.</summary>
    public void Discover(IReadOnlyList<GateCandidate> candidates, WorldModel.Snapshot world, uint territory = 0)
    {
        foreach (var candidate in candidates)
        {
            if (_gates.Any(g => Vector3.Distance(g.Beyond, candidate.Beyond) <= SameGate))
                continue;

            var combat = world.Hostiles.Any(h => Vector3.Distance(h.Object.Position, candidate.Beyond) <= NearbyHostile);

            var kind = combat ? GateKind.Combat : GateKind.Unknown;

            var unlock = combat
                ? (GateCondition)new GateCondition.HostilesCleared(candidate.Beyond, HostileRegion)
                : FallbackHypothesis(world, candidate.Beyond);

            var id = $"{territory}:{MathF.Round(candidate.Frontier.X)}:{MathF.Round(candidate.Frontier.Y)}:" +
                     $"{MathF.Round(candidate.Frontier.Z)}";

            _gates.Add(new Gate(id, candidate.Frontier, candidate.Beyond, kind, unlock, _clock()));
        }
    }

    /// <summary>
    /// Takes this tick's world and decides which gates are worth asking about — which is the
    /// event-driven rule from the architecture: a gate is re-probed when the inputs of its own
    /// hypothesis move, never merely because a frame went by.
    /// </summary>
    public void Note(WorldModel.Snapshot world)
    {
        foreach (var recognised in world.Objects)
        {
            if (recognised.Object.Kind != WorldObjectKind.Hostile)
                continue;

            if (!_hostiles.Any(h => h.Id == recognised.Object.Id))
                _hostiles.Add((recognised.Object.Id, recognised.Object.Position));

            if (recognised.Done)
                _ghosted.Add(recognised.Object.Id);
        }

        _stage = world.Stage;

        foreach (var gate in _gates)
        {
            if (gate.State == GateState.Locked)
            {
                var signature = Signature(gate.Unlock);

                // The first look is a baseline, not a trigger. A gate the grid has just shown us is
                // still shut by definition — the mesh is what said so — and the architecture's rule
                // is to ask again when something that could have opened it has actually moved.
                if (gate.Signature == int.MinValue)
                    gate.Signature = signature;
                else if (signature != gate.Signature)
                {
                    gate.Signature = signature;
                    gate.Due = true;
                }
            }

            if (gate.State == GateState.Passed)
                continue;

            var dx = gate.Beyond.X - world.Position.X;
            var dz = gate.Beyond.Z - world.Position.Z;
            if (MathF.Sqrt((dx * dx) + (dz * dz)) > PassedSlack)
                continue;

            // Being past it is proof, whatever the condition said.
            gate.State = GateState.Passed;
            gate.Due = false;
            gate.CheckedUtc = _clock();
            gate.Evidence = "the character stood beyond it.";
        }
    }

    /// <summary>
    /// Ladder rung 1, from the other side: marks every locked gate worth asking about once more,
    /// because a gate whose condition the run has not understood may still have opened.
    /// </summary>
    public void RequestReprobe()
    {
        foreach (var gate in _gates.Where(g => g.State == GateState.Locked))
            gate.Due = true;
    }

    /// <summary>The locked gates whose condition has moved since the last probe — the only ones worth asking about.</summary>
    public IReadOnlyList<Gate> DueForProbe() => [.. _gates.Where(g => g.State == GateState.Locked && g.Due)];

    /// <summary>Records that an object's interaction resolved, which some gates are waiting on.</summary>
    public void Resolved(uint dataId)
    {
        _resolved.Add(dataId);

        foreach (var gate in _gates.Where(g => g.State == GateState.Locked))
        {
            if (ObjectsNamedBy(gate.Unlock).Contains(dataId))
                gate.Due = true;
        }
    }

    /// <summary>
    /// The way through exists now — a probe answered, or a person walked it. Returns whichever
    /// condition holds at this moment, which is the reason the gate opened and what the caller
    /// teaches the taxonomy from; null when it opened for a reason the run has not understood.
    /// </summary>
    public GateCondition? Opened(Gate gate, string how)
    {
        gate.State = GateState.Open;
        gate.Due = false;
        gate.Probes++;
        gate.CheckedUtc = _clock();
        gate.Evidence = how;

        if (Satisfied(gate.Unlock, out var why) is not { } held)
            return null;

        gate.Kind = held switch
        {
            GateCondition.HostilesCleared => GateKind.Combat,
            GateCondition.ObjectiveReached => GateKind.Objective,
            GateCondition.Triggered => GateKind.Trigger,
            _ => gate.Kind,
        };

        gate.Evidence = $"{how} What holds now: {why}.";
        return held;
    }

    /// <summary>An edge that answered "not yet": it is not asked about again until its inputs move.</summary>
    public void ProbeFailed(Gate gate, string reason)
    {
        gate.Due = false;
        gate.Probes++;
        gate.CheckedUtc = _clock();
        gate.Evidence = $"probed ({reason}) — still shut.";
    }

    /// <summary>A peer reports being beyond a gate, which is the fleet's proof that it opens (§5.4).</summary>
    public void PeerBeyond(string gateId)
    {
        if (_gates.FirstOrDefault(g => g.Id == gateId) is not { State: GateState.Locked } gate)
            return;

        gate.State = GateState.Open;
        gate.Due = false;
        gate.CheckedUtc = _clock();
        gate.Evidence = "a fleet peer reported being beyond it.";
    }

    /// <summary>A new run: gates are per-run state, and the taxonomy is where learning persists.</summary>
    public void Reset()
    {
        _gates.Clear();
        _hostiles.Clear();
        _ghosted.Clear();
        _resolved.Clear();
        _stage = 0;
    }

    /// <summary>The DataIds a condition names — what opening the gate teaches.</summary>
    public static IEnumerable<uint> ObjectsNamedBy(GateCondition condition)
        => condition switch
        {
            GateCondition.Triggered triggered => [triggered.DataId],
            GateCondition.AnyOf any => any.Alternatives.SelectMany(ObjectsNamedBy),
            _ => [],
        };

    /// <summary>
    /// A cheap digest of the inputs a condition depends on. If it has not moved, nothing about the
    /// condition can have changed and there is no reason to spend a pathfind on it.
    /// </summary>
    private int Signature(GateCondition condition)
        => condition switch
        {
            GateCondition.HostilesCleared cleared => HashCode.Combine(7, GhostsInRegion(cleared)),
            GateCondition.ObjectiveReached => HashCode.Combine(11, _stage),
            GateCondition.Triggered triggered => HashCode.Combine(13, _resolved.Contains(triggered.DataId)),
            GateCondition.AnyOf any => any.Alternatives.Aggregate(1, (hash, c) => HashCode.Combine(hash, Signature(c))),
            _ => 0,
        };

    private int GhostsInRegion(GateCondition.HostilesCleared cleared)
    {
        var ghosts = 0;
        foreach (var (id, position) in _hostiles)
        {
            if (Vector3.Distance(position, cleared.Centre) <= cleared.Radius && _ghosted.Contains(id))
                ghosts++;
        }

        return ghosts;
    }

    /// <summary>
    /// The condition that currently holds, or null when none does — in which case the gate is open
    /// for a reason the run has not understood, and its evidence says so rather than guessing.
    /// </summary>
    private GateCondition? Satisfied(GateCondition condition, out string why)
    {
        switch (condition)
        {
            case GateCondition.HostilesCleared cleared:
            {
                var total = 0;
                var ghosts = 0;
                foreach (var (id, position) in _hostiles)
                {
                    if (Vector3.Distance(position, cleared.Centre) > cleared.Radius)
                        continue;

                    total++;
                    if (_ghosted.Contains(id))
                        ghosts++;
                }

                why = $"{ghosts} of {total} hostiles in its region are ghosts";
                return total > 0 && ghosts == total ? cleared : null;
            }

            case GateCondition.ObjectiveReached reached:
                why = $"the objective stage is {_stage}";
                return _stage >= reached.Stage ? reached : null;

            case GateCondition.Triggered triggered:
                why = $"object {triggered.DataId} resolved";
                return _resolved.Contains(triggered.DataId) ? triggered : null;

            case GateCondition.AnyOf any:
            {
                var reasons = new List<string>();
                foreach (var alternative in any.Alternatives)
                {
                    if (Satisfied(alternative, out var alternativeWhy) is not null)
                    {
                        why = alternativeWhy;
                        return alternative;
                    }

                    reasons.Add(alternativeWhy);
                }

                why = string.Join("; ", reasons);
                return null;
            }

            default:
                why = "no condition is understood";
                return null;
        }
    }

    private static GateCondition FallbackHypothesis(WorldModel.Snapshot world, Vector3 beyond)
    {
        var alternatives = new List<GateCondition> { new GateCondition.ObjectiveReached(world.Stage + 1) };

        var nearestUnknown = world.Interactables
            .Where(o => o.Class == BehaviourClass.Unknown)
            .OrderBy(o => Vector3.Distance(o.Object.Position, beyond))
            .Cast<WorldModel.Recognised?>()
            .FirstOrDefault();

        if (nearestUnknown is { } unknown)
            alternatives.Add(new GateCondition.Triggered(unknown.Object.DataId));

        return alternatives.Count == 1 ? alternatives[0] : new GateCondition.AnyOf(alternatives);
    }
}
