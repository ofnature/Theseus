using System;
using System.Numerics;

namespace Theseus.Services.Run;

/// <summary>
/// Notices the character going down something and coming to rest: a candidate crossing.
///
/// <para>
/// Deliberately indifferent to what moved the character. A route's auto-run, the mover stepping
/// off an edge, a person playing by hand, a cutscene that cancelled the auto-run and left the
/// character to walk off the lip anyway — each is the same event to the mesh, and every one is
/// evidence of where the zone lets a character go that the mesh does not.
/// </para>
///
/// <para>
/// This only proposes. Stairs and steep ramps descend too, so every candidate is checked against
/// the mesh before it is reported: a descent the mesh can route on foot is an ordinary walk. The
/// watch can therefore be generous about what it proposes, and it is.
/// </para>
/// </summary>
public sealed class CrossingWatch
{
    /// <summary>A descent at least this deep is worth asking the mesh about.</summary>
    public const float DropThreshold = 2.5f;

    /// <summary>Height change within which the character counts as still on the level it was on.</summary>
    private const float LevelTolerance = 0.5f;

    /// <summary>Movement below which the character counts as still.</summary>
    private const float StillMovement = 0.25f;

    /// <summary>Stillness that counts as having come to rest.</summary>
    private static readonly TimeSpan StillFor = TimeSpan.FromMilliseconds(400);

    /// <summary>A jump this far in one frame is a warp or a zone load, not something walked.</summary>
    private const float Teleport = 15f;

    /// <summary>A descent still going after this long is abandoned: no crossing takes that long.</summary>
    private static readonly TimeSpan LongestDescent = TimeSpan.FromSeconds(30);

    private bool _anchored;
    private Vector3 _anchor;
    private Vector3 _last;
    private Vector3 _stillAt;
    private DateTime _stillSince;
    private bool _descending;
    private DateTime _descentStarted;

    /// <summary>Forgets everything, as on leaving a duty.</summary>
    public void Reset()
    {
        _anchored = false;
        _descending = false;
    }

    /// <summary>
    /// One frame. Returns a candidate crossing — the last level point before the descent, and where
    /// the character came to rest — at most once per descent.
    /// </summary>
    /// <param name="ready">Not between areas, not in a cutscene that owns the character.</param>
    /// <param name="inCombat">A descent that begins in combat is a knockback, not a route.</param>
    public (Vector3 From, Vector3 To)? Observe(DateTime now, Vector3 position, bool ready, bool inCombat)
    {
        if (!ready)
        {
            Reset();
            return null;
        }

        if (!_anchored || Vector3.Distance(position, _last) > Teleport)
        {
            _anchored = true;
            _descending = false;
            _anchor = position;
            _stillAt = position;
            _stillSince = now;
            _last = position;
            return null;
        }

        _last = position;

        if (Vector3.Distance(position, _stillAt) > StillMovement)
        {
            _stillAt = position;
            _stillSince = now;
        }

        if (!_descending)
        {
            // The anchor follows the character across level ground and up; it stays behind as soon
            // as the character starts down, which is what leaves it at the lip.
            if (position.Y >= _anchor.Y - LevelTolerance || inCombat)
            {
                _anchor = position;
                return null;
            }

            if (position.Y <= _anchor.Y - DropThreshold)
            {
                _descending = true;
                _descentStarted = now;
            }

            return null;
        }

        // Back up above where it started: whatever that was, it was not a crossing.
        if (position.Y > _anchor.Y + LevelTolerance)
        {
            _descending = false;
            _anchor = position;
            return null;
        }

        if (now - _descentStarted > LongestDescent)
        {
            _descending = false;
            _anchor = position;
            return null;
        }

        if (now - _stillSince < StillFor)
            return null;

        var from = _anchor;
        _descending = false;
        _anchor = position;
        return (from, position);
    }
}
