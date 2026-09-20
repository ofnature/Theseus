using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>
/// BossMod Reborn, wrapped — boss detection and the AI handoff.
///
/// <para>
/// <b>There is no IPC to turn the AI on or off.</b> Verified against BossMod's own IPC provider:
/// the AI's follow/idle switches are reachable only from the <c>/bmrai</c> chat command, the DTR
/// click handler and the AI debug window. So the boss handoff has to be a chat command, and it is
/// wrapped here rather than scattered through the executor — if BossMod ever renames it there is
/// one line to change.
/// </para>
/// </summary>
public sealed class BossModIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string> _sendChatCommand;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<bool>? _hasActiveModule;
    private ICallGateSubscriber<string?>? _activeModuleName;
    private ICallGateSubscriber<uint, bool>? _hasModuleByDataId;
    private ICallGateSubscriber<string, object>? _setPreset;
    private ICallGateSubscriber<string?>? _getPreset;

    private bool _warned;

    public BossModIpc(IDalamudPluginInterface pluginInterface, Action<string> sendChatCommand, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _sendChatCommand = sendChatCommand;
        _log = log;
    }

    /// <summary>
    /// A boss module's state machine is running.
    ///
    /// <para>
    /// Goes false→true on the actual pull and true→false when the encounter ends — but it ends the
    /// same way for a clear and for a wipe. Pair it with the objective delta to tell them apart;
    /// see <c>EncounterClassifier</c>.
    /// </para>
    /// </summary>
    public bool HasActiveModule => Try(() =>
        (_hasActiveModule ??= _pluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule")).InvokeFunc());

    /// <summary>Name of the active boss, for logs and the run window.</summary>
    public string ActiveModuleName => Try(() =>
        (_activeModuleName ??= _pluginInterface.GetIpcSubscriber<string?>("BossMod.ActiveModuleName")).InvokeFunc()
        ?? string.Empty) ?? string.Empty;

    /// <summary>Whether an actor has a boss module at all — checked before handing off to it.</summary>
    public bool HasModuleForDataId(uint dataId) => Try(() =>
        (_hasModuleByDataId ??= _pluginInterface.GetIpcSubscriber<uint, bool>("BossMod.HasModuleByDataId"))
        .InvokeFunc(dataId));

    /// <summary>Turns the AI on or off around a boss. Chat command by necessity, not by choice.</summary>
    public void SetAiEnabled(bool enabled) => _sendChatCommand($"/bmrai {(enabled ? "on" : "off")}");

    /// <summary>
    /// Selects an AI preset by name, then reads it back.
    ///
    /// <para>
    /// The read-back is not defensive padding. BossMod's <c>AI.SetPreset</c> resolves the name
    /// against its preset list and calls the setter with whatever it found — including null. A
    /// name that does not match therefore <b>clears the active preset</b> rather than failing, and
    /// reports nothing at all. Setting and not verifying would leave the AI running with no preset
    /// in the middle of a boss.
    /// </para>
    /// </summary>
    /// <returns>Whether the preset is actually active afterwards.</returns>
    public bool TrySetPreset(string name)
    {
        Try(() =>
        {
            (_setPreset ??= _pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset"))
                .InvokeAction(name);
            return true;
        });

        var active = ActivePreset;
        if (string.Equals(active, name, StringComparison.OrdinalIgnoreCase))
            return true;

        _log?.Invoke(
            $"BossMod did not accept the AI preset \"{name}\" (now: \"{active}\"). " +
            "Default presets are hidden by default, so a stock name will not resolve.");
        return false;
    }

    /// <summary>The AI preset currently selected, or empty.</summary>
    public string ActivePreset => Try(() =>
        (_getPreset ??= _pluginInterface.GetIpcSubscriber<string?>("BossMod.AI.GetPreset")).InvokeFunc()
        ?? string.Empty) ?? string.Empty;

    private T? Try<T>(Func<T> call)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"BossMod unavailable ({ex.GetType().Name}) — bosses will not be handed off.");
            }

            return default;
        }
    }

    private bool Try(Func<bool> call) => Try<bool>(call);
}
