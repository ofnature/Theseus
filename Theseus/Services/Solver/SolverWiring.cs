using System;

namespace Theseus.Services.Solver;

/// <summary>
/// Everything the solver needs to exist, in one argument, so the run controller takes one field
/// instead of six parameters it only passes through.
///
/// <para>
/// The pieces are separated because they are used apart: the perception runs under every driver,
/// the driver only under the solver's own, and the record is written once a run and read once at
/// its start. <see cref="Usable"/> is the honest question — a connected Ariadne with a grid answer
/// and the user's switch on — and everything downstream degrades to the frontier navigator when it
/// answers no.
/// </para>
/// </summary>
public sealed record SolverWiring(
    ShadowObserver Perception,
    Arbiter Arbiter,
    SolverDriver Driver,
    SolverRecordStore Records,
    string RecordsPath,
    Func<bool> Usable)
{
    /// <summary>Counts a run for the promotion record: driven to the end, or given back.</summary>
    public void NoteRun(uint territory, bool fellBack, DateTime utcNow)
    {
        var record = Records.For(territory);
        record.LastUtc = utcNow;

        if (fellBack)
            record.SolverFallbacks++;
        else
            record.SolverRuns++;

        Records.Save(RecordsPath);
    }
}
