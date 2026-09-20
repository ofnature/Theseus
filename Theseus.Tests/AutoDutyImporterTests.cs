using Theseus.Services.Paths;

namespace Theseus.Tests;

/// <summary>
/// Every fixture here is a shape taken from the user's real 309-file AutoDuty library. The traps
/// are all things that parse "successfully" into something wrong rather than failing loudly.
/// </summary>
public class AutoDutyImporterTests
{
    private static string File(params string[] actions)
        => $$"""{"Actions":[{{string.Join(",", actions)}}]}""";

    /// <param name="tag">Raw JSON for the Tag field — a quoted name, or a bare number.</param>
    private static string Action(string name, string tag = "\"None\"", string args = "[]", string note = "")
        => $$"""
            {"Tag":{{tag}},"Name":"{{name}}","Position":{"X":0.0,"Y":0.0,"Z":0.0},
             "Arguments":{{args}},"Conditions":[],"Note":"{{note}}"}
            """;

    [Fact]
    public void Territory_id_and_name_come_from_the_file_name()
    {
        var path = AutoDutyImporter.Import("(1036) Sastasha.json", File(Action("MoveTo", "\"None\"")));

        Assert.Equal(1036u, path.TerritoryId);
        Assert.Equal("Sastasha", path.Name);
    }

    [Fact]
    public void Lowercase_keys_still_import()
    {
        // Six library files spell every key in lower case. A case-sensitive reader turns those
        // into an empty action list, and an empty path is indistinguishable from a working one
        // until a character stands still in a dungeon entrance.
        const string json = """
            {"actions":[{"tag":"None","name":"MoveTo","position":{"x":1.0,"y":2.0,"z":3.0},
             "arguments":[],"note":"lower"}]}
            """;

        var path = AutoDutyImporter.Import("(162) Halatali.json", json);

        var step = Assert.Single(path.Steps);
        Assert.Equal(StepVerb.MoveTo, step.Verb);
        Assert.Equal(new PathPoint(1f, 2f, 3f), step.Position);
        Assert.Empty(path.Blockers);
    }

    [Fact]
    public void A_numeric_tag_does_not_lose_the_file()
    {
        // Forty-odd steps write Tag as a bare number instead of a name. A strict string reader
        // throws on the first one and takes the whole path with it.
        var path = AutoDutyImporter.Import("(1036) Sastasha.json", File(Action("MoveTo", "0")));

        Assert.Empty(path.Blockers);
        Assert.Equal(StepTag.None, Assert.Single(path.Steps).Tag);
    }

    [Fact]
    public void Combined_tags_parse_as_flags()
    {
        var path = AutoDutyImporter.Import("(1036) x.json", File(Action("MoveTo", "\"Synced, W2W\"")));

        Assert.Equal(StepTag.Synced | StepTag.W2W, Assert.Single(path.Steps).Tag);
    }

    [Fact]
    public void Comment_steps_are_recognised_by_their_literal_name()
    {
        // Comments are not written as the word "Comment" — they are written as "<-- Comment -->".
        var path = AutoDutyImporter.Import("(1036) x.json", File(Action("<-- Comment -->", "\"Comment\"")));

        Assert.Equal(StepVerb.Comment, Assert.Single(path.Steps).Verb);
        Assert.Empty(path.Warnings);
    }

    [Fact]
    public void Verb_casing_variants_map_to_the_same_verb()
    {
        // The library contains both "BossMod" and "Bossmod". An exact-match table silently drops
        // the second spelling, removing the boss handoff from two routes.
        var path = AutoDutyImporter.Import("(1036) x.json", File(
            Action("BossMod", args: """["on"]"""),
            Action("Bossmod", args: """["off"]""")));

        Assert.All(path.Steps, s => Assert.Equal(StepVerb.BossMod, s.Verb));
        Assert.Empty(path.Warnings);
    }

    [Fact]
    public void An_unrecognised_verb_is_kept_and_reported()
    {
        var path = AutoDutyImporter.Import("(1036) x.json", File(Action("Teleport")));

        Assert.Equal(StepVerb.Unknown, Assert.Single(path.Steps).Verb);
        Assert.Equal("Teleport", path.Steps[0].RawVerb);
        Assert.Contains(path.Warnings, w => w.Contains("Teleport"));
    }

    [Fact]
    public void A_single_empty_argument_counts_as_no_arguments()
    {
        // Roughly fifteen hundred MoveTo steps are written with [""] rather than []. Counting that
        // as one argument makes every arity check downstream wrong.
        var path = AutoDutyImporter.Import("(1036) x.json", File(Action("MoveTo", args: """[""]""")));

        Assert.Empty(Assert.Single(path.Steps).Arguments);
    }

    [Fact]
    public void DutySpecificCode_blocks_the_path_rather_than_importing_a_hole()
    {
        // Five dungeons in the library use it. It calls AutoDuty's own hardcoded C#, so a route
        // containing it can only ever run partway.
        var path = AutoDutyImporter.Import("(1036) Sastasha.json", File(
            Action("MoveTo"),
            Action("DutySpecificCode", args: """["1"]""")));

        Assert.False(path.IsRunnable);
        Assert.Contains(path.Blockers, b => b.Contains("DutySpecificCode"));
    }

    [Fact]
    public void A_jump_past_the_end_of_the_path_is_reported()
    {
        // The real library has one: Aetherochemical Research Facility jumps +51 in a 52-step path,
        // and the step's own note admits its purpose is unknown.
        var path = AutoDutyImporter.Import("(1110) x.json", File(
            Action("ConditionAction", args: """["GetDistanceToPlayer;1,2,3;>;10","ModifyIndex;51"]"""),
            Action("MoveTo")));

        Assert.Contains(path.Warnings, w => w.Contains("outside the path"));
    }

    [Fact]
    public void Malformed_json_produces_a_blocker_instead_of_throwing()
    {
        var path = AutoDutyImporter.Import("(1036) x.json", "{ not json");

        Assert.False(path.IsRunnable);
        Assert.NotEmpty(path.Blockers);
    }

    [Fact]
    public void An_imported_path_starts_with_no_objective_tags()
    {
        // Imports carry no objective information at all — that is learned on the first clean run,
        // which is why a first run resumes position-only and every run after resumes properly.
        var path = AutoDutyImporter.Import("(1036) x.json", File(Action("MoveTo"), Action("Boss")));

        Assert.False(path.IsObjectiveTagged);
        Assert.All(path.Steps, s => Assert.Equal(ThreadPath.UnknownObjective, s.ObjectiveIndex));
    }
}
