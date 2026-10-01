using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Theseus.Services.Frontier;

/// <summary>What an event object is for, as the game's own data says.</summary>
public enum EventObjectRole
{
    /// <summary>
    /// Runs no event: an invisible wall, a VFX marker, a trigger nobody clicks. Never something to
    /// use. These were most of the solver's "unknown interactables" — 590 gap-log entries across
    /// four clients, and every disagreement it had with a route was it heading for one.
    /// </summary>
    Scenery,

    /// <summary>Leaves the instance. Appears once the duty is cleared.</summary>
    Exit,

    /// <summary>
    /// At the entrance, once a boss is down: warps to any boss already beaten. Not a progression
    /// mechanism, and not to be touched while exploring — but a legitimate way back after a wipe,
    /// which is why it has a role of its own rather than being lumped in with the exit.
    /// </summary>
    Shortcut,

    /// <summary>Runs a gimmick: a lever, a key, a gate, a lift, a switch. The solver's business.</summary>
    Mechanism,
}

/// <summary>
/// Sorts event objects by the event the game attaches to them, rather than by anything learned.
///
/// <para>
/// Checked offline against the object sheet for The Burn, The Ghimlyt Dark and Xelphatol
/// (2026-09-30): every lever, key, gate, airstone and tailwind relic carries a gimmick event, and
/// every marker, wall and barrier carries none. The exit and the shortcut carry gimmick events too,
/// but they are the same two sheet rows in every dungeon, so they are recognised by row.
/// </para>
/// </summary>
public static class EventObjectFilter
{
    /// <summary>The "exit" object, present in every dungeon.</summary>
    public const uint ExitObject = 2000139;

    /// <summary>The "shortcut" object, present in every dungeon.</summary>
    public const uint ShortcutObject = 2000700;

    /// <summary>A second shortcut object AutoDuty's revive handler also looks for.</summary>
    public const uint SecondShortcutObject = 2000789;

    /// <param name="dataId">The object's sheet row.</param>
    /// <param name="eventData">
    /// The event the sheet attaches to it, 0 for none, or null when the sheet could not be read —
    /// which keeps the object: an unreadable sheet must degrade to the old behaviour, not blind the
    /// solver.
    /// </param>
    public static EventObjectRole Classify(uint dataId, uint? eventData) => dataId switch
    {
        ExitObject => EventObjectRole.Exit,
        ShortcutObject or SecondShortcutObject => EventObjectRole.Shortcut,
        _ when eventData == 0 => EventObjectRole.Scenery,
        _ => EventObjectRole.Mechanism,
    };
}

/// <summary>Reads the event attached to each event object from the game's object sheet, once per row.</summary>
public sealed class EventObjectCatalog
{
    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, uint?> _events = [];
    private bool _warned;

    public EventObjectCatalog(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public EventObjectRole RoleOf(uint dataId) => EventObjectFilter.Classify(dataId, EventOf(dataId));

    private uint? EventOf(uint dataId)
    {
        if (_events.TryGetValue(dataId, out var cached))
            return cached;

        uint? found;
        try
        {
            found = _data.GetExcelSheet<EObj>().GetRowOrDefault(dataId) is { } row ? row.Data.RowId : null;
        }
        catch (Exception ex)
        {
            found = null;
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Event object sheet unreadable ({ex.GetType().Name}) — every event object counts as usable.");
            }
        }

        _events[dataId] = found;
        return found;
    }
}
