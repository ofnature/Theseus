using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>How making sure of Minerva's auto-dodge turned out.</summary>
public enum DodgeHandoff
{
    /// <summary>The user's preset was applied, and auto-dodge is on.</summary>
    PresetApplied,

    /// <summary>There was no preset; auto-dodge was switched on directly.</summary>
    SwitchedOn,

    /// <summary>It was on before anything was asked, so nothing was touched.</summary>
    AlreadyOn,

    /// <summary>It is off and Minerva would not let us turn it on.</summary>
    Off,

    /// <summary>Nothing could be switched and nothing could be read.</summary>
    Unknown,
}

/// <summary>
/// Minerva, wrapped — boss detection and the AI handoff.
///
/// <para>
/// <b>The handoff is "make sure auto-dodge is on".</b> A preset is one way: <c>ApplyPreset</c>
/// copies a saved preset's settings into the live config, clearance and arc margins included, and
/// is the route to take when the user has made one. <c>SetAutoDodge</c> is the other, added to
/// Minerva for duty runners on 2026-09-20 so that nobody has to create a preset first. Requiring
/// the preset was the original design, from before that gate existed — and in the field nobody had
/// made one, so every run logged a refusal that meant nothing where auto-dodge happened to be on
/// and hid the problem where it was not.
/// </para>
///
/// <para>
/// The slot is claimed and never released. Minerva's release re-applies Default, which turns
/// auto-dodge off — and the AI is the fleet's normal operating state, not something borrowed, the
/// same reason <c>/bmrai off</c> is never sent.
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
    private ICallGateSubscriber<string, bool, bool>? _setAutoDodge;
    private ICallGateSubscriber<bool>? _bossEngaged;
    private ICallGateSubscriber<bool>? _isAutoDodgeEnabled;

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
    /// Whether the active module's boss is fighting, or null when this Minerva cannot say.
    ///
    /// <para>
    /// The module being up is not that: Minerva loads it as soon as the boss exists. Trash pulled
    /// within earshot of a loaded module put the character in combat with the module up, which is
    /// indistinguishable from the boss fight without this.
    /// </para>
    /// </summary>
    public bool? BossEngaged
    {
        get
        {
            try
            {
                return (_bossEngaged ??= _pluginInterface.GetIpcSubscriber<bool>("Minerva.BossEngaged")).InvokeFunc();
            }
            catch
            {
                return null; // an older Minerva: not a warning, just no answer
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

    /// <summary>
    /// Makes sure auto-dodge is on: by the named preset when it exists, directly when it does not,
    /// and without touching anything when it already is.
    ///
    /// <para>
    /// The answer is read back rather than inferred from what the calls returned. A preset can
    /// carry auto-dodge off, and the direct switch is refused while another plugin holds the slot
    /// — in both cases the question that matters is only whether bosses will be dodged.
    /// </para>
    /// </summary>
    public DodgeHandoff EnsureAutoDodge(string preset)
    {
        var applied = !string.IsNullOrWhiteSpace(preset) && ApplyPreset(preset);

        var on = AutoDodgeEnabled;
        if (on == true)
            return applied ? DodgeHandoff.PresetApplied : DodgeHandoff.AlreadyOn;

        var switched = EnableAutoDodge();

        return AutoDodgeEnabled switch
        {
            true => applied ? DodgeHandoff.PresetApplied : DodgeHandoff.SwitchedOn,
            false => DodgeHandoff.Off,

            // Unreadable: a Minerva from before these gates. A preset that applied is then the
            // only evidence there is, and it is the evidence the old design ran on.
            null => applied ? DodgeHandoff.PresetApplied
                : switched ? DodgeHandoff.SwitchedOn
                : DodgeHandoff.Unknown,
        };
    }

    /// <summary>Whether auto-dodge is on, or null when this Minerva cannot say.</summary>
    private bool? AutoDodgeEnabled
    {
        get
        {
            try
            {
                return (_isAutoDodgeEnabled ??= _pluginInterface.GetIpcSubscriber<bool>("Minerva.IsAutoDodgeEnabled"))
                    .InvokeFunc();
            }
            catch
            {
                // Not a warning: an older Minerva is loaded and working, it just lacks the gate.
                return null;
            }
        }
    }

    /// <summary>Switches auto-dodge on directly. False when refused, or when the gate is absent.</summary>
    private bool EnableAutoDodge()
    {
        try
        {
            return (_setAutoDodge ??= _pluginInterface.GetIpcSubscriber<string, bool, bool>("Minerva.SetAutoDodge"))
                .InvokeFunc(Owner, true);
        }
        catch
        {
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
