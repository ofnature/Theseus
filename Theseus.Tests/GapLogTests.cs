using System.Numerics;
using System.Text.Json;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The gap log is read offline, by a person or a script, so the tests are about the line being
/// readable and the run never caring whether it was written.
/// </summary>
public class GapLogTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"theseus-gaps-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void A_line_carries_everything_an_offline_pass_needs()
    {
        var path = TempPath();
        try
        {
            var log = new GapLog(path, () => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
            log.Append(
                GapKind.UnknownInteractable,
                runId: "1314:103:0",
                territory: 1314,
                cacheKey: "ex5_01_xkt_x6_dun_x6d9_level_x6d9__483CF____0",
                stage: 2,
                position: new Vector3(104.18f, 38.06f, 275.93f),
                dataId: 2001234,
                name: "Quan Mechanism",
                nearby: [new GapNeighbour(2009999, "Interactable", 4.5f)],
                detail: "nothing observed in the window");

            var line = Assert.Single(File.ReadAllLines(path));

            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            Assert.Equal("UnknownInteractable", root.GetProperty("Kind").GetString());
            Assert.Equal("1314:103:0", root.GetProperty("RunId").GetString());
            Assert.Equal(1314u, root.GetProperty("Territory").GetUInt32());
            Assert.Equal(2, root.GetProperty("Stage").GetInt32());
            Assert.Equal(104.18f, root.GetProperty("X").GetSingle(), 2);
            Assert.Equal(2001234u, root.GetProperty("DataId").GetUInt32());
            Assert.Equal("Quan Mechanism", root.GetProperty("Name").GetString());
            Assert.Equal("nothing observed in the window", root.GetProperty("Detail").GetString());
            Assert.Equal("Interactable", root.GetProperty("Nearby")[0].GetProperty("Kind").GetString());
            Assert.Equal(4.5f, root.GetProperty("Nearby")[0].GetProperty("Distance").GetSingle(), 2);
            Assert.Equal(1, log.Written);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Lines_accumulate_rather_than_replace()
    {
        var path = TempPath();
        try
        {
            var log = new GapLog(path);
            log.Append(GapKind.Inert, "run", 1, "key", 0, Vector3.Zero, detail: "first");
            log.Append(GapKind.UnknownGate, "run", 1, "key", 0, Vector3.Zero, detail: "second");

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("first", lines[0]);
            Assert.Contains("second", lines[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_log_that_cannot_be_written_warns_once_and_never_throws()
    {
        var warnings = new List<string>();
        var unwritable = Path.Combine(Path.GetTempPath(), $"theseus-no-such-dir-{Guid.NewGuid():N}", "gaps.jsonl");
        var log = new GapLog(unwritable, log: warnings.Add);

        log.Append(GapKind.ProbeFailed, "run", 1314, "key", 0, Vector3.Zero, detail: "one");
        log.Append(GapKind.ProbeFailed, "run", 1314, "key", 0, Vector3.Zero, detail: "two");
        log.Append(GapKind.ProbeFailed, "run", 1314, "key", 0, Vector3.Zero, detail: "three");

        Assert.Single(warnings);
        Assert.Equal(0, log.Written);
    }
}
