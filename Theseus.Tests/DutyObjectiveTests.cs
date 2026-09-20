using Theseus.Services.Duty;

namespace Theseus.Tests;

public class DutyObjectiveTests
{
    [Fact]
    public void Fraction_objective_is_complete_when_current_reaches_total()
    {
        Assert.True(new DutyObjective(0, 1, 1, false, "Traverse Quan Caverns").IsComplete);
        Assert.False(new DutyObjective(1, 0, 1, false, "Traverse the Treno lowlands").IsComplete);
    }

    [Fact]
    public void Multi_step_objective_needs_every_step()
    {
        var partial = new DutyObjective(0, 2, 3, false, "Defeat the catoblepas");
        Assert.False(partial.IsComplete);
        Assert.True((partial with { Current = 3 }).IsComplete);
    }

    [Fact]
    public void Objective_without_a_fraction_is_not_inferred_complete()
    {
        // Text/Bar shapes carry no denominator — treating "0 of 0" as done would mark an entire
        // dungeon finished the moment a non-fraction objective appeared.
        Assert.False(new DutyObjective(0, 0, 0, false, "Explore the caverns").IsComplete);
    }

    [Fact]
    public void Game_reported_completion_wins_over_the_fraction()
    {
        // "Explore the caverns" ticks in the HUD without any number ever moving, so the director's
        // own Complete flag is the only thing that can say so — inference cannot.
        Assert.True(new DutyObjective(0, 0, 0, false, "Explore the caverns", ReportedComplete: true).IsComplete);
    }

    [Fact]
    public void Unavailable_is_distinguishable_from_nothing_completed()
    {
        // The whole resume design depends on this: "we cannot read objectives" must never look
        // like "we are at the start of the dungeon", because the first means fall back to
        // position-only and the second means step 0.
        var unavailable = DutyObjectiveSnapshot.Unavailable;
        Assert.False(unavailable.Available);
        Assert.Equal(0, unavailable.TotalCount);
        Assert.Equal(-1, unavailable.CurrentIndex);

        var atStart = new DutyObjectiveSnapshot(
            [new DutyObjective(0, 0, 1, false, "Traverse Quan Caverns")], 0, 0, true);
        Assert.True(atStart.Available);
        Assert.Equal(0, atStart.CompletedCount);
    }

    [Fact]
    public void Hidden_slots_still_count_toward_the_total()
    {
        // The game renders unrevealed objectives as "???" but they occupy slots, so the total is
        // knowable before the names are — that is what makes "objective 3 of 6" possible.
        var snapshot = new DutyObjectiveSnapshot(
            [
                new DutyObjective(0, 1, 1, false, "Traverse Quan Caverns"),
                new DutyObjective(1, 1, 1, false, "Defeat the Treno catoblepas"),
                new DutyObjective(2, 0, 1, false, "Traverse the Treno lowlands"),
                new DutyObjective(3, 0, 0, true, string.Empty),
                new DutyObjective(4, 0, 0, true, string.Empty),
            ],
            CurrentIndex: 2,
            CompletedCount: 2,
            Available: true);

        Assert.Equal(5, snapshot.TotalCount);
        Assert.Equal(2, snapshot.CompletedCount);
        Assert.Equal(2, snapshot.CurrentIndex);
    }

    [Fact]
    public void Null_reader_reports_unavailable_rather_than_empty()
    {
        Assert.False(new NullObjectiveReader().Read().Available);
    }
}
