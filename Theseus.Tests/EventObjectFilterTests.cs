using Theseus.Services.Frontier;

namespace Theseus.Tests;

/// <summary>
/// The rows are real, read from the object sheet on 2026-09-30 for The Burn, The Ghimlyt Dark and
/// Xelphatol, so these tests pin the field data rather than an invented example.
/// </summary>
public class EventObjectFilterTests
{
    [Theory]
    [InlineData(2007457u, 0u)] // the unnamed marker in every Burn and Ghimlyt room
    [InlineData(2002735u, 0u)] // boss-room VFX
    [InlineData(2000182u, 0u)] // the entrance barrier
    [InlineData(2002872u, 0u)] // a wall
    public void An_object_that_runs_no_event_is_scenery(uint dataId, uint eventData)
        => Assert.Equal(EventObjectRole.Scenery, EventObjectFilter.Classify(dataId, eventData));

    [Theory]
    [InlineData(2007388u, 983602u)] // lift lever
    [InlineData(2007373u, 983606u)] // imposing gate
    [InlineData(2007397u, 983607u)] // bone key
    [InlineData(2007398u, 983608u)] // airstone
    [InlineData(2007400u, 983611u)] // tailwind relic
    public void An_object_that_runs_a_gimmick_is_a_mechanism(uint dataId, uint eventData)
        => Assert.Equal(EventObjectRole.Mechanism, EventObjectFilter.Classify(dataId, eventData));

    [Fact]
    public void The_exit_and_the_shortcut_have_roles_of_their_own()
    {
        // Both run gimmick events like a lever does, so the event alone would call them mechanisms.
        Assert.Equal(EventObjectRole.Exit, EventObjectFilter.Classify(2000139, 983045));
        Assert.Equal(EventObjectRole.Shortcut, EventObjectFilter.Classify(2000700, 983114));
    }

    [Fact]
    public void An_unreadable_sheet_keeps_the_object()
        => Assert.Equal(EventObjectRole.Mechanism, EventObjectFilter.Classify(2009999, null));
}
