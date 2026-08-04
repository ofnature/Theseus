using System;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Theseus.Config;
using Theseus.Services.Duty;
using Theseus.Services.Ipc;
using Theseus.Services.Run;
using Theseus.Windows;

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

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private readonly WindowSystem _windowSystem = new("Theseus");
    private readonly TheseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IObjectiveReader _objectiveReader;
    private readonly ConfigWindow _configWindow;
    private readonly RunWindow _runWindow;

    // Framework cut: no run controller yet, so the state is a field rather than a service.
    // P1 replaces this with RunController and the seam stays the same for the UI.
    private RunState _runState = RunState.Idle;

    public TheseusPlugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<TheseusPlugin>();

        _config = PluginInterface.GetPluginConfig() as TheseusConfig ?? new TheseusConfig();
        _presence = new PluginPresence(PluginInterface);

        // TODO(P0): replace with the real reader over Director.DirectorTodos, falling back to
        // ToDoListNumberArray. Until then every consumer takes its documented "unavailable"
        // path rather than being told, wrongly, that nothing is complete.
        _objectiveReader = new NullObjectiveReader();

        _configWindow = new ConfigWindow(_config, SaveConfig, _presence);
        _runWindow = new RunWindow(_config, _presence, _objectiveReader, () => _runState, OpenConfig);

        _windowSystem.AddWindow(_configWindow);
        _windowSystem.AddWindow(_runWindow);

        PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;

        CommandManager.AddHandler(CommandMain, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Theseus. \"/theseus config\" opens settings.",
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
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config":
            case "settings":
                OpenConfig();
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
