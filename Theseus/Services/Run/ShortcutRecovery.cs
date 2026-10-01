using System;
using System.Numerics;
using Theseus.Services.Frontier;

namespace Theseus.Services.Run;

/// <summary>
/// After a wipe, takes the dungeon's shortcut back to the furthest checkpoint before the route
/// resumes.
///
/// <para>
/// A dungeon puts a shortcut at its entrance once a boss is down, and using it after a wipe is the
/// difference between re-walking every cleared room — and re-doing every one-way drop on the way —
/// and being back at the last boss in seconds. The Ghimlyt Dark, 2026-09-30, showed the cost of not
/// using it: the respawned character walked back to a ledge it had already dropped from and stopped.
/// </para>
///
/// <para>
/// The sequence is AutoDuty's, read from its own revive handler rather than guessed: look for the
/// shortcut for a few seconds after reviving, walk to it, interact until the yes/no prompt appears,
/// answer yes, and count it done once the character has been moved away from where it stood to use
/// it. No shortcut — the first boss is not down yet — is not an error: the route simply resumes
/// from the entrance.
/// </para>
/// </summary>
public sealed class ShortcutRecovery
{
    /// <summary>The shortcut objects AutoDuty knows. The first is the one every dungeon checked so far uses.</summary>
    private static readonly uint[] Shortcuts = [EventObjectFilter.ShortcutObject, EventObjectFilter.SecondShortcutObject];

    /// <summary>How long a shortcut is waited for after reviving. It can take a moment to become targetable.</summary>
    private static readonly TimeSpan SearchFor = TimeSpan.FromSeconds(5);

    /// <summary>Longest the whole attempt may take before the route resumes without it.</summary>
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(1);

    private const float UseRange = 2f;

    /// <summary>Moved this far from where the shortcut was used: it has carried the character.</summary>
    private const float Carried = 5f;

    private readonly IStepWorld _world;

    private DateTime _startedUtc;
    private DateTime _lastActionUtc = DateTime.MinValue;
    private uint _shortcut;
    private Vector3? _usedAt;

    public ShortcutRecovery(IStepWorld world) => _world = world;

    /// <summary>An attempt is under way.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether the last attempt actually took the shortcut. Diagnostic.</summary>
    public bool Used { get; private set; }

    public void Begin()
    {
        Active = true;
        Used = false;
        _startedUtc = _world.UtcNow;
        _lastActionUtc = DateTime.MinValue;
        _shortcut = 0;
        _usedAt = null;
    }

    public void Cancel() => Active = false;

    /// <summary>One frame. Returns true on the frame the attempt ends, used or not.</summary>
    public bool Tick()
    {
        if (!Active)
            return false;

        var now = _world.UtcNow;
        if (now - _startedUtc > GiveUpAfter)
            return Finish(used: false);

        // Taken: the character is somewhere else now.
        if (_usedAt is { } usedAt && Vector3.Distance(_world.PlayerPosition, usedAt) > Carried)
            return Finish(used: true);

        if (!_world.IsReady)
            return false;

        if (_shortcut == 0)
        {
            foreach (var id in Shortcuts)
            {
                if (_world.IsDataIdTargetable(id))
                {
                    _shortcut = id;
                    break;
                }
            }

            if (_shortcut == 0)
                return now - _startedUtc > SearchFor && Finish(used: false);
        }

        if (_world.PositionOfDataId(_shortcut) is not { } at || !_world.IsDataIdTargetable(_shortcut))
            return _usedAt is null && Finish(used: false);

        if (Vector3.Distance(_world.PlayerPosition, at) > UseRange)
        {
            if (!_world.IsMoving && now - _lastActionUtc >= Retry)
            {
                _lastActionUtc = now;
                _world.MoveTo(at);
            }

            return false;
        }

        if (_world.IsAddonVisible("SelectYesno"))
        {
            _world.SelectYesNo(true);
            _usedAt ??= _world.PlayerPosition;
            return false;
        }

        if (now - _lastActionUtc >= Retry)
        {
            _lastActionUtc = now;
            _world.StopMoving();
            _world.TryInteractWithDataId(_shortcut, UseRange + 3f);
        }

        return false;
    }

    private bool Finish(bool used)
    {
        Active = false;
        Used = used;
        return true;
    }
}
