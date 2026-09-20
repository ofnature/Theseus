using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>
/// What Theseus calls on Daedalus. Every member fails open — a missing or older Daedalus is a
/// degraded feature, never an exception on our side.
/// </summary>
public sealed class DaedalusIpc
{
    private const string RecordExternalWriteGate = "Daedalus.Targeting.RecordExternalWrite";
    private const string SetEnabledGate = "Daedalus.SetEnabled";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _logDegraded;

    private ICallGateSubscriber<ulong, object>? _recordExternalWrite;
    private ICallGateSubscriber<bool, object>? _setEnabled;
    private bool _warnedMissing;

    public DaedalusIpc(IDalamudPluginInterface pluginInterface, Action<string>? logDegraded = null)
    {
        _pluginInterface = pluginInterface;
        _logDegraded = logDegraded;
    }

    /// <summary>
    /// Tells Daedalus that the hard-target write we are about to make is automation, not the user.
    ///
    /// <para>
    /// Daedalus arms a four-second "hands off the wheel" grace whenever the hard target changes
    /// without one of its own writers claiming it, and holds its movement pulses for the duration.
    /// Its internal claim method is <c>public static</c>, so it is reachable in-process only —
    /// which means that without this call every target Theseus sets looks exactly like the user
    /// clicking a mob, and Daedalus quietly stops moving for four seconds each time. Nothing errors
    /// and nothing logs; the run just walks like it is limping.
    /// </para>
    ///
    /// <para>Call immediately before the write: the claim is only honoured for about a second.</para>
    /// </summary>
    public void RecordTargetWrite(ulong gameObjectId)
    {
        if (gameObjectId == 0)
            return;

        try
        {
            _recordExternalWrite ??= _pluginInterface.GetIpcSubscriber<ulong, object>(RecordExternalWriteGate);
            _recordExternalWrite.InvokeAction(gameObjectId);
            _warnedMissing = false;
        }
        catch (Exception ex)
        {
            // Daedalus absent, or too old to have the endpoint. The run still works; movement just
            // gets choppier around retargets, so say it once rather than every target change.
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                _logDegraded?.Invoke(
                    $"{RecordExternalWriteGate} unavailable ({ex.GetType().Name}) — Daedalus will " +
                    "read our retargets as manual clicks and hold its movement pulses.");
            }
        }
    }

    /// <summary>
    /// Turns Daedalus's rotation on or off — what a route's <c>Rotation</c> steps mean.
    ///
    /// <para>
    /// Distinct from the <c>Theseus.IsBusy</c> gate, which asks Daedalus to fight <i>during</i> a
    /// run. This is the user's own master switch, and routes toggle it around sections where
    /// attacking is wrong. Failing open here means the rotation simply keeps doing whatever it was
    /// already doing, which is the safe direction.
    /// </para>
    /// </summary>
    public void SetRotationEnabled(bool enabled)
    {
        try
        {
            _setEnabled ??= _pluginInterface.GetIpcSubscriber<bool, object>(SetEnabledGate);
            _setEnabled.InvokeAction(enabled);
        }
        catch (Exception ex)
        {
            _logDegraded?.Invoke($"{SetEnabledGate} unavailable ({ex.GetType().Name}) — rotation left as-is.");
        }
    }
}
