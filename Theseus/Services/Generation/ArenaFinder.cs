using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Frontier;

namespace Theseus.Services.Generation;

/// <summary>One event object, where the zone files place it.</summary>
/// <param name="DataId">The object's sheet row.</param>
/// <param name="EventData">The event the sheet attaches to it, 0 for none.</param>
/// <param name="Layer">The name of the layer it sits in, which is the level designer's own label.</param>
public readonly record struct ZoneObject(uint DataId, uint EventData, Vector3 Position, string Layer);

/// <summary>A boss room, as the zone files mark it.</summary>
/// <param name="Door">Where to walk to: the arena's wall line when it has one.</param>
/// <param name="Exit">Where the duty's exit appears, when it appears in this room.</param>
public sealed record Arena(Vector3 Door, Vector3? Exit)
{
    /// <summary>The exit appears here, so this is the last room.</summary>
    public bool HoldsExit => Exit is not null;
}

/// <summary>
/// Finds a dungeon's boss rooms in its zone files.
///
/// <para>
/// Three things mark one: the wall line that seals the arena when the fight starts, the glow at
/// its entrance, and any object the level designer filed in a layer named for a boss. The exit
/// tells which room is the last: it appears on that room's floor, and in some dungeons it is the
/// only mark the room has.
/// </para>
///
/// <para>
/// Scored against the 100 dungeons with an imported route to compare with (2026-11-21): every boss
/// step lies within 45 yalms of a room found this way in 87 of them. It over-finds: 19 of the
/// rooms it reported across those 87 have no boss in them. A room with nothing to fight costs the
/// run one pull grace, not a fault.
/// </para>
/// </summary>
public static class ArenaFinder
{
    /// <summary>The glow across a boss room's entrance.</summary>
    public const uint EntranceGlow = 2002735;

    /// <summary>The line that becomes the arena wall once the boss is pulled.</summary>
    public const uint ArenaWall = 2002872;

    /// <summary>The barrier across the dungeon's entrance until the duty commences.</summary>
    public const uint EntranceBarrier = 2000182;

    /// <summary>The event every exit object runs, whichever sheet row the dungeon uses for it.</summary>
    public const uint ExitEvent = 983045;

    /// <summary>
    /// Marks this close together belong to one room. Rooms in the same dungeon are a hundred yalms
    /// apart or more; the marks of one room are spread across its doors and its floor.
    /// </summary>
    public const float LinkDistance = 50f;

    /// <summary>
    /// An exit this close to a room's marks is that room's. Further than a link, because the exit
    /// stands at the far end of the floor: the measured gap from wall to exit runs up to 85 yalms.
    /// </summary>
    public const float ExitReach = 90f;

    public static IReadOnlyList<Arena> Find(IReadOnlyList<ZoneObject> objects)
    {
        var rooms = new List<List<ZoneObject>>();

        foreach (var mark in objects)
        {
            if (!IsBossMark(mark))
                continue;

            // Joined to every room it is near, which merges two rooms a mark sits between.
            var room = new List<ZoneObject> { mark };
            for (var i = rooms.Count - 1; i >= 0; i--)
            {
                if (!rooms[i].Any(other => Vector3.Distance(other.Position, mark.Position) <= LinkDistance))
                    continue;

                room.AddRange(rooms[i]);
                rooms.RemoveAt(i);
            }

            rooms.Add(room);
        }

        var exits = new Vector3?[rooms.Count];
        var alone = new List<Arena>();

        foreach (var exit in objects)
        {
            if (!IsExit(exit))
                continue;

            var nearest = -1;
            var nearestDistance = ExitReach;
            for (var i = 0; i < rooms.Count; i++)
            {
                var distance = rooms[i].Min(mark => Vector3.Distance(mark.Position, exit.Position));
                if (distance <= nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
            }

            if (nearest < 0)
                alone.Add(new Arena(exit.Position, exit.Position)); // a last room marked by nothing else
            else
                exits[nearest] ??= exit.Position;
        }

        return rooms.Select((room, i) => new Arena(DoorOf(room), exits[i])).Concat(alone).ToList();
    }

    /// <summary>
    /// Where the dungeon starts: the shortcut, which the game puts at the front, or the barrier
    /// across the entrance. Null when the zone has neither.
    /// </summary>
    public static Vector3? Entrance(IReadOnlyList<ZoneObject> objects)
    {
        foreach (var id in new[] { EventObjectFilter.ShortcutObject, EntranceBarrier })
        {
            foreach (var o in objects)
            {
                if (o.DataId == id)
                    return o.Position;
            }
        }

        return null;
    }

    private static bool IsWall(ZoneObject o) => o.DataId is ArenaWall or EntranceGlow;

    private static bool IsBossMark(ZoneObject o)
        => !IsExit(o) && (IsWall(o) || o.Layer.Contains("boss", StringComparison.OrdinalIgnoreCase));

    private static bool IsExit(ZoneObject o) => o.EventData == ExitEvent;

    /// <summary>The wall line when the room has one, and the middle of its other marks when not.</summary>
    private static Vector3 DoorOf(List<ZoneObject> room)
    {
        var marks = room.Where(IsWall).ToList();
        if (marks.Count == 0)
            marks = room;

        var sum = Vector3.Zero;
        foreach (var o in marks)
            sum += o.Position;

        return sum / marks.Count;
    }
}
