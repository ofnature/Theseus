using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The promotion decision needs an answer to one question at every objective boundary: if the solver
/// had been driving, would the run have gone the same way? What these pin is that the answer is
/// taken from what actually happened, that a disagreement is information rather than a strike, and
/// that only demonstrated agreement promotes a territory.
/// </summary>
public sealed class PromotionWatchTests
{
    private const uint Territory = 1314;

    private sealed class Fixture : IDisposable
    {
        public readonly SolverRecordStore Records = new();
        public readonly List<string> Log = [];
        public readonly GapLog Gaps;
        public readonly PromotionWatch Watch;

        private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        public Fixture(string recordsPath = "")
        {
            Gaps = new GapLog(Path.Combine(Path.GetTempPath(), $"theseus-watch-{Guid.NewGuid():N}.jsonl"));
            Watch = new PromotionWatch(Records, Gaps, () => "run-1", () => "key", Log.Add, recordsPath);
        }

        public void Dispose() => Watch.Describe();

        public WorldModel.Snapshot World(
            int stage = 0,
            Vector3? at = null,
            Vector3? unexplored = null,
            params WorldObject[] objects)
        {
            var where = at ?? Vector3.Zero;
            return new WorldModel.Snapshot(
                _now, where, (Territory, 1, 103), stage, stage, 1, true, false, false,
                [.. objects.Select(o => new WorldModel.Recognised(o, null, false, Vector3.Distance(o.Position, where)))],
                [], unexplored, false);
        }

        public void Advance(double seconds) => _now = _now.AddSeconds(seconds);

        /// <summary>A boundary where the solver wanted to walk somewhere, then a tick to score it.</summary>
        public void Boundary(int stage, Vector3 destination)
        {
            Watch.Observe(World(stage: stage, unexplored: destination), new LoopDecision(LoopKind.Exploration, "ground"), false, destination);
        }
    }

    private static readonly Vector3 Ground = new(40f, 0f, 0f);

    [Fact]
    public void The_route_walking_to_the_solvers_destination_agrees()
    {
        using var f = new Fixture();

        f.Boundary(stage: 1, Ground);
        f.Advance(5);
        f.Watch.Observe(f.World(stage: 1, at: Ground, unexplored: Ground), LoopDecision.Nothing, false, Ground);

        Assert.Equal(1, f.Watch.Agreed);
        Assert.Equal(0, f.Watch.Disagreed);
        Assert.Contains("agreed", f.Watch.LastVerdict);
        Assert.Equal(0, f.Gaps.Written);
    }

    [Fact]
    public void The_route_going_elsewhere_disagrees_and_writes_a_gap()
    {
        using var f = new Fixture();

        f.Boundary(stage: 1, Ground);
        f.Advance(10);

        // Inside the window the route might still be on its way, so nothing is decided yet.
        f.Watch.Observe(f.World(stage: 1, at: new Vector3(-40f, 0f, 0f), unexplored: Ground),
            LoopDecision.Nothing, false, Ground);
        Assert.Equal(0, f.Watch.Disagreed);

        f.Advance(PromotionWatch.BoundaryTimeout.TotalSeconds + 1);
        f.Watch.Observe(f.World(stage: 1, at: new Vector3(-40f, 0f, 0f), unexplored: Ground),
            LoopDecision.Nothing, false, Ground);

        Assert.Equal(0, f.Watch.Agreed);
        Assert.Equal(1, f.Watch.Disagreed);
        Assert.Contains("disagreed", f.Watch.LastVerdict);
    }

    [Fact]
    public void A_boundary_with_nothing_to_compare_is_not_scored()
    {
        using var f = new Fixture();

        // The solver wanted to fight, or wanted nothing at all: there is no plan to compare against,
        // so this boundary is not evidence in either direction.
        f.Boundary(stage: 1, Ground);
        f.Watch.Observe(f.World(stage: 2), new LoopDecision(LoopKind.Combat, "in combat"), false);

        Assert.Equal(1, f.Watch.Boundaries);
        Assert.Equal(0, f.Watch.Agreed);
        Assert.Equal(0, f.Watch.Disagreed);
        Assert.Contains("nothing to compare", f.Watch.LastVerdict);
    }

