using System;

namespace Theseus.Services.Run;

/// <summary>
/// The game's own auto-run, engaged the way routes were written to expect it.
///
/// <para>
/// <b>Auto-run goes where the movement mode says, not where the route means.</b> In Standard mode
/// the character runs the way it is facing, which after walking to a waypoint is the direction of
/// travel — and that is the whole design of an <c>AutoMoveFor</c> step: walk to the lip, then keep
/// going. In Legacy mode auto-run follows the <i>camera</i>, so the same step heads wherever the
/// camera happens to point. AutoDuty forces Standard for the length of the run and puts Legacy back
/// afterwards, and every route in the library was recorded under that behaviour.
/// </para>
///
/// <para>
/// The Burn is where it showed: nine seconds of auto-run carry the character down a slope and over
/// a 45-yalm drop into the Scorpion's Den, there is no route on the mesh between the two, and
/// without the mode switch the run ended on the upper level with no way to reach the first boss.
/// </para>
///
/// <para>
/// The setting is the user's, so it is only touched when it has to be and always put back — on
/// every stop, including the ones that happen because a run was cancelled mid-slide.
/// </para>
/// </summary>
public sealed class AutoRun
{
    private const uint Standard = 0;
    private const uint Legacy = 1;

    private readonly Func<uint?> _readMoveMode;
    private readonly Action<uint> _writeMoveMode;
    private readonly Action<string> _sendCommand;
    private readonly Action<string>? _log;

    /// <summary>Legacy was switched off by us and is owed back.</summary>
    private bool _restoreLegacy;

    /// <param name="readMoveMode">The game's movement mode, or null when it cannot be read.</param>
    /// <param name="writeMoveMode">Sets the game's movement mode.</param>
    /// <param name="sendCommand">Sends a chat command.</param>
    public AutoRun(
        Func<uint?> readMoveMode,
        Action<uint> writeMoveMode,
        Action<string> sendCommand,
        Action<string>? log = null)
    {
        _readMoveMode = readMoveMode;
        _writeMoveMode = writeMoveMode;
        _sendCommand = sendCommand;
        _log = log;
    }

    public void Set(bool enabled)
    {
        if (enabled)
        {
            // Asked again while already running must not forget the debt: a second "on" reads
            // Standard — because we set it — and would otherwise conclude there is nothing to restore.
            if (!_restoreLegacy && Read() == Legacy)
            {
                Write(Standard);
                _restoreLegacy = true;
                _log?.Invoke("Auto-run: movement mode set to Standard for the run — Legacy follows the camera.");
            }

            // Explicit on/off, never the bare toggle: as a toggle it would desync from our own idea
            // of the state the first time anything else moved the character.
            _sendCommand("/automove on");
            return;
        }

        _sendCommand("/automove off");

        if (!_restoreLegacy)
            return;

        _restoreLegacy = false;
        Write(Legacy);
    }

    private uint? Read()
    {
        try
        {
            return _readMoveMode();
        }
        catch
        {
            return null; // unreadable is not Legacy: leave the setting alone and just run
        }
    }

    private void Write(uint mode)
    {
        try
        {
            _writeMoveMode(mode);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Auto-run: could not set the movement mode ({ex.GetType().Name}).");
        }
    }
}
