using Theseus.Services.Paths;

namespace Theseus.Tests;

public class StepArgumentsTests
{
    [Theory]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    [InlineData("true", true)]
    [InlineData("On", true)]
    [InlineData("ON", true)]
    [InlineData("Yes", true)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("false", false)]
    [InlineData("Off", false)]
    [InlineData("No", false)]
    public void Booleans_parse_in_every_spelling_the_library_uses(string text, bool expected)
        => Assert.Equal(expected, StepArguments.ParseBool(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("maybe")]
    public void A_non_boolean_reads_as_absent_rather_than_false(string text)
    {
        // "absent" and "false" mean different things for StopForCombat: one leaves the current
        // setting alone, the other turns combat stops off for the rest of the route.
        Assert.Null(StepArguments.ParseBool(text));
    }

    [Theory]
    [InlineData("+3", 3)]
    [InlineData("-2", -2)]
    [InlineData("51", 51)]
    public void Relative_jumps_parse_with_or_without_a_sign(string text, int expected)
        => Assert.Equal(expected, StepArguments.ParseInt(text));

    [Theory]
    [InlineData("289,27,-233")]
    [InlineData("<289,27,-233>")]
    [InlineData("289, 27, -233")]
    [InlineData("<289, 27, -233>")]
    public void Inline_positions_parse_in_both_written_forms(string text)
        => Assert.Equal(new PathPoint(289f, 27f, -233f), StepArguments.ParsePoint(text));

    [Fact]
    public void ConditionAction_splits_when_the_clauses_share_one_argument()
    {
        // "ObjectData;…&ModifyIndex;-1" — the most common form in the library.
        var parsed = StepArguments.ParseConditionAction(["ObjectData;1016994;IsTargetable;false&ModifyIndex;-1"]);

        Assert.NotNull(parsed);
        Assert.Equal("ObjectData;1016994;IsTargetable;false", parsed.Value.Predicate);
        Assert.Equal(-1, parsed.Value.IndexDelta);
    }

    [Fact]
    public void ConditionAction_splits_when_the_clauses_are_separate_arguments()
    {
        var parsed = StepArguments.ParseConditionAction(
            ["ObjectDistanceToPoint;14619;-213.157,-28.100,34.952;<;3", "ModifyIndex;+9"]);

        Assert.NotNull(parsed);
        Assert.Equal("ObjectDistanceToPoint;14619;-213.157,-28.100,34.952;<;3", parsed.Value.Predicate);
        Assert.Equal(9, parsed.Value.IndexDelta);
    }

    [Fact]
    public void A_branch_to_anything_but_ModifyIndex_is_refused()
    {
        // ModifyIndex is the only branch target in the whole library. An unknown one is a grammar
        // we have not seen, and guessing would mis-route the run rather than fail.
        Assert.Null(StepArguments.ParseConditionAction(["ObjectData;1;IsTargetable;false&Teleport;3"]));
        Assert.Null(StepArguments.ParseConditionAction(["ObjectData;1;IsTargetable;false"]));
    }

    [Theory]
    [InlineData("<", ComparisonOp.LessThan)]
    [InlineData("<=", ComparisonOp.LessOrEqual)]
    [InlineData(">", ComparisonOp.GreaterThan)]
    [InlineData(">=", ComparisonOp.GreaterOrEqual)]
    public void Comparison_operators_parse(string text, ComparisonOp expected)
        => Assert.Equal(expected, StepArguments.ParseComparison(text));

    [Fact]
    public void Blank_arguments_are_dropped()
    {
        Assert.Empty(StepArguments.Meaningful([""]));
        Assert.Empty(StepArguments.Meaningful(["  "]));
        Assert.Equal("15", Assert.Single(StepArguments.Meaningful(["", "15", "  "])));
    }
}
