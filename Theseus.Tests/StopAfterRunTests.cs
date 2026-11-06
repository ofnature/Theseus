using Theseus.Services.Run;

namespace Theseus.Tests;

public class StopAfterRunTests
{
    [Fact]
    public void Asking_an_idle_Theseus_is_refused_and_leaves_nothing_behind()
    {
        var stop = new StopAfterRun();

        Assert.False(stop.Request(busy: false));
        Assert.False(stop.Pending);
    }

    [Fact]
    public void Asking_mid_run_is_accepted_and_holds_until_the_run_ends()
    {
        var stop = new StopAfterRun();

        Assert.True(stop.Request(busy: true));
        Assert.False(stop.Settle(busy: true)); // still clearing the duty
        Assert.True(stop.Pending);

        Assert.True(stop.Settle(busy: false)); // left the duty
        Assert.False(stop.Pending);
        Assert.False(stop.Settle(busy: false)); // once
    }

    [Fact]
    public void Asking_twice_is_not_a_toggle()
    {
        var stop = new StopAfterRun();

        Assert.True(stop.Request(busy: true));
        Assert.True(stop.Request(busy: true));
        Assert.True(stop.Pending);
    }

    [Fact]
    public void An_honoured_request_does_not_touch_the_next_run()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        stop.Settle(busy: false);

        // A fresh start (the run window, or EnterDuty) is busy again with no request pending.
        Assert.False(stop.Settle(busy: true));
        Assert.False(stop.Pending);
    }
}
