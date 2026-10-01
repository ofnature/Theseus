using System.Numerics;

namespace Theseus.Services.Run;

/// <summary>
/// Notices the character coming back to life somewhere other than where it died.
///
/// <para>
/// A wipe in a dungeon sends the party back to the entrance or a checkpoint, and nothing in the
/// duty's own events is guaranteed to say so: The Ghimlyt Dark, 2026-09-30, a Trust party died on
/// step 6 and respawned at the entrance with no duty-wiped event at all. The route kept its step
/// index, walked the only partial route it had from the entrance — to the top of the ledge it had
/// already dropped from — and faulted there.
/// </para>
///
/// <para>
/// A raise where the character fell is not this: the route picks up where it was, and the step it
/// was on is still the right one. Only a respawn somewhere else means the step index is stale.
/// </para>
/// </summary>
public sealed class RespawnWatch
{
    /// <summary>Further than this from where it died is somewhere else, not a raise on the spot.</summary>
    private const float Elsewhere = 30f;

    private bool _dead;
    private Vector3 _diedAt;

    public void Reset() => _dead = false;

    /// <summary>True once, on the frame the character is alive, ready, and far from where it died.</summary>
    public bool Observe(bool isDead, bool ready, Vector3 position)
    {
        if (isDead)
        {
            if (!_dead)
            {
                _dead = true;
                _diedAt = position;
            }

            return false;
        }

        if (!_dead || !ready)
            return false;

        _dead = false;
        return Vector3.Distance(position, _diedAt) > Elsewhere;
    }
}
