using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Theseus.Services.Paths;

/// <summary>
/// A step's action. Every verb observed in the AutoDuty library has an entry, including the ones
/// the executor does not implement — a verb that maps to nothing at import time disappears
/// silently, and a route with a hole in it is worse than one that refuses to load.
/// </summary>
public enum StepVerb
{
    /// <summary>Verb not recognised. Carries its original spelling in <see cref="ThreadStep.RawVerb"/>.</summary>
    Unknown = 0,

    // ── Movement ──
    MoveTo,
    AutoMoveFor,
    Jump,
    JumpTo,
    CameraFacing,

    // ── Combat ──
    Boss,
    KillInRange,
    ForceAttack,
    Target,
    Action,
    StopForCombat,
    Rotation,
    BossMod,
    DisableBMModule,
    BLULoad,

    // ── World interaction ──
    Interactable,
    TreasureCoffer,
    SelectString,
    SelectYesno,
    SelectJournalResult,
    VariantVote,

    // ── Timing ──
    Wait,
    WaitFor,
    Revival,

    // ── Flow control ──
    ModifyIndex,
    ConditionAction,

    // ── Other ──
    ChatCommand,
    PausePandora,
    Comment,

    /// <summary>Calls hardcoded per-dungeon C# inside AutoDuty. Cannot be imported — see <see cref="ThreadPath.Blockers"/>.</summary>
    DutySpecificCode,
}

/// <summary>
/// Step tags, as combinable flags. Sync tags gate a step on whether the duty is level-synced;
/// the rest are annotations.
/// </summary>
[Flags]
public enum StepTag
{
    None = 0,
    Synced = 1 << 0,
    Unsynced = 1 << 1,
    Comment = 1 << 2,
    Treasure = 1 << 3,
    Revival = 1 << 4,

    /// <summary>Wall-to-wall pulling.</summary>
    W2W = 1 << 5,
}

/// <summary>
/// What kind of pull a route is written for.
///
/// <para>
/// The library encodes this in the file name, and the routes really are different plans rather
/// than variations on one. A tank route chains packs together — move, wait for the AoE to land,
/// move on, with combat stops disabled — while the matching non-tank route stops and kills at each
/// pack. Running the wrong one is not a matter of efficiency: a damage dealer following the tank's
/// route pulls the entire wing onto itself.
/// </para>
/// </summary>
public enum RouteVariant
{
    /// <summary>The ordinary route. What a lone character runs.</summary>
    Standard,

    /// <summary>Wall-to-wall pull written for the tank.</summary>
    TankWallToWall,

    /// <summary>Written for everyone else while a tank is pulling wall to wall.</summary>
    OtherWallToWall,

    /// <summary>Wall-to-wall with no role named. Safe for the puller; not for anyone following.</summary>
    WallToWall,
}

/// <summary>
/// A position in a path. Kept separate from <see cref="Vector3"/> so the serialized form is plain
/// named properties rather than depending on how a JSON library happens to treat that struct's
/// fields.
/// </summary>
public readonly record struct PathPoint(float X, float Y, float Z)
{
    public Vector3 ToVector3() => new(X, Y, Z);

    /// <summary>Whether this is a real destination rather than the origin placeholder.</summary>
    public bool IsSet => X != 0f || Y != 0f || Z != 0f;

    public override string ToString() => $"{X:0.##}, {Y:0.##}, {Z:0.##}";
}

/// <summary>One instruction in a route.</summary>
public sealed class ThreadStep
{
    public StepVerb Verb { get; set; }

    /// <summary>
    /// The verb exactly as the source wrote it. Kept because the library is not consistent about
    /// casing — <c>BossMod</c> and <c>Bossmod</c> both appear — so this is what a diagnostic should
    /// quote back, and it is the only way to identify an <see cref="StepVerb.Unknown"/> step.
    /// </summary>
    public string RawVerb { get; set; } = string.Empty;

    public PathPoint Position { get; set; }

    /// <summary>
    /// Verb arguments, verbatim and unparsed.
    ///
    /// <para>
    /// Deliberately kept raw. Conversion happens once and the result is what we run forever after,
    /// so anything discarded here is discarded permanently — including argument meanings we have
    /// not pinned down yet, like the lone boolean some <see cref="StepVerb.MoveTo"/> steps carry.
    /// Storing the source text costs nothing and means teaching the executor a new argument later
    /// does not require every user to re-import.
    /// </para>
    /// </summary>
    public string[] Arguments { get; set; } = [];

