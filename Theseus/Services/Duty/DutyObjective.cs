using System.Collections.Generic;

namespace Theseus.Services.Duty;

/// <summary>
/// One row of the in-game Duty Information panel ("Traverse Quan Caverns  1/1 ✔").
/// </summary>
/// <param name="Index">Slot position, 0-based. This — never the localized text — is the identity.</param>
/// <param name="Current">Progress numerator (the 1 in 1/1).</param>
/// <param name="Total">Progress denominator (the 1 in 1/1); 0 when the shape carries no fraction.</param>
/// <param name="Hidden">The game is still rendering this slot as "???" — it exists but is not yet revealed.</param>
/// <param name="Text">
/// Display text, for the UI only. LOCALIZED and often carries dynamic counts — never match on it.
/// </param>
/// <param name="ReportedComplete">
/// The game saying so directly, rather than us inferring it from the numbers.
///
/// <para>
/// Corroborating rather than authoritative. Measured in Mistwake: <c>DirectorTodo.Complete</c> was
/// still <b>false</b> on two objectives the HUD had already ticked, so a reader trusting only that
/// flag would have reported a dungeon barely started. The fraction is what actually moves. This
/// stays because the HUD-array fallback has no fraction of its own and does have a reliable
/// completion bitmask, and because a shape that ticks without any number moving would otherwise be
/// undetectable — the two sources cover each other's blind spots.
/// </para>
/// </param>
public readonly record struct DutyObjective(
    int Index,
    int Current,
    int Total,
    bool Hidden,
    string Text,
    bool ReportedComplete = false)
{
    /// <summary>
    /// Objective satisfied — the checkmark state in the HUD. The game's flag wins; the fraction is
    /// only consulted for sources that do not carry one (the UI-array fallback).
    /// </summary>
    public bool IsComplete => ReportedComplete || (Total > 0 && Current >= Total);
}

/// <summary>Which reader produced a snapshot. Diagnostic, but load-bearing diagnostics.</summary>
public enum ObjectiveSource
{
    /// <summary>Nothing was readable.</summary>
    None,

    /// <summary>
    /// <c>Director.DirectorTodos</c> — director state, populated whether or not the Duty
    /// Information HUD is shown, and carrying an explicit per-objective completion flag.
    /// </summary>
    Director,

    /// <summary>
    /// <c>ToDoListNumberArray</c> — the UI mirror. Only populated while the HUD element exists, so
    /// falling back to it silently is worth noticing: hide the HUD and objectives vanish.
    /// </summary>
    ToDoList,
}

/// <summary>
/// A snapshot of every objective for the current duty, plus the derived position in the list.
/// </summary>
/// <param name="Objectives">All slots in order, including still-hidden ones.</param>
/// <param name="CurrentIndex">First incomplete slot, or -1 when everything is done / nothing is readable.</param>
/// <param name="CompletedCount">How many slots are satisfied.</param>
/// <param name="Available">
/// False when no objective data could be read at all. Callers MUST degrade to position-only
/// behaviour rather than treating this as "no objectives complete" — see the plan doc's
/// fallback rule.
/// </param>
/// <param name="Source">Which reader answered. <see cref="ObjectiveSource.None"/> when none did.</param>
public readonly record struct DutyObjectiveSnapshot(
    IReadOnlyList<DutyObjective> Objectives,
    int CurrentIndex,
    int CompletedCount,
    bool Available,
    ObjectiveSource Source = ObjectiveSource.None)
{
    public static DutyObjectiveSnapshot Unavailable { get; } =
        new([], -1, 0, false);

    public int TotalCount => Objectives.Count;

    /// <summary>
    /// How far through the objective list the duty is: the first incomplete slot, or the total
    /// count once everything is done.
    ///
    /// <para>
    /// This is what steps are tagged and fleet gates are keyed on, rather than
    /// <see cref="CurrentIndex"/> directly. The difference is the tail: after the last boss dies
    /// there is still a walk to the exit, and <see cref="CurrentIndex"/> reports -1 for all of it,
    /// which would leave those steps permanently untagged and unresumable. Counting completions
    /// instead runs 0..N and never goes backwards.
    /// </para>
    /// </summary>
    public int Stage => CurrentIndex >= 0 ? CurrentIndex : TotalCount;

    /// <summary>
    /// Builds a snapshot from ordered slots, deriving the current index and completed count so the
    /// two readers cannot disagree about what "current" means.
    /// </summary>
    public static DutyObjectiveSnapshot From(IReadOnlyList<DutyObjective> objectives, ObjectiveSource source)
    {
        var completed = 0;
        var current = -1;
        for (var i = 0; i < objectives.Count; i++)
        {
            if (objectives[i].IsComplete)
                completed++;
            else if (current < 0)
                current = i;
        }

        return new DutyObjectiveSnapshot(objectives, current, completed, true, source);
    }
}
