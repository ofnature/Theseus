using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Theseus.Services.Frontier;

/// <summary>A labelled point on a zone's map, converted to world coordinates.</summary>
/// <param name="Position">
/// World X and Z with a height of zero — map markers carry no height at all. The navigator snaps
/// this onto the navmesh before walking to it.
/// </param>
public sealed record MapLandmark(string Name, Vector3 Position, ushort Icon);

/// <summary>
/// The map's own labels, per territory, as navigation targets.
///
/// <para>
/// This is what replaces guessing at "deeper". A dungeon's map is already annotated by the people
/// who built it — room names, the boss chamber, the exit — and those labels sit exactly where a
/// route would want to go. Reading them costs nothing and needs no recording, so a zone nobody has
/// walked yet still has somewhere to aim.
/// </para>
/// </summary>
public sealed class MapMarkerCatalog
{
    /// <summary>
    /// Map markers are stored in the map texture's own space: 2048 units across, centre at 1024.
    /// The in-game coordinate conversion runs <c>(world + offset) * scale + 1024</c>, so this is
    /// that arithmetic turned around.
    /// </summary>
    private const float TextureCentre = 1024f;

    private readonly Dictionary<uint, IReadOnlyList<MapLandmark>> _byTerritory = [];

    /// <summary>
    /// Landmarks supplied directly rather than read from the sheets — for tests, and for any future
    /// source of places worth walking to.
    /// </summary>
    public MapMarkerCatalog(IReadOnlyDictionary<uint, IReadOnlyList<MapLandmark>> landmarks)
    {
        foreach (var (territory, list) in landmarks)
            _byTerritory[territory] = list;
    }

    public MapMarkerCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var markers = data.GetSubrowExcelSheet<MapMarker>();

            foreach (var territory in data.GetExcelSheet<TerritoryType>())
            {
                if (territory.Map.ValueNullable is not { } map || map.MapMarkerRange == 0)
                    continue;

                if (!markers.TryGetRow(map.MapMarkerRange, out var subrows))
                    continue;

                var scale = map.SizeFactor / 100f;
                if (scale <= 0f)
                    continue;

                var landmarks = new List<MapLandmark>();
                foreach (var marker in subrows)
                {
                    // Unlabelled markers are decoration — aetheryte glyphs, quest icons. A label is
                    // what makes a marker a place worth walking to.
                    var name = marker.PlaceNameSubtext.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    landmarks.Add(new MapLandmark(
                        name,
                        new Vector3(
                            ((marker.X - TextureCentre) / scale) - map.OffsetX,
                            0f,
                            ((marker.Y - TextureCentre) / scale) - map.OffsetY),
                        marker.Icon));
                }

                if (landmarks.Count > 0)
                    _byTerritory[territory.RowId] = landmarks;
            }
        }
        catch (Exception ex)
        {
            // No markers means the frontier mode has nothing to aim at and says so, which is a
            // worse fallback rather than a broken one.
            log($"Could not read map markers: {ex.Message}");
        }
    }

    public IReadOnlyList<MapLandmark> For(uint territoryId)
        => _byTerritory.GetValueOrDefault(territoryId, []);

    /// <summary>Diagnostic — the landmarks as converted, so a wrong conversion is visible.</summary>
    public string Describe(uint territoryId)
    {
        var landmarks = For(territoryId);
        return landmarks.Count == 0
            ? $"territory {territoryId}: no labelled map markers"
            : string.Join("\n", landmarks.Select(l =>
                $"{l.Name} — ({l.Position.X:0.#}, {l.Position.Z:0.#}) icon {l.Icon}"));
    }
}
