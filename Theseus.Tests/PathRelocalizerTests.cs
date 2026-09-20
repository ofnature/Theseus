using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Paths;
using Theseus.Services.Thread;

namespace Theseus.Tests;

/// <summary>
/// Field-driven: on the first real run, stopping and starting made the character try to retrace
/// the entire dungeon, and past Mistwake's one-way slide it could not get back at all.
/// </summary>
public class PathRelocalizerTests
{
    private static ThreadPath PathOf(params (float X, int Objective)[] steps)
    {
        var path = new ThreadPath { TerritoryId = 1314, Name = "Mistwake" };
        foreach (var (x, objective) in steps)
        {
            path.Steps.Add(new ThreadStep
            {
                Verb = StepVerb.MoveTo,
                Position = new PathPoint(x, 0, 0),
                ObjectiveIndex = objective,
            });
        }

        return path;
    }

    private static DutyObjectiveSnapshot At(int completed, int total)
    {
        var objectives = new DutyObjective[total];
        for (var i = 0; i < total; i++)
            objectives[i] = new DutyObjective(i, i < completed ? 1 : 0, 1, false, $"objective {i}");

        return DutyObjectiveSnapshot.From(objectives, ObjectiveSource.Director);
    }

    [Fact]
    public void Resuming_picks_the_step_you_are_standing_at_not_the_first_one()
    {
        var path = PathOf((10, -1), (100, -1), (200, -1), (300, -1));

        var resume = PathRelocalizer.Find(path, new Vector3(205, 0, 0), DutyObjectiveSnapshot.Unavailable);

        Assert.NotNull(resume);
        Assert.Equal(2, resume.Value.StepIndex);
        Assert.True(resume.Value.IsConfident);
    }

    [Fact]
    public void Standing_between_waypoints_resumes_forward()
    {
        // The one-way case. Past Mistwake's slide, resuming at the waypoint behind means walking
        // back to a slide that cannot be climbed — the run never arrives.
        var path = PathOf((10, -1), (100, -1), (200, -1));

        var resume = PathRelocalizer.Find(path, new Vector3(100.5f, 0, 0), DutyObjectiveSnapshot.Unavailable);

        Assert.Equal(1, resume!.Value.StepIndex);

        // Exactly midway must not fall back to the earlier step either.
        var midway = PathRelocalizer.Find(path, new Vector3(150, 0, 0), DutyObjectiveSnapshot.Unavailable);
        Assert.Equal(2, midway!.Value.StepIndex);
    }

    [Fact]
    public void An_untagged_path_falls_back_to_position_only()
    {
        // Freshly imported routes carry no objective tags, so the coarse pass cannot apply — which
        // is the documented fallback, not a failure.
        var path = PathOf((10, -1), (100, -1), (200, -1));

        var resume = PathRelocalizer.Find(path, new Vector3(10, 0, 0), At(completed: 2, total: 3));

        Assert.False(resume!.Value.UsedObjectives);
        Assert.Equal(0, resume.Value.StepIndex);
    }

    [Fact]
    public void A_tagged_path_is_constrained_by_the_live_objective()
    {
        // The coarse pass is what stops the fine pass teleporting the run across the map to a
        // coincidentally-close step on the far side of a wall. Steps 0 and 3 sit at the same
        // place; only the objective separates them.
        var path = PathOf((10, 0), (100, 1), (200, 2), (15, 2));

        var resume = PathRelocalizer.Find(path, new Vector3(10, 0, 0), At(completed: 2, total: 3));

        Assert.True(resume!.Value.UsedObjectives);
        Assert.Equal(3, resume.Value.StepIndex);
    }

    [Fact]
    public void An_objective_with_no_positioned_steps_widens_rather_than_giving_up()
    {
        var path = PathOf((10, 0), (100, 0));

        var resume = PathRelocalizer.Find(path, new Vector3(90, 0, 0), At(completed: 2, total: 3));

        Assert.NotNull(resume);
        Assert.Equal(1, resume.Value.StepIndex);
    }

    [Fact]
    public void Being_nowhere_near_the_route_resumes_but_says_it_is_a_guess()
    {
        // Still resumes: walking somewhere wrong is recoverable, refusing to run is not.
        var path = PathOf((10, -1), (100, -1));

        var resume = PathRelocalizer.Find(path, new Vector3(9000, 0, 0), DutyObjectiveSnapshot.Unavailable);

        Assert.NotNull(resume);
        Assert.False(resume.Value.IsConfident);
    }

    [Fact]
    public void A_route_with_no_positioned_steps_has_nowhere_to_resume()
    {
        var path = new ThreadPath { TerritoryId = 1314, Name = "Mistwake" };
        path.Steps.Add(new ThreadStep { Verb = StepVerb.Wait, Arguments = ["1000"] });

        Assert.Null(PathRelocalizer.Find(path, Vector3.Zero, DutyObjectiveSnapshot.Unavailable));
    }

    [Fact]
    public void Comments_are_never_a_resume_target()
    {
        var path = PathOf((10, -1), (100, -1));
        path.Steps[1].Verb = StepVerb.Comment;

        var resume = PathRelocalizer.Find(path, new Vector3(100, 0, 0), DutyObjectiveSnapshot.Unavailable);

        Assert.Equal(0, resume!.Value.StepIndex);
    }
}
