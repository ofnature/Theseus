using System;
using Theseus.Services.Paths;

namespace Theseus.Services.Run;

/// <summary>
/// Evaluates the predicate half of a <see cref="StepVerb.ConditionAction"/>.
///
/// <para>
/// Five predicate forms appear across the whole library and nothing else does, so an unrecognised
/// one is a grammar we have never seen rather than a case to guess at. All five resolve to false
/// when they cannot be evaluated — a branch that does not fire leaves the route running straight
/// on, which is the recoverable failure; a branch that fires wrongly jumps the run somewhere it
/// has no business being.
/// </para>
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Whether a predicate holds. <paramref name="recognised"/> distinguishes "the condition is
    /// false" from "we could not read the condition", which the caller reports once per route.
    /// </summary>
    public static bool Evaluate(string predicate, IStepWorld world, out bool recognised)
    {
        recognised = true;
        var parts = predicate.Split(';', StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            recognised = false;
            return false;
        }

        switch (parts[0].ToLowerInvariant())
        {
            // ObjectData;<dataId>;IsTargetable;<bool>  ·  ObjectData;<dataId>;NamePlateIconId;<int>
            case "objectdata" when parts.Length == 4:
                return EvaluateObjectData(parts, world, out recognised);

            // ObjectSpawned;<dataId>;<bool>
            case "objectspawned" when parts.Length == 3:
            {
                var dataId = ParseDataId(parts[1]);
                var expected = StepArguments.ParseBool(parts[2]);
                if (dataId is null || expected is null)
                    break;

                return world.IsDataIdSpawned(dataId.Value) == expected.Value;
            }

            // ObjectDistanceToPoint;<dataId>;<x,y,z>;<op>;<value>
            case "objectdistancetopoint" when parts.Length == 5:
            {
                var dataId = ParseDataId(parts[1]);
                var point = StepArguments.ParsePoint(parts[2]);
                var op = StepArguments.ParseComparison(parts[3]);
                var threshold = ParseFloat(parts[4]);
                if (dataId is null || point is null || op is null || threshold is null)
                    break;

                var distance = world.DistanceFromDataIdToPoint(dataId.Value, point.Value.ToVector3());
                return distance is not null && Compare(distance.Value, op.Value, threshold.Value);
            }

            // GetDistanceToPlayer;<x,y,z>;<op>;<value>
            case "getdistancetoplayer" when parts.Length == 4:
            {
                var point = StepArguments.ParsePoint(parts[1]);
                var op = StepArguments.ParseComparison(parts[2]);
                var threshold = ParseFloat(parts[3]);
                if (point is null || op is null || threshold is null)
                    break;

                var distance = System.Numerics.Vector3.Distance(world.PlayerPosition, point.Value.ToVector3());
                return Compare(distance, op.Value, threshold.Value);
            }
        }

        recognised = false;
        return false;
    }

    private static bool EvaluateObjectData(string[] parts, IStepWorld world, out bool recognised)
    {
        recognised = true;
        var dataId = ParseDataId(parts[1]);
        if (dataId is null)
        {
            recognised = false;
            return false;
        }

        switch (parts[2].ToLowerInvariant())
        {
            case "istargetable":
            {
                var expected = StepArguments.ParseBool(parts[3]);
                if (expected is null)
                    break;

                return world.IsDataIdTargetable(dataId.Value) == expected.Value;
            }

            case "nameplateiconid":
            {
                var expected = StepArguments.ParseInt(parts[3]);
                if (expected is null)
                    break;

                return world.NamePlateIconId(dataId.Value) == expected.Value;
            }
        }

        recognised = false;
        return false;
    }

    private static bool Compare(float value, ComparisonOp op, float threshold) => op switch
    {
        ComparisonOp.LessThan => value < threshold,
        ComparisonOp.LessOrEqual => value <= threshold,
        ComparisonOp.GreaterThan => value > threshold,
        ComparisonOp.GreaterOrEqual => value >= threshold,
        ComparisonOp.Equal => Math.Abs(value - threshold) < 0.01f,
        ComparisonOp.NotEqual => Math.Abs(value - threshold) >= 0.01f,
        _ => false,
    };

    private static uint? ParseDataId(string text)
        => uint.TryParse(text, out var value) ? value : null;

    private static float? ParseFloat(string text)
        => float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
