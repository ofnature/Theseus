using System.Numerics;
using System.Text.Json;
using Theseus.Services.Frontier;
using Theseus.Services.Generation;
using Theseus.Services.Paths;

namespace Theseus.Tests;

public class RoutePlannerTests
{
    private static readonly DateTime Start = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private static ZoneObject Wall(float x, float z)
        => new(ArenaFinder.ArenaWall, 0, new Vector3(x, 0f, z), "planmap.lgb/LVD_bosswall");

    private static ZoneObject Exit(float x, float z)
        => new(EventObjectFilter.ExitObject, ArenaFinder.ExitEvent, new Vector3(x, 0f, z), "planmap.lgb/LVD_exit");

    private static ZoneObject Shortcut(float x, float z)
        => new(EventObjectFilter.ShortcutObject, 983114, new Vector3(x, 0f, z), "planmap.lgb/LVD_entrance");

    private static (string, List<Vector3>, Vector3?, bool) Straight(Vector3 from, Vector3 to)
        => ("ok", [from, to], null, false);

    private static (string, List<Vector3>, Vector3?, bool) Nothing => ("serviceUnavailable", [], null, false);

    /// <summary>A planner on a fake navmesh and a fake clock.</summary>
    private sealed class Fixture
    {
        public readonly List<(Vector3 From, Vector3 To)> Asked = [];
        public readonly List<string> Log = [];
        public Func<Vector3, Vector3, (string, List<Vector3>, Vector3?, bool)> Answer = (_, _) => Nothing;
        public bool NeverAnswers;
        public bool MeshReady = true;
        public DateTime Now = Start;
        public Vector3 Player = Vector3.Zero;

        public RoutePlanner Planner(params ZoneObject[] objects)
            => new(1065, objects, Player, Query, () => MeshReady, () => Now, Log.Add);

        public RoutePlanner Plan(params ZoneObject[] objects)
        {
            var planner = Planner(objects);
            for (var i = 0; i < 100 && planner.Status == PlanStatus.Planning; i++)
                planner.Tick();

            return planner;
        }

