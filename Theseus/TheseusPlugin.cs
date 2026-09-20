using System;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Theseus.Config;
using Theseus.Services.Duty;
using Frontier = Theseus.Services.Frontier;
using Theseus.Services.Ipc;
using Theseus.Services.Paths;
using Theseus.Services.Run;
using Theseus.Windows;
using Solver = Theseus.Services.Solver;
using static Theseus.Service;

namespace Theseus;

/// <summary>
/// Theseus — automated dungeon running for multibox fleets.
///
/// <para>
/// He went into the labyrinth Daedalus built, killed what was inside, and used Ariadne's thread
/// to find his way back out. This plugin does the first two and treats the third as the feature
/// that matters: an interrupted run resumes where it stopped.
/// </para>
///
/// <para>Design and phased scope live in <c>theseus-plan.md</c> at the repo root (local-only).</para>
/// </summary>
public sealed class TheseusPlugin : IDalamudPlugin
{
    private const string CommandMain = "/theseus";
    private const string CommandShort = "/ts";

    public static string PluginVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly WindowSystem _windowSystem = new("Theseus");
    private readonly TheseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IObjectiveReader _objectiveReader;
    private readonly DutyLifecycle _dutyLifecycle;
    private readonly TheseusIpc _ipc;
    private readonly DaedalusIpc _daedalusIpc;
    private readonly AriadneIpc _ariadneIpc;
    private readonly TargetService _targetService;
    private readonly PathStore _pathStore;
    private readonly DutyCatalog _dutyCatalog;
    private readonly Frontier.MapMarkerCatalog _mapMarkers;
    private readonly Frontier.ZoneOverrideStore _zoneOverrides;
    private readonly GameDutyEntryWorld _dutyEntryWorld;
    private readonly RunController _runController;
    private readonly Solver.SolverWiring _solver;
    private readonly ConfigWindow _configWindow;
    private readonly RunWindow _runWindow;
    private readonly DebugWindow _debugWindow;
    private readonly PathEditorWindow _pathEditorWindow;

    public TheseusPlugin(IDalamudPluginInterface pluginInterface)
    {
        // Create<T> constructs a T. It must be given the service holder, never this plugin —
        // see Service for why that distinction silently kills the client.
        pluginInterface.Create<Service>();

        _config = PluginInterface.GetPluginConfig() as TheseusConfig ?? new TheseusConfig();
        _presence = new PluginPresence(PluginInterface, () => _config.BossHandler);

        var objectiveReader = new ObjectiveReader(fault =>
            Log.Warning($"Objective read failed — falling back to position-only. {fault}"));
        _objectiveReader = objectiveReader;

        _dutyLifecycle = new DutyLifecycle(Service.DutyState, Condition, ClientState, Framework);
        _dutyLifecycle.RunEntered += key => Log.Information($"Entered duty {key}.");
        _dutyLifecycle.DutyCompleted += key => Log.Information($"Duty {key} cleared.");
        _dutyLifecycle.DutyWiped += key => Log.Information($"Duty {key} wiped.");

        _daedalusIpc = new DaedalusIpc(PluginInterface, message => Log.Warning(message));
        // Constructed here rather than inside GameStepWorld: the debug window reads it, and the
        // nav-source switch (config) will decide whether the run does. Nothing consumes its gates
        // yet, so today it is a probe — which is exactly what the migration's first in-game check
        // needs it to be.
        _ariadneIpc = new AriadneIpc(PluginInterface, message => Log.Warning(message));
        _targetService = new TargetService(Service.TargetManager, _daedalusIpc);

        var chat = new ChatCommandSender(message => Log.Warning(message));
        var world = new GameStepWorld(
            ClientState, ObjectTable, PartyList, Condition, Service.GameGui,
            new VnavIpc(PluginInterface, message => Log.Warning(message)),
            _ariadneIpc,
            () => _config.NavSource,
            new BossModIpc(PluginInterface, chat.Send, message => Log.Warning(message)),
            new MinervaIpc(PluginInterface, message => Log.Warning(message)),
            () => _config.BossHandler,
            () => _config.MinervaPreset,
            _daedalusIpc, _targetService, chat, message => Log.Information(message));


        _pathStore = new PathStore(
            System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "paths"),
            message => Log.Warning(message));

