namespace Theseus.Services.Paths;

/// <summary>
/// Decides whether a route can be run, and what is worth warning about.
///
/// <para>
/// Shared by the importer and the editor on purpose. Conversion and hand-editing can both produce
/// a broken route, and a path that the importer would have rejected must not become runnable just
/// because someone edited it afterwards — one implementation means one answer.
/// </para>
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Rewrites <see cref="ThreadPath.Blockers"/> and <see cref="ThreadPath.Warnings"/> from the
    /// route's current contents.
    /// </summary>
    public static void Validate(ThreadPath path)
    {
        path.Blockers.Clear();
        path.Warnings.Clear();

        var dutySpecific = 0;
        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            switch (step.Verb)
            {
                case StepVerb.DutySpecificCode:
                    dutySpecific++;
                    break;

                case StepVerb.ConditionAction when StepArguments.ParseConditionAction(step.Arguments) is null:
                    path.Warnings.Add(
                        $"Step {i}: could not read the ConditionAction branch " +
                        $"(\"{string.Join(" | ", step.Arguments)}\") — it will not branch at runtime.");
                    break;

                case StepVerb.ModifyIndex when StepArguments.ParseInt(Single(step)) is null:
                    path.Warnings.Add($"Step {i}: ModifyIndex has no readable jump target.");
                    break;

                case StepVerb.Unknown:
                    path.Warnings.Add(
                        $"Step {i}: unrecognised verb \"{step.RawVerb}\" — the step will be skipped at runtime.");
                    break;
            }
        }

        if (dutySpecific > 0)
        {
            // These call hardcoded per-dungeon C# inside AutoDuty that has no equivalent here.
            // Five dungeons in the whole library need it, and each one needs a recorded path.
            path.Blockers.Add(
                $"Uses DutySpecificCode ×{dutySpecific}, which runs AutoDuty's own hardcoded logic " +
                "and cannot be converted. This duty needs a recorded path.");
        }

        ValidateJumpTargets(path);
    }

    /// <summary>
    /// Checks that relative jumps still land inside the path.
    ///
    /// <para>
    /// This is the check that earns its keep in the editor rather than the importer. Jumps are
    /// <b>relative</b>, so inserting, deleting or reordering a step silently re-points every jump
    /// that crosses it — a one-step insert can turn "step back and check the door again" into a
    /// jump into the middle of a boss fight, with nothing to see in the step list.
    /// </para>
    /// </summary>
    private static void ValidateJumpTargets(ThreadPath path)
    {
        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            var delta = step.Verb switch
            {
                StepVerb.ModifyIndex => StepArguments.ParseInt(Single(step)),
                StepVerb.ConditionAction => StepArguments.ParseConditionAction(step.Arguments)?.IndexDelta,
                _ => null,
            };

            if (delta is null)
                continue;

            var target = i + delta.Value;
            if (target < 0 || target >= path.Steps.Count)
                path.Warnings.Add($"Step {i}: jump of {delta.Value:+#;-#;0} lands outside the path (step {target}).");
        }
    }

    private static string? Single(ThreadStep step) => step.Arguments is [var only] ? only : null;
}
