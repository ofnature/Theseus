using Dalamud.Plugin;
using Moq;
using Theseus.Config;
using Theseus.Services.Ipc;

namespace Theseus.Tests;

/// <summary>What "ready to run" means for each path source.</summary>
public class PluginPresenceTests
{
    private static PluginPresence With(NavSource source, params string[] loaded)
    {
        var plugins = loaded.Select(name =>
        {
            var plugin = new Mock<IExposedPlugin>();
            plugin.SetupGet(p => p.InternalName).Returns(name);
            plugin.SetupGet(p => p.IsLoaded).Returns(true);
            plugin.SetupGet(p => p.IsOutdated).Returns(false);
            return plugin.Object;
        }).ToList();

        var pi = new Mock<IDalamudPluginInterface>();
        pi.SetupGet(p => p.InstalledPlugins).Returns(plugins);
        return new PluginPresence(pi.Object, () => BossHandler.Minerva, () => source);
    }

    [Fact]
    public void With_ariadne_selected_vnavmesh_is_not_required()
    {
        // The field report: "Missing: vnavmesh" and a disabled Queue and run on a client running
        // Ariadne, Minerva and Daedalus as intended.
        var presence = With(NavSource.Ariadne, "Ariadne", "Minerva", "Daedalus");

        Assert.True(presence.CoreReady);
        Assert.Equal(string.Empty, presence.MissingSummary());
    }

    [Fact]
    public void With_ariadne_selected_but_absent_vnavmesh_still_carries_the_run()
    {
        // The mover hands every move to vnavmesh when Ariadne cannot route.
        var presence = With(NavSource.Ariadne, "vnavmesh", "Minerva", "Daedalus");

        Assert.True(presence.CoreReady);
    }

    [Fact]
    public void With_neither_path_source_the_selected_one_is_named()
    {
        var presence = With(NavSource.Ariadne, "Minerva", "Daedalus");

        Assert.False(presence.CoreReady);
        Assert.Equal("Missing: Ariadne", presence.MissingSummary());
    }

    [Fact]
    public void With_vnavmesh_selected_it_is_still_required()
    {
        var presence = With(NavSource.Vnavmesh, "Ariadne", "Minerva", "Daedalus");

        Assert.False(presence.CoreReady);
        Assert.Equal("Missing: vnavmesh", presence.MissingSummary());
    }
}
