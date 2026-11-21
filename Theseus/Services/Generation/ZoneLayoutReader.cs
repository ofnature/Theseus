using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;

namespace Theseus.Services.Generation;

/// <summary>
/// Reads a territory's event objects out of its zone files, the way the game ships them.
///
/// <para>
/// Read fresh each time and kept nowhere: the route planned from it lives for one run. Any failure
/// is an empty list, which the planner reports as "nothing to plan from" and the run falls back.
/// </para>
/// </summary>
public sealed class ZoneLayoutReader
{
    // Event objects live in these two. Across 111 dungeons the scenery file held two of 3,597.
    private static readonly string[] Files = ["planmap.lgb", "planevent.lgb"];

    private readonly IDataManager _data;
    private readonly Action<string>? _log;

    public ZoneLayoutReader(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<ZoneObject> Read(uint territoryId)
    {
        var found = new List<ZoneObject>();

        try
        {
            if (_data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId) is not { } territory)
                return found;

            var bg = territory.Bg.ExtractText();
            var level = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (level < 0)
                return found;

            var folder = "bg/" + bg[..(level + 1)] + "level/";
            var events = _data.GetExcelSheet<EObj>();

            foreach (var name in Files)
            {
                // Not every zone has both, and one missing must not cost the other.
                LgbFile? file;
                try
                {
                    file = _data.GetFile<LgbFile>(folder + name);
                }
                catch (Exception)
                {
                    file = null;
                }

                if (file is null)
                    continue;

                foreach (var layer in file.Layers)
                {
                    foreach (var instance in layer.InstanceObjects)
                    {
                        if (instance.Object is not LayerCommon.EventInstanceObject placed)
                            continue;

                        var id = placed.ParentData.BaseId;
                        var at = instance.Transform.Translation;
                        found.Add(new ZoneObject(
                            id,
                            events.GetRowOrDefault(id) is { } row ? row.Data.RowId : 0,
                            new Vector3(at.X, at.Y, at.Z),
                            layer.Name ?? string.Empty));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Zone files for territory {territoryId} unreadable ({ex.GetType().Name}: {ex.Message}).");
            found.Clear();
        }

        return found;
    }
}
