using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Theseus.Services.Paths;

namespace Theseus.Services.Generation;

/// <summary>Asks the navmesh for a walk between two points. Ariadne's detailed pathfind, in practice.</summary>
public delegate Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)> PathQuery(
    Vector3 from, Vector3 to);

public enum PlanStatus
{
    Planning,
    Done,
    Failed,
}

/// <summary>
/// Plans a dungeon's route from its zone files, at zone-in, for this run only.
///
/// <para>
/// The route is the boss rooms in order: walk to the door, fight what is inside. Everything
/// between two rooms is the navmesh's business and the step executor's, which already holds for
/// trash on the way. The order is by walking distance from the entrance, with the room that holds
/// the exit last. Measured by straight-line distance, that rule put the bosses in the imported
/// routes' order in 81 of 87 dungeons (2026-11-21); walking distance is untested, because only a
/// handful of dungeons had a mesh to measure with.
/// </para>
///
/// <para>
/// Ticked on the framework thread and never blocking it: one path question is outstanding at a
/// time, and the plan resumes when it is answered. A question that goes unanswered counts as "no
/// route known" and the plan carries on with straight-line distance — a poorer order, not a fault.
/// </para>
/// </summary>
public sealed class RoutePlanner
{
    /// <summary>How long to wait for the navmesh before planning without it.</summary>
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(20);

    /// <summary>How long one path question may take.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The character is at the entrance when it stands this close to the entrance's marker.</summary>
    public const float AtEntrance = 40f;

    /// <summary>A door the mesh puts this far from where the zone files have it is still that door.</summary>
    public const float SnapReach = 15f;

    /// <summary>
    /// How far past the door the boss step stands. The imported routes' boss steps sit a median 20
    /// yalms from the door, and the pull reaches 30 from wherever this lands.
    /// </summary>
    public const float InsideDistance = 18f;

    /// <summary>
    /// In the last room the boss step stands half-way from the door to the exit, and never further
    /// from the exit than this: the exit is on the boss's floor, and across 85 dungeons the
    /// imported routes' last boss step sits a median 5 yalms from that half-way point.
    /// </summary>
    public const float ExitStandoff = 25f;

    /// <summary>The stretch of the approach the heading into the room is read from.</summary>
    private const float HeadingRun = 4f;

    /// <summary>What a yalm with no known route is charged, against a yalm of real path.</summary>
    private const float NoRoutePenalty = 3f;

    /// <summary>What the unwalked remainder of a partial route is charged.</summary>
    private const float PartialPenalty = 1.5f;

    private readonly uint _territoryId;
    private readonly IReadOnlyList<ZoneObject> _objects;
    private readonly Vector3 _player;
    private readonly PathQuery _query;
    private readonly Func<bool> _meshReady;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly DateTime _createdUtc;

    private IEnumerator<Task>? _plan;
    private Task? _waiting;
    private DateTime _askedUtc;

    public RoutePlanner(
        uint territoryId,
        IReadOnlyList<ZoneObject> objects,
        Vector3 playerPosition,
        PathQuery query,
        Func<bool> meshReady,
        Func<DateTime> now,
        Action<string> log)
    {
        _territoryId = territoryId;
        _objects = objects;
        _player = playerPosition;
        _query = query;
        _meshReady = meshReady;
        _now = now;
        _log = log;
        _createdUtc = now();
    }

    public PlanStatus Status { get; private set; } = PlanStatus.Planning;

    /// <summary>The planned route once <see cref="Status"/> is <see cref="PlanStatus.Done"/>.</summary>
    public ThreadPath? Route { get; private set; }

    public string Detail { get; private set; } = "Planning a route from the zone files.";

