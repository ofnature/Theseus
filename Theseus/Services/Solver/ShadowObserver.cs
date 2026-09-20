using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Theseus.Services.Duty;
using Theseus.Services.Frontier;
using Theseus.Services.Ipc;
using Theseus.Services.Run;

namespace Theseus.Services.Solver;

/// <summary>
/// The solver's perception, running underneath whatever is driving — the route executor today.
///
/// <para>
/// It never issues a move, never interacts and never touches the character. What it does is watch:
/// classify the objects the run passes, discover the edges the mesh refuses, notice what happens to
/// the things the route touches, and write all of it down — the taxonomy, the gate ledger, the gap
/// log. A farm run that was going to happen anyway teaches the dungeons the fleet already runs,
/// from the ground truth of a working route, which is what makes promotion to solver-driven a
/// data decision later rather than a leap of faith.
/// </para>
///
/// <para>
/// The one thing it learns without the loop's help is the pickup: an interactable that was there
/// and is now gone, while the character is still standing where it was, has been taken. That is the
/// architecture's own signal, and it needs nothing but the object table.
/// </para>
/// </summary>
public sealed class ShadowObserver : IPerception
{
    /// <summary>How far around the character the shadow looks. Grows when the ladder's first rung asks it to.</summary>
    private float _scanRadius = DefaultScanRadius;

    public const float DefaultScanRadius = 40f;

    /// <summary>The rung-1 widened radius: twice as far, for a solver that has run out of things to do.</summary>
    public const float WidenedScanRadius = 80f;

    /// <summary>How much of the scan radius still counts as looking at the same place.</summary>
    private const float StillLooking = 0.75f;

    private readonly IStepWorld _world;
    private readonly IObjectiveReader _objectives;
    private readonly Taxonomy _taxonomy;
    private readonly string _taxonomyPath;
    private readonly GhostCache _done;
    private readonly ReachableFrontier _frontier;
    private readonly GateLedger _ledger;
    private readonly GapLog _gaps;
    private readonly AriadneIpc _ariadne;
    private readonly Arbiter _arbiter;
    private readonly PromotionWatch? _promotion;
    private readonly Func<bool> _solverDriving;
    private readonly Func<bool> _inDuty;
    private readonly Func<string> _runId;
    private readonly Action<string>? _log;

    /// <summary>What the previous tick saw, so a disappearance can be told from a walk away.</summary>
    private readonly Dictionary<ulong, WorldObject> _seen = [];

    private readonly HashSet<uint> _gapLogged = [];

    /// <summary>Objects learned this run, by DataId — the run's own report card.</summary>
    private readonly HashSet<uint> _learned = [];

