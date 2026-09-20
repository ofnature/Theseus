using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Theseus.Services.Ipc;

/// <summary>
/// What Theseus publishes for other plugins.
///
/// <para>
/// <b><c>Theseus.IsBusy</c></b> — true while a run is driving the character. Daedalus polls this
/// through its <c>AutomationBusyBridge</c> and holds its external-combat override for as long as
/// it reads true, which is how trash gets killed: Theseus owns movement and the route, Daedalus
/// owns the rotation, and this one boolean is the entire handshake between them. It follows the
/// same shape as the bridge's existing sources (<c>Henchman.IsBusy</c>, <c>AutoDuty.IsStopped</c>),
/// so no new mechanism is involved on either side.
/// </para>
///
/// <para>
/// The gate is level-triggered and read about once a second, so it must reflect the truth right
/// now rather than latching. If Theseus crashes or is unloaded mid-run the gate simply stops
/// existing, and Daedalus reads a missing gate as idle and releases the override — the failure
/// mode is "rotation stops", never "rotation runs forever".
/// </para>
///
/// <para>
/// <b><c>Theseus.EnterDuty(uint contentFinderConditionId)</c></b> and <b><c>Theseus.CanEnterDuty</c></b>
/// — for Odysseus. An MSQ quest that contains a dungeon or trial hands it here: Odysseus walks to
/// the quest step, asks Theseus to enter and run the duty through Duty Support, waits on
/// <c>IsBusy</c> to fall, then reads its own quest state to see the sequence moved. Returns false
/// (with the reason logged on this side) when Theseus is disabled, already in a duty, or the entry
/// could not begin.
/// </para>
/// </summary>
public sealed class TheseusIpc : IDisposable
{
    public const string IsBusyGate = "Theseus.IsBusy";
    public const string EnterDutyGate = "Theseus.EnterDuty";
    public const string CanEnterDutyGate = "Theseus.CanEnterDuty";

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<uint, bool> _enterDuty;
    private readonly ICallGateProvider<bool> _canEnterDuty;

    /// <param name="isBusy">
    /// Reads the live run state. Must never throw — an exception here surfaces inside another
    /// plugin's poll.
    /// </param>
    /// <param name="enterDuty">Begins entering and running a duty by ContentFinderCondition id; true when the entry began.</param>
    /// <param name="canEnterDuty">Entry is possible right now: enabled, outside a duty, nothing in flight.</param>
    public TheseusIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy, Func<uint, bool> enterDuty, Func<bool> canEnterDuty)
    {
        _isBusy = pluginInterface.GetIpcProvider<bool>(IsBusyGate);
        _isBusy.RegisterFunc(() =>
        {
            try
            {
                return isBusy();
            }
            catch
            {
                // Fail open to idle, matching how the consumer treats an unavailable gate.
                return false;
            }
        });

        _enterDuty = pluginInterface.GetIpcProvider<uint, bool>(EnterDutyGate);
        _enterDuty.RegisterFunc(cfc =>
        {
            try
            {
                return enterDuty(cfc);
            }
            catch
            {
                return false;
            }
        });

        _canEnterDuty = pluginInterface.GetIpcProvider<bool>(CanEnterDutyGate);
        _canEnterDuty.RegisterFunc(() =>
        {
            try
            {
                return canEnterDuty();
            }
            catch
            {
                return false;
            }
        });
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _enterDuty.UnregisterFunc();
        _canEnterDuty.UnregisterFunc();
    }
}
