using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Theseus.Services.Ipc;

namespace Theseus.Services.Run;

/// <summary>
/// The real <see cref="IStepWorld"/> — a translation layer over the game and the plugins Theseus
/// leans on. Deliberately decision-free: everything here is "do the thing" or "report the fact",
/// and all judgement lives in <see cref="StepExecutor"/> where it can be tested.
/// </summary>
public sealed unsafe class GameStepWorld : IStepWorld
{
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IPartyList _partyList;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly VnavIpc _vnav;
    private readonly AriadneIpc _ariadne;
    private readonly AriadneMover _ariadneMover;
    private readonly Func<Config.NavSource> _navSource;
    private readonly BossModIpc _bossMod;
    private readonly MinervaIpc _minerva;
    private readonly Func<Config.BossHandler> _bossHandler;
    private readonly Func<string> _minervaPreset;
    private readonly DaedalusIpc _daedalus;
    private readonly TargetService _targets;
    private readonly ChatCommandSender _chat;
    private readonly Action<string> _log;

    public GameStepWorld(
        IClientState clientState,
        IObjectTable objectTable,
        IPartyList partyList,
        ICondition condition,
        IGameGui gameGui,
        VnavIpc vnav,
        AriadneIpc ariadne,
        Func<Config.NavSource> navSource,
        BossModIpc bossMod,
        MinervaIpc minerva,
        Func<Config.BossHandler> bossHandler,
        Func<string> minervaPreset,
        DaedalusIpc daedalus,
        TargetService targets,
        ChatCommandSender chat,
        Action<string> log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _partyList = partyList;
        _condition = condition;
        _gameGui = gameGui;
        _vnav = vnav;
        _ariadne = ariadne;
        _navSource = navSource;
        _ariadneMover = new AriadneMover(ariadne, vnav, () => PlayerPosition, SetForwardMovement, log: log);
        _bossMod = bossMod;
        _minerva = minerva;
        _bossHandler = bossHandler;
        _minervaPreset = minervaPreset;
        _daedalus = daedalus;
        _targets = targets;
        _chat = chat;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public Vector3 PlayerPosition => _objectTable.LocalPlayer?.Position ?? Vector3.Zero;

    // ── Navigation ──

    /// <summary>
    /// The pathfinding source from the config, read fresh each time: the switch is meant to be
    /// flipped between runs — and, territory by territory, between duties — without a reload.
    /// </summary>
    private Config.NavSource Source => _navSource();

    /// <summary>
    /// Whether a move can be routed right now, by the selected source.
    ///
    /// <para>
    /// With Ariadne selected, either source counts. Its readiness is the whole reason to select it
    /// — true about a tenth of a second after zone-in, where vnavmesh needs its build — but a zone
    /// Ariadne cannot serve must not read as "not ready yet": that would leave a run standing at the
    /// gate while the fallback source sat ready beside it. The same rule the moves themselves use,
    /// which is that a run never stalls on its source.
    /// </para>
    /// </summary>
    public bool NavmeshReady => Source == Config.NavSource.Ariadne
        ? _ariadne.NavReady || _vnav.IsReady
        : _vnav.IsReady;

    public bool IsMoving => Source == Config.NavSource.Ariadne ? _ariadneMover.IsBusy : _vnav.IsBusy;

    public int PathWaypointCount => Source == Config.NavSource.Ariadne
        ? _ariadneMover.WaypointCount
        : _vnav.WaypointCount;

    public bool MoveTo(Vector3 destination) => Source == Config.NavSource.Ariadne
        ? _ariadneMover.Begin(destination)
        : _vnav.MoveTo(destination);

    /// <summary>
    /// vnavmesh, in both modes. Nothing calls it yet — the near-goal on Ariadne is
    /// <c>SimpleMove.PathfindAndMoveCloseTo</c>, which is not wrapped — and when something does, it
    /// becomes that gate rather than this one staying quietly on the other source.
    /// </summary>
    public bool MoveCloseTo(Vector3 destination, float tolerance) => _vnav.MoveCloseTo(destination, tolerance);

    public void SetMoveTolerance(float tolerance)
    {
        _vnav.SetTolerance(tolerance);
        _ariadneMover.SetTolerance(tolerance);
    }

    public void StopMoving()
    {
        if (Source == Config.NavSource.Ariadne)
            _ariadneMover.Stop(); // stops vnavmesh too: the move in hand may be a fallback one
        else
            _vnav.Stop();
    }

    /// <summary>
    /// vnavmesh's movement state, split into the parts <see cref="IsMoving"/> collapses together.
    ///
    /// <para>
    /// Diagnostic only, and the reason it exists is that the collapsed view cannot tell apart the
    /// two things that matter when a run pulses: vnavmesh never accepting the move, and vnavmesh
    /// accepting it, finding a path, and stopping instantly because it already considers itself
    /// arrived. Both look identical from outside — a completed pathfind and a character that does
    /// not move — and guessing between them has been wrong repeatedly.
    /// </para>
    /// </summary>
    public string DescribeMovement()
        => $"ready {_vnav.IsReady} · running {_vnav.IsPathRunning} · pathfinding {_vnav.IsPathfinding} · " +
           $"waypoints {_vnav.WaypointCount}" +
           (Source == Config.NavSource.Ariadne
               ? $" · source Ariadne [{_ariadneMover.Describe()}]"
               : string.Empty);

    /// <summary>
    /// Raw forward movement — the game's own auto-run, with no pathfinding involved.
    ///
    /// <para>
    /// This is what <see cref="Paths.StepVerb.AutoMoveFor"/> means, and it is the only way to walk
    /// somewhere the navmesh will not route to: off a ledge, onto a slide, through a one-way drop.
    /// Pathfinding actively works against those, because it routes around the fall.
    /// </para>
    ///
    /// <para>
    /// The explicit <c>on</c>/<c>off</c> arguments are field-confirmed. Do not reduce this to a
    /// bare <c>/automove</c>: as a toggle it would desync from our own idea of the state the first
    /// time anything else moved the character.
    /// </para>
    /// </summary>
    public void SetForwardMovement(bool enabled)
        => SendChatCommand(enabled ? "/automove on" : "/automove off");

    public void Jump() => SendChatCommand("/generalaction Jump");

    // ── Player state ──

    public bool InCombat => _condition[ConditionFlag.InCombat];

    public bool IsReady
        => _objectTable.LocalPlayer is not null
           && !_condition[ConditionFlag.BetweenAreas]
           && !_condition[ConditionFlag.BetweenAreas51]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.Casting]
           && !_condition[ConditionFlag.Unconscious];

