using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Tests;

public class RespawnWatchTests
{
    private static readonly Vector3 Step6 = new(131.2f, 35.1f, 38.8f);
    private static readonly Vector3 Entrance = new(-413.4f, 107f, 157.9f);

    [Fact]
    public void Dying_and_coming_back_at_the_entrance_is_a_respawn()
    {
        // The Ghimlyt Dark, 2026-09-30: died on step 6, back at the entrance through a zone load.
        var watch = new RespawnWatch();
        Assert.False(watch.Observe(isDead: false, ready: true, Step6));
        Assert.False(watch.Observe(isDead: true, ready: true, Step6));
        Assert.False(watch.Observe(isDead: false, ready: false, Entrance)); // mid zone load
        Assert.True(watch.Observe(isDead: false, ready: true, Entrance));
        Assert.False(watch.Observe(isDead: false, ready: true, Entrance)); // once
    }

    [Fact]
    public void A_raise_where_the_character_fell_is_not_a_respawn()
    {
        var watch = new RespawnWatch();
        watch.Observe(isDead: true, ready: true, Step6);

        Assert.False(watch.Observe(isDead: false, ready: true, Step6 + new Vector3(1f, 0f, 1f)));
    }

    [Fact]
    public void Being_alive_throughout_is_never_a_respawn()
    {
        var watch = new RespawnWatch();
        Assert.False(watch.Observe(isDead: false, ready: true, Step6));
        Assert.False(watch.Observe(isDead: false, ready: true, Entrance)); // a warp, not a death
    }
}
