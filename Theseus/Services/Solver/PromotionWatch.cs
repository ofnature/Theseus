using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Theseus.Services.Solver;

/// <summary>
/// Scores whether the solver would have done what the route did (§8.2).
///
/// <para>
/// The promotion decision needs an answer to one question — if the solver had been driving, would the
/// run have gone the same way? — and the honest way to get it is to ask at every objective boundary,
/// because that is where a route commits to a plan: at each stage change, this takes the solver's
/// would-be pick, then watches whether the route does that thing. A destination counts as agreed
/// when the character comes within a few yalms of it while the stage holds; an object counts as
/// agreed when it leaves the scan from where it stood, which is the same pickup signal the shadow
/// already uses.
/// </para>
///
/// <para>
/// <b>Disagreement is information, not a strike.</b> It writes a gap log line naming both what the
/// solver wanted and where the run went instead, because the route may simply be taking a shortcut
/// the solver should learn as a zone override. What it does mean is that the run did not demonstrate
/// the solver could have driven it, so it does not count toward promotion — three consecutive clean
/// route runs do, and a disagreement restarts that count.
/// </para>
///
/// <para>
/// Boundaries where the solver wanted to fight, or wanted nothing at all, are not scored: there is
/// nothing to compare. A run with no scored boundary demonstrates nothing either, and is not counted
/// as evidence in either direction.
/// </para>
/// </summary>
public sealed class PromotionWatch
{
    /// <summary>Close enough to the solver's destination to call the route's walk the same walk.</summary>
    public const float AgreementRadius = 6f;

    /// <summary>How long a boundary stays open before the route is judged to have gone elsewhere.</summary>
    public static readonly TimeSpan BoundaryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How close the character must be to an object for its vanishing to count as taken.</summary>
    private const float TakenRadius = 8f;

    private readonly SolverRecordStore _records;
    private readonly GapLog _gaps;
    private readonly Func<string> _runId;
    private readonly Func<string> _cacheKey;
    private readonly Action<string>? _log;
    private readonly string _recordsPath;

    private int _stage = int.MinValue;
    private bool _open;
    private LoopKind _pickKind = LoopKind.Idle;
    private Vector3 _pickPosition;
    private uint _pickDataId;
    private string _pickName = string.Empty;
    private DateTime _openedUtc;

    public PromotionWatch(
        SolverRecordStore records,
        GapLog gaps,
        Func<string> runId,
        Func<string> cacheKey,
        Action<string>? log = null,
        string recordsPath = "")
    {
        _recordsPath = recordsPath;
        _records = records;
        _gaps = gaps;
        _runId = runId;
        _cacheKey = cacheKey;
        _log = log;
    }

    /// <summary>Boundaries scored this run, and how they came out.</summary>
    public int Boundaries { get; private set; }

    public int Agreed { get; private set; }

    public int Disagreed { get; private set; }

    /// <summary>The most recent boundary's verdict, for the debug window.</summary>
    public string LastVerdict { get; private set; } = "no boundary scored yet";

    /// <summary>One line: how the last boundary went and what this run has shown so far.</summary>
    public string Describe()
        => $"{LastVerdict} · this run {Agreed} agreed, {Disagreed} disagreed of {Boundaries} boundary(ies)";

    /// <summary>
    /// Called once per tick, with the shadow's snapshot and the arbiter's would-be decision. Does
    /// nothing at all while the solver is the one driving: a promoted run is scored by whether it
    /// gets to the end, not by agreement with itself.
    /// </summary>
    public void Observe(WorldModel.Snapshot world, LoopDecision decision, bool solverDriving, Vector3? destination = null)
    {
        if (solverDriving)
            return;

        if (world.Stage != _stage)
        {
            _stage = world.Stage;
            Open(world, decision, destination);
            return;
        }

        if (!_open)
            return;

        if (Satisfied(world))
        {
            Close(true, world, "the route did what the solver wanted");
            return;
        }

        if (world.UtcNow - _openedUtc >= BoundaryTimeout)
            Close(false, world, "the route went elsewhere inside the boundary's window");
    }