        _dutyCatalog = new DutyCatalog(DataManager, message => Log.Warning(message));
        _mapMarkers = new Frontier.MapMarkerCatalog(DataManager, message => Log.Warning(message));
        _zoneOverrides = new Frontier.ZoneOverrideStore(
            System.IO.Path.Combine(PluginInterface.ConfigDirectory.FullName, "zone-overrides.json"),
            message => Log.Warning(message));
        var trustRoster = new TrustRoster(DataManager, message => Log.Warning(message));
        _dutyEntryWorld = new GameDutyEntryWorld(
            Condition, Service.GameGui, _dutyLifecycle, trustRoster, message => Log.Warning(message));

        // The solver's perception, running beside the route executor: it classifies what the run
        // passes, discovers the edges the mesh refuses, and writes down what it learns — never
        // issuing a move. Nothing here decides anything yet; the arbiter and the loops come next.
        var configDir = PluginInterface.ConfigDirectory.FullName;
        var taxonomyPath = System.IO.Path.Combine(configDir, "taxonomy.json");
        var taxonomy = new Solver.Taxonomy(log: message => Log.Warning(message));
        taxonomy.Load(taxonomyPath);

        var gapLog = new Solver.GapLog(
            System.IO.Path.Combine(configDir, "gaps.jsonl"), log: message => Log.Warning(message));

        // One ghost cache for both halves: the model marks a ghosted object done, and the loop must
        // not pick what it already wrote off. Two caches would mean the loop retries its own ghosts.
        var ghosts = new Frontier.GhostCache();

        var ledger = new Solver.GateLedger();
        var interactables = new Solver.InteractableLoop(new Solver.InteractableContext(
            taxonomy,
            ghosts,
            gapLog,
            () => _dutyLifecycle.RunKey.ToString(),
            () => _ariadneIpc.CurrentCacheKey,
            // A lever a locked gate is waiting on is the frontier, not an errand: it is wanted from
            // wherever the character happens to be standing.
            WantedByAGate: dataId => ledger.Gates.Any(g =>
                g.State == Solver.GateState.Locked && Solver.GateLedger.ObjectsNamedBy(g.Unlock).Contains(dataId)),
            Resolved: ledger.Resolved,
            Log: message => Log.Information(message)));

        var arbiter = new Solver.Arbiter(
            new Solver.CombatLoop(),
            interactables,
            new Solver.ExplorationLoop(() => ledger.Gates),
            () => world.Transit);

        var frontierQuery = new Solver.ReachableFrontier(
            _ariadneIpc, () => world.PlayerPosition, message => Log.Warning(message));

        var perception = new Solver.ShadowObserver(
            world, objectiveReader, taxonomy, taxonomyPath,
            ghosts, frontierQuery, ledger, gapLog, _ariadneIpc, arbiter,
            () => _dutyLifecycle.IsInDuty,
            () => _dutyLifecycle.RunKey.ToString(),
            message => Log.Information(message));

        var solverRecordPath = System.IO.Path.Combine(configDir, "solver.json");
        var records = new Solver.SolverRecordStore(message => Log.Warning(message));
        records.Load(solverRecordPath);

        // The solver can drive when the user's switch is on and Ariadne is actually answering with a
        // grid. Anything less and the run keeps the driver it has today — failing open is the whole
        // point of the seam.
        Func<bool> usable = () => _config.SolverDrives && _ariadneIpc.IsConnected && frontierQuery.Grid is not null;

        _solver = new Solver.SolverWiring(
            perception, arbiter,
            new Solver.SolverDriver(perception, arbiter, world, usable, message => Log.Information(message)),
            records, solverRecordPath, usable);

