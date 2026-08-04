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
    public void Fleet_gates_have_a_peer_timeout_so_one_dead_box_cannot_freeze_the_run()
    {
        var config = new TheseusConfig();
        Assert.True(config.EnableFleetGates);
        Assert.True(config.PeerStaleSeconds > 0f);
    }
}
