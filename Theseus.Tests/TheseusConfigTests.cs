using Theseus.Config;

namespace Theseus.Tests;

public class TheseusConfigTests
{
    [Fact]
    public void Ships_dark()
    {
        // A dungeon runner that starts driving the character on install is a bad neighbour.
        Assert.False(new TheseusConfig().Enabled);
    }

    [Fact]
    public void Gear_changing_chores_are_opt_in_but_safe_ones_are_not()
    {
        var config = new TheseusConfig();

        // Repair and materia extraction are the AutoDuty gaps this exists to close.
        Assert.True(config.EnableRepair);
        Assert.True(config.EnableMateriaExtraction);

        // Charon swaps equipped gear, so it waits to be asked.
        Assert.False(config.EnableCharonUpgrades);
    }

    [Fact]
    public void Resume_is_on_and_silent_by_default()
    {
        var config = new TheseusConfig();
        Assert.True(config.EnableResume);
        Assert.False(config.ConfirmBeforeResume);
    }

    [Fact]
    public void Pathfinding_stays_on_vnavmesh_until_it_is_asked_for()
    {
        // The migration flips one territory at a time, and a config that arrived from an older
        // install must not silently start routing through Ariadne.
        Assert.Equal(NavSource.Vnavmesh, new TheseusConfig().NavSource);
    }

    [Fact]
    public void The_solver_drives_unrouted_zones_unless_it_is_told_not_to()
    {
        // A territory with a route is unaffected either way, so the default only reaches content
        // that has nothing recorded and would otherwise go to the frontier navigator.
        var config = new TheseusConfig();

        Assert.True(config.SolverDrives);
        Assert.True(config.ExploreUnmappedZones);
    }

    [Fact]
    public void Fleet_gates_have_a_peer_timeout_so_one_dead_box_cannot_freeze_the_run()
    {
        var config = new TheseusConfig();
        Assert.True(config.EnableFleetGates);
        Assert.True(config.PeerStaleSeconds > 0f);
    }
}
