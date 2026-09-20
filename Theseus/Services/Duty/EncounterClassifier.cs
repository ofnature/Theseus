namespace Theseus.Services.Duty;

/// <summary>How a boss encounter ended.</summary>
public enum EncounterOutcome
{
    /// <summary>Objectives could not be read, so the two cases are indistinguishable — ask, do not guess.</summary>
    Unknown,

    /// <summary>The boss died: an objective advanced while the module was ending.</summary>
    Cleared,

    /// <summary>The module ended with nothing gained. The party is dead or the encounter reset.</summary>
    Wiped,
}

/// <summary>
/// Decides whether a boss encounter was a clear or a wipe.
///
/// <para>
/// BossMod's <c>HasActiveModule</c> goes true→false on both outcomes and carries no discriminator,
/// and the duty objective list on its own cannot tell "boss not dead yet" from "boss fight over".
/// Neither signal is sufficient; together they are decisive. The caller supplies the module-ended
/// edge, this supplies the verdict.
/// </para>
/// </summary>
public static class EncounterClassifier
{
    /// <summary>
    /// Classifies an encounter that has just ended, from objective state either side of it.
    /// </summary>
    /// <param name="before">Objectives sampled when the module became active.</param>
    /// <param name="after">Objectives sampled once the module went inactive.</param>
    public static EncounterOutcome Classify(DutyObjectiveSnapshot before, DutyObjectiveSnapshot after)
    {
        // Refusing to answer is a real answer here. Calling an unreadable encounter a wipe would
        // send the run back to the last gate after a perfectly good clear.
        if (!before.Available || !after.Available)
            return EncounterOutcome.Unknown;

        // Readable but empty is not evidence of anything. Content that populates no objectives
        // would otherwise report a wipe after every single boss it ever cleared.
        if (before.TotalCount == 0 && after.TotalCount == 0)
            return EncounterOutcome.Unknown;

        return ObjectivesAdvanced(before, after) ? EncounterOutcome.Cleared : EncounterOutcome.Wiped;
    }

    /// <summary>
    /// Whether the duty made progress between two snapshots. Slots are compared by index — never
    /// by text — and a newly appearing slot counts, since revealing the next objective is itself
    /// evidence the previous one was satisfied.
    /// </summary>
    internal static bool ObjectivesAdvanced(DutyObjectiveSnapshot before, DutyObjectiveSnapshot after)
    {
        if (after.CompletedCount > before.CompletedCount)
            return true;

        // A duty that swaps its objective list wholesale (phase changes do this) has advanced by
        // definition — there is no prior slot left to compare against.
        if (after.TotalCount != before.TotalCount)
            return true;

        for (var i = 0; i < after.TotalCount; i++)
        {
            // Partial progress counts: "Defeat 3 adds" going 2/3 → 3/3 completes the objective,
            // but 1/3 → 2/3 during a multi-pull boss is progress too.
            if (after.Objectives[i].Current > before.Objectives[i].Current)
                return true;
        }

        return false;
    }
}