    public bool IsOccupied
        => _condition[ConditionFlag.Occupied]
           || _condition[ConditionFlag.Occupied30]
           || _condition[ConditionFlag.Occupied33]
           || _condition[ConditionFlag.Occupied38]
           || _condition[ConditionFlag.OccupiedInEvent]
           || _condition[ConditionFlag.OccupiedInQuestEvent]
           || _condition[ConditionFlag.OccupiedInCutSceneEvent];

    public bool IsCasting => _condition[ConditionFlag.Casting];

    public bool IsJumping => _condition[ConditionFlag.Jumping] || _condition[ConditionFlag.Jumping61];

    public bool IsDead => _objectTable.LocalPlayer?.IsDead ?? false;

    /// <summary>
    /// Gladiator, Marauder, Paladin, Warrior, Dark Knight, Gunbreaker. Matched by job id rather
    /// than by the sheet's role column so that the set is visible and cannot shift under us.
    /// </summary>
    private static readonly uint[] TankJobs = [1, 3, 19, 21, 32, 37];

    public bool IsTank
    {
        get
        {
            var job = _objectTable.LocalPlayer?.ClassJob.RowId;
            return job is not null && Array.IndexOf(TankJobs, job.Value) >= 0;
        }
    }

    public bool CanLeaveDuty => FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.CanLeaveCurrentContent();

    /// <summary>
    /// Leaves through the game's own path rather than by driving the Duty Finder's menu, so there
    /// is no addon to be open, translated, or in the wrong state.
    /// </summary>
    public void LeaveDuty()
    {
        try
        {
            if (!CanLeaveDuty)
                return;

            // Not forced: the polite exit, which respects the game's own "you cannot leave yet".
            FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.LeaveCurrentContent(false);
        }
        catch (Exception ex)
        {
            _log($"Leaving the duty failed: {ex.Message}");
        }
    }

    public bool HasConditionFlag(string nameOrId)
    {
        // Routes name flags both ways — "Jumping61" and bare numbers both appear.
        if (int.TryParse(nameOrId, out var numeric))
            return Enum.IsDefined(typeof(ConditionFlag), numeric) && _condition[(ConditionFlag)numeric];

        return Enum.TryParse<ConditionFlag>(nameOrId, ignoreCase: true, out var flag) && _condition[flag];
    }

    // ── Combat ──

    public bool BossModuleActive
        => _bossHandler() == Config.BossHandler.Minerva ? _minerva.HasActiveModule : _bossMod.HasActiveModule;

