using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Services.Frontier;

public enum FrontierStatus
{
    Idle,

    /// <summary>Working: fighting, interacting, or walking somewhere.</summary>
    Running,

    /// <summary>Nothing left to do and nowhere left to go.</summary>
    Exhausted,

    /// <summary>Stopped and waiting. <see cref="FrontierNavigator.Detail"/> says why.</summary>
    Faulted,
}

/// <summary>
/// Runs a dungeon nobody has recorded a route for.
///
/// <para>
/// The recorded-path runner knows exactly what to do and only works where someone has been first.
/// This knows nothing and works anywhere: it looks at what is in front of the character, deals with
/// it, and when there is nothing in front of the character it walks to the nearest place the map
/// has a name for. That is a worse dungeon run than a recorded route by a wide margin — and it is
/// the difference between new content being slow and new content being dead.
/// </para>
///
/// <para>
/// The order matters and is not arbitrary. Objects first, because anything present is either in the
/// way or the point; markers only when the local area is quiet, because a marker is a guess about
/// direction and an object is a fact. Hand-authored overrides sit above both, since they exist
/// precisely for the places where neither perception works.
/// </para>
/// </summary>
public sealed class FrontierNavigator
{
    /// <summary>How far out objects are noticed. Roughly a room.</summary>
    private const float ScanRadius = 40f;

    /// <summary>Close enough to swing at something.</summary>
    private const float EngageRange = 4f;

    /// <summary>Close enough to interact with something.</summary>
    private const float InteractRange = 4f;

    /// <summary>Counts as having arrived at a landmark, which is a broad target by nature.</summary>
    private const float LandmarkArrival = 12f;

    /// <summary>How far from a marker's flat position to look for real ground.</summary>
    private const float SnapExtent = 25f;

    /// <summary>Move requests are throttled the same way the step executor throttles its own.</summary>
    private static readonly TimeSpan MoveRetry = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Standing still with nothing to do for this long ends the run.
    ///
    /// <para>
    /// This mode has no idea how long a dungeon should take, so it cannot use progress as a clock.
    /// What it can tell is that it has run out of both objects and unvisited landmarks — and that,
    /// sustained, means either the dungeon is finished or it is stuck somewhere perception cannot
    /// describe. Either way a person should look.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ExhaustedGrace = TimeSpan.FromSeconds(20);

    private readonly IStepWorld _world;
    private readonly MapMarkerCatalog _markers;
    private readonly ZoneOverrideStore _overrides;
    private readonly GhostCache _ghosts = new();
    private readonly Func<bool> _lootChests;

    private readonly HashSet<string> _visitedLandmarks = [];

    private ulong _engaging;
    private Vector3? _destination;
    private string _destinationName = string.Empty;
    private DateTime _lastMoveRequest = DateTime.MinValue;
    private DateTime _nothingToDoSince = DateTime.MaxValue;

    public FrontierNavigator(
        IStepWorld world,
        MapMarkerCatalog markers,
        ZoneOverrideStore overrides,
        Func<bool>? lootChests = null)
    {
        _world = world;
        _markers = markers;
        _overrides = overrides;
        _lootChests = lootChests ?? (() => true);
    }

    public FrontierStatus Status { get; private set; } = FrontierStatus.Idle;

    /// <summary>One line for the run window.</summary>
    public string Detail { get; private set; } = string.Empty;

    public int GhostCount => _ghosts.Count;

    public int VisitedLandmarks => _visitedLandmarks.Count;

    public void Start()
    {
        _ghosts.Forget();
        _visitedLandmarks.Clear();
        _engaging = 0;
        _destination = null;
        _destinationName = string.Empty;
        _lastMoveRequest = DateTime.MinValue;
        _nothingToDoSince = DateTime.MaxValue;
        Status = FrontierStatus.Running;
        Detail = "Exploring.";
    }

    public void Stop()
    {
        Status = FrontierStatus.Idle;
        _world.StopMoving();
    }

    public void Tick()
    {
        if (Status != FrontierStatus.Running)
            return;

        var scope = _world.Scope;
        _ghosts.SyncScope(scope.Territory, scope.Map, scope.ContentId);

        if (!_world.IsReady || _world.IsDead)
            return;

        // A boss module owns the fight outright; nothing here should be steering during one.
        if (_world.BossModuleActive)
        {
            Detail = "Boss — boss handler is driving.";
            _nothingToDoSince = DateTime.MaxValue;
            return;
        }

        if (TryFinishEngagement())
            return;

        var nearby = _world.ScanNearby(ScanRadius)
            .Where(o => !_ghosts.IsGhost(o))
            .ToList();

        if (TryFight(nearby) || TryInteract(nearby))
        {
            _nothingToDoSince = DateTime.MaxValue;
            return;
        }

        // Combat with nothing selectable left nearby: the fight is someone else's to finish.
        if (_world.InCombat)
        {
            Detail = "In combat.";
            _nothingToDoSince = DateTime.MaxValue;
            return;
        }

        if (TryOverride() || TryLandmark())
        {
            _nothingToDoSince = DateTime.MaxValue;
            return;
        }

        Idle();
    }

    // ── Objects ──

