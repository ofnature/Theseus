using System;
using System.Collections.Generic;
using System.Numerics;

namespace Theseus.Services.Run;

/// <summary>
/// Everything the step executor needs from the game and from other plugins, behind one seam.
///
/// <para>
/// The executor is where a route's flow control lives — retry loops, conditional jumps, combat
/// gating — and that logic is both the easiest to get wrong and the only part that can be tested
/// without a client attached. Keeping every game touch behind this interface is what makes those
/// tests possible; the real implementation is a thin translation layer with no decisions in it.
/// </para>
/// </summary>
public interface IStepWorld
{
    DateTime UtcNow { get; }

    Vector3 PlayerPosition { get; }

    // ── Navigation ──

    /// <summary>The zone's navmesh is built. Movement before this silently does nothing.</summary>
    bool NavmeshReady { get; }

    /// <summary>A path is being computed or followed.</summary>
    bool IsMoving { get; }

    /// <summary>
    /// Waypoints in the current path, or -1 when it cannot be read. Zero after a pathfind has
    /// finished means the destination could not be reached.
    /// </summary>
    int PathWaypointCount { get; }

    /// <summary>Paths to a point. False when the destination is unreachable.</summary>
    bool MoveTo(Vector3 destination);

    /// <summary>Paths to within a tolerance of a point, for standing next to something.</summary>
    bool MoveCloseTo(Vector3 destination, float tolerance);

    /// <summary>
    /// How close the navigator itself walks before declaring arrival.
    ///
    /// <para>
    /// Set by us rather than inherited, because it is half of an agreement the run depends on: the
    /// navigator has to get closer than we require, or a waypoint can never be reached. Left to
    /// whatever the other plugin happened to be configured with, a six-yalm setting stopped short
    /// of every waypoint in the route and each one re-pathed forever.
    /// </para>
    /// </summary>
    void SetMoveTolerance(float tolerance);

    void StopMoving();

    /// <summary>Raw forward movement, with no pathing — what <see cref="Paths.StepVerb.AutoMoveFor"/> uses.</summary>
    void SetForwardMovement(bool enabled);

    void Jump();

    // ── Player state ──

    bool InCombat { get; }

    /// <summary>Not occupied, casting, zoning or otherwise mid-something.</summary>
    bool IsReady { get; }

    bool IsOccupied { get; }

    bool IsCasting { get; }

    bool IsJumping { get; }

    bool IsDead { get; }

    /// <summary>
    /// The character is playing a tank job. Decides which wall-to-wall route to run, so getting it
    /// wrong hands a damage dealer the tank's chain-pull route.
    /// </summary>
    bool IsTank { get; }

    /// <summary>The game will accept a request to leave the instance right now.</summary>
    bool CanLeaveDuty { get; }

    /// <summary>Leaves the instance, as the Duty Finder "leave" option does.</summary>
    void LeaveDuty();

    /// <summary>A condition flag by name or numeric id, as routes spell them.</summary>
    bool HasConditionFlag(string nameOrId);

    // ── Combat ──

    /// <summary>A boss module's state machine is running.</summary>
    bool BossModuleActive { get; }

    /// <summary>Hands the fight to, or takes it back from, BossMod's AI.</summary>
    void SetBossModAi(bool enabled);

    /// <summary>Turns the rotation plugin's combat on or off.</summary>
    void SetRotationEnabled(bool enabled);

    /// <summary>Targets and engages the nearest hostile within a radius. False when there is none.</summary>
    bool AttackNearestWithin(float radius);

    // ── World objects ──

    /// <summary>Targets and interacts with an object by data id.</summary>
    bool TryInteractWithDataId(uint dataId, float searchRadius);

    /// <summary>Opens the nearest treasure coffer to a point.</summary>
    bool TryOpenCofferNear(Vector3 near, float radius);

    /// <summary>
    /// Nearest treasure coffer within <paramref name="radius"/> that is not in
    /// <paramref name="ignore"/>. Returns its id as well as its position, because a looted coffer
    /// lingers in the object table and has to be remembered rather than re-found.
    /// </summary>
    (ulong Id, Vector3 Position)? NearestCoffer(float radius, IReadOnlyCollection<ulong> ignore);

    /// <summary>Opens a specific coffer by id.</summary>
    bool OpenCoffer(ulong id);

    /// <summary>
    /// Short description of the interactable objects nearby, for the log.
    ///
    /// <para>
    /// Diagnostic only. Some treasure coffers are not <c>Treasure</c>-kind objects and repeated
    /// inference about which kind they are has been wrong more than once — this asks the game
    /// instead.
    /// </para>
    /// </summary>
    string DescribeNearby(float radius);

    bool TryTargetDataId(uint dataId);

    // ── Fleet ──

    /// <summary>
    /// The party, in list order, Trust companions and Duty Support NPCs included.
    ///
    /// <para>
    /// The order is the load-bearing part: it is identical on every client, so a role derived from
    /// it needs no election and nothing passing between boxes.
    /// </para>
    /// </summary>
    IReadOnlyList<Fleet.FleetMember> PartyMembers { get; }

    // ── Frontier navigation ──

    /// <summary>Territory, map and content id — the scope a ghost cache is valid within.</summary>
    (uint Territory, uint Map, uint ContentId) Scope { get; }

    /// <summary>
    /// Everything nearby worth acting on: hostiles, interactables, coffers.
    ///
    /// <para>
    /// The frontier navigator's whole perception. Returns instance ids rather than data ids,
    /// because four copies of one mob are four things to kill and a data id would call them one.
    /// </para>
    /// </summary>
    IReadOnlyList<Frontier.WorldObject> ScanNearby(float radius);

    /// <summary>Targets and engages a specific object.</summary>
    bool AttackObject(ulong id);

    /// <summary>Targets and interacts with a specific object.</summary>
    bool InteractWithObject(ulong id);

    /// <summary>
    /// Snaps a point onto walkable ground, or null when nothing reachable is near it.
    ///
    /// <para>
    /// Map markers carry no height, so this is what makes one walkable — and a marker that snaps
    /// to nothing is a marker behind a door that has not opened, which is worth knowing.
    /// </para>
    /// </summary>
    Vector3? NearestReachablePoint(Vector3 near, float halfExtent);

    bool IsDataIdTargetable(uint dataId);

    bool IsDataIdSpawned(uint dataId);

    /// <summary>Nameplate icon on an object, or null when it is not present.</summary>
    int? NamePlateIconId(uint dataId);

    /// <summary>Distance from an object to a point, or null when the object is not present.</summary>
    float? DistanceFromDataIdToPoint(uint dataId, Vector3 point);

    // ── UI and chat ──

    bool IsAddonVisible(string name);

    void SendChatCommand(string command);

    /// <summary>Picks an entry in a list dialog.</summary>
    void SelectStringIndex(int index);

    /// <summary>Answers a yes/no dialog.</summary>
    void SelectYesNo(bool yes);

    void Log(string message);
}