    private (uint Territory, uint Map, uint ContentId) _scope;
    private bool _active;
    private Vector3? _entrance;
    private Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)>? _probe;
    private Gate? _probing;

    /// <summary>What the arbiter would grant right now. Nothing acts on it: this is the evidence.</summary>
    private LoopDecision _decision = LoopDecision.Nothing;
    private int _decisionStage = int.MinValue;

    /// <param name="taxonomyPath">Where the learned taxonomy is written — in the plugin's config directory.</param>
    public ShadowObserver(
        IStepWorld world,
        IObjectiveReader objectives,
        Taxonomy taxonomy,
        string taxonomyPath,
        GhostCache done,
        ReachableFrontier frontier,
        GateLedger ledger,
        GapLog gaps,
        AriadneIpc ariadne,
        Arbiter arbiter,
        Func<bool> inDuty,
        Func<string> runId,
        Action<string>? log = null,
        PromotionWatch? promotion = null,
        Func<bool>? solverDriving = null)
    {
        _world = world;
        _objectives = objectives;
        _taxonomy = taxonomy;
        _taxonomyPath = taxonomyPath;
        _done = done;
        _frontier = frontier;
        _ledger = ledger;
        _gaps = gaps;
        _ariadne = ariadne;
        _arbiter = arbiter;
        _inDuty = inDuty;
        _runId = runId;
        _log = log;
        _promotion = promotion;
        _solverDriving = solverDriving ?? (() => false);
    }

    public int LearnedCount => _learned.Count;

    public IReadOnlyList<Gate> Gates => _ledger.Gates;

    /// <summary>
    /// The snapshot this tick was built from — the same world the driver is about to act on, so a
    /// decision and its evidence are never looking at two different frames. Null before the first
    /// tick and between duties.
    /// </summary>
    public WorldModel.Snapshot? LastSnapshot { get; private set; }

    /// <summary>Ladder rung 1: look twice as far, and re-ask every gate once.</summary>
    public void Widen()
    {
        _scanRadius = WidenedScanRadius;
        _ledger.RequestReprobe();
        _frontier.Invalidate();
    }

    /// <summary>How far it is looking, for whoever is wondering why something went unnoticed.</summary>
    public float ScanRadius => _scanRadius;

    /// <summary>One line for the debug window: what the shadow has seen and written down.</summary>
    public string Describe()
        => _active || _ledger.Gates.Count > 0
            ? $"watching · learned {_learned.Count} object(s) · gates {_ledger.Gates.Count} " +
              $"({_ledger.OpenCount} open, {_ledger.PassedCount} passed) · gaps {_gaps.Written} · " +
              $"frontier {_frontier.LastResult} · would be {_decision.Kind}: {_decision.Reason} · " +
              $"{_arbiter.Interactables.Describe} · {_arbiter.Exploration.Describe}"
            : "idle";

    /// <summary>One tick of watching. Cheap and non-blocking; safe to call every frame.</summary>
    public void Tick()
    {
        if (!_inDuty())
        {
            // Leaving is when a run's learning becomes worth keeping: the objects it touched will
            // be there next time, and the taxonomy is what remembers them.
            if (_active)
                End();

            return;
        }

        var objectives = _objectives.Read();
        var grid = _frontier.Refresh();
        var snapshot = WorldModel.Observe(_world, objectives, _taxonomy, _done, grid, _entrance, ScanRadius);
        LastSnapshot = snapshot;

        if (!_active || snapshot.Scope != _scope)
            Begin(snapshot);

        PumpProbe();

        _entrance ??= snapshot.Position;

        LearnFromWhatLeft(snapshot);
        NoteGaps(snapshot);
        ObserveGates(snapshot);
        NoteDecision(snapshot);

        _seen.Clear();
        foreach (var recognised in snapshot.Objects)
            _seen[recognised.Object.Id] = recognised.Object;
    }

    /// <summary>Writes what has been learned. Called when a run ends and on unload.</summary>
    public void Save() => _taxonomy.Save(_taxonomyPath);

    private void Begin(WorldModel.Snapshot snapshot)
    {
        if (_active)
            Save(); // the last run's learning before its state is dropped

        _active = true;
        _scope = snapshot.Scope;
        _entrance = snapshot.Position;
        _seen.Clear();
        _gapLogged.Clear();
        _learned.Clear();
        _ledger.Reset();
        _frontier.Invalidate();
        _log?.Invoke($"Shadow solver: watching {snapshot.Scope.Territory} — perception only, no movement.");
    }

    private void End()
    {
        Save();

        // The run's own verdict for the promotion record, written once the run is over.
        _promotion?.NoteRunEnded(_scope.Territory);

        _active = false;
        _probe = null;
        _probing = null;
        LastSnapshot = null;
        _scanRadius = DefaultScanRadius;
        _log?.Invoke($"Shadow solver: {_learned.Count} object(s) learned, {_ledger.Gates.Count} gate(s) seen " +
                     $"({_ledger.OpenCount} open).");
        _ledger.Reset();
        _seen.Clear();
        _entrance = null;
    }

    /// <summary>
    /// An interactable that was in the scan and is not any more, while the character is still
    /// standing where it was: it was taken. This is the architecture's own pickup signal — no
    /// inventory query, no dialog, nothing that can stall a run.
    /// </summary>
    private void LearnFromWhatLeft(WorldModel.Snapshot snapshot)
    {
        foreach (var (id, before) in _seen)
        {
            if (snapshot.Objects.Any(o => o.Object.Id == id))
                continue;

            if (before.Kind != WorldObjectKind.Interactable)
                continue;

            // Out of view is not gone: only count it if we are still looking at where it was.
            if (Vector3.Distance(snapshot.Position, before.Position) > ScanRadius * StillLooking)
                continue;

            if (!_learned.Add(before.DataId))
                continue;

            _taxonomy.Observe(before.DataId, BehaviourClass.PickupHold);
            _log?.Invoke($"Shadow solver: \"{before.Name}\" left the scan where it stood — learned as a pickup.");
        }
    }

    /// <summary>
    /// Objects nothing can name yet, one line each per run. Offline this is what a person reads to
    /// decide what an unknown thing actually was.
    /// </summary>
    private void NoteGaps(WorldModel.Snapshot snapshot)
    {
        foreach (var recognised in snapshot.Objects)
        {
            if (recognised.Object.Kind != Theseus.Services.Frontier.WorldObjectKind.Interactable)
                continue;

            if (recognised.Class != BehaviourClass.Unknown || !_gapLogged.Add(recognised.Object.DataId))
                continue;

            var nearby = snapshot.Objects
                .Where(o => o.Object.Id != recognised.Object.Id)
                .Take(5)
                .Select(o => new GapNeighbour(o.Object.DataId, o.Object.Kind.ToString(), o.Distance))
                .ToList();

            _gaps.Append(
                GapKind.UnknownInteractable,
                _runId(),
                snapshot.Scope.Territory,
                _ariadne.CurrentCacheKey,
                snapshot.Stage,
                recognised.Object.Position,
                recognised.Object.DataId,
                recognised.Object.Name,
                nearby,
                "seen by the shadow solver with no class in the taxonomy");
        }
    }

    /// <summary>
    /// Asks the arbiter what it would run, and writes the answer down at every objective boundary.
    ///
    /// <para>
    /// Nothing acts on this. It is the evidence the promotion decision is made from (§8.2): a
    /// territory is ready to hand over when the solver's would-be decisions line up with what the
    /// route actually did — at each stage, the next region it wanted was the one the route walked
    /// to, and the object it wanted was the one the route touched. A run where those disagree is a
    /// run with a gap, and the gap log says which kind.
    /// </para>
    /// </summary>
    private void NoteDecision(WorldModel.Snapshot snapshot)
    {
        _decision = _arbiter.Decide(snapshot);

        // §8.2's evidence, taken every tick: the stage boundary is where a route commits to a plan,
        // and it is the only place where "would the solver have done the same?" has an answer.
        _promotion?.Observe(snapshot, _decision, _solverDriving(), _arbiter.Exploration.Destination);

        if (snapshot.Stage == _decisionStage)
            return;

        _decisionStage = snapshot.Stage;
        _log?.Invoke($"Shadow solver: stage {snapshot.Stage} — it would be on {_decision.Kind}: {_decision.Reason}");
    }

    private void ObserveGates(WorldModel.Snapshot snapshot)
    {
        _ledger.Discover(snapshot.Gates, snapshot, snapshot.Scope.Territory);
        _ledger.Note(snapshot);

        // One edge at a time: a probe is a pipe round trip and none of this is urgent.
        if (_probe is not null)
            return;

        if (_ledger.DueForProbe().FirstOrDefault() is { } gate)
        {
            _probing = gate;
            _probe = _ariadne.PathfindDetailed(snapshot.Position, gate.Beyond, fly: false);
        }
    }

    private void PumpProbe()
    {
        if (_probe is not { IsCompleted: true } probe || _probing is not { } gate)
            return;

        _probe = null;
        _probing = null;

        if (probe.IsFaulted)
        {
            _ledger.ProbeFailed(gate, "the query failed");
            return;
        }

        var (result, waypoints, _, _) = probe.Result;

        if (waypoints.Count == 0)
        {
            _ledger.ProbeFailed(gate, result);
            return;
        }

        // A route exists that did not before: the edge is open. Whichever condition holds now is the
        // reason, and that is what teaches the objects involved — a gate that opened for a reason
        // nobody understands teaches nothing, and says so instead.
        var reason = _ledger.Opened(gate,
            $"a route to ({gate.Beyond.X:0.#}, {gate.Beyond.Y:0.#}, {gate.Beyond.Z:0.#}) exists now ({result}).");

        if (reason is null)
        {
            _log?.Invoke($"Shadow solver: gate at ({gate.Beyond.X:0.#}, {gate.Beyond.Y:0.#}, {gate.Beyond.Z:0.#}) " +
                         "opened for a reason the run has not understood.");
            _frontier.Invalidate();
            return;
        }

        foreach (var dataId in GateLedger.ObjectsNamedBy(reason))
        {
            // A gate that opens when an object resolves says that object is a trigger. TurnIn is not
            // inferable here: handing something in needs a held item, and only the loop has one.
            _taxonomy.Observe(dataId, BehaviourClass.DirectTrigger);
            _learned.Add(dataId);
        }

        _log?.Invoke($"Shadow solver: gate at ({gate.Beyond.X:0.#}, {gate.Beyond.Y:0.#}, {gate.Beyond.Z:0.#}) opened — {gate.Evidence}");
        _frontier.Invalidate(); // the walkable world changed: asks for a fresh grid
    }
}