        private Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)> Query(
            Vector3 from, Vector3 to)
        {
            Asked.Add((from, to));
            return NeverAnswers
                ? new TaskCompletionSource<(string, List<Vector3>, Vector3?, bool)>().Task
                : Task.FromResult(Answer(from, to));
        }
    }

    private static List<Vector3> Doors(RoutePlanner planner)
        => planner.Route!.Steps.Where(s => s.Verb == StepVerb.MoveTo).Select(s => s.Position.ToVector3()).ToList();

    private static List<Vector3> BossSteps(RoutePlanner planner)
        => planner.Route!.Steps.Where(s => s.Verb == StepVerb.Boss).Select(s => s.Position.ToVector3()).ToList();

    [Fact]
    public void Each_room_is_a_walk_to_its_door_and_then_its_boss()
    {
        var f = new Fixture { Answer = Straight };

        var planner = f.Plan(Wall(100f, 0f), Wall(300f, 0f));

        Assert.Equal(PlanStatus.Done, planner.Status);
        Assert.Equal(
            [StepVerb.MoveTo, StepVerb.Boss, StepVerb.MoveTo, StepVerb.Boss],
            planner.Route!.Steps.Select(s => s.Verb));
        Assert.Equal([new Vector3(100f, 0f, 0f), new Vector3(300f, 0f, 0f)], Doors(planner));
        Assert.True(planner.Route.IsRunnable);
        Assert.True(planner.Route.IsGenerated);
        Assert.Equal(1065u, planner.Route.TerritoryId);
    }

    [Fact]
    public void Rooms_come_in_walking_order_not_straight_line_order()
    {
        // The second room is sixty yalms away through a wall and four hundred and sixty on foot.
        var near = new Vector3(0f, 0f, 60f);
        var f = new Fixture
        {
            Answer = (from, to) => to == near
                ? ("ok", [from, new Vector3(200f, 0f, 0f), new Vector3(200f, 0f, 60f), near], null, false)
                : Straight(from, to),
        };

        var planner = f.Plan(Wall(0f, 60f), Wall(100f, 0f));

        Assert.Equal([new Vector3(100f, 0f, 0f), near], Doors(planner));
    }

    [Fact]
    public void The_room_with_the_exit_is_last_however_near_it_is()
    {
        var f = new Fixture { Answer = Straight };

        var planner = f.Plan(Wall(30f, 0f), Exit(40f, 0f), Wall(200f, 0f), Wall(400f, 0f));

        Assert.Equal(
            [new Vector3(200f, 0f, 0f), new Vector3(400f, 0f, 0f), new Vector3(30f, 0f, 0f)],
            Doors(planner));
    }

    [Fact]
    public void A_route_that_stops_short_is_charged_for_the_ground_it_does_not_cover()
    {
        // 50 walked and 50 unknown costs more than 110 walked: the unknown half may be a long way round.
        var gapped = new Vector3(100f, 0f, 0f);
        var f = new Fixture
        {
            Answer = (from, to) => to == gapped
                ? ("noRouteOnMesh", [from, new Vector3(50f, 0f, 0f)], new Vector3(50f, 0f, 0f), true)
                : Straight(from, to),
        };

        var planner = f.Plan(Wall(100f, 0f), Wall(0f, 110f));

        Assert.Equal([new Vector3(0f, 0f, 110f), gapped], Doors(planner));
    }

    [Fact]
    public void The_boss_step_stands_inside_the_room_along_the_approach()
    {
        var f = new Fixture { Answer = Straight };

        var planner = f.Plan(Wall(100f, 0f));

        Assert.Equal([new Vector3(100f + RoutePlanner.InsideDistance, 0f, 0f)], BossSteps(planner));
    }

    [Fact]
    public void In_the_last_room_the_boss_step_stands_between_the_door_and_the_exit_on_the_exits_floor()
    {
        // The Ghimlyt Dark: the wall is on a ledge 29 yalms above the floor the boss and the exit
        // are on. The imported route's boss step is at (371, -15, -264).
        var f = new Fixture { Answer = Straight };
        var door = new ZoneObject(ArenaFinder.ArenaWall, 0, new Vector3(370.2f, 13.85f, -238f), "planmap.lgb/LVD_bosswall");
        var exit = new ZoneObject(EventObjectFilter.ExitObject, ArenaFinder.ExitEvent, new Vector3(371f, -15f, -278.6f), "planmap.lgb/LVD_exit");

        var planner = f.Plan(door, exit);

        var stand = Assert.Single(BossSteps(planner));
        Assert.Equal(-15f, stand.Y);
        Assert.InRange(Vector3.Distance(stand, new Vector3(371f, -15f, -264f)), 0f, 8f);
        Assert.Equal([door.Position], Doors(planner));
    }

    [Fact]
    public void A_far_exit_keeps_the_boss_step_within_reach_of_it()
    {
        // Eighty yalms from wall to exit: half-way would leave the far end of the room out of reach.
        var f = new Fixture { Answer = Straight };

        var planner = f.Plan(Wall(100f, 0f), Exit(180f, 0f));

        Assert.Equal([new Vector3(180f - RoutePlanner.ExitStandoff, 0f, 0f)], BossSteps(planner));
    }

    [Fact]
    public void A_last_room_marked_only_by_its_exit_is_walked_to_and_fought_at_the_exit()
    {
        var f = new Fixture { Answer = Straight };

        var planner = f.Plan(Wall(100f, 0f), Exit(400f, 0f));

        Assert.Equal(new Vector3(400f, 0f, 0f), Doors(planner)[^1]);
        Assert.Equal(new Vector3(400f, 0f, 0f), BossSteps(planner)[^1]);
    }

    [Fact]
    public void A_heading_that_leads_into_a_wall_keeps_the_boss_step_at_the_door()
    {
        // The point past the door is on the mesh, but only by going a long way round: not this room.
        var door = new Vector3(100f, 0f, 0f);
        var f = new Fixture
        {
            Answer = (from, to) => from == door
                ? ("ok", [from, new Vector3(100f, 0f, 80f), to], null, false)
                : Straight(from, to),
        };

        var planner = f.Plan(Wall(100f, 0f));

        Assert.Equal([door], BossSteps(planner));
    }

    [Fact]
    public void A_door_off_the_mesh_moves_to_the_nearest_floor()
    {
        var marked = new Vector3(100f, 0f, 0f);
        var floor = new Vector3(100f, -2f, 3f);
        var f = new Fixture
        {
            Answer = (from, to) => to == marked ? ("targetOffMesh", [], floor, false) : Straight(from, to),
        };

        var planner = f.Plan(Wall(100f, 0f));

        Assert.Equal([floor], Doors(planner));
    }

    [Fact]
    public void Without_a_navmesh_the_plan_is_by_straight_line_with_the_boss_step_at_the_door()
    {
        var f = new Fixture(); // every question comes back "unavailable"

        var planner = f.Plan(Wall(300f, 0f), Wall(100f, 0f));

        Assert.Equal(PlanStatus.Done, planner.Status);
        Assert.Equal([new Vector3(100f, 0f, 0f), new Vector3(300f, 0f, 0f)], Doors(planner));
        Assert.Equal(Doors(planner), BossSteps(planner));
    }

    [Fact]
    public void A_run_picked_up_half_way_still_measures_from_the_entrance()
    {
        // Standing past the second room: measured from here, the second room would come first.
        var f = new Fixture { Answer = Straight, Player = new Vector3(500f, 0f, 0f) };

        var planner = f.Plan(Shortcut(0f, 0f), Wall(100f, 0f), Wall(400f, 0f));

        Assert.Equal([new Vector3(100f, 0f, 0f), new Vector3(400f, 0f, 0f)], Doors(planner));
        Assert.Equal(Vector3.Zero, f.Asked[0].From);
    }

    [Fact]
    public void At_the_entrance_distances_are_measured_from_the_character()
    {
        // The shortcut's marker need not be on the mesh; the character certainly is.
        var f = new Fixture { Answer = Straight, Player = new Vector3(5f, 0f, 5f) };

        f.Plan(Shortcut(0f, 0f), Wall(100f, 0f));

        Assert.Equal(new Vector3(5f, 0f, 5f), f.Asked[0].From);
    }

    [Fact]
    public void Planning_waits_for_the_navmesh_and_then_goes_without_it()
    {
        var f = new Fixture { MeshReady = false };
        var planner = f.Planner(Wall(100f, 0f));

        planner.Tick();
        Assert.Equal(PlanStatus.Planning, planner.Status);
        Assert.Empty(f.Asked);

        f.Now += RoutePlanner.ReadyWait + TimeSpan.FromSeconds(1);
        planner.Tick();

        Assert.Equal(PlanStatus.Done, planner.Status);
    }

    [Fact]
    public void A_question_never_answered_times_out_and_the_plan_carries_on()
    {
        var f = new Fixture { NeverAnswers = true };
        var planner = f.Planner(Wall(100f, 0f));

        planner.Tick();
        planner.Tick();
        Assert.Equal(PlanStatus.Planning, planner.Status);
        Assert.Single(f.Asked); // waiting, not asking again every frame

        f.Now += RoutePlanner.QueryTimeout + TimeSpan.FromSeconds(1);
        planner.Tick();

        Assert.Equal(PlanStatus.Done, planner.Status);
        Assert.Equal([new Vector3(100f, 0f, 0f)], BossSteps(planner));
    }

    [Fact]
    public void A_zone_with_no_boss_room_marked_fails_and_says_so()
    {
        var f = new Fixture { Answer = Straight };

        var unmarked = f.Plan(Shortcut(0f, 0f));
        var unread = f.Plan();

        Assert.Equal(PlanStatus.Failed, unmarked.Status);
        Assert.Null(unmarked.Route);
        Assert.Contains("no boss room", unmarked.Detail);
        Assert.Equal(PlanStatus.Failed, unread.Status);
        Assert.Contains("could not be read", unread.Detail);
    }

    [Fact]
    public void A_planned_route_is_never_written_to_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "theseus-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var planned = new Fixture { Answer = Straight }.Plan(Wall(100f, 0f)).Route!;
            var store = new PathStore(root);

            store.Save(planned);

            Assert.False(Directory.Exists(root) && Directory.EnumerateFiles(root).Any());
            Assert.Null(store.Resolve(planned.TerritoryId, null, wallToWall: false, isTank: false));
            Assert.DoesNotContain("IsGenerated", JsonSerializer.Serialize(planned));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
