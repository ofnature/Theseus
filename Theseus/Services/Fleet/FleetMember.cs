using System.Numerics;

namespace Theseus.Services.Fleet;

/// <summary>
/// One party member as the fleet sees them.
/// </summary>
/// <param name="Slot">
/// Position in the party list. Identical on every client, which is what makes it usable as a
/// role: "slot 1 fetches the key" needs no election and no messages between boxes.
/// </param>
/// <param name="IsPlayer">
/// A real character rather than a Trust companion or Duty Support NPC. The distinction decides
/// whether anyone is worth waiting for — NPCs follow you by themselves, so a gate that counted
/// them would hold forever for allies that were never going to arrive on their own.
/// </param>
public readonly record struct FleetMember(
    ulong ContentId,
    string Name,
    int Slot,
    Vector3 Position,
    bool IsSelf,
    bool IsPlayer,
    bool IsLeader = false);