    /// <summary>
    /// Watches the thing currently being dealt with, and ghosts it once it is done.
    ///
    /// <para>
    /// Done means "no longer offered by the scan": killed, opened, or despawned. Waiting for that
    /// rather than assuming the swing landed is what keeps the navigator from walking away from a
    /// half-killed pack.
    /// </para>
    /// </summary>
    private bool TryFinishEngagement()
    {
        if (_engaging == 0)
            return false;

        var still = _world.ScanNearby(ScanRadius).FirstOrDefault(o => o.Id == _engaging);
        if (still.Id == _engaging && still.IsTargetable)
        {
            Detail = $"Engaged: {still.Name}.";
            return _world.InCombat; // still fighting; nothing else to decide this tick
        }

        // Gone. Remember it so the next scan does not offer it again.
        _ghosts.Remember(new WorldObject(_engaging, 0, string.Empty, _world.PlayerPosition,
            WorldObjectKind.Hostile, false));
        _engaging = 0;
        return false;
    }

    private bool TryFight(IReadOnlyList<WorldObject> nearby)
    {
        var target = Nearest(nearby.Where(o => o.Kind == WorldObjectKind.Hostile && o.IsTargetable));
        if (target is not { } hostile)
            return false;

        var distance = Vector3.Distance(_world.PlayerPosition, hostile.Position);
        if (distance > EngageRange)
        {
            Approach(hostile.Position, $"Approaching {hostile.Name}");
            return true;
        }

        _world.StopMoving();
        _world.AttackObject(hostile.Id);
        _engaging = hostile.Id;
        Detail = $"Fighting {hostile.Name}.";
        return true;
    }

    private bool TryInteract(IReadOnlyList<WorldObject> nearby)
    {
        var candidates = nearby.Where(o => o.Kind == WorldObjectKind.Interactable && o.IsTargetable);
        if (_lootChests())
            candidates = nearby.Where(o => o.Kind is WorldObjectKind.Interactable or WorldObjectKind.Treasure && o.IsTargetable);

        var target = Nearest(candidates);
        if (target is not { } thing)
            return false;

        var distance = Vector3.Distance(_world.PlayerPosition, thing.Position);
        if (distance > InteractRange)
        {
            Approach(thing.Position, $"Approaching {thing.Name}");
            return true;
        }

        _world.StopMoving();
        _world.InteractWithObject(thing.Id);

        // Interactables give no reliable "done" signal — a pulled lever looks exactly like an
        // unpulled one — so they are ghosted on the attempt rather than on an outcome.
        _ghosts.Remember(thing);
        Detail = $"Interacting with {thing.Name}.";
        return true;
    }

    private WorldObject? Nearest(IEnumerable<WorldObject> candidates)
    {
        var here = _world.PlayerPosition;
        WorldObject? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in candidates)
        {
            var distance = Vector3.DistanceSquared(candidate.Position, here);
            if (distance >= bestDistance)
                continue;

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    // ── Direction ──

    /// <summary>
    /// A hand-authored escape, when standing on one.
    ///
    /// <para>
    /// Above the marker search deliberately: overrides exist for the places perception cannot
    /// describe, and a slide entrance often sits right next to a labelled room the navigator would
    /// otherwise happily walk back to.
    /// </para>
    /// </summary>
    private bool TryOverride()
    {
        var scope = _world.Scope;
        if (_overrides.Covering(scope.Territory, _world.PlayerPosition) is not { } jump)
            return false;

        // Arriving is the exit point, so the entry stops covering us and this fires only once.
        Approach(jump.To, jump.Note.Length > 0 ? jump.Note : "Authored waypoint");
        return true;
    }

    private bool TryLandmark()
    {
        if (_destination is { } current)
        {
            if (Vector3.Distance(_world.PlayerPosition, current) > LandmarkArrival)
            {
                Approach(current, _destinationName);
                return true;
            }

            _visitedLandmarks.Add(_destinationName);
            _destination = null;
        }

        var scope = _world.Scope;
        var here = _world.PlayerPosition;

        foreach (var landmark in _markers.For(scope.Territory)
                     .Where(l => !_visitedLandmarks.Contains(l.Name))
                     .OrderBy(l => Flat(l.Position, here)))
        {
            // A marker snaps to nothing when the ground under it is not reachable yet — a room
            // behind a door that has not opened. That is not a failure, it is "not the frontier".
            var flat = landmark.Position with { Y = here.Y };
            if (_world.NearestReachablePoint(flat, SnapExtent) is not { } ground)
                continue;

            _destination = ground;
            _destinationName = landmark.Name;
            Approach(ground, landmark.Name);
            return true;
        }

        return false;
    }

    private static float Flat(Vector3 a, Vector3 b)
        => ((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z));

    private void Approach(Vector3 destination, string what)
    {
        Detail = $"→ {what}.";

        if (_world.IsMoving || _world.UtcNow - _lastMoveRequest < MoveRetry)
            return;

        _lastMoveRequest = _world.UtcNow;
        _world.MoveTo(destination);
    }

    private void Idle()
    {
        if (_nothingToDoSince == DateTime.MaxValue)
        {
            _nothingToDoSince = _world.UtcNow;
            Detail = "Nothing nearby and no unvisited landmarks — waiting.";
            return;
        }

        if (_world.UtcNow - _nothingToDoSince < ExhaustedGrace)
            return;

        Status = FrontierStatus.Exhausted;
        Detail = $"Explored everything reachable: {_visitedLandmarks.Count} landmarks, {_ghosts.Count} objects.";
        _world.StopMoving();
        _world.Log(Detail);
    }
}