    /// <summary>
    /// Ends the run's scoring and writes it down. A run that agreed on every boundary it scored, and
    /// scored at least one, is evidence; anything else is not, and the gap log already said which
    /// boundary went the other way.
    /// </summary>
    public void NoteRunEnded(uint territory)
    {
        if (_open)
        {
            // The run ended mid-boundary. Neither verdict is available — the route did not get the
            // chance to agree or disagree — so it is not counted at all.
            _open = false;
        }

        var record = _records.For(territory);

        if (Boundaries > 0 && Agreed > 0 && Disagreed == 0)
        {
            record.ShadowAgreements++;
            record.LastUtc = DateTime.UtcNow;
            _log?.Invoke($"Solver: territory {territory} agreed on {Agreed} boundary(ies) — " +
                         $"{record.ShadowAgreements}/{SolverRecordStore.AgreementsToPromote} clean route runs.");

            if (_records.Promote(territory))
            {
                _log?.Invoke($"Solver: territory {territory} is promoted — the next run there drives " +
                             "with the solver, and the route stays as the way back in.");
            }
        }
        else if (Disagreed > 0)
        {
            record.ShadowDisagreements++;
            record.ShadowAgreements = 0;
            record.LastUtc = DateTime.UtcNow;
            _log?.Invoke($"Solver: territory {territory} disagreed on {Disagreed} boundary(ies) — " +
                         "the clean-run count starts again, and the gap log says where.");
        }

        if (_recordsPath.Length > 0)
            _records.Save(_recordsPath);

        Boundaries = 0;
        Agreed = 0;
        Disagreed = 0;
        _stage = int.MinValue;
    }

    /// <summary>Takes the solver's pick at the moment the stage moved.</summary>
    private void Open(WorldModel.Snapshot world, LoopDecision decision, Vector3? destination)
    {
        _openedUtc = world.UtcNow;
        _pickKind = decision.Kind;
        _pickDataId = 0;
        _pickName = string.Empty;

        Vector3? place = null;

        if (decision.Kind == LoopKind.Exploration)
        {
            place = destination ?? world.Unexplored;
        }
        else if (decision.Kind == LoopKind.Interactable && world.Interactables.FirstOrDefault() is { } interactable)
        {
            // The nearest object is what the loop would go for too — its own pick, minus the claims
            // and gates it filters by, which at a boundary are the same answer almost always.
            place = interactable.Object.Position;
            _pickDataId = interactable.Object.DataId;
            _pickName = interactable.Object.Name;
        }

        if (place is not { } target)
        {
            // Combat, a boss handoff, a transit, or a pick with nowhere to point at: nothing to
            // compare, so the boundary is not scored rather than scored against the route.
            _open = false;
            LastVerdict = $"boundary at stage {world.Stage}: nothing to compare";
            return;
        }

        _pickPosition = target;
        _open = true;
        Boundaries++;
        LastVerdict = $"boundary at stage {world.Stage}: solver wanted {DescribePick()}";
    }

    private string DescribePick()
        => _pickKind switch
        {
            LoopKind.Exploration => $"the ground at ({_pickPosition.X:0.#}, {_pickPosition.Z:0.#})",
            LoopKind.Interactable => $"\"{_pickName}\"",
            _ => _pickKind.ToString(),
        };

    /// <summary>Whether the route has now done the thing the solver wanted at this boundary.</summary>
    private bool Satisfied(WorldModel.Snapshot world)
    {
        switch (_pickKind)
        {
            case LoopKind.Exploration:
                return Vector3.Distance(world.Position, _pickPosition) <= AgreementRadius;

            case LoopKind.Interactable:
                var stillThere = world.Objects.Any(o => o.Object.DataId == _pickDataId);
                return !stillThere && Vector3.Distance(world.Position, _pickPosition) <= TakenRadius;

            default:
                return false;
        }
    }

    private void Close(bool agreed, WorldModel.Snapshot world, string why)
    {
        _open = false;

        if (agreed)
        {
            Agreed++;
            LastVerdict = $"boundary at stage {world.Stage}: agreed — the route went to {DescribePick()}";
            return;
        }

        Disagreed++;
        LastVerdict = $"boundary at stage {world.Stage}: disagreed — the route did not go to {DescribePick()}";

        _gaps.Append(
            GapKind.DriverDisagreement,
            _runId(),
            world.Scope.Territory,
            _cacheKey(),
            world.Stage,
            world.Position,
            _pickDataId is 0 ? null : _pickDataId,
            _pickName.Length > 0 ? _pickName : null,
            detail: $"the solver would have gone to {DescribePick()} and the route went elsewhere");
    }
}
