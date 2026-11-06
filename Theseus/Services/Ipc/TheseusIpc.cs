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
///
/// <para>
/// <b><c>Theseus.RequestStopAfterRun</c></b> — for Argus, before it moves the character to resend
/// the FC's vessels. Theseus finishes the run it is in (clears the duty and leaves it as usual),
/// then stays stopped; it never abandons a duty. True when it was busy and will stop, or was
/// already asked; false, with nothing changed, when idle. The caller then waits on <c>IsBusy</c>
/// to fall. The request clears once Theseus is idle, so a later start is not refused. The same
/// contract is published by every automation plugin in the suite.
/// </para>
///
/// <para>
/// <c>IsBusy</c> is true while a run is entering, running, fighting or exiting, and in the pause
/// between an entry we were asked for landing and the run beginning by itself at the barrier.
/// </para>
/// </summary>
public sealed class TheseusIpc : IDisposable
{
    public const string IsBusyGate = "Theseus.IsBusy";
    public const string EnterDutyGate = "Theseus.EnterDuty";
    public const string CanEnterDutyGate = "Theseus.CanEnterDuty";
    public const string RequestStopAfterRunGate = "Theseus.RequestStopAfterRun";

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<uint, bool> _enterDuty;
    private readonly ICallGateProvider<bool> _canEnterDuty;
    private readonly ICallGateProvider<bool> _requestStopAfterRun;

    /// <param name="isBusy">
    /// Reads the live run state. Must never throw — an exception here surfaces inside another
    /// plugin's poll.
    /// </param>
    /// <param name="enterDuty">Begins entering and running a duty by ContentFinderCondition id; true when the entry began.</param>
    /// <param name="canEnterDuty">Entry is possible right now: enabled, outside a duty, nothing in flight.</param>
    /// <param name="requestStopAfterRun">Asks the run in progress to be the last; true when there was one.</param>
    public TheseusIpc(
        IDalamudPluginInterface pluginInterface,
        Func<bool> isBusy,
        Func<uint, bool> enterDuty,
        Func<bool> canEnterDuty,
        Func<bool> requestStopAfterRun)
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

        _requestStopAfterRun = pluginInterface.GetIpcProvider<bool>(RequestStopAfterRunGate);
        _requestStopAfterRun.RegisterFunc(() =>
        {
            try
            {
                return requestStopAfterRun();
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
        _requestStopAfterRun.UnregisterFunc();
    }
}
