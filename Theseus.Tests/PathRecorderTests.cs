using System.Numerics;
using Theseus.Services.Paths;

namespace Theseus.Tests;

public class PathRecorderTests
{
    private static PathRecorder Recording(FakeStepWorld world, FakeObjectiveReader? objectives = null)
    {
        var recorder = new PathRecorder(world, objectives ?? new FakeObjectiveReader());
        recorder.Start(territoryId: 1314);
        return recorder;
    }

    /// <summary>Walks the character along a line, ticking as it goes.</summary>
    private static void Walk(FakeStepWorld world, PathRecorder recorder, Vector3 from, Vector3 to, int steps)
    {
        for (var i = 0; i <= steps; i++)
        {
            world.PlayerPosition = Vector3.Lerp(from, to, i / (float)steps);
            recorder.Tick();
            world.Advance(0.1);
        }
    }

    // ── Tagging ──

    [Fact]
    public void Waypoints_are_never_tagged_wall_to_wall()
    {
        // The trap this exists to avoid: skipping a step skips its position too, so a route whose
        // travel is tagged W2W collapses for anyone who is not a tank. Mistwake's imported path
        // does exactly that, and a recorder that copied the pattern would reproduce it.
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(80, 0, 0), 40);

        var path = recorder.Build("Test");
        var travel = path.Steps.Where(s => s.Verb == StepVerb.MoveTo).ToList();

        Assert.NotEmpty(travel);
        Assert.All(travel, s => Assert.False(s.Tag.HasFlag(StepTag.W2W)));
    }

    [Fact]
    public void Ground_covered_in_combat_becomes_a_tagged_chain_pull()
    {
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(80, 0, 0), 40);

        var path = recorder.Build("Test");
        var pull = path.Steps.First(s => s.Verb == StepVerb.StopForCombat);

        Assert.Equal("false", pull.Arguments[0]);
        Assert.True(pull.Tag.HasFlag(StepTag.W2W));
    }

    [Fact]
    public void The_matching_switch_back_is_left_untagged()
    {
        // A character that skipped the W2W "false" must still be correct: running the "true" is a
        // harmless no-op, whereas tagging it would leave the route depending on a skipped step.
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(80, 0, 0), 40);

        var path = recorder.Build("Test");
        var restore = path.Steps.Last(s => s.Verb == StepVerb.StopForCombat);

        Assert.Equal("true", restore.Arguments[0]);
        Assert.False(restore.Tag.HasFlag(StepTag.W2W));
    }

    [Fact]
    public void A_route_never_ends_still_chain_pulling()
    {
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(80, 0, 0), 40);

        var path = recorder.Build("Test");

        Assert.Equal("true", path.Steps.Last(s => s.Verb == StepVerb.StopForCombat).Arguments[0]);
    }

    [Fact]
    public void Standing_and_fighting_is_not_a_chain_pull()
    {
        // A damage dealer kills a pack where it stands. Nothing about the mode should change.
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(4, 0, 0), 10);

        var path = recorder.Build("Test");

        Assert.DoesNotContain(path.Steps, s => s.Verb == StepVerb.StopForCombat);
    }

    [Fact]
    public void A_fight_mid_wing_splits_the_pull_in_two()
    {
        // Field result from Mistwake: the tank pulled pack one, dragged it to pack two, and killed
        // them together — and the whole wing came out as a single pull pair, because a stationary
        // fight leaves no samples, only a hole in the clock. Replayed, that route would drag
        // everything to the boss door. The hole is the fight, and the pull must close there.
        var world = new FakeStepWorld { InCombat = true };
        var recorder = Recording(world);

        Walk(world, recorder, Vector3.Zero, new Vector3(40, 0, 0), 20);
        world.Advance(12); // stood here killing the merged packs
        recorder.Tick();
        Walk(world, recorder, new Vector3(40, 0, 0), new Vector3(80, 0, 0), 20);
        world.InCombat = false;
        recorder.Tick();

        var path = recorder.Build("Test");
        var toggles = path.Steps.Where(s => s.Verb == StepVerb.StopForCombat).ToList();

        Assert.Equal(4, toggles.Count);
        Assert.Equal(["false", "true", "false", "true"], toggles.Select(t => t.Arguments[0]));

        // The fight happened around x=40; the first pull must close there, not at the wing's end.
        Assert.InRange(toggles[1].Position.X, 30f, 50f);
    }

    // ── Events ──

    [Fact]
    public void A_boss_module_starting_becomes_a_boss_step()
    {
        var world = new FakeStepWorld();
        var recorder = Recording(world);
        Walk(world, recorder, Vector3.Zero, new Vector3(30, 0, 0), 15);

        world.BossModuleActive = true;
        recorder.Tick();

        var path = recorder.Build("Test");

        Assert.Contains(path.Steps, s => s.Verb == StepVerb.Boss);
    }

    // ── Simplification ──

    [Fact]
    public void A_straight_line_collapses_to_its_ends()
    {
        var points = Enumerable.Range(0, 40).Select(i => new Vector3(i, 0, 0)).ToList();

        var kept = PathRecorder.Simplify(points);

        // Only the max-gap rule adds anything on a perfectly straight run.
        Assert.True(kept.Count <= 4, $"expected a handful of points, got {kept.Count}");
        Assert.Equal(points[0], kept[0]);
        Assert.Equal(points[^1], kept[^1]);
    }

    [Fact]
    public void A_corner_is_kept_rather_than_rounded()
    {
        // Corners are where a run clips scenery, and the hand fix has always been more waypoints
        // there. Simplification that treats a corner as noise recreates the failure it prevents.
        var points = new List<Vector3>();
        for (var i = 0; i <= 10; i++)
            points.Add(new Vector3(i, 0, 0));
        for (var i = 1; i <= 10; i++)
            points.Add(new Vector3(10, 0, i));

        var kept = PathRecorder.Simplify(points);

        Assert.Contains(kept, p => Vector3.Distance(p, new Vector3(10, 0, 0)) < 1.5f);
    }

    [Fact]
    public void A_long_straight_still_gets_something_to_aim_at()
    {
        var points = Enumerable.Range(0, 200).Select(i => new Vector3(i, 0, 0)).ToList();

        var kept = PathRecorder.Simplify(points);

        Assert.True(kept.Count >= 8, $"a 200-yalm leg needs waypoints along it, got {kept.Count}");
    }
}