    public void Tick()
    {
        if (Status != PlanStatus.Planning)
            return;

        if (_plan is null)
        {
            if (!_meshReady() && _now() - _createdUtc < ReadyWait)
            {
                Detail = "Planning a route — waiting for the navmesh.";
                return;
            }

            Detail = "Planning a route from the zone files.";
            _plan = Plan().GetEnumerator();
        }

        if (_waiting is { IsCompleted: false } && _now() - _askedUtc < QueryTimeout)
            return;

        try
        {
            // Runs on until a question is actually outstanding, so answers that are already in
            // do not each cost a frame.
            while (_plan.MoveNext())
            {
                _waiting = _plan.Current;
                _askedUtc = _now();
                if (!_waiting.IsCompleted)
                    return;
            }
        }
        catch (Exception ex)
        {
            Fail($"planning threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (Status == PlanStatus.Planning)
            Fail("the plan ended without a route");
    }

    private void Fail(string why)
    {
        Status = PlanStatus.Failed;
        Detail = why;
    }

    private sealed record Leg(Vector3 Door, Vector3? Exit, float Cost, Vector3? Heading, string How)
    {
        public bool HoldsExit => Exit is not null;
    }

    private IEnumerable<Task> Plan()
    {
        var arenas = ArenaFinder.Find(_objects);
        if (arenas.Count == 0)
        {
            Fail(_objects.Count == 0
                ? "the zone files could not be read"
                : "the zone files mark no boss room here");
            yield break;
        }

        // Distances are measured from the front of the dungeon, wherever the character is, so a
        // run picked up half-way through still gets the rooms in the dungeon's order. Standing at
        // the front, the character's own position is used: it is certainly on the mesh, and the
        // marker need not be.
        var entrance = ArenaFinder.Entrance(_objects);
        var origin = entrance is { } front && Vector3.Distance(front, _player) > AtEntrance ? front : _player;

        var legs = new List<Leg>();
        foreach (var arena in arenas)
        {
            var door = arena.Door;
            var ask = _query(origin, door);
            yield return ask;
            var answer = Read(ask);

            if (answer.Result == "targetOffMesh" && answer.Nearest is { } near
                && Vector3.Distance(near, door) <= SnapReach)
            {
                door = near;
                ask = _query(origin, door);
                yield return ask;
                answer = Read(ask);
            }

            var walked = Length(origin, answer.Waypoints);
            if (answer.Result == "ok" && !answer.Partial && answer.Waypoints.Count > 0)
            {
                legs.Add(new Leg(door, arena.Exit, walked, HeadingInto(origin, answer.Waypoints),
                    $"{walked:0}y by mesh"));
            }
            else if (answer.Partial && answer.Waypoints.Count > 0)
            {
                var reached = answer.Waypoints[^1];
                var rest = Vector3.Distance(reached, door);
                legs.Add(new Leg(door, arena.Exit, walked + PartialPenalty * rest, Flat(door - reached),
                    $"{walked:0}y by mesh, then {rest:0}y with no route"));
            }
            else
            {
                legs.Add(new Leg(door, arena.Exit, NoRoutePenalty * Vector3.Distance(origin, door), null,
                    $"straight line ({answer.Result})"));
            }
        }

        var ordered = legs.OrderBy(leg => leg.HoldsExit).ThenBy(leg => leg.Cost).ToList();

        var steps = new List<ThreadStep>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var leg = ordered[i];
            var stand = leg.Door;

            // The door is where the wall comes down, not where the boss is. The last room says
            // where its floor is: the exit stands on it, so the pull goes between door and exit, at
            // the exit's height. No check that the walk there is short — in The Ghimlyt Dark and
            // The Burn it is a drop off the door's ledge, and the mover knows how to take one.
            // A room marked by its exit alone has the exit for a door, and the pull is right there.
            if (leg.Exit is { } exit)
            {
                if (Flat(leg.Door - exit) is { } back)
                {
                    var gap = new Vector2(leg.Door.X - exit.X, leg.Door.Z - exit.Z).Length();
                    stand = exit + back * MathF.Min(gap / 2f, ExitStandoff);

                    var ask = _query(leg.Door, stand);
                    yield return ask;
                    var answer = Read(ask);

                    if (answer.Result == "targetOffMesh" && answer.Nearest is { } near
                        && Vector3.Distance(near, stand) <= SnapReach)
                    {
                        stand = near;
                    }
                }
            }

            // Any other room: carrying on along the approach puts the pull in the middle of it —
            // but only if the mesh agrees there is floor there, close by. A heading read off a
            // corner would aim through a wall.
            else if (leg.Heading is { } heading)
            {
                var inside = leg.Door + heading * InsideDistance;
                var ask = _query(leg.Door, inside);
                yield return ask;
                var answer = Read(ask);

                if (answer.Result == "ok" && !answer.Partial && answer.Waypoints.Count > 0
                    && Length(leg.Door, answer.Waypoints) <= InsideDistance * 2f)
                {
                    stand = answer.Waypoints[^1];
                }
                else if (answer.Result == "targetOffMesh" && answer.Nearest is { } near
                         && Vector3.Distance(near, inside) <= SnapReach / 2f)
                {
                    stand = near;
                }
            }

            var room = leg.HoldsExit ? "the last room" : $"room {i + 1}";
            steps.Add(new ThreadStep
            {
                Verb = StepVerb.MoveTo,
                RawVerb = "MoveTo",
                Position = Point(leg.Door),
                Note = $"To {room}.",
            });
            steps.Add(new ThreadStep
            {
                Verb = StepVerb.Boss,
                RawVerb = "Boss",
                Position = Point(stand),
                Note = $"Boss of {room}.",
            });

            _log($"Plan, {room}: door at {Point(leg.Door)}, {leg.How}; " +
                 (stand == leg.Door
                     ? "boss step at the door."
                     : $"boss step {Vector3.Distance(stand, leg.Door):0}y inside."));
        }

        Route = new ThreadPath
        {
            TerritoryId = _territoryId,
            Name = "Planned from the zone files",
            Source = "generated",
            Steps = steps,
            IsGenerated = true,
        };
        Status = PlanStatus.Done;
        Detail = $"Planned {ordered.Count} boss rooms from the zone files.";
    }

    /// <summary>An unanswered, failed or timed-out question reads as "no route known".</summary>
    private static (string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial) Read(
        Task<(string Result, List<Vector3> Waypoints, Vector3? Nearest, bool Partial)> ask)
    {
        if (!ask.IsCompletedSuccessfully)
            return ("unanswered", [], null, false);

        var answer = ask.Result;
        return (answer.Result ?? "unanswered", answer.Waypoints ?? [], answer.Nearest, answer.Partial);
    }

    private static float Length(Vector3 from, List<Vector3> waypoints)
    {
        var total = 0f;
        var previous = from;
        foreach (var point in waypoints)
        {
            total += Vector3.Distance(previous, point);
            previous = point;
        }

        return total;
    }

    /// <summary>The level heading of the last few yalms of a walk, or null when it has none to read.</summary>
    private static Vector3? HeadingInto(Vector3 from, List<Vector3> waypoints)
    {
        var end = waypoints[^1];
        for (var i = waypoints.Count - 2; i >= -1; i--)
        {
            var back = i >= 0 ? waypoints[i] : from;
            var run = new Vector3(end.X - back.X, 0f, end.Z - back.Z);
            if (run.Length() >= HeadingRun)
                return Vector3.Normalize(run);
        }

        return null;
    }

    private static Vector3? Flat(Vector3 direction)
    {
        var level = new Vector3(direction.X, 0f, direction.Z);
        return level.Length() < 1f ? null : Vector3.Normalize(level);
    }

    private static PathPoint Point(Vector3 v) => new(v.X, v.Y, v.Z);
}
