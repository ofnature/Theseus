using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The promotion record is what makes the driver decision data rather than a setting: a territory
/// only leaves the route behind once runs have earned it, and a record that cannot be read costs
/// nothing but another run the way it already runs today.
/// </summary>
public sealed class SolverRecordTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"theseus-record-{Guid.NewGuid():N}.json");

    [Fact]
    public void A_routed_territory_runs_its_route_and_the_solver_watches()
    {
        var store = new SolverRecordStore();

        Assert.Equal(DriverKind.RouteAndShadow, store.Decide(1314, hasRoute: true, solverReady: true));
    }

    [Fact]
    public void An_unrouted_territory_is_the_solvers_when_it_can_drive()
    {
        var store = new SolverRecordStore();

        Assert.Equal(DriverKind.Solver, store.Decide(1314, hasRoute: false, solverReady: true));
    }

    [Fact]
    public void Without_a_working_solver_nothing_changes()
    {
        var store = new SolverRecordStore();

        // No Ariadne answer: routed territories behave exactly as today, and unrouted ones go to the
        // frontier navigator, which is also today.
        Assert.Equal(DriverKind.RouteAndShadow, store.Decide(1314, hasRoute: true, solverReady: false));
        Assert.Equal(DriverKind.Route, store.Decide(1314, hasRoute: false, solverReady: false));
    }

    [Fact]
    public void A_promoted_territory_drives_with_its_route_as_the_fallback()
    {
        var store = new SolverRecordStore();
        store.For(1314).ShadowAgreements = SolverRecordStore.AgreementsToPromote;

        Assert.True(store.Promote(1314));
        Assert.Equal(DriverKind.SolverWithRouteFallback, store.Decide(1314, hasRoute: true, solverReady: true));
    }

    [Fact]
    public void Agreement_is_what_promotion_costs()
    {
        var store = new SolverRecordStore();
        store.For(1314).ShadowAgreements = SolverRecordStore.AgreementsToPromote - 1;

        Assert.False(store.Promote(1314));
        Assert.False(store.IsPromoted(1314));
    }

    [Fact]
    public void Promotion_and_retirement_are_per_territory()
    {
        var store = new SolverRecordStore();
        store.For(1314).ShadowAgreements = SolverRecordStore.AgreementsToPromote;
        store.Promote(1314);

        Assert.True(store.IsPromoted(1314));
        Assert.False(store.IsPromoted(1315));
        Assert.Equal(DriverKind.SolverWithRouteFallback, store.Decide(1314, hasRoute: true, solverReady: true));
        Assert.Equal(DriverKind.RouteAndShadow, store.Decide(1315, hasRoute: true, solverReady: true));
    }

    [Fact]
    public void The_record_survives_a_round_trip()
    {
        var path = TempPath();

        try
        {
            var written = new SolverRecordStore();
            var record = written.For(1314);
            record.ShadowAgreements = 2;
            record.ShadowDisagreements = 1;
            record.SolverRuns = 3;
            record.SolverFallbacks = 1;
            record.Promoted = true;
            record.LastUtc = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            written.Save(path);

            var read = new SolverRecordStore();
            read.Load(path);

            var back = read.For(1314);
            Assert.Equal(2, back.ShadowAgreements);
            Assert.Equal(1, back.ShadowDisagreements);
            Assert.Equal(3, back.SolverRuns);
            Assert.Equal(1, back.SolverFallbacks);
            Assert.True(back.Promoted);
            Assert.True(read.IsPromoted(1314));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_record_that_cannot_be_read_is_an_empty_record()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ this is not json");

        try
        {
            var store = new SolverRecordStore();

            store.Load(path); // must not throw

            Assert.Empty(store.Records);
            Assert.False(store.IsPromoted(1314));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
