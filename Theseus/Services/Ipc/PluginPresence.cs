using System;
using System.Linq;
using Dalamud.Plugin;

namespace Theseus.Services.Ipc;

/// <summary>
/// Which of the plugins Theseus leans on are actually loaded right now.
///
/// <para>
/// Theseus does movement and route logic; it deliberately does not do pathfinding, boss
/// mechanics, or rotations. Missing dependencies are therefore a first-class UI state, not a
/// crash — the settings window shows a chip per dependency so "why isn't it moving" is
/// answerable at a glance instead of from a log file.
/// </para>
/// </summary>
public sealed class PluginPresence
{
    /// <summary>Pathfinding and movement.</summary>
    public const string VnavmeshInternalName = "vnavmesh";

    /// <summary>
    /// The alternative pathfinding source (with Mnemosyne, out of process). Optional: vnavmesh is
    /// the fallback in either direction, so this is only required when it is the selected source.
    /// </summary>
    public const string AriadneInternalName = "Ariadne";

    /// <summary>Boss mechanics + AI. Reborn is the fork this fleet runs; upstream is accepted as a fallback.</summary>
    public const string BossModRebornInternalName = "BossModReborn";
    public const string BossModInternalName = "BossMod";

    /// <summary>Boss mechanics + AI, the alternative handler.</summary>
    public const string MinervaInternalName = "Minerva";

    /// <summary>Rotation engine and the LAN relay Theseus rides instead of opening its own socket.</summary>
    public const string DaedalusInternalName = "Daedalus";

    /// <summary>Optional — equip-upgrade passes between runs.</summary>
    public const string CharonInternalName = "Charon";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Func<Config.BossHandler> _bossHandler;

    public PluginPresence(IDalamudPluginInterface pluginInterface, Func<Config.BossHandler>? bossHandler = null)
    {
        _pluginInterface = pluginInterface;
        _bossHandler = bossHandler ?? (() => Config.BossHandler.BossModReborn);
    }

    public bool Vnavmesh => IsLoaded(VnavmeshInternalName);

    public bool Ariadne => IsLoaded(AriadneInternalName);

    public bool BossMod => IsLoaded(BossModRebornInternalName) || IsLoaded(BossModInternalName);

    public bool Minerva => IsLoaded(MinervaInternalName);

    /// <summary>The selected boss handler is loaded.</summary>
    public bool BossHandler => _bossHandler() == Config.BossHandler.Minerva ? Minerva : BossMod;

    /// <summary>Display name of the selected boss handler.</summary>
    public string BossHandlerName => _bossHandler() == Config.BossHandler.Minerva ? "Minerva" : "BossMod Reborn";

    public bool Daedalus => IsLoaded(DaedalusInternalName);

    public bool Charon => IsLoaded(CharonInternalName);

    /// <summary>Everything required to run a dungeon is present.</summary>
    public bool CoreReady => Vnavmesh && BossHandler && Daedalus;

    /// <summary>
    /// Human-readable reason the run cannot start, or empty when it can. Named so the UI and the
    /// eventual run controller give the user the same sentence.
    /// </summary>
    public string MissingSummary()
    {
        var missing = new System.Collections.Generic.List<string>();
        if (!Vnavmesh) missing.Add("vnavmesh");
        if (!BossHandler) missing.Add(BossHandlerName);
        if (!Daedalus) missing.Add("Daedalus");
        return missing.Count == 0 ? string.Empty : "Missing: " + string.Join(", ", missing);
    }

    private bool IsLoaded(string internalName)
    {
        try
        {
            return _pluginInterface.InstalledPlugins.Any(p =>
                string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase)
                && p.IsLoaded
                && !p.IsOutdated);
        }
        catch
        {
            // Fail closed: an unreadable plugin list means we cannot promise the dependency.
            return false;
        }
    }
}
