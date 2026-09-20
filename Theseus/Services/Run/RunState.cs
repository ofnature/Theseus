namespace Theseus.Services.Run;

/// <summary>
/// The run state machine from the plan doc. Movement and combat ownership is defined per state
/// and must stay unambiguous — that table is the contract:
///
/// <code>
/// State          Movement          Trash kills        Boss mechanics
/// Running        Theseus (vnav)    —                  —
/// Combat         Theseus / hold    Daedalus rotation  —
/// BossHandoff    BossMod AI        Daedalus rotation  BossMod AI
/// Recover        Theseus (vnav)    Daedalus           —
/// </code>
///
/// BossMod suppresses its own movement automatically while a vnavmesh path is running, so the
/// only explicit toggle is <c>/bmrai on|off</c> around the boss (plan doc findings #2 and #3).
/// </summary>
public enum RunState
{
    /// <summary>Nothing in flight.</summary>
    Idle,

    /// <summary>Queueing for, or zoning into, the duty.</summary>
    Entering,

    /// <summary>Walking the path.</summary>
    Running,

    /// <summary>Trash engaged; the rotation plugin is killing it.</summary>
    Combat,

    /// <summary>A boss module is active and BossMod AI owns the fight.</summary>
    BossHandoff,

    /// <summary>Collecting chests / finishing objectives before the exit.</summary>
    Loot,

    /// <summary>Leaving the instance.</summary>
    Exiting,

    /// <summary>Between-run chores: repair, materia extraction, optional Charon upgrades.</summary>
    Chores,

    /// <summary>
    /// Off the rails — the live objective disagrees with the step we think we are on, or the
    /// path failed. Relocalize via the Thread instead of continuing to walk a path that no
    /// longer describes reality.
    /// </summary>
    Recover,

    /// <summary>Stopped and waiting for the user; the reason is on the run status line.</summary>
    Faulted,
}

public static class RunStateExtensions
{
    /// <summary>
    /// Theseus is driving the character. This is the value published on <c>Theseus.IsBusy</c>, so
    /// it decides when Daedalus fights for us — every state that moves, loots or chores counts,
    /// and only the two states where the character is under nobody's control do not.
    /// </summary>
    public static bool IsDriving(this RunState state)
        => state is not (RunState.Idle or RunState.Faulted);
}
