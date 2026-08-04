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
public readonly record struct DutyObjective(int Index, int Current, int Total, bool Hidden, string Text)
{
    /// <summary>Objective satisfied — the checkmark state in the HUD.</summary>
    public bool IsComplete => Total > 0 && Current >= Total;
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
public readonly record struct DutyObjectiveSnapshot(
    IReadOnlyList<DutyObjective> Objectives,
    int CurrentIndex,
    int CompletedCount,
    bool Available)
{
    public static DutyObjectiveSnapshot Unavailable { get; } =
        new([], -1, 0, false);

    public int TotalCount => Objectives.Count;
}
