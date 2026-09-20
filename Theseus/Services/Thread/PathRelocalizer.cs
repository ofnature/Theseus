using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Paths;

namespace Theseus.Services.Thread;

/// <summary>Where a run should pick up, and how sure we are.</summary>
/// <param name="StepIndex">Step to resume at.</param>
/// <param name="Distance">How far the character is from that step, in yalms.</param>
/// <param name="UsedObjectives">Whether the objective list narrowed the search.</param>
public readonly record struct ResumePoint(int StepIndex, float Distance, bool UsedObjectives)
{
    /// <summary>
    /// Far enough away that the match is a guess. The run still resumes — walking somewhere wrong
    /// is recoverable, refusing to run is not — but it is worth saying so.
    /// </summary>
    public bool IsConfident => Distance < 40f;
}

/// <summary>
/// Works out which step of a route the character is currently standing in.
///
/// <para>
/// This is the Thread's core, and it is what separates Theseus from restarting a dungeon. A run
/// that always begins at step 0 replays the whole route after any interruption — and once the
/// party is past a one-way transition (Mistwake's slide, the drops in the variant dungeons) it
/// cannot walk back, so the run is simply dead. Measured in the field on the first real run: stop,
/// start, and the character tried to retrace the entire dungeon.
/// </para>
///
/// <para>
/// Two levels, per the design. The <b>coarse</b> pass constrains candidates to the objective the
/// game says is live, which is what stops the fine pass teleporting the run across the map to a
/// coincidentally-close step on the far side of a wall. The <b>fine</b> pass is nearest position
/// within that range. Freshly imported routes carry no objective tags, so the coarse pass simply
/// does not apply yet — position-only, exactly the documented fallback, until one clean run
/// teaches the map.
/// </para>
/// </summary>
public static class PathRelocalizer
{
    /// <summary>
    /// Steps this much further away than the best match are still considered, so that a character
    /// standing between two waypoints can be resolved by order rather than by centimetres.
    /// </summary>
    private const float TieRadius = 5f;

    /// <summary>
    /// Finds the step to resume at. Returns null only when the route has no positioned steps at
    /// all, in which case the caller should start from the beginning.
    /// </summary>
    public static ResumePoint? Find(ThreadPath path, Vector3 position, DutyObjectiveSnapshot objectives)
    {
        if (path.Steps.Count == 0)
            return null;

        // Coarse: restrict to the objective the game says is current. Only meaningful once the
        // route has been tagged, which happens after one clean run.
        var stage = objectives.Available ? objectives.Stage : ThreadPath.UnknownObjective;
        var constrained = stage != ThreadPath.UnknownObjective && path.IsObjectiveTagged;

        var match = Nearest(path, position, constrained ? stage : null);

        // A tagged objective with no positioned steps of its own is not a reason to give up.
        match ??= constrained ? Nearest(path, position, null) : null;

        return match is null
            ? null
            : match.Value with { UsedObjectives = constrained && match.Value.UsedObjectives };
    }

    private static ResumePoint? Nearest(ThreadPath path, Vector3 position, int? objectiveStage)
    {
        var bestIndex = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            if (!step.Position.IsSet || step.Verb == StepVerb.Comment)
                continue;

            if (objectiveStage is not null && step.ObjectiveIndex != objectiveStage)
                continue;

            var distance = Vector3.Distance(position, step.Position.ToVector3());

            // Strictly closer wins; near-equal prefers the LATER step. Standing between two
            // waypoints is the normal case after an interruption, and choosing the earlier one
            // sends the run backwards — which across a one-way transition means never arriving.
            if (distance < bestDistance - TieRadius
                || (distance < bestDistance + TieRadius && i > bestIndex))
            {
                if (distance < bestDistance)
                    bestDistance = distance;

                bestIndex = i;
            }
        }

        return bestIndex < 0 ? null : new ResumePoint(bestIndex, bestDistance, objectiveStage is not null);
    }
}
