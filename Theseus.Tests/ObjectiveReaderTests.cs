using Theseus.Services.Duty;
using UiObjectiveType = FFXIVClientStructs.FFXIV.Client.UI.Arrays.ToDoListNumberArray.ObjectiveType;

namespace Theseus.Tests;

/// <summary>
/// The parts of the reader that can be tested without a game attached: slot decoding, the
/// unrevealed-slot heuristic, and the snapshot derivation both sources share.
/// </summary>
public class ObjectiveReaderTests
{
    [Theory]
    [InlineData("???")]
    [InlineData("??? ??? ???")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unrevealed_slots_are_recognised(string text)
        => Assert.True(ObjectiveReader.LooksHidden(text));

    [Theory]
    [InlineData("Traverse Quan Caverns")]
    [InlineData("Defeat the Treno catoblepas")]
    [InlineData("Where is the exit?")]
    public void Revealed_text_is_not_treated_as_hidden(string text)
        => Assert.False(ObjectiveReader.LooksHidden(text));

    [Fact]
    public void The_hud_array_reports_a_percentage_not_a_count()
    {
        // Measured in Mistwake: two objectives the HUD showed as "1/1" both carried 0x64 (100),
        // and a "0/1" carried 0. The counts live only in the localized display string.
        Assert.Equal((100, 100), ObjectiveReader.SplitToDoListValue(UiObjectiveType.FractionBar, 0x64));
        Assert.Equal((0, 100), ObjectiveReader.SplitToDoListValue(UiObjectiveType.FractionBar, 0));

        Assert.True(new DutyObjective(0, 100, 100, false, "done").IsComplete);
        Assert.False(new DutyObjective(0, 0, 100, false, "not done").IsComplete);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Unset_hud_slots_read_as_empty_rather_than_enormous(int raw)
    {
        // Unused slots hold -1. Taken at face value that is an objective at four billion percent.
        Assert.Equal((0, 0), ObjectiveReader.SplitToDoListValue(UiObjectiveType.FractionBar, raw));
        Assert.Equal((0, 0), ObjectiveReader.SplitToDoListValue(UiObjectiveType.Text, raw));
    }

    [Theory]
    [InlineData(UiObjectiveType.Text)]
    [InlineData(UiObjectiveType.TimeRemaining)]
    [InlineData(UiObjectiveType.QuestTitle)]
    public void Shapes_that_carry_no_progress_never_read_as_complete(UiObjectiveType shape)
    {
        // Over-reporting completion walks a fleet gate open; under-reporting only costs precision.
        var (current, total) = ObjectiveReader.SplitToDoListValue(shape, 100);
        Assert.Equal(0, total);
        Assert.False(new DutyObjective(0, current, total, false, "x").IsComplete);
    }

    [Fact]
    public void A_completion_bitmask_marks_the_objectives_it_names()
    {
        // Confirmed in Mistwake: 0x3 with objectives 0 and 1 done. Read as a count that would be
        // 2 — which as a mask marks objective 1 done and objective 0 not, exactly backwards.
        const uint mask = 0x3;

        var objectives = new DutyObjective[3];
        for (var i = 0; i < objectives.Length; i++)
            objectives[i] = new DutyObjective(i, 0, 0, false, $"objective {i}",
                ReportedComplete: (mask & (1u << i)) != 0);

        var snapshot = DutyObjectiveSnapshot.From(objectives, ObjectiveSource.ToDoList);

        Assert.Equal(2, snapshot.CompletedCount);
        Assert.Equal(2, snapshot.CurrentIndex);
    }

    [Fact]
    public void A_completed_objective_is_recognised_even_when_the_game_flag_stays_false()
    {
        // Measured in Mistwake: DirectorTodo.Complete was still false on two objectives the HUD
        // had already ticked. A reader trusting only that flag reports a barely-started dungeon.
        var snapshot = DutyObjectiveSnapshot.From(
            [
                new DutyObjective(0, 1, 1, false, "Traverse Quan Caverns", ReportedComplete: false),
                new DutyObjective(1, 1, 1, false, "Defeat the Treno catoblepas", ReportedComplete: false),
                new DutyObjective(2, 0, 1, false, "Traverse the Treno lowlands", ReportedComplete: false),
            ],
            ObjectiveSource.Director);

        Assert.Equal(2, snapshot.CompletedCount);
        Assert.Equal(2, snapshot.CurrentIndex);
    }

    [Fact]
    public void Only_slots_the_duty_is_using_count_toward_the_total()
    {
        // Mistwake, measured: the director publishes ten slots of which six are enabled, matching
        // the HUD's own DutyObjectiveCount. Counting the whole block reported "0/10" for a
        // six-objective dungeon, which would push every fleet gate four objectives off the end.
        //
        // This mirrors what the reader builds after stopping at the first disabled slot — five
        // unrevealed objectives behind one visible first one.
        var snapshot = DutyObjectiveSnapshot.From(
            [
                new DutyObjective(0, 0, 1, false, "Traverse Quan Caverns"),
                new DutyObjective(1, 0, 0, true, string.Empty),
                new DutyObjective(2, 0, 0, true, string.Empty),
                new DutyObjective(3, 0, 0, true, string.Empty),
                new DutyObjective(4, 0, 0, true, string.Empty),
                new DutyObjective(5, 0, 0, true, string.Empty),
            ],
            ObjectiveSource.Director);

        Assert.Equal(6, snapshot.TotalCount);
        Assert.Equal(0, snapshot.CompletedCount);
        Assert.Equal(0, snapshot.CurrentIndex);
        Assert.Equal(0, snapshot.Stage);
    }

    [Fact]
    public void Snapshot_derivation_counts_hidden_slots_and_finds_the_first_incomplete()
    {
        var snapshot = DutyObjectiveSnapshot.From(
            [
                new DutyObjective(0, 1, 1, false, "Traverse Quan Caverns"),
                new DutyObjective(1, 1, 1, false, "Defeat the Treno catoblepas"),
                new DutyObjective(2, 0, 1, false, "Traverse the Treno lowlands"),
                new DutyObjective(3, 0, 0, true, string.Empty),
            ],
            ObjectiveSource.Director);

        Assert.True(snapshot.Available);
        Assert.Equal(ObjectiveSource.Director, snapshot.Source);
        Assert.Equal(4, snapshot.TotalCount);
        Assert.Equal(2, snapshot.CompletedCount);
        Assert.Equal(2, snapshot.CurrentIndex);
    }

    [Fact]
    public void Snapshot_with_everything_done_reports_no_current_objective()
    {
        var snapshot = DutyObjectiveSnapshot.From(
            [
                new DutyObjective(0, 1, 1, false, "Traverse Quan Caverns"),
                new DutyObjective(1, 0, 0, false, "Defeat the boss", ReportedComplete: true),
            ],
            ObjectiveSource.Director);

        Assert.Equal(2, snapshot.CompletedCount);
        Assert.Equal(-1, snapshot.CurrentIndex);
    }

    [Fact]
    public void An_empty_but_readable_duty_is_not_the_same_as_unavailable()
    {
        // A director that publishes no todos is an answer: "this content has no objectives".
        // Having no director to ask is not, and only the second may trigger position-only resume.
        var empty = DutyObjectiveSnapshot.From([], ObjectiveSource.Director);
        Assert.True(empty.Available);
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(-1, empty.CurrentIndex);

        Assert.False(DutyObjectiveSnapshot.Unavailable.Available);
        Assert.Equal(ObjectiveSource.None, DutyObjectiveSnapshot.Unavailable.Source);
    }
}
