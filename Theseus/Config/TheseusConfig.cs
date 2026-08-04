using System;
using Dalamud.Configuration;

namespace Theseus.Config;

/// <summary>
/// Persisted settings. Kept deliberately flat and small for the framework cut — sections are
/// added as their phases land (see the plan doc's Phases).
/// </summary>
[Serializable]
public sealed class TheseusConfig : IPluginConfiguration
{
    /// <summary>Bumped when a migration is needed; migrations live in <c>TheseusPlugin</c>.</summary>
    public int Version { get; set; } = 1;

    // ── Master ──

    /// <summary>
    /// Master switch. Off means Theseus never drives movement, combat, or chores — it only
    /// observes. Default OFF: a dungeon runner that starts running the moment it is installed
    /// is a bad neighbour.
    /// </summary>
    public bool Enabled { get; set; }

    // ── Run ──

    /// <summary>Stop after this many completed runs. 0 = keep going until stopped.</summary>
    public int RunLimit { get; set; }

    /// <summary>
    /// Offer to resume an interrupted run rather than restarting it. The whole point of the
    /// Thread — on by default, with the confirmation prompt below as the safety valve.
    /// </summary>
    public bool EnableResume { get; set; } = true;

    /// <summary>
    /// Ask before resuming instead of resuming automatically. Off = auto-resume whenever
    /// confidence is high (see the Thread section of the plan doc).
    /// </summary>
    public bool ConfirmBeforeResume { get; set; }

    // ── Fleet ──

    /// <summary>
    /// Hold at objective boundaries until every live peer has caught up. Off = each box runs
    /// its own pace, which is only sane solo.
    /// </summary>
    public bool EnableFleetGates { get; set; } = true;

    /// <summary>A peer unheard from for this long stops holding a gate, so one dead box cannot freeze the fleet.</summary>
    public float PeerStaleSeconds { get; set; } = 10f;

    // ── Chores (between runs) ──

    /// <summary>Repair when durability drops below <see cref="RepairThresholdPercent"/>.</summary>
    public bool EnableRepair { get; set; } = true;

    /// <summary>Durability percentage that triggers a repair trip.</summary>
    public int RepairThresholdPercent { get; set; } = 30;

    /// <summary>Extract materia from spiritbond-100 gear between runs.</summary>
    public bool EnableMateriaExtraction { get; set; } = true;

    /// <summary>
    /// Fire Charon's equip-upgrade pass between runs. Default OFF — it changes your gear, so it
    /// is opt-in (user decision, 2026-08-04).
    /// </summary>
    public bool EnableCharonUpgrades { get; set; }

    // ── Diagnostics ──

    /// <summary>Show the debug section and verbose run logging.</summary>
    public bool DebugMode { get; set; }
}
