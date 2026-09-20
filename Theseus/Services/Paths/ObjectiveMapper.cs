using System.Collections.Generic;
using Theseus.Services.Duty;

namespace Theseus.Services.Paths;

/// <summary>
/// Learns which duty objective each step belongs to, by watching the game while the run executes.
///
/// <para>
/// Imported routes carry no objective information — AutoDuty's format has nowhere to put it — so
/// the mapping is derived rather than authored: record what the game says is the current objective
/// as each step runs, and after one clean run the route is tagged. The tags are what make resume
/// safe, because relocalization then searches only the steps belonging to the objective that is
/// live now, instead of the whole route where a coincidentally-nearby step on the far side of a
/// wall can win.
/// </para>
///
/// <para>
/// The learned map is <b>ours</b>: it is derived from the game's own state, not from anything in
/// AutoDuty's files, so unlike the routes themselves it can ship as a seed.
/// </para>
/// </summary>
public sealed class ObjectiveMapper
{
    private readonly Dictionary<int, int> _observed = [];
    private readonly HashSet<int> _conflicts = [];

    /// <summary>Steps whose objective has been observed this run.</summary>
    public int ObservedStepCount => _observed.Count;

    /// <summary>
    /// Steps seen under more than one objective. Nonzero is not a failure — a retry loop can
    /// straddle an objective boundary — but a large number means the route and the duty's own
    /// structure disagree, which is worth surfacing to whoever maintains the path.
    /// </summary>
    public int ConflictCount => _conflicts.Count;

    /// <summary>
    /// Records the objective that was live as a step ran. Called once per step transition.
    ///
    /// <para>
    /// The first observation of a step wins. Backward jumps in the library are overwhelmingly
    /// short retry loops — "is the door targetable yet? no, step back one and look again" — and a
    /// retry that happens to straddle an objective boundary should not relabel the step that
    /// legitimately belongs to the earlier one.
    /// </para>
    /// </summary>
    public void Observe(int stepIndex, DutyObjectiveSnapshot snapshot)
    {
        if (stepIndex < 0 || !snapshot.Available || snapshot.TotalCount == 0)
            return;

        var stage = snapshot.Stage;
        if (_observed.TryGetValue(stepIndex, out var existing))
        {
            if (existing != stage)
                _conflicts.Add(stepIndex);
            return;
        }

        _observed[stepIndex] = stage;
    }

    /// <summary>
    /// Writes what was learned onto a path.
    ///
    /// <para>
    /// Only fills in steps that are still unknown: once a step is tagged the tag is stable, so a
    /// later run that took a different route through the same dungeon cannot churn a working map.
    /// Correcting a wrong tag is a re-import, deliberately.
    /// </para>
    /// </summary>
    /// <returns>Whether anything changed, so the caller only writes to disk when it must.</returns>
    public bool Apply(ThreadPath path)
    {
        var changed = false;

        foreach (var (stepIndex, stage) in _observed)
        {
            if (stepIndex >= path.Steps.Count)
                continue;

            var step = path.Steps[stepIndex];
            if (step.ObjectiveIndex != ThreadPath.UnknownObjective)
                continue;

            step.ObjectiveIndex = stage;
            changed = true;
        }

        return changed;
    }

    /// <summary>Clears the run's observations, ready for the next one.</summary>
    public void Reset()
    {
        _observed.Clear();
        _conflicts.Clear();
    }
}
