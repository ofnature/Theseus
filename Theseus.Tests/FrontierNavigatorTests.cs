using System.Numerics;
using Theseus.Services.Frontier;

namespace Theseus.Tests;

public class FrontierNavigatorTests
{
    private static readonly string OverrideFile =
        Path.Combine(Path.GetTempPath(), "theseus-tests", Guid.NewGuid().ToString("N"), "overrides.json");

    private static MapMarkerCatalog Markers(uint territory, params MapLandmark[] landmarks)
        => new(new Dictionary<uint, IReadOnlyList<MapLandmark>> { [territory] = landmarks });

    private static FrontierNavigator Started(FakeStepWorld world, MapMarkerCatalog? markers = null,
        ZoneOverrideStore? overrides = null)
    {
        var navigator = new FrontierNavigator(
            world,
            markers ?? new MapMarkerCatalog(new Dictionary<uint, IReadOnlyList<MapLandmark>>()),
            overrides ?? new ZoneOverrideStore(OverrideFile));
        navigator.Start();
        return navigator;
    }

    private static WorldObject Hostile(ulong id, Vector3 at, string name = "mob")
        => new(id, 100, name, at, WorldObjectKind.Hostile, true);

    private static WorldObject Lever(ulong id, Vector3 at, string name = "lever")
        => new(id, 200, name, at, WorldObjectKind.Interactable, true);

    // ── Objects first ──

    [Fact]
    public void Walks_to_a_distant_hostile_before_engaging_it()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Hostile(1, new Vector3(20, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();

        Assert.Contains(new Vector3(20, 0, 0), world.MoveRequests);
        Assert.Empty(world.Attacked);
    }

    [Fact]
    public void Engages_a_hostile_it_is_standing_next_to()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Hostile(1, new Vector3(2, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();

        Assert.Equal([1ul], world.Attacked);
    }

    [Fact]
    public void Fights_before_it_explores()
    {
        // An object present is a fact; a marker is a guess about direction. Walking off to a
        // landmark with a pack still up is how a run collects the whole wing behind it.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Hostile(1, new Vector3(3, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();

        Assert.NotEmpty(world.Attacked);
        Assert.Empty(world.MoveRequests);
    }

    [Fact]
    public void Interacts_with_the_nearest_object_when_nothing_is_hostile()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Lever(7, new Vector3(30, 0, 0), "far lever"));
        world.Nearby.Add(Lever(8, new Vector3(2, 0, 0), "near lever"));

        var navigator = Started(world);
        navigator.Tick();

        Assert.Equal([8ul], world.InteractedObjects);
    }

    // ── Ghosts ──

    [Fact]
    public void An_interacted_object_is_not_offered_again()
    {
        // A pulled lever looks exactly like an unpulled one, so without the ghost the navigator
        // stands there pulling it forever.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Lever(8, new Vector3(2, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();
        navigator.Tick();

        Assert.Equal([8ul], world.InteractedObjects);
    }

    [Fact]
    public void Ghosts_are_forgotten_when_the_zone_changes()
    {
        // Instance ids are recycled, so a cache carried across a boundary ghosts things that were
        // never touched.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Nearby.Add(Lever(8, new Vector3(2, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();
        Assert.Single(world.InteractedObjects);

        world.Scope = (9999, 2, 500);
        navigator.Tick();

        Assert.Equal(2, world.InteractedObjects.Count);
    }

    // ── Frontier ──

    [Fact]
    public void Walks_to_the_nearest_unvisited_landmark_when_the_room_is_empty()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var markers = Markers(world.Scope.Territory,
            new MapLandmark("Far Hall", new Vector3(200, 0, 0), 1),
            new MapLandmark("Near Hall", new Vector3(50, 0, 0), 1));

        var navigator = Started(world, markers);
        navigator.Tick();

        Assert.Contains(world.MoveRequests, m => m.X == 50f);
    }

    [Fact]
    public void A_landmark_that_snaps_to_nothing_is_skipped()
    {
        // No reachable ground under a marker means a door that has not opened — not the frontier
        // yet, and not a failure either.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.SnapPoint = (p, _) => p.X > 100f ? p : null;

        var markers = Markers(world.Scope.Territory,
            new MapLandmark("Sealed Room", new Vector3(50, 0, 0), 1),
            new MapLandmark("Open Hall", new Vector3(200, 0, 0), 1));

        var navigator = Started(world, markers);
        navigator.Tick();

        Assert.Contains(world.MoveRequests, m => m.X == 200f);
        Assert.DoesNotContain(world.MoveRequests, m => m.X == 50f);
    }

    [Fact]
    public void Exploring_ends_when_nothing_is_left()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };

        var navigator = Started(world);
        for (var i = 0; i < 40 && navigator.Status == FrontierStatus.Running; i++)
        {
            navigator.Tick();
            world.Advance(1);
        }

        Assert.Equal(FrontierStatus.Exhausted, navigator.Status);
    }

    // ── Overrides ──

    [Fact]
    public void An_authored_waypoint_beats_the_marker_search()
    {
        // Overrides exist for what perception cannot describe — a slide entrance often sits beside
        // a labelled room the navigator would otherwise happily walk back into.
        var world = new FakeStepWorld { PlayerPosition = new Vector3(10, 0, 0) };
        var file = Path.Combine(Path.GetTempPath(), "theseus-tests", Guid.NewGuid().ToString("N"), "o.json");
        var overrides = new ZoneOverrideStore(file);
        overrides.Add(new ZoneOverride
        {
            TerritoryId = world.Scope.Territory,
            FromX = 10, FromY = 0, FromZ = 0,
            ToX = 500, ToY = 0, ToZ = 0,
            Radius = 8f,
            Note = "the slide",
        });

        var markers = Markers(world.Scope.Territory, new MapLandmark("Hall", new Vector3(20, 0, 0), 1));

        var navigator = Started(world, markers, overrides);
        navigator.Tick();

        Assert.Contains(new Vector3(500, 0, 0), world.MoveRequests);
    }

    [Fact]
    public void Boss_modules_are_left_alone()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, BossModuleActive = true };
        world.Nearby.Add(Hostile(1, new Vector3(2, 0, 0)));

        var navigator = Started(world);
        navigator.Tick();

        Assert.Empty(world.Attacked);
        Assert.Empty(world.MoveRequests);
    }
}
