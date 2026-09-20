using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Theseus.Services.Frontier;

/// <summary>
/// One hand-authored escape: stand near <see cref="From"/>, go to <see cref="To"/>.
/// </summary>
/// <param name="Note">Why it exists. Written for whoever reads this file in six months.</param>
public sealed class ZoneOverride
{
    public uint TerritoryId { get; set; }

    public float FromX { get; set; }

    public float FromY { get; set; }

    public float FromZ { get; set; }

    public float ToX { get; set; }

    public float ToY { get; set; }

    public float ToZ { get; set; }

    /// <summary>How close counts as being at the entry point.</summary>
    public float Radius { get; set; } = 8f;

    public string Note { get; set; } = string.Empty;

    [JsonIgnore]
    public Vector3 From => new(FromX, FromY, FromZ);

    [JsonIgnore]
    public Vector3 To => new(ToX, ToY, ToZ);
}

/// <summary>
/// Hand-authored waypoints for the places automation cannot see.
///
/// <para>
/// The frontier navigator reasons about things it can perceive — objects in the table, labels on
/// the map — and some of a dungeon is neither. A one-way slide is not an object; the far side of a
/// hyperpoint is not reachable ground until you are through it; a drop looks like a wall to a
/// navmesh. These are the seams, and they are rare enough to be worth writing down by hand and
/// common enough that pretending otherwise leaves the mode stuck.
/// </para>
///
/// <para>
/// Deliberately the <i>only</i> hand-authored input in this mode. Every override is an admission
/// that perception failed somewhere, so the file staying small is the measure of whether the rest
/// of it works.
/// </para>
/// </summary>
public sealed class ZoneOverrideStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _file;
    private readonly Action<string>? _log;
    private List<ZoneOverride> _overrides = [];

    public ZoneOverrideStore(string file, Action<string>? log = null)
    {
        _file = file;
        _log = log;
        Reload();
    }

    public IReadOnlyList<ZoneOverride> All => _overrides;

    public void Reload()
    {
        try
        {
            _overrides = File.Exists(_file)
                ? JsonSerializer.Deserialize<List<ZoneOverride>>(File.ReadAllText(_file)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            _overrides = [];
            _log?.Invoke($"Could not read zone overrides: {ex.Message}");
        }
    }

    public IReadOnlyList<ZoneOverride> For(uint territoryId)
        => _overrides.Where(o => o.TerritoryId == territoryId).ToList();

    /// <summary>
    /// The override covering a position, or null. Nearest wins where several overlap, so an
    /// authored point placed tightly beats a loose one that happens to reach.
    /// </summary>
    public ZoneOverride? Covering(uint territoryId, Vector3 position)
        => _overrides
            .Where(o => o.TerritoryId == territoryId)
            .Select(o => (Override: o, Distance: Vector3.Distance(o.From, position)))
            .Where(x => x.Distance <= x.Override.Radius)
            .OrderBy(x => x.Distance)
            .Select(x => x.Override)
            .FirstOrDefault();

    public void Add(ZoneOverride entry)
    {
        _overrides.Add(entry);
        Save();
    }

    public bool Remove(ZoneOverride entry)
    {
        if (!_overrides.Remove(entry))
            return false;

        Save();
        return true;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(_overrides, Options));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not write zone overrides: {ex.Message}");
        }
    }
}