    [Fact]
    public void Nothing_is_scored_while_the_solver_is_the_one_driving()
    {
        using var f = new Fixture();

        // Observation belongs to route runs: a promoted run is scored by getting to the end, and the
        // solver agreeing with itself is not evidence.
        f.Watch.Observe(f.World(stage: 1, unexplored: Ground),
            new LoopDecision(LoopKind.Exploration, "ground"), solverDriving: true, Ground);

        Assert.Equal(0, f.Watch.Boundaries);
    }

    [Fact]
    public void Three_clean_route_runs_promote_the_territory()
    {
        using var f = new Fixture();

        for (var run = 0; run < SolverRecordStore.AgreementsToPromote; run++)
        {
            f.Boundary(stage: 1, Ground);
            f.Advance(5);
            f.Watch.Observe(f.World(stage: 1, at: Ground, unexplored: Ground), LoopDecision.Nothing, false, Ground);

            Assert.False(f.Records.IsPromoted(Territory));   // not before the last one
            f.Watch.NoteRunEnded(Territory);
        }

        Assert.True(f.Records.IsPromoted(Territory));
        Assert.Contains(f.Log, line => line.Contains("is promoted"));
        Assert.Equal(DriverKind.SolverWithRouteFallback, f.Records.Decide(Territory, hasRoute: true, solverReady: true));
    }

    [Fact]
    public void A_disagreement_restarts_the_clean_run_count()
    {
        using var f = new Fixture();

        for (var run = 0; run < SolverRecordStore.AgreementsToPromote - 1; run++)
        {
            f.Boundary(stage: 1, Ground);
            f.Advance(5);
            f.Watch.Observe(f.World(stage: 1, at: Ground, unexplored: Ground), LoopDecision.Nothing, false, Ground);
            f.Watch.NoteRunEnded(Territory);
        }

        Assert.Equal(SolverRecordStore.AgreementsToPromote - 1, f.Records.For(Territory).ShadowAgreements);

        // A run that went elsewhere: the count starts again, and nothing is punished beyond that.
        f.Boundary(stage: 5, Ground);
        f.Advance(PromotionWatch.BoundaryTimeout.TotalSeconds + 1);
        f.Watch.Observe(f.World(stage: 5, unexplored: Ground), LoopDecision.Nothing, false, Ground);
        f.Watch.NoteRunEnded(Territory);

        Assert.Equal(0, f.Records.For(Territory).ShadowAgreements);
        Assert.Equal(1, f.Records.For(Territory).ShadowDisagreements);
        Assert.False(f.Records.IsPromoted(Territory));
    }

    [Fact]
    public void A_run_with_no_scored_boundary_is_evidence_of_nothing()
    {
        using var f = new Fixture();

        f.Watch.NoteRunEnded(Territory);

        Assert.Equal(0, f.Records.For(Territory).ShadowAgreements);
        Assert.Equal(0, f.Records.For(Territory).ShadowDisagreements);
    }

    [Fact]
    public void An_object_taken_where_it_stood_is_an_agreement()
    {
        using var f = new Fixture();
        var lever = new WorldObject(900, 2001234, "lever", new Vector3(4f, 0f, 0f),
            Theseus.Services.Frontier.WorldObjectKind.Interactable, true);

        f.Watch.Observe(f.World(stage: 1, at: Vector3.Zero, objects: lever),
            new LoopDecision(LoopKind.Interactable, "lever is 4y away"), false);

        Assert.Equal(1, f.Watch.Boundaries);

        // Gone from the scan while the character is still where it was: the route took it, which is
        // exactly what the solver wanted to do.
        f.Advance(3);
        f.Watch.Observe(f.World(stage: 1, at: new Vector3(2f, 0f, 0f)), LoopDecision.Nothing, false);

        Assert.Equal(1, f.Watch.Agreed);
    }

    [Fact]
    public void The_record_is_written_when_a_run_ends()
    {
        var path = Path.Combine(Path.GetTempPath(), $"theseus-watch-{Guid.NewGuid():N}.json");

        try
        {
            using (var f = new Fixture(path))
            {
                f.Boundary(stage: 1, Ground);
                f.Advance(5);
                f.Watch.Observe(f.World(stage: 1, at: Ground, unexplored: Ground), LoopDecision.Nothing, false, Ground);
                f.Watch.NoteRunEnded(Territory);
            }

            var read = new SolverRecordStore();
            read.Load(path);

            Assert.Equal(1, read.For(Territory).ShadowAgreements);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
