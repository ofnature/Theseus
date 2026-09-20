using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>
/// Minerva, wrapped — boss detection and the AI handoff.
///
/// <para>
/// <b>Minerva has no command or setter for its AI.</b> Auto-dodge is reachable only from its own UI,
/// or by applying a dodge preset through <c>Minerva.ApplyPreset</c> — which copies the preset's
/// auto-dodge flag into the live config. So the handoff is "claim a preset the user created with
/// auto-dodge on". The built-in Default preset has it off, which is why a name must be configured.
/// </para>
///
/// <para>
/// The slot is claimed and never released. Minerva's <c>ReleasePreset</c> re-applies Default, which
/// turns auto-dodge off — and the AI is the fleet's normal operating state, not something borrowed,
/// the same reason <c>/bmrai off</c> is never sent.
/// </para>
/// </summary>
public sealed class MinervaIpc
{
    /// <summary>Owner name shown in Minerva while Theseus holds the preset slot.</summary>
    private const string Owner = "Theseus";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<string>? _activeModule;
    private ICallGateSubscriber<string, string, bool>? _applyPreset;

    private bool _warned;

    public MinervaIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>A boss module is running — Minerva publishes the active module's name, empty when none.</summary>
    public bool HasActiveModule
    {
        get
        {
            try
            {
                var name = (_activeModule ??= _pluginInterface.GetIpcSubscriber<string>("Minerva.ActiveModule"))
                    .InvokeFunc();
                _warned = false;
                return !string.IsNullOrEmpty(name);
            }
            catch (Exception ex)
            {
                Warn(ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Claims a dodge preset. False when Minerva is absent or has no preset by that name — create it in
    /// Minerva with auto-dodge on.
    /// </summary>
    public bool ApplyPreset(string name)
    {
        try
        {
            var applied = (_applyPreset ??= _pluginInterface.GetIpcSubscriber<string, string, bool>("Minerva.ApplyPreset"))
                .InvokeFunc(name, Owner);
            _warned = false;
            return applied;
        }
        catch (Exception ex)
        {
            Warn(ex);
            return false;
        }
    }

    private void Warn(Exception ex)
    {
        if (_warned)
            return;

        _warned = true;
        _log?.Invoke($"Minerva unavailable ({ex.GetType().Name}) — boss handling is off until it loads.");
    }
}