        _runController = new RunController(
            _config, _dutyLifecycle, _objectiveReader, _pathStore,
            new StepExecutor(
                world,
                () => _config.MatchRouteToRole && world.IsTank,
                tuning: null,
                () => _config.LootChests,
                () => _config.LootChests && !_config.LootBossChestsOnly,
                objectiveReader.Read),
            new DutyEntry(_dutyEntryWorld, () => _config.TrustParty),
            world, Framework, message => Log.Information(message),
            // The catalog holds dungeons only, so membership is the "may auto-start here" test.
            territory => _dutyCatalog.ByTerritory(territory) is not null,
            new Frontier.FrontierNavigator(world, _mapMarkers, _zoneOverrides, () => _config.LootChests),
            _solver);

        // Published once the controller exists, so the gate never reports on a half-built run.
        // Daedalus reads a missing gate as idle, so appearing a moment late is harmless.
        _ipc = new TheseusIpc(PluginInterface,
            () => _config.Enabled && _runController.State.IsDriving(),
            // Odysseus hands MSQ dungeons and trials here. Alone that means Duty Support, not Trust: the story's own
            // cast is what an MSQ duty wants, and it is the only option before Shadowbringers.
            cfc =>
            {
                if (!_runController.CanEnterDuty)
                    return false;
                // Same group check as the run window: a party queues through the Duty Finder rather
                // than walking a lone Duty Support entry past three waiting players.
                _runController.Enter(cfc, _dutyCatalog.NameOf(cfc), trust: false);
                return _runController.State == RunState.Entering;
            },
            () => _config.Enabled && _runController.CanEnterDuty);


        _pathEditorWindow = new PathEditorWindow(
            _pathStore,
            () => ClientState.TerritoryType,
            () => ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero,
            _objectiveReader,
            step => _runController.StepOnce(step),
            () => _runController.State != RunState.Idle);

        _configWindow = new ConfigWindow(
            _config, SaveConfig, _presence, _pathStore,
            PathStore.DefaultAutoDutyDirectory(PluginInterface.ConfigDirectory),
            () => ClientState.TerritoryType,
            () => _pathEditorWindow.IsOpen = true);
        _runWindow = new RunWindow(
            _config, _presence, _objectiveReader, _runController, _dutyCatalog, _pathStore,
            OpenConfig, SaveConfig);
        _debugWindow = new DebugWindow(
            objectiveReader, _dutyLifecycle,
            () => world.DescribeNearby(40f),
            world.DescribeMovement,
            _ariadneIpc.Describe,
            _runController.DescribeShadow,
            _runController.DescribeSolver,
            _runController.DescribeMovement,
            // Both agents, because which of them holds which system's roster is exactly the
            // question — reading one and assuming has been wrong twice.
            () => $"AgentDawn (opened by OpenDawn):\n{_runController.DescribeCompanionData(trust: true)}\n" +
                  $"AgentDawnStory (opened by OpenDawnStory):\n{_runController.DescribeCompanionData(trust: false)}");

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_runWindow);
        _windowSystem.AddWindow(_debugWindow);
        _windowSystem.AddWindow(_pathEditorWindow);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Theseus. \"/theseus config\" opens settings, \"/theseus debug\" the objective dump, \"/theseus paths\" the route editor.",
        });
        CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /theseus.",
        });

        Log.Information($"Theseus v{PluginVersion} loaded.");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(CommandMain);
        CommandManager.RemoveHandler(CommandShort);

        PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;

        _windowSystem.RemoveAllWindows();
        _ipc.Dispose();
        _runController.Dispose();
        _dutyLifecycle.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                OpenConfig();
                break;
            case "debug":
                _debugWindow.IsOpen = true;
                break;
            case "paths":
                _pathEditorWindow.IsOpen = true;
                break;
            default:
                OpenMain();
                break;
        }
    }

    private void OpenMain() => _runWindow.IsOpen = true;

    private void OpenConfig() => _configWindow.IsOpen = true;

    private void SaveConfig() => PluginInterface.SavePluginConfig(_config);
}
