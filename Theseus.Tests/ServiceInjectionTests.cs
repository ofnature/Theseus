using System.Reflection;
using Dalamud.IoC;
using Dalamud.Plugin;

namespace Theseus.Tests;

/// <summary>
/// Guards the plugin against re-acquiring a load-time infinite recursion.
///
/// <para>
/// <c>IDalamudPluginInterface.Create&lt;T&gt;()</c> constructs a T. If T is the plugin, its
/// constructor calls <c>Create</c> again and the load recurses until the client dies — and it does
/// so <b>silently</b>: Dalamud logs "Creating plugin instance" and then nothing, no exception, no
/// timeout, no error. It cost a full debugging session to find, and the compiler will never
/// complain about it, so the invariant is pinned here instead.
/// </para>
/// </summary>
public class ServiceInjectionTests
{
    [Fact]
    public void The_plugin_declares_no_injectable_services_of_its_own()
    {
        // If services live on the plugin type, the only way to populate them is Create<Plugin>() —
        // which is the recursion. Keeping the plugin free of them makes the mistake unavailable.
        var injectable = typeof(TheseusPlugin)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<PluginServiceAttribute>() is not null)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(injectable);
    }

    [Fact]
    public void The_service_holder_is_cheap_to_construct()
    {
        // Create<T> instantiates T, so the holder must have a constructor that does nothing. One
        // that did real work would run on every injection pass.
        var constructors = typeof(Service).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.All(constructors, c => Assert.Empty(c.GetParameters()));
    }

    [Fact]
    public void The_service_holder_is_not_the_plugin()
    {
        Assert.NotEqual(typeof(TheseusPlugin), typeof(Service));
        Assert.False(typeof(IDalamudPlugin).IsAssignableFrom(typeof(Service)));
    }

    [Fact]
    public void Every_service_the_plugin_needs_is_declared_on_the_holder()
    {
        // A service added to the holder but never injected would be null at runtime; one the code
        // uses but the holder omits would not compile. This just pins that the holder is where
        // they live.
        var declared = typeof(Service)
            .GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Count(p => p.GetCustomAttribute<PluginServiceAttribute>() is not null);

        Assert.Equal(13, declared);
    }
}
