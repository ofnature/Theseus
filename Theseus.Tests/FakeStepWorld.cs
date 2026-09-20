using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Tests;

/// <summary>
/// A scriptable stand-in for the game, so the executor's decisions can be tested without a client.
/// Records what was asked of it and answers whatever the test sets.
/// </summary>
public sealed class FakeStepWorld : IStepWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    public Vector3 PlayerPosition { get; set; }

    public bool NavmeshReady { get; set; } = true;

    public bool IsMoving { get; set; }

    /// <summary>-1 means "cannot tell", which is the safe default for tests that do not care.</summary>
    public int PathWaypointCount { get; set; } = -1;

    public bool InCombat { get; set; }

    public bool IsReady { get; set; } = true;

    public bool IsOccupied { get; set; }

    public bool IsCasting { get; set; }

    public bool IsJumping { get; set; }

    public bool IsDead { get; set; }

    public bool IsTank { get; set; }

    public bool CanLeaveDuty { get; set; } = true;

    public int LeaveDutyCalls { get; private set; }

    public bool BossModuleActive { get; set; }

    public HashSet<string> ConditionFlags { get; } = [];

    public HashSet<uint> TargetableDataIds { get; } = [];

    public HashSet<uint> SpawnedDataIds { get; } = [];

    public Dictionary<uint, int> NamePlateIcons { get; } = [];

    public Dictionary<uint, Vector3> ObjectPositions { get; } = [];

    public HashSet<string> VisibleAddons { get; } = [];

    /// <summary>Enemies the next <c>AttackNearestWithin</c> will find, decremented per call.</summary>
    public int EnemiesInRange { get; set; }

    // ── Recorded calls ──
    public List<Vector3> MoveRequests { get; } = [];

    public int StopMovingCalls { get; private set; }

    public List<bool> BossAiCalls { get; } = [];

    public List<bool> RotationCalls { get; } = [];

    public List<string> ChatCommands { get; } = [];

    public List<uint> Interacted { get; } = [];

    public List<uint> Targeted { get; } = [];

    public List<Vector3> CoffersOpened { get; } = [];

    /// <summary>Coffers present in the world, by id.</summary>
    public Dictionary<ulong, Vector3> Coffers { get; } = [];

    public List<ulong> OpenedCofferIds { get; } = [];

    public List<bool> ForwardMovement { get; } = [];

    public int Jumps { get; private set; }

    // ── Fleet ──

    public List<Theseus.Services.Fleet.FleetMember> Party { get; } = [];

    public IReadOnlyList<Theseus.Services.Fleet.FleetMember> PartyMembers => Party;

    // ── Frontier navigation ──

    public (uint Territory, uint Map, uint ContentId) Scope { get; set; } = (1314, 1, 103);

    public List<Theseus.Services.Frontier.WorldObject> Nearby { get; } = [];

    /// <summary>Points the navmesh will snap to, keyed loosely by proximity. Null result otherwise.</summary>
    public Func<Vector3, float, Vector3?> SnapPoint { get; set; } = (p, _) => p;

    public List<ulong> Attacked { get; } = [];

    /// <summary>Objects interacted with by instance id, as the frontier navigator does it.</summary>
    public List<ulong> InteractedObjects { get; } = [];

    public IReadOnlyList<Theseus.Services.Frontier.WorldObject> ScanNearby(float radius)
        => Nearby.Where(o => Vector3.Distance(o.Position, PlayerPosition) <= radius).ToList();

    public bool AttackObject(ulong id)
    {
        Attacked.Add(id);
        return true;
    }

    public bool InteractWithObject(ulong id)
    {
        InteractedObjects.Add(id);
        return true;
    }

    public Vector3? NearestReachablePoint(Vector3 near, float halfExtent) => SnapPoint(near, halfExtent);

    public List<int> SelectedStrings { get; } = [];

    public List<bool> YesNoAnswers { get; } = [];

    public List<string> Logs { get; } = [];

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    public bool MoveTo(Vector3 destination)
    {
        MoveRequests.Add(destination);
        return true;
    }

    public bool MoveCloseTo(Vector3 destination, float tolerance) => MoveTo(destination);

    public float? MoveTolerance { get; private set; }

    public void SetMoveTolerance(float tolerance) => MoveTolerance = tolerance;

    public void StopMoving() => StopMovingCalls++;

    public void SetForwardMovement(bool enabled) => ForwardMovement.Add(enabled);

    public void Jump() => Jumps++;

    public bool HasConditionFlag(string nameOrId) => ConditionFlags.Contains(nameOrId);

    public void SetBossModAi(bool enabled) => BossAiCalls.Add(enabled);

    public void SetRotationEnabled(bool enabled) => RotationCalls.Add(enabled);

    public bool AttackNearestWithin(float radius)
    {
        if (EnemiesInRange <= 0)
            return false;

        EnemiesInRange--;
        return true;
    }

    public bool TryInteractWithDataId(uint dataId, float searchRadius)
    {
        Interacted.Add(dataId);
        return true;
    }

    public bool TryOpenCofferNear(Vector3 near, float radius)
    {
        CoffersOpened.Add(near);
        return true;
    }

    public (ulong Id, Vector3 Position)? NearestCoffer(float radius, IReadOnlyCollection<ulong> ignore)
    {
        var candidates = Coffers
            .Where(c => !ignore.Contains(c.Key))
            .Where(c => Vector3.Distance(c.Value, PlayerPosition) <= radius)
            .OrderBy(c => Vector3.Distance(c.Value, PlayerPosition))
            .ToList();

        return candidates.Count == 0 ? null : (candidates[0].Key, candidates[0].Value);
    }

    public bool OpenCoffer(ulong id)
    {
        OpenedCofferIds.Add(id);
        return true;
    }

    public string DescribeNearby(float radius) => "fake";

    public bool TryTargetDataId(uint dataId)
    {
        Targeted.Add(dataId);
        return true;
    }

    public bool IsDataIdTargetable(uint dataId) => TargetableDataIds.Contains(dataId);

    public bool IsDataIdSpawned(uint dataId) => SpawnedDataIds.Contains(dataId);

    public int? NamePlateIconId(uint dataId) => NamePlateIcons.TryGetValue(dataId, out var icon) ? icon : null;

    public float? DistanceFromDataIdToPoint(uint dataId, Vector3 point)
        => ObjectPositions.TryGetValue(dataId, out var position) ? Vector3.Distance(position, point) : null;

    public bool IsAddonVisible(string name) => VisibleAddons.Contains(name);

    public void SendChatCommand(string command) => ChatCommands.Add(command);

    public void SelectStringIndex(int index) => SelectedStrings.Add(index);

    public void SelectYesNo(bool yes) => YesNoAnswers.Add(yes);

    public void LeaveDuty() => LeaveDutyCalls++;

    public void Log(string message) => Logs.Add(message);
}
