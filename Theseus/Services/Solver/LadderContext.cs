using System;
using System.Numerics;

namespace Theseus.Services.Solver;

/// <summary>Which rung of §6.2's ladder the solver is on.</summary>
public enum LadderRung
{
    /// <summary>Not climbing: something is happening.</summary>
    None,

    /// <summary>Rung 1: look twice as far, and re-ask every locked edge.</summary>
    Widen,

    /// <summary>Rung 2: is a fleet peer already past something we think is shut?</summary>
    Fleet,

    /// <summary>Rung 3: give the objects that failed once another go.</summary>
    Retry,

    /// <summary>Rung 4: is there an authored way out written down for this spot?</summary>
    Override,

    /// <summary>Rung 5: hand the run back, named.</summary>
    HandBack,
}

/// <summary>
/// What the ladder needs from the rest of Theseus. Each is a question the driver asks at most once
/// per climb, and each answers with what it did — the count is what the log line reports, and zero
/// is a perfectly good answer that moves the climb on to the next rung.
/// </summary>
/// <param name="FleetSweep">
/// Rung 2. Returns how many edges a fleet peer's own position says are open — observation, not
/// messages, since the party list already says where everybody is standing.
/// </param>
/// <param name="RetryInteractables">Rung 3. Returns how many failed objects were given another go.</param>
/// <param name="OverrideWaypoint">Rung 4. The authored escape covering this spot, if there is one.</param>
public sealed record LadderContext(
    Func<int> FleetSweep,
    Func<int> RetryInteractables,
    Func<Vector3?> OverrideWaypoint);
