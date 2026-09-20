using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Theseus.Services.Duty;

/// <summary>One duty as the Duty Finder knows it.</summary>
/// <param name="ContentFinderConditionId">
/// What every entry API takes. Not the territory id — a territory can be reached by more than one
/// condition row, and the condition is the one that carries the level gate and the queue rules.
/// </param>
/// <param name="TerritoryId">Where you end up, and therefore which route runs.</param>
/// <param name="HasDutySupport">
/// The duty ships with Duty Support (or a Trust party, which the game files treat as the same
/// thing). Theseus can only enter through that door, so a duty without it is unenterable here
/// however good its route is.
/// </param>
public sealed record DutyListing(
    uint ContentFinderConditionId,
    uint TerritoryId,
    string Name,
    byte LevelRequired,
    ushort ItemLevelRequired,
    uint ExpansionId,
    string ExpansionName,
    bool HasDutySupport)
{
    /// <summary>
    /// Shadowbringers onwards, where Trust exists and you choose who comes. Everything before it
    /// is Duty Support only, with a fixed cast — which is what the MSQ dungeons run on.
    /// </summary>
    public bool SupportsTrust => ExpansionId >= 3;

    /// <summary>What the game calls this duty's companion system.</summary>
    public string CompanionSystem => SupportsTrust ? "Trust" : "Duty Support";

    /// <summary>The picker's row: what it is, what it costs to get in, and whether you can.</summary>
    public string Label
        => $"{Name} (Lv {LevelRequired}, ilvl {ItemLevelRequired}" +
           (HasDutySupport ? $", {CompanionSystem})" : ", no Duty Support)");
}

/// <summary>
/// The dungeon list, read once from the game's own sheets.
///
/// <para>
/// Entering needs a ContentFinderCondition id and Theseus everywhere else speaks territory ids, so
/// something has to bridge the two. Reading it from Lumina rather than shipping a table means it
/// cannot go stale against a patch, and it means the names in the picker are the game's own — a
/// hand-maintained list would drift on both counts.
/// </para>
/// </summary>
public sealed class DutyCatalog
{
    /// <summary>ContentType row for Dungeons. The only content Theseus runs.</summary>
    private const uint DungeonContentType = 2;

    private readonly Dictionary<uint, DutyListing> _byCondition;
    private readonly Dictionary<uint, string> _names = [];
    private readonly Dictionary<uint, DutyListing> _byTerritory;

    public DutyCatalog(IDataManager data, Action<string> log)
    {
        var listings = new List<DutyListing>();
        var expansions = new Dictionary<uint, string>();

        try
        {
            // Duty Support is a sheet, not a runtime question: DawnContent lists every duty that
            // ships with one. Asking the game instead would mean the picker could not be built
            // until a character was logged in.
            var dawn = data.GetExcelSheet<DawnContent>()
                .Select(d => d.Content.RowId)
                .ToHashSet();

            foreach (var row in data.GetExcelSheet<ContentFinderCondition>())
            {
                // Every duty's name is kept, trials included: the Duty Finder is matched by name, and
                // Odysseus hands MSQ trials here as well as dungeons.
                var name = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    _names[row.RowId] = name;

                if (row.ContentType.RowId != DungeonContentType)
                    continue;

                var territory = row.TerritoryType.RowId;
                if (territory == 0 || string.IsNullOrWhiteSpace(name))
                    continue;

                var expansion = row.RequiredExVersion.RowId;
                var expansionName = row.RequiredExVersion.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (expansionName.Length > 0)
                    expansions.TryAdd(expansion, expansionName);

                listings.Add(new DutyListing(
                    row.RowId,
                    territory,
                    name,
                    row.ClassJobLevelRequired,
                    row.ItemLevelRequired,
                    expansion,
                    expansionName,
                    dawn.Contains(row.RowId)));
            }
        }
        catch (Exception ex)
        {
            // A catalog that fails to load costs the duty picker and nothing else — everything
            // that runs a route works from the territory you are already standing in.
            log($"Could not read the duty list: {ex.Message}");
        }

        Dungeons = listings.OrderBy(d => d.LevelRequired).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Expansions = expansions.OrderBy(e => e.Key).Select(e => (Id: e.Key, Name: e.Value)).ToList();
        _byCondition = Dungeons.ToDictionary(d => d.ContentFinderConditionId);

        // First condition wins per territory: where a territory has several (a story version and a
        // repeatable one), the lower row id is the one you can queue for normally.
        _byTerritory = new Dictionary<uint, DutyListing>();
        foreach (var listing in Dungeons.OrderBy(d => d.ContentFinderConditionId))
            _byTerritory.TryAdd(listing.TerritoryId, listing);
    }

    /// <summary>Every dungeon, ordered by level then name — the order the picker shows.</summary>
    public IReadOnlyList<DutyListing> Dungeons { get; }

    /// <summary>Expansions that actually have dungeons, in release order.</summary>
    public IReadOnlyList<(uint Id, string Name)> Expansions { get; }

    public DutyListing? ByCondition(uint contentFinderConditionId)
        => _byCondition.GetValueOrDefault(contentFinderConditionId);

    public DutyListing? ByTerritory(uint territoryId)
        => _byTerritory.GetValueOrDefault(territoryId);

    /// <summary>Display name for a condition id, falling back to the raw id.</summary>
    public string NameOf(uint contentFinderConditionId)
        => _names.GetValueOrDefault(contentFinderConditionId) ?? $"duty {contentFinderConditionId}";
}
