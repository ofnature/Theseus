using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Run;

namespace Theseus.Services.Fleet;

/// <summary>
/// Who is in the fleet, where they are, and which job is this box's.
///
/// <para>
/// Reads entirely from the party list, which is the point: every client sees the same members in
/// the same order, so coordination that looks like it needs messages between boxes mostly does
/// not. Positions are live, the slot order is stable, and both are already on this machine.
/// </para>
///
/// <para>
/// Trust companions and Duty Support NPCs sit in the same list and are filtered out everywhere
/// that matters. They are not peers: they follow the player by themselves, so waiting for one is
/// waiting for something that was never going to arrive under its own steam.
/// </para>
/// </summary>
public sealed class FleetRoster
{
    private readonly IStepWorld _world;

    public FleetRoster(IStepWorld world) => _world = world;

    /// <summary>Every party entry, NPCs included.</summary>
    public IReadOnlyList<FleetMember> Members => _world.PartyMembers;

    /// <summary>Real characters only — the boxes actually being coordinated.</summary>
    public IReadOnlyList<FleetMember> Players
        => _world.PartyMembers.Where(m => m.IsPlayer).ToList();

    /// <summary>
    /// True when there is nobody to coordinate with: solo, or solo with a Trust party. Every gate
    /// checks this first, so a fleet feature can never hold up a single-box run.
    /// </summary>
    public bool IsSolo => Players.Count <= 1;

    /// <summary>
    /// This box leads the party — the only box allowed to queue it. Solo counts as leading, since
    /// there is nobody else to do it.
    /// </summary>
    public bool IsLeader => IsSolo || Players.Any(m => m.IsSelf && m.IsLeader);

    /// <summary>This box's slot among the real players, or -1 when it cannot be found.</summary>
    public int MySlot
    {
        get
        {
            var players = Players;
            for (var i = 0; i < players.Count; i++)
            {
                if (players[i].IsSelf)
                    return i;
            }

            return -1;
        }
    }

    /// <summary>
    /// Whether this box holds a given fleet duty, where duties are numbered from zero.
    ///
    /// <para>
    /// Derived from the slot rather than assigned, so four boxes independently reach the same
    /// answer with nothing passing between them. Solo always holds every duty — there is nobody
    /// else to hold it.
    /// </para>
    /// </summary>
    public bool Holds(int duty)
    {
        if (IsSolo)
            return true;

        var slot = MySlot;
        return slot >= 0 && slot == duty;
    }

    public IReadOnlyList<FleetMember> PlayersWithin(Vector3 point, float radius)
        => Players.Where(m => Vector3.Distance(m.Position, point) <= radius).ToList();
}
