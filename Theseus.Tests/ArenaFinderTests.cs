using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Generation;

namespace Theseus.Tests;

public class ArenaFinderTests
{
    private const string Scenery = "planmap.lgb/LVD_Zone_01";

    private static ZoneObject Wall(float x, float y, float z, string layer = Scenery)
        => new(ArenaFinder.ArenaWall, 0, new Vector3(x, y, z), layer);

    private static ZoneObject Glow(float x, float y, float z, string layer = Scenery)
        => new(ArenaFinder.EntranceGlow, 0, new Vector3(x, y, z), layer);

    private static ZoneObject Exit(float x, float y, float z, uint row = EventObjectFilter.ExitObject)
        => new(row, ArenaFinder.ExitEvent, new Vector3(x, y, z), "planmap.lgb/LVD_entrance_exit");

    private static ZoneObject Other(uint row, float x, float y, float z, string layer, uint eventData = 0)
        => new(row, eventData, new Vector3(x, y, z), layer);

    [Fact]
    public void A_wall_and_its_glow_are_one_room_with_the_door_between_them()
    {
        // The Aery's first boss, as its zone files place the two marks.
        var rooms = ArenaFinder.Find([Glow(345.2f, 93.9f, -179.0f), Wall(346.8f, 93.9f, -178.6f)]);

        var room = Assert.Single(rooms);
        Assert.False(room.HoldsExit);
        Assert.Equal(346.0f, room.Door.X, 1);
        Assert.Equal(-178.8f, room.Door.Z, 1);
    }

    [Fact]
    public void An_object_filed_in_a_boss_layer_marks_a_room_and_other_scenery_does_not()
    {
        // The Aery's last boss has nothing but two objects in a layer named for it; a lever in an
        // ordinary layer, however interesting, is not a boss room.
        var rooms = ArenaFinder.Find(
        [
            Other(2005542, 36.3f, 148.4f, -243.0f, "planmap.lgb/LVD_boss_03"),
            Other(2006454, 32.7f, 148.4f, -251.8f, "planmap.lgb/LVD_boss_03"),
            Other(2000184, -300f, 0f, 300f, "planmap.lgb/LVD_gimmick", eventData: 983081),
        ]);

        var room = Assert.Single(rooms);
        Assert.Equal(34.5f, room.Door.X, 1);
    }

    [Fact]
    public void The_door_is_the_wall_line_not_the_middle_of_everything_marked()
    {
        // A boss-layer object deep in the room would drag a plain average inside, past the wall.
        var rooms = ArenaFinder.Find(
        [
            Wall(100f, 0f, 0f),
            Glow(100f, 0f, 2f),
            Other(2005233, 130f, 0f, 0f, "planmap.lgb/LVD_bosswall"),
        ]);

        var room = Assert.Single(rooms);
        Assert.Equal(new Vector3(100f, 0f, 1f), room.Door);
    }

    [Fact]
    public void The_exit_beside_a_room_makes_it_the_last_room()
    {
        var rooms = ArenaFinder.Find([Wall(0f, 0f, 0f), Wall(300f, 0f, 0f), Exit(330f, 0f, 0f)]);

        Assert.Equal(2, rooms.Count);
        Assert.False(rooms.Single(r => r.Door.X < 100f).HoldsExit);

        var last = rooms.Single(r => r.Door.X > 100f);
        Assert.True(last.HoldsExit);
        Assert.Equal(300f, last.Door.X); // still the wall, not dragged toward the exit
    }

    [Fact]
    public void An_exit_with_no_other_mark_is_a_room_of_its_own()
    {
        // Several dungeons mark their last boss room with nothing else.
        var rooms = ArenaFinder.Find([Wall(0f, 0f, 0f), Exit(400f, 20f, 0f)]);

        var last = Assert.Single(rooms, r => r.HoldsExit);
        Assert.Equal(new Vector3(400f, 20f, 0f), last.Door);
    }

    [Fact]
    public void The_exit_is_known_by_its_event_not_its_row()
    {
        // Old dungeons use other rows for the exit; the event it runs is the same everywhere.
        var rooms = ArenaFinder.Find([Exit(0f, 0f, 0f, row: 2000370)]);

        Assert.True(Assert.Single(rooms).HoldsExit);
    }

    [Fact]
    public void A_mark_between_two_groups_joins_them_into_one_room()
    {
        // The ends are ninety yalms apart; the mark in the middle is within a link of both.
        var rooms = ArenaFinder.Find(
        [
            Wall(0f, 0f, 0f),
            Wall(90f, 0f, 0f),
            Other(2000186, 45f, 0f, 0f, "planmap.lgb/LVD_BNPC_boss_01"),
        ]);

        Assert.Single(rooms);
    }

    [Fact]
    public void An_exit_belongs_to_the_room_it_is_at_the_far_end_of()
    {
        // The Burn's last room: the wall is on a ledge 62 yalms and a nine-yalm drop from the exit.
        var rooms = ArenaFinder.Find(
        [
            Wall(-299.3f, 19.2f, -351.7f),
            Exit(-300.3f, 10.0f, -414.2f),
            Wall(174.1f, 13.0f, 197.9f),
        ]);

        Assert.Equal(2, rooms.Count);
        var last = Assert.Single(rooms, r => r.HoldsExit);
        Assert.Equal(new Vector3(-299.3f, 19.2f, -351.7f), last.Door);
        Assert.Equal(new Vector3(-300.3f, 10.0f, -414.2f), last.Exit);
    }

    [Fact]
    public void An_exit_in_a_boss_layer_is_still_only_an_exit()
    {
        // Copperbell Mines files its exit in a boss layer. As a mark it would move the door.
        var rooms = ArenaFinder.Find(
        [
            Wall(-102f, -58f, 36f),
            new ZoneObject(EventObjectFilter.ExitObject, ArenaFinder.ExitEvent, new Vector3(-92f, -57f, -7f),
                "planmap.lgb/LVD_REMAKE_BOSS_03"),
        ]);

        var room = Assert.Single(rooms);
        Assert.Equal(new Vector3(-102f, -58f, 36f), room.Door);
        Assert.True(room.HoldsExit);
    }

    [Fact]
    public void Rooms_a_dungeon_apart_stay_apart()
    {
        var rooms = ArenaFinder.Find([Wall(0f, 0f, 0f), Wall(0f, 0f, ArenaFinder.LinkDistance + 1f)]);

        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public void The_entrance_is_the_shortcut_and_failing_that_the_barrier()
    {
        var shortcut = Other(EventObjectFilter.ShortcutObject, 144.4f, 68.0f, 177.1f, Scenery, eventData: 983114);
        var barrier = Other(ArenaFinder.EntranceBarrier, 150f, 68f, 170f, Scenery);

        Assert.Equal(shortcut.Position, ArenaFinder.Entrance([barrier, shortcut]));
        Assert.Equal(barrier.Position, ArenaFinder.Entrance([barrier]));
        Assert.Null(ArenaFinder.Entrance([Wall(0f, 0f, 0f)]));
    }
}
