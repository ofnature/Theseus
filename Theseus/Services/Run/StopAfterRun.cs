namespace Theseus.Services.Run;

/// <summary>
/// Another plugin's request that Theseus give the character back once the run it is in is over.
///
/// <para>
/// Argus asks before it moves the character to resend the FC's vessels: every automation plugin
/// finishes what it is doing, and Argus goes once they all read idle. A run already ends on its
/// own — Theseus never queues its next one — so the request changes nothing about how this run
/// finishes. What it adds is the answer the caller needs, and that it cannot outlive the run: it
/// clears the moment Theseus is idle, so a later start, by hand or through <c>EnterDuty</c>, is
/// never refused or cut short by a request that was already honoured.
/// </para>
/// </summary>
public sealed class StopAfterRun
{
    /// <summary>A request is waiting for the current run to end.</summary>
    public bool Pending { get; private set; }

    /// <summary>
    /// True when Theseus was busy and will stop after this run, including when it was already
    /// asked. False, with nothing changed, when it is idle: there is no run to stop after, and a
    /// flag left behind would stop the next one.
    /// </summary>
    public bool Request(bool busy)
    {
        if (!busy)
            return false;

        Pending = true;
        return true;
    }

    /// <summary>Clears a pending request once Theseus is idle. True on the tick it was honoured.</summary>
    public bool Settle(bool busy)
    {
        if (!Pending || busy)
            return false;

        Pending = false;
        return true;
    }
}
