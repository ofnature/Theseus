using Theseus.Services.Run;

namespace Theseus.Tests;

/// <summary>What <c>Theseus.IsBusy</c> answers, as a function of the run's state.</summary>
public class BusyTests
{
    private static bool Busy(
        RunState state,
        bool inDuty = true,
        bool enabled = true,
        bool runOnArrival = false,
        bool autoStartWaiting = false,
        bool stopPending = false)
        => RunController.ComputeBusy(enabled, state, inDuty, runOnArrival, autoStartWaiting, stopPending);

    [Theory]
    [InlineData(RunState.Entering)]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Combat)]
    [InlineData(RunState.BossHandoff)]
    [InlineData(RunState.Exiting)]
    public void Driving_states_are_busy(RunState state) => Assert.True(Busy(state));

    [Theory]
    [InlineData(RunState.Idle)]
    [InlineData(RunState.Faulted)]
    public void Idle_and_faulted_are_not(RunState state) => Assert.False(Busy(state));

    [Fact]
    public void A_pending_stop_stays_busy_while_the_character_is_still_in_the_duty()
    {
        // The run faulted at step 6 after another plugin asked for the character back. Reading
        // idle here told that plugin to go, with the character still inside the instance.
        Assert.True(Busy(RunState.Faulted, stopPending: true));
        Assert.True(Busy(RunState.Idle, stopPending: true));
    }

    [Fact]
    public void A_pending_stop_settles_once_the_character_is_out()
        => Assert.False(Busy(RunState.Idle, inDuty: false, stopPending: true));

    [Fact]
    public void The_pause_before_a_run_starts_by_itself_is_busy()
    {
        Assert.True(Busy(RunState.Idle, runOnArrival: true));     // an entry we were asked for
        Assert.True(Busy(RunState.Idle, autoStartWaiting: true)); // "start automatically", barrier still up
        Assert.False(Busy(RunState.Idle, inDuty: false, runOnArrival: true));
    }

    [Fact]
    public void A_disabled_plugin_is_never_busy()
    {
        Assert.False(Busy(RunState.Running, enabled: false));
        Assert.False(Busy(RunState.Faulted, enabled: false, stopPending: true));
    }
}
