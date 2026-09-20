using Theseus.Services.Paths;

namespace Theseus.Tests;

/// <summary>
/// Validation matters most in the editor. Jumps are relative, so moving a step silently re-points
/// every jump that crosses it — the warning list is the only place that becomes visible before a
/// run walks into it.
/// </summary>
public class PathValidatorTests
{
    private static ThreadPath PathOf(params ThreadStep[] steps)
    {
        var path = new ThreadPath { TerritoryId = 1036, Name = "Sastasha" };
        path.Steps.AddRange(steps);
        return path;
    }

    private static ThreadStep Step(StepVerb verb, params string[] arguments)
        => new() { Verb = verb, RawVerb = verb.ToString(), Arguments = arguments };

    [Fact]
    public void Inserting_a_step_can_break_a_jump_and_it_is_reported()
    {
        // "Step back one and check the door again" becomes a jump somewhere else entirely once a
        // step is inserted between. Nothing in the step list looks different.
        var path = PathOf(
            Step(StepVerb.MoveTo),
            Step(StepVerb.ModifyIndex, "-1"));

        PathValidator.Validate(path);
        Assert.Empty(path.Warnings);

        path.Steps.Insert(0, Step(StepVerb.Wait, "1000"));
        path.Steps[2].Arguments = ["-5"]; // now points off the front
        PathValidator.Validate(path);

        Assert.Contains(path.Warnings, w => w.Contains("outside the path"));
    }

    [Fact]
    public void Validation_replaces_earlier_findings_rather_than_appending()
    {
        // Called on every edit, so stale warnings must not pile up and outlive the problem.
        var path = PathOf(Step(StepVerb.ModifyIndex, "+9"));

        PathValidator.Validate(path);
        var first = path.Warnings.Count;

        PathValidator.Validate(path);

        Assert.Equal(first, path.Warnings.Count);
    }

    [Fact]
    public void Fixing_a_route_clears_its_blocker()
    {
        // The editor's whole point: a blocked route becomes runnable once the offending step goes.
        var path = PathOf(Step(StepVerb.MoveTo), Step(StepVerb.DutySpecificCode, "1"));
        PathValidator.Validate(path);
        Assert.False(path.IsRunnable);

        path.Steps.RemoveAt(1);
        PathValidator.Validate(path);

        Assert.True(path.IsRunnable);
        Assert.Empty(path.Blockers);
    }

    [Fact]
    public void An_unknown_verb_is_reported_without_blocking()
    {
        var path = PathOf(new ThreadStep { Verb = StepVerb.Unknown, RawVerb = "Teleport" });

        PathValidator.Validate(path);

        Assert.Contains(path.Warnings, w => w.Contains("Teleport"));
        Assert.True(path.IsRunnable);
    }
}
