using System;
using System.Collections.Generic;
using System.Globalization;

namespace Theseus.Services.Paths;

/// <summary>Comparison used by a <see cref="ConditionActionStep"/> distance predicate.</summary>
public enum ComparisonOp
{
    LessThan,
    LessOrEqual,
    GreaterThan,
    GreaterOrEqual,
    Equal,
    NotEqual,
}

/// <summary>
/// A parsed <see cref="StepVerb.ConditionAction"/>: "if this is true, jump there".
///
/// <para>
/// This is how imported routes branch, and the only branch target in the whole library is a
/// relative step jump — so a path is a straight line with backward loops ("wait for that door to
/// become targetable, otherwise step back one and check again") and forward skips.
/// </para>
/// </summary>
/// <param name="Predicate">Predicate clause, semicolon-delimited, verbatim.</param>
/// <param name="IndexDelta">Relative jump applied when the predicate holds.</param>
public readonly record struct ConditionActionStep(string Predicate, int IndexDelta);

/// <summary>
/// Parsers for the argument grammars inside AutoDuty step arguments.
///
/// <para>
/// Pure and separate from the importer because these are the parts most likely to be wrong: the
/// library spells booleans six ways (<c>True</c>, <c>TRUE</c>, <c>true</c>, and the same for
/// false), writes positions both as <c>x,y,z</c> and <c>&lt;x,y,z&gt;</c>, and splits a
/// <c>ConditionAction</c>'s two clauses either across two arguments or across one joined by
/// <c>&amp;</c>. Every one of those variants is present in the real library.
/// </para>
/// </summary>
public static class StepArguments
{
    /// <summary>
    /// Reads a boolean the way the library writes them. Returns null when the text is not a
    /// boolean at all, which callers treat as "argument absent" rather than "false".
    /// </summary>
    public static bool? ParseBool(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return text.Trim().ToLowerInvariant() switch
        {
            "true" or "on" or "yes" or "1" => true,
            "false" or "off" or "no" or "0" => false,
            _ => null,
        };
    }

    /// <summary>Reads an integer, tolerating the leading <c>+</c> that relative jumps are written with.</summary>
    public static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.StartsWith('+'))
            trimmed = trimmed[1..];

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Reads a position written inline in an argument, in either the bare <c>x,y,z</c> or the
    /// angle-bracketed <c>&lt;x,y,z&gt;</c> form. Both appear, sometimes in the same verb.
    /// </summary>
    public static PathPoint? ParsePoint(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim().TrimStart('<').TrimEnd('>');
        var parts = trimmed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return null;

        if (!TryFloat(parts[0], out var x) || !TryFloat(parts[1], out var y) || !TryFloat(parts[2], out var z))
            return null;

        return new PathPoint(x, y, z);
    }

    /// <summary>
    /// Splits a <see cref="StepVerb.ConditionAction"/>'s arguments into predicate and jump.
    ///
    /// <para>
    /// The two clauses are separated either by <c>&amp;</c> inside a single argument or by simply
    /// being two arguments, and both spellings are common. Joining everything first and splitting
    /// once collapses that difference instead of branching on it.
    /// </para>
    /// </summary>
    public static ConditionActionStep? ParseConditionAction(IReadOnlyList<string> arguments)
    {
        var joined = string.Join("&", Meaningful(arguments));
        var clauses = joined.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (clauses.Length < 2)
            return null;

        var predicate = string.Join("&", clauses[..^1]);
        var action = clauses[^1].Split(';', StringSplitOptions.TrimEntries);

        // ModifyIndex is the only action the library ever branches to. Anything else is a grammar
        // we have not seen, and guessing at it would silently mis-route a run.
        if (action.Length != 2 || !action[0].Equals(nameof(StepVerb.ModifyIndex), StringComparison.OrdinalIgnoreCase))
            return null;

        var delta = ParseInt(action[1]);
        return delta is null ? null : new ConditionActionStep(predicate, delta.Value);
    }

    /// <summary>Reads a comparison operator from a distance predicate.</summary>
    public static ComparisonOp? ParseComparison(string? text) => text?.Trim() switch
    {
        "<" => ComparisonOp.LessThan,
        "<=" => ComparisonOp.LessOrEqual,
        ">" => ComparisonOp.GreaterThan,
        ">=" => ComparisonOp.GreaterOrEqual,
        "=" or "==" => ComparisonOp.Equal,
        "!=" => ComparisonOp.NotEqual,
        _ => null,
    };

    /// <summary>
    /// Drops arguments that carry nothing.
    ///
    /// <para>
    /// Over a third of the library's steps are written with a single empty-string argument rather
    /// than an empty list — <c>MoveTo</c> alone accounts for roughly fifteen hundred. Treating
    /// <c>[""]</c> as "one argument" makes every arity check downstream wrong.
    /// </para>
    /// </summary>
    public static string[] Meaningful(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return [];

        var kept = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (!string.IsNullOrWhiteSpace(argument))
                kept.Add(argument.Trim());
        }

        return kept.ToArray();
    }

    private static bool TryFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
