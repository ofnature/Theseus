using Theseus.Services.Solver;

namespace Theseus.Services.Solver;

/// <summary>
/// The two things a driver needs from perception: the world as of this tick, and permission to look
/// further. Narrow on purpose — the driver is not allowed to reach into the taxonomy or the ledger
/// and start making up its own answers.
/// </summary>
public interface IPerception
{
    /// <summary>This tick's world, or null before the first tick and between duties.</summary>
    WorldModel.Snapshot? LastSnapshot { get; }

    /// <summary>Ladder rung 1: look further, and re-ask everything worth asking again.</summary>
    void Widen();
}
