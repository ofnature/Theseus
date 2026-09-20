using Theseus.Services.Duty;

namespace Theseus.Tests;

public class EncounterClassifierTests
{
    private static DutyObjectiveSnapshot Snapshot(params (int Current, int Total)[] slots)
    {
        var objectives = new DutyObjective[slots.Length];
        for (var i = 0; i < slots.Length; i++)
            objectives[i] = new DutyObjective(i, slots[i].Current, slots[i].Total, false, $"objective {i}");

        return DutyObjectiveSnapshot.From(objectives, ObjectiveSource.Director);
    }

    [Fact]
    public void A_completed_objective_means_the_boss_died()
    {
        var outcome = EncounterClassifier.Classify(
            Snapshot((1, 1), (0, 1)),
            Snapshot((1, 1), (1, 1)));

        Assert.Equal(EncounterOutcome.Cleared, outcome);
    }

    [Fact]
    public void An_unchanged_objective_list_means_a_wipe()
    {
        // The module ended and the duty gained nothing — the party is dead, not victorious.
        var outcome = EncounterClassifier.Classify(
            Snapshot((1, 1), (0, 1)),
            Snapshot((1, 1), (0, 1)));

        Assert.Equal(EncounterOutcome.Wiped, outcome);
    }

    [Fact]
    public void Partial_progress_counts_as_a_clear()
    {
        var outcome = EncounterClassifier.Classify(
            Snapshot((1, 3)),
            Snapshot((2, 3)));

        Assert.Equal(EncounterOutcome.Cleared, outcome);
    }

    [Fact]
    public void A_newly_revealed_objective_counts_as_a_clear()
    {
        // Revealing the next objective is itself evidence the previous one was satisfied.
        var outcome = EncounterClassifier.Classify(
            Snapshot((0, 1)),
            Snapshot((1, 1), (0, 1)));

        Assert.Equal(EncounterOutcome.Cleared, outcome);
    }

    [Fact]
    public void Unreadable_objectives_refuse_to_answer()
    {
        // Guessing "wipe" here would send a successful run back to the last gate.
        Assert.Equal(
            EncounterOutcome.Unknown,
            EncounterClassifier.Classify(DutyObjectiveSnapshot.Unavailable, Snapshot((1, 1))));

        Assert.Equal(
            EncounterOutcome.Unknown,
            EncounterClassifier.Classify(Snapshot((0, 1)), DutyObjectiveSnapshot.Unavailable));
    }

    [Fact]
    public void A_duty_with_no_objectives_carries_no_evidence()
    {
        // Readable, but there is nothing that could have advanced. Answering "wipe" here would
        // report a wipe after every boss such content ever cleared.
        Assert.Equal(EncounterOutcome.Unknown, EncounterClassifier.Classify(Snapshot(), Snapshot()));
    }
}
