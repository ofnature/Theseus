using Theseus.Services.Duty;
using Theseus.Services.Paths;

namespace Theseus.Tests;

public class ObjectiveMapperTests
{
    private static DutyObjectiveSnapshot At(int completed, int total)
    {
        var objectives = new DutyObjective[total];
        for (var i = 0; i < total; i++)
            objectives[i] = new DutyObjective(i, i < completed ? 1 : 0, 1, false, $"objective {i}");

        return DutyObjectiveSnapshot.From(objectives, ObjectiveSource.Director);
    }

    private static ThreadPath PathOf(int steps)
    {
        var path = new ThreadPath { TerritoryId = 1252, Name = "Mistwake" };
        for (var i = 0; i < steps; i++)
            path.Steps.Add(new ThreadStep { Verb = StepVerb.MoveTo });

        return path;
    }

    [Fact]
    public void One_clean_run_tags_the_whole_path()
    {
        var mapper = new ObjectiveMapper();
        var path = PathOf(4);

        mapper.Observe(0, At(completed: 0, total: 3));
        mapper.Observe(1, At(completed: 1, total: 3));
        mapper.Observe(2, At(completed: 2, total: 3));
        mapper.Observe(3, At(completed: 3, total: 3));

        Assert.True(mapper.Apply(path));
        Assert.Equal([0, 1, 2, 3], path.Steps.Select(s => s.ObjectiveIndex));
        Assert.True(path.IsObjectiveTagged);
    }

    [Fact]
    public void Steps_after_the_last_objective_are_still_tagged()
    {
        // The walk to the exit happens with every objective complete. Tagging on the raw current
        // index would leave all of it at -1 and unresumable.
        var mapper = new ObjectiveMapper();
        var path = PathOf(2);

        mapper.Observe(0, At(completed: 2, total: 2));
        mapper.Observe(1, At(completed: 2, total: 2));
        mapper.Apply(path);

        Assert.All(path.Steps, s => Assert.Equal(2, s.ObjectiveIndex));
    }

    [Fact]
    public void A_retry_loop_does_not_relabel_the_step_it_revisits()
    {
        // Backward jumps in the library are overwhelmingly "check again" loops of one to three
        // steps. One that straddles an objective boundary must not move the step's tag.
        var mapper = new ObjectiveMapper();
        var path = PathOf(2);

        mapper.Observe(0, At(completed: 0, total: 3));
        mapper.Observe(1, At(completed: 0, total: 3));
        mapper.Observe(0, At(completed: 1, total: 3)); // ModifyIndex -1, objective ticked meanwhile
        mapper.Apply(path);

        Assert.Equal(0, path.Steps[0].ObjectiveIndex);
        Assert.Equal(1, mapper.ConflictCount);
    }

    [Fact]
    public void Unreadable_objectives_teach_nothing()
    {
        // A run in content that populates no objectives must leave the path untagged rather than
        // tagging every step 0 — which would make resume confidently wrong instead of honestly
        // position-only.
        var mapper = new ObjectiveMapper();
        var path = PathOf(2);

        mapper.Observe(0, DutyObjectiveSnapshot.Unavailable);
        mapper.Observe(1, DutyObjectiveSnapshot.From([], ObjectiveSource.Director));

        Assert.False(mapper.Apply(path));
        Assert.False(path.IsObjectiveTagged);
        Assert.Equal(0, mapper.ObservedStepCount);
    }

    [Fact]
    public void An_existing_tag_is_never_overwritten()
    {
        // Once learned, a tag is stable: a later run that took a different route through the same
        // dungeon must not churn a working map. Correcting one is a re-import.
        var mapper = new ObjectiveMapper();
        var path = PathOf(2);
        path.Steps[0].ObjectiveIndex = 5;

        mapper.Observe(0, At(completed: 0, total: 3));
        mapper.Observe(1, At(completed: 1, total: 3));

        Assert.True(mapper.Apply(path));
        Assert.Equal(5, path.Steps[0].ObjectiveIndex);
        Assert.Equal(1, path.Steps[1].ObjectiveIndex);
    }

    [Fact]
    public void Applying_twice_reports_no_second_change()
    {
        // The caller writes to disk on a true return, so a no-op second pass must say so.
        var mapper = new ObjectiveMapper();
        var path = PathOf(1);

        mapper.Observe(0, At(completed: 0, total: 2));

        Assert.True(mapper.Apply(path));
        Assert.False(mapper.Apply(path));
    }

    [Fact]
    public void Observations_survive_a_run_being_stopped_and_restarted()
    {
        // A dungeon that takes three stop/start cycles to get through still produced real
        // observations on every one of them. Resetting per start meant a route that could not be
        // finished in a single unbroken run could never be tagged at all — which is exactly the
        // situation a route with a bad waypoint puts you in.
        var mapper = new ObjectiveMapper();
        var path = PathOf(4);

        // First attempt, stopped partway.
        mapper.Observe(0, At(completed: 0, total: 3));
        mapper.Observe(1, At(completed: 1, total: 3));

        // Resumed later, further in. No Reset in between.
        mapper.Observe(2, At(completed: 2, total: 3));
        mapper.Observe(3, At(completed: 3, total: 3));

        Assert.True(mapper.Apply(path));
        Assert.Equal([0, 1, 2, 3], path.Steps.Select(s => s.ObjectiveIndex));
        Assert.True(path.IsObjectiveTagged);
    }

    [Fact]
    public void A_partial_run_tags_what_it_saw_and_no_more()
    {
        // Partial learning is still worth keeping: the tags it does write are real, and the route
        // simply is not "fully tagged" until the rest are filled in on a later run.
        var mapper = new ObjectiveMapper();
        var path = PathOf(3);

        mapper.Observe(0, At(completed: 0, total: 2));

        Assert.True(mapper.Apply(path));
        Assert.Equal(0, path.Steps[0].ObjectiveIndex);
        Assert.Equal(ThreadPath.UnknownObjective, path.Steps[1].ObjectiveIndex);
        Assert.False(path.IsObjectiveTagged);
    }

    [Fact]
    public void Observations_past_the_end_of_the_path_are_ignored()
    {
        var mapper = new ObjectiveMapper();
        var path = PathOf(1);

        mapper.Observe(9, At(completed: 0, total: 2));

        Assert.False(mapper.Apply(path));
    }
}