    /// <summary>
    /// Hands boss mechanics to whichever plugin is selected. For Minerva only "on" means anything: its
    /// AI has no off switch short of releasing the preset slot, and the AI is never turned off anyway.
    /// </summary>
    public void SetBossModAi(bool enabled)
    {
        if (_bossHandler() != Config.BossHandler.Minerva)
        {
            _bossMod.SetAiEnabled(enabled);
            return;
        }

        if (!enabled)
            return;

        var preset = _minervaPreset();
        if (!_minerva.ApplyPreset(preset))
            _log($"Minerva refused preset \"{preset}\" — create it in Minerva with auto-dodge on.");
    }

    public void SetRotationEnabled(bool enabled) => _daedalus.SetRotationEnabled(enabled);

    public bool AttackNearestWithin(float radius)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return false;

        var target = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.BattleNpc && IsAttackable(o))
            .Where(o => Vector3.Distance(o.Position, player.Position) <= radius)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();

        if (target is null)
            return false;

        _targets.SetTarget(target);
        return true;
    }

    // ── World objects ──

    public bool TryInteractWithDataId(uint dataId, float searchRadius)
    {
        var target = NearestWithDataId(dataId, searchRadius);
        if (target is null)
            return false;

        _targets.SetTarget(target);
        return Interact(target);
    }

    public bool TryOpenCofferNear(Vector3 near, float radius)
    {
        var coffer = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.Treasure)
            .Where(o => Vector3.Distance(o.Position, near) <= radius)
            .OrderBy(o => Vector3.Distance(o.Position, near))
            .FirstOrDefault();

        if (coffer is null)
            return false;

        _targets.SetTarget(coffer);
        return Interact(coffer);
    }

    public (ulong Id, Vector3 Position)? NearestCoffer(float radius, IReadOnlyCollection<ulong> ignore)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;

        var coffer = _objectTable
            .Where(o => o.ObjectKind == ObjectKind.Treasure)
            .Where(o => !ignore.Contains(o.GameObjectId))
            .Where(o => Vector3.Distance(o.Position, player.Position) <= radius)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();

        return coffer is null ? null : (coffer.GameObjectId, coffer.Position);
    }

    public bool OpenCoffer(ulong id)
    {
        var coffer = _objectTable.FirstOrDefault(o => o.GameObjectId == id);
        if (coffer is null)
            return false;

        _targets.SetTarget(coffer);
        return Interact(coffer);
    }

    public string DescribeNearby(float radius)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return "no player";

        var nearby = _objectTable
            .Where(o => o.ObjectKind is not (ObjectKind.Pc or ObjectKind.BattleNpc or ObjectKind.Companion))
            .Select(o => new { Object = o, Distance = Vector3.Distance(o.Position, player.Position) })
            .Where(o => o.Distance <= radius)
            .OrderBy(o => o.Distance)
            .Take(8)
            .Select(o => $"{o.Object.ObjectKind}#{o.Object.BaseId} \"{o.Object.Name}\" {o.Distance:0}y")
            .ToList();

        return nearby.Count == 0 ? "nothing" : string.Join(", ", nearby);
    }

    public bool TryTargetDataId(uint dataId)
    {
        var target = NearestWithDataId(dataId, float.MaxValue);
        if (target is null)
            return false;

        _targets.SetTarget(target);
        return true;
    }

    public bool IsDataIdTargetable(uint dataId)
        => NearestWithDataId(dataId, float.MaxValue) is { IsTargetable: true };

    public bool IsDataIdSpawned(uint dataId) => NearestWithDataId(dataId, float.MaxValue) is not null;

    public int? NamePlateIconId(uint dataId)
    {
        var target = NearestWithDataId(dataId, float.MaxValue);
        if (target is null)
            return null;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
        return native is not null ? (int)native->NamePlateIconId : null;
    }

    public float? DistanceFromDataIdToPoint(uint dataId, Vector3 point)
    {
        var target = NearestWithDataId(dataId, float.MaxValue);
        return target is null ? null : Vector3.Distance(target.Position, point);
    }

    // ── Fleet ──

    /// <summary>
    /// The party list, classified into real characters and NPC allies.
    ///
    /// <para>
    /// The player/NPC split is by object kind rather than by any id convention: a Trust companion
    /// is a BattleNpc and a real character is a Pc, which the game states outright. Counting a
    /// Trust healer as a peer would make a gate wait for an ally that follows you anyway.
    /// </para>
    /// </summary>
    public IReadOnlyList<Fleet.FleetMember> PartyMembers
    {
        get
        {
            var self = _objectTable.LocalPlayer;
            var members = new List<Fleet.FleetMember>(_partyList.Length);

            for (var i = 0; i < _partyList.Length; i++)
            {
                var member = _partyList[i];
                if (member is null)
                    continue;

                members.Add(new Fleet.FleetMember(
                    member.ContentId,
                    member.Name.TextValue,
                    i,
                    member.Position,
                    self is not null && member.EntityId == self.EntityId,
                    // Content id first: only real characters have one, and it is present when the
                    // member is out of range. The loaded object is null then, and relying on it alone
                    // counted a spread-out party as solo.
                    member.ContentId != 0 || member.GameObject?.ObjectKind == ObjectKind.Pc,
                    i == _partyList.PartyLeaderIndex));
            }

            // Solo, the party list is empty — but this box is still the fleet of one, and every
            // caller reasons about "the players here" rather than "the party".
            if (members.Count == 0 && self is not null)
                members.Add(new Fleet.FleetMember(0, self.Name.TextValue, 0, self.Position, true, true));

            return members;
        }
    }

    // ── Frontier navigation ──

    public (uint Territory, uint Map, uint ContentId) Scope
        => (_clientState.TerritoryType, _clientState.MapId, Duty.DirectorAccess.CurrentRunIdentity().ContentId);

    /// <summary>
    /// Nearby objects worth acting on, classified by what can be done with them.
    ///
    /// <para>
    /// Classification is by object kind and targetability rather than by name or data id, because
    /// the frontier mode runs in content nobody has catalogued — anything that needs a lookup table
    /// to recognise is exactly what this mode cannot rely on.
    /// </para>
    /// </summary>
    public IReadOnlyList<Frontier.WorldObject> ScanNearby(float radius)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return [];

        var found = new List<Frontier.WorldObject>();

        foreach (var o in _objectTable)
        {
            if (o.EntityId == player.EntityId || Vector3.Distance(o.Position, player.Position) > radius)
                continue;

            var kind = o.ObjectKind switch
            {
                ObjectKind.BattleNpc when IsAttackable(o) => Frontier.WorldObjectKind.Hostile,
                ObjectKind.Treasure => Frontier.WorldObjectKind.Treasure,
                ObjectKind.EventObj => Frontier.WorldObjectKind.Interactable,
                _ => (Frontier.WorldObjectKind?)null,
            };

            if (kind is not { } classified)
                continue;

            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address;
            var targetable = native is not null && native->GetIsTargetable();

            found.Add(new Frontier.WorldObject(
                o.GameObjectId, o.BaseId, o.Name.TextValue, o.Position, classified, targetable));
        }

        return found;
    }

    public bool AttackObject(ulong id)
    {
        var target = _objectTable.FirstOrDefault(o => o.GameObjectId == id);
        if (target is null)
            return false;

        _targets.SetTarget(target);
        return true;
    }

    public bool InteractWithObject(ulong id)
    {
        var target = _objectTable.FirstOrDefault(o => o.GameObjectId == id);
        if (target is null)
            return false;

        _targets.SetTarget(target);
        return Interact(target);
    }

    public Vector3? NearestReachablePoint(Vector3 near, float halfExtent)
        => _vnav.NearestReachablePoint(near, halfExtent, halfExtent);

    // ── UI and chat ──

    public bool IsAddonVisible(string name)
    {
        var addon = _gameGui.GetAddonByName(name);
        return !addon.IsNull && addon.IsVisible;
    }

    public void SendChatCommand(string command) => _chat.Send(command);

    public void SelectStringIndex(int index) => FireAddonCallback("SelectString", index);

    public void SelectYesNo(bool yes) => FireAddonCallback("SelectYesno", yes ? 0 : 1);

    public void Log(string message) => _log(message);

    // ── Helpers ──

    private IGameObject? NearestWithDataId(uint dataId, float radius)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return null;

        return _objectTable
            .Where(o => o.BaseId == dataId)
            .Where(o => radius >= float.MaxValue || Vector3.Distance(o.Position, player.Position) <= radius)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();
    }

    private bool Interact(IGameObject target)
    {
        try
        {
            var system = TargetSystem.Instance();
            if (system is null)
                return false;

            var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
            if (native is null)
                return false;

            system->InteractWithObject(native, false);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Interact failed: {ex.Message}");
            return false;
        }
    }

    private void FireAddonCallback(string addonName, int value)
    {
        try
        {
            var addon = _gameGui.GetAddonByName(addonName);
            if (addon.IsNull || !addon.IsVisible)
                return;

            ((AtkUnitBase*)addon.Address)->FireCallbackInt(value);
        }
        catch (Exception ex)
        {
            _log($"Dialog \"{addonName}\" callback failed: {ex.Message}");
        }
    }

    private static bool IsAttackable(IGameObject o)
    {
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)o.Address;
        return native is not null && native->GetIsTargetable() && !native->IsDead();
    }
}
