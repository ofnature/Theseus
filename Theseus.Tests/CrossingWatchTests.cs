using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Tests;

public class CrossingWatchTests
{
    private static readonly DateTime Start = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Run
    {
        private readonly CrossingWatch _watch = new();
        public DateTime Now = Start;
        public readonly List<(Vector3 From, Vector3 To)> Found = [];

        public void At(Vector3 position, bool ready = true, bool inCombat = false, double seconds = 0.1)
        {
            if (_watch.Observe(Now, position, ready, inCombat) is { } crossing)
                Found.Add(crossing);
            Now = Now.AddSeconds(seconds);
        }

        public void Still(Vector3 position, double seconds = 0.6)
        {
            for (var t = 0.0; t < seconds; t += 0.1)
                At(position);
        }
    }

    [Fact]
    public void A_drop_is_proposed_from_the_lip_to_where_the_character_came_to_rest()
    {
        // The Ghimlyt Dark's last drop, as the log measured it: off a lip at 13.8, down to -15.
        var run = new Run();
        run.At(new Vector3(371f, 13.8f, -236f));
        run.At(new Vector3(371f, 13.8f, -239f));
        run.At(new Vector3(371.2f, 13.8f, -240f)); // the lip
        for (var i = 1; i <= 10; i++) // falling and drifting forward, a few yalms a frame
            run.At(new Vector3(371.2f + 0.04f * i, 13.8f - 2.88f * i, -240f - 2.76f * i));
        run.Still(new Vector3(371.6f, -15f, -267.6f));

        var crossing = Assert.Single(run.Found);
        Assert.Equal(13.8f, crossing.From.Y, 1);
        Assert.Equal(-267.6f, crossing.To.Z, 1);
    }

    [Fact]
    public void Nothing_is_proposed_until_the_character_stops()
    {
        // Mid-slide is not a landing: asking the mesh then gets the lip back, not the floor.
        var run = new Run();
        run.At(Vector3.Zero);
        for (var i = 1; i <= 20; i++)
            run.At(new Vector3(i, -i, 0f));

        Assert.Empty(run.Found);
    }

    [Fact]
    public void Level_ground_and_small_steps_propose_nothing()
    {
        var run = new Run();
        for (var i = 0; i < 40; i++)
            run.At(new Vector3(i, (i % 3) * 0.3f, 0f)); // walking over bumps
        run.Still(new Vector3(40f, 0f, 0f));

        Assert.Empty(run.Found);
    }

    [Fact]
    public void A_knockback_in_combat_is_not_a_route()
    {
        var run = new Run();
        run.At(Vector3.Zero, inCombat: true);
        run.At(new Vector3(2f, -4f, 0f), inCombat: true);
        run.Still(new Vector3(3f, -6f, 0f));

        Assert.Empty(run.Found);
    }

    [Fact]
    public void A_warp_is_not_walked()
    {
        var run = new Run();
        run.At(Vector3.Zero);
        run.At(new Vector3(80f, -30f, 0f)); // one frame, eighty yalms: a teleport
        run.Still(new Vector3(80f, -30f, 0f));

        Assert.Empty(run.Found);
    }

    [Fact]
    public void A_zone_load_forgets_the_level_it_came_from()
    {
        var run = new Run();
        run.At(new Vector3(0f, 50f, 0f));
        run.At(new Vector3(0f, 50f, 0f), ready: false);
        run.At(new Vector3(1f, 10f, 1f));
        run.Still(new Vector3(1f, 10f, 1f));

        Assert.Empty(run.Found);
    }

    [Fact]
    public void Two_drops_in_a_row_are_two_crossings()
    {
        var run = new Run();
        run.At(Vector3.Zero);
        run.At(new Vector3(1f, -5f, 0f));
        run.Still(new Vector3(2f, -5f, 0f));
        run.At(new Vector3(3f, -5f, 0f));
        run.At(new Vector3(4f, -12f, 0f));
        run.Still(new Vector3(5f, -12f, 0f));

        Assert.Equal(2, run.Found.Count);
        Assert.Equal(-5f, run.Found[1].From.Y, 1); // the second starts where the first landed
    }
}