    public StepTag Tag { get; set; }

    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// Which duty objective was live when this step ran, or -1 when not yet known.
    ///
    /// <para>
    /// Imported paths arrive with -1 everywhere; <c>ObjectiveMapper</c> fills it in on the first
    /// clean run. This is what makes resume safe: relocalization searches only the steps belonging
    /// to the objective the game says is current, so it cannot jump the run to a coincidentally
    /// nearby step on the far side of a wall.
    /// </para>
    /// </summary>
    public int ObjectiveIndex { get; set; } = ThreadPath.UnknownObjective;

    public override string ToString()
        => $"{Verb}{(Position.IsSet ? $" @ {Position}" : string.Empty)}";
}

/// <summary>
/// A converted route for one territory. This is Theseus's own format: paths are converted once
/// and persisted, never re-read from the source on every launch, because AutoDuty's format is
/// internal and not a contract anyone owes us.
/// </summary>
public sealed class ThreadPath
{
    /// <summary>Sentinel for "this step's objective has not been learned yet".</summary>
    public const int UnknownObjective = -1;

    /// <summary>Bumped when the persisted shape changes; a mismatch forces a re-import.</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public uint TerritoryId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Where this came from — an import, or our own recorder.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the source file this was converted from. Lets a re-import detect that the user's
    /// AutoDuty library changed underneath us without re-converting 309 files every launch.
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>Importer that produced this, so a fixed converter bug can invalidate its output.</summary>
    public int ImporterVersion { get; set; }

    public List<ThreadStep> Steps { get; set; } = [];

    /// <summary>
    /// Reasons this path cannot be run, if any. Non-empty means refuse the run and say why rather
    /// than starting a route that will strand the character partway in.
    /// </summary>
    public List<string> Blockers { get; set; } = [];

    /// <summary>Import notes that did not stop the conversion — unrecognised verbs, malformed arguments.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Someone has edited this route by hand.
    ///
    /// <para>
    /// Which makes it unrecoverable. An imported route can always be rebuilt from the source file,
    /// but a hand-fixed one exists only here — so re-importing must leave it alone, including the
    /// deliberate "re-convert everything" pass. Losing an evening of waypoint fixes to a button
    /// press is not a recoverable mistake.
    /// </para>
    /// </summary>
    public bool IsEdited { get; set; }

    public bool IsRunnable => Blockers.Count == 0 && Steps.Count > 0;

    /// <summary>
    /// Stable identifier for this route, derived from territory and name.
    ///
    /// <para>
    /// A territory is not enough on its own — twenty-seven of them carry more than one route, and
    /// the library distinguishes them only by name ("「Tank W2W - タンクまとめ」 Sohm Al" beside a
    /// plain "Sohm Al"). This is what a saved preference points at, so it has to survive a
    /// re-import: it is computed from the source rather than assigned, so converting the same file
    /// twice yields the same key and the user's choice is not silently forgotten.
    /// </para>
    /// </summary>
    public string Key => BuildKey(TerritoryId, Name);

    /// <summary>
    /// Which kind of pull this route is written for, read from its name.
    ///
    /// <para>
    /// The library names them "「Tank W2W - タンクまとめ」 Sohm Al", "「Other W2W - タンク以外まとめ」
    /// Sohm Al", and an unqualified "「W2W-まとめ」 Alexandria". Matching on the Latin part only —
    /// the Japanese is a translation of the same thing — keeps this readable and has full coverage
    /// across the library.
    /// </para>
    /// </summary>
    public RouteVariant Variant
    {
        get
        {
            if (Name.Contains("W2W", StringComparison.OrdinalIgnoreCase))
            {
                if (Name.Contains("Tank", StringComparison.OrdinalIgnoreCase))
                    return RouteVariant.TankWallToWall;

                return Name.Contains("Other", StringComparison.OrdinalIgnoreCase)
                    ? RouteVariant.OtherWallToWall
                    : RouteVariant.WallToWall;
            }

            return RouteVariant.Standard;
        }
    }

    /// <summary>Key for a route that has not been loaded, so callers can look one up by name.</summary>
    public static string BuildKey(uint territoryId, string name)
    {
        var slug = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-')
            .ToLowerInvariant();

        return $"{territoryId}-{slug}";
    }

    /// <summary>
    /// Whether every non-annotation step knows its objective. False on a freshly imported path and
    /// true after one clean run, which is exactly when resume graduates from position-only to
    /// objective-gated.
    /// </summary>
    public bool IsObjectiveTagged
    {
        get
        {
            foreach (var step in Steps)
            {
                if (step.Verb != StepVerb.Comment && step.ObjectiveIndex == UnknownObjective)
                    return false;
            }

            return Steps.Count > 0;
        }
    }
}
