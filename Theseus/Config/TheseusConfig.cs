using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Leave the instance once the duty is cleared. Off means the run stops inside and waits for
    /// you, which is what you want while a route is still being proven.
    /// </summary>
    public bool LeaveWhenComplete { get; set; } = true;

    /// <summary>
    /// Run as the character's role, decided at the start of each run.
    ///
    /// <para>
    /// Two things follow from it. Route choice — a tank gets the wall-to-wall route where the
    /// library ships one. And step filtering: routes tag their chain-pull steps <c>W2W</c>, and
    /// those only run for a tank. Mistwake is the clearest case, where eighteen of thirty steps
    /// are tagged: run them and the character merges packs, skip them and it kills each pack where
    /// it stands. One route, both behaviours.
    /// </para>
    ///
    /// <para>
    /// On by default because it mirrors AutoDuty. Off runs every route plainly, ignoring roles.
    /// </para>
    /// </summary>
    public bool MatchRouteToRole { get; set; } = true;

    // ── Navigation ──

    /// <summary>
    /// Who computes paths. vnavmesh by default — the migration to Ariadne is flipped one user (and
    /// then one territory) at a time, and vnavmesh is the fallback either way.
    ///
    /// <para>
    /// The win is readiness: Ariadne asks Mnemosyne, which keeps meshes out of process, so a zone
    /// can be routed about a tenth of a second after zone-in instead of after vnavmesh's 7–15 s
    /// build. A move Ariadne cannot answer falls back to vnavmesh with one line in the log.
    /// </para>
    /// </summary>
    public NavSource NavSource { get; set; } = NavSource.Vnavmesh;

    /// <summary>
    /// Which plugin handles boss mechanics. BossMod Reborn by default, matching every run so far.
    /// </summary>
    public BossHandler BossHandler { get; set; } = BossHandler.BossModReborn;

    /// <summary>
    /// The Minerva dodge preset claimed at the start of a run. Must exist in Minerva with auto-dodge on —
    /// Minerva's built-in Default preset has it off, and an unknown name is refused.
    /// </summary>
    public string MinervaPreset { get; set; } = "Theseus";

    /// <summary>
    /// Take treasure coffers at all. Off means neither the route's chests nor a boss's.
    /// </summary>
    public bool LootChests { get; set; } = true;

    /// <summary>
    /// Take only the chest a boss drops, skipping the route's own detours.
    ///
    /// <para>
    /// The two are worth separating because they cost differently. Routes tag the whole chest
    /// excursion <c>Treasure</c> — the walk over, whatever guards it, any dialog — so a route chest
    /// costs a detour, and turning them off makes the run go straight past rather than stand at an
    /// unopened coffer. A boss chest costs nothing: it is in the room you are already standing in
    /// when the fight ends.
    /// </para>
    /// </summary>
    public bool LootBossChestsOnly { get; set; }

    /// <summary>
    /// Begin running as soon as a duty starts, without waiting to be told.
    ///
    /// <para>
    /// Covers the case where the duty was entered by some other means — a roulette, a manual queue,
    /// a party finder — and the character simply arrives somewhere. Theseus already works out the
    /// route from the territory, so the only thing missing is the decision to begin.
    /// </para>
    ///
    /// <para>
    /// Off by default, for the same reason the master switch is: a dungeon runner that starts
    /// moving the instant you zone in is a bad neighbour. A territory with no usable route leaves
    /// the run idle and says so rather than faulting, so a roulette into unknown content is
    /// harmless.
    /// </para>
    /// </summary>
    public bool AutoStartInDuty { get; set; }

    /// <summary>
    /// Explore zones that have no recorded route, instead of refusing to run them.
    ///
    /// <para>
    /// A far worse dungeon run than a recorded route: it fights what it can see and walks to the
    /// map's own labels when the room is quiet, with no knowledge of the dungeon's shape. What it
    /// buys is that new content works at all on the day it ships, rather than after someone has
    /// recorded it. Zones that <i>do</i> have a route are untouched by this.
    /// </para>
    /// </summary>
    public bool ExploreUnmappedZones { get; set; } = true;

    /// <summary>Stop after this many completed runs. 0 = keep going until stopped.</summary>
    public int RunLimit { get; set; }

    /// <summary>
    /// The duty the entry picker is sitting on, as a ContentFinderCondition id. Remembered because
    /// a fleet grinds the same dungeon for hours and re-picking it on every box is pure friction.
    /// </summary>
    public uint LastDutyCondition { get; set; }

    /// <summary>
    /// Show every dungeon in the entry picker rather than only those with a route. Off by default:
    /// entering content Theseus cannot run leaves you standing in it.
    /// </summary>
    public bool ShowAllDutiesInPicker { get; set; }

    /// <summary>
    /// Expansion the entry picker is filtered to, as an ExVersion row id. -1 shows everything.
    /// A hundred-odd dungeons is too many to scroll, and you grind within one expansion at a time.
    /// </summary>
    public int DutyPickerExpansion { get; set; } = -1;

    /// <summary>
    /// Trust companions to bring, by the game's member id. Empty lets the game choose.
    ///
    /// <para>
    /// Kept global rather than per-duty because the roster barely changes at level cap and a fleet
    /// grinds one dungeon at a time. Any companion that turns out not to be offered — a story
    /// unlock this character has not reached — makes the whole party fall back to the game's own
    /// default rather than failing to register.
    /// </para>
    /// </summary>
    public List<byte> TrustParty { get; set; } = [];

    /// <summary>
    /// Enter through Duty Support even where Trust is available.
    ///
    /// <para>
    /// The two are separate systems, not two names for one, and a duty that has both can be run
    /// either way. Duty Support brings the story's own cast and asks nothing of you, which is what
    /// an MSQ dungeon wants; Trust brings companions you pick and level. Content older than
    /// Shadowbringers has only Duty Support, so this changes nothing there.
    /// </para>
    /// </summary>
    public bool PreferDutySupport { get; set; }

    /// <summary>
    /// Which route to run in a given territory, keyed by territory id.
    ///
    /// <para>
    /// Twenty-seven territories in the library carry more than one route — wall-to-wall pulls
    /// intended for a tank, a gentler route for everyone else, separate exits in the variant
    /// dungeons. Without a choice recorded here the runner takes whichever sorted first, which on
    /// a healer means quietly running the tank's route. Territories with a single route never
    /// appear.
    /// </para>
    /// </summary>
    public Dictionary<uint, string> PreferredRoutes { get; set; } = [];

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
