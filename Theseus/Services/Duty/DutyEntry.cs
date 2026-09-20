using System;
using System.Collections.Generic;

namespace Theseus.Services.Duty;

public enum DutyEntryStatus
{
    /// <summary>Nothing in flight.</summary>
    Idle,

    /// <summary>Asking the game to open the Duty Support window.</summary>
    Opening,

    /// <summary>The window is up; pressing commence.</summary>
    Registering,

    /// <summary>Answering the commence confirmation.</summary>
    Confirming,

    /// <summary>In the Duty Finder queue, or waiting for the party leader to queue.</summary>
    Queued,

    /// <summary>Registered; waiting for the zone.</summary>
    Loading,

    /// <summary>Inside the duty.</summary>
    Entered,

    /// <summary>Gave up. <see cref="DutyEntry.Detail"/> says why.</summary>
    Faulted,
}

/// <summary>
/// Walks a character into a dungeon from the field.
///
/// <para>
/// Two doors. Alone, through Trust or Duty Support — the only entry a single character can
/// complete by itself. In a party, through the Duty Finder: the leader queues, every member waits
/// for the pop and answers the same commence prompt.
/// </para>
///
/// <para>
/// Every step is idempotent and retried on a throttle rather than fired once and trusted. The
/// window can fail to open because the character was mounted, in a cutscene, or half a second from
/// a loading screen; asking again a second later costs nothing and is the difference between a run
/// that starts and one that sits at a closed menu. The deadlines are what stop that becoming an
/// infinite loop.
/// </para>
///
/// <para>
/// This never touches a duty already in progress. <see cref="Begin"/> refuses outright when the
/// character is inside one — resuming a run from inside a dungeon is the older and more important
/// path, and entering must not be able to interfere with it.
/// </para>
/// </summary>
public sealed class DutyEntry
{
    /// <summary>How often a step that did not take effect is retried.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    /// <summary>Budget for getting the Duty Support window open.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Budget for registering and answering the confirmation once the window is up.</summary>
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Budget for the zone itself. Generous on purpose — this covers a real loading screen on a
    /// slow disk, and giving up early would leave the run idle inside a dungeon it did enter.
    /// </summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Budget for a Duty Finder queue to pop. A full premade pops in seconds; a part-party is
    /// matched with strangers and can take much longer, so this is generous and Stop is the way out.
    /// </summary>
    private static readonly TimeSpan QueueTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Quiet time after pressing Join before the Duty Finder window is touched again. The window
    /// closes on Join a moment before the in-queue flag rises, and reopening it in that gap looks
    /// exactly like a queue that failed.
    /// </summary>
    private static readonly TimeSpan JoinSettle = TimeSpan.FromSeconds(3);

    /// <summary>How long the leader's queue may be gone before it queues again — a declined pop drops it.</summary>
    private static readonly TimeSpan QueueLostGrace = TimeSpan.FromSeconds(10);

    private readonly IDutyEntryWorld _world;
    private readonly Func<IReadOnlyList<byte>> _companions;

    /// <summary>Companions a light party holds beside the player.</summary>
    private const int PartySlots = 3;

    /// <summary>Commence attempts with a hand-picked party before handing the choice back.</summary>
    private const int CustomPartyAttempts = 3;

    private DateTime _deadline = DateTime.MaxValue;
    private DateTime _nextAttempt = DateTime.MinValue;
    private int _registerAttempts;
    private bool _customPartyFielded;
    private bool _partyStaged;
    private string _dutyName = string.Empty;
    private bool _isLeader;
    private DateTime _joinedAt = DateTime.MinValue;
    private DateTime _queueLostSince = DateTime.MaxValue;

    public DutyEntry(IDutyEntryWorld world, Func<IReadOnlyList<byte>>? companions = null)
    {
        _world = world;
        _companions = companions ?? (() => []);
    }

    public DutyEntryStatus Status { get; private set; } = DutyEntryStatus.Idle;

    /// <summary>One line for the run window's status row.</summary>
    public string Detail { get; private set; } = string.Empty;

    /// <summary>The duty being entered, or 0.</summary>
    public uint ContentFinderConditionId { get; private set; }

    /// <summary>
    /// The duty uses Trust rather than Duty Support, which decides every agent call. Sticky for
    /// the whole attempt: switching doors halfway would open one window and register against the
    /// other.
    /// </summary>
    public bool IsTrust { get; private set; }

    public bool IsActive => Status is DutyEntryStatus.Opening or DutyEntryStatus.Registering
        or DutyEntryStatus.Confirming or DutyEntryStatus.Queued or DutyEntryStatus.Loading;

    /// <summary>This attempt queues through the Duty Finder rather than Trust or Duty Support.</summary>
    public bool ViaDutyFinder { get; private set; }

    /// <summary>The duty being looked at lets you choose companions.</summary>
    public bool CanChooseCompanions(bool trust) => _world.CanChooseCompanions(trust);

    /// <summary>Raw companion arrays, for the debug window.</summary>
    public string DescribeCompanionData(bool trust) => _world.DescribeCompanionData(trust);

    /// <summary>The duty can be entered alone through Duty Support or Trust.</summary>
    public bool SupportsDutySupport(uint contentFinderConditionId) => _world.SupportsDutySupport(contentFinderConditionId);

    /// <summary>Companions on offer, or empty when the Trust window is not open.</summary>
    public IReadOnlyList<TrustCompanion> ReadCompanions(uint contentFinderConditionId, bool trust)
        => _world.ReadCompanions(contentFinderConditionId, trust);

    /// <summary>
    /// Opens the Trust window so its roster becomes readable, without entering anything.
    ///
    /// <para>
    /// The roster only exists while the window is up, so from the field there is nothing to show
    /// and no way to show it. This is the way in: open the window the game already uses, read what
    /// it offers, and let the picker fill itself in.
    /// </para>
    /// </summary>
    public void OpenCompanionWindow(uint contentFinderConditionId, bool trust)
    {
        if (IsActive)
            return;

        _world.OpenCompanionWindow(contentFinderConditionId, trust);
        _world.SelectDuty(contentFinderConditionId, trust);
    }

    /// <summary>Points an already-open window at a duty, so the picker reads that duty's roster.</summary>
    public bool SelectDuty(uint contentFinderConditionId, bool trust)
        => _world.SelectDuty(contentFinderConditionId, trust);

    /// <summary>
    /// Starts entering. False means it never began and <paramref name="reason"/> says why — the
    /// preconditions are all things the user can see and fix, so they are worth reporting rather
    /// than failing into a retry loop over.
    /// </summary>
    public bool Begin(uint contentFinderConditionId, bool trust, out string reason)
    {
        if (_world.IsInDuty)
        {
            reason = "Already in a duty.";
            return false;
        }

        if (_world.IsInCombat)
        {
            reason = "In combat.";
            return false;
        }

        if (!_world.DutySupportUnlocked)
        {
            reason = "Duty Support is not unlocked on this character.";
            return false;
        }

        if (!_world.SupportsDutySupport(contentFinderConditionId))
        {
            reason = "That duty has no Duty Support.";
            return false;
        }

        ContentFinderConditionId = contentFinderConditionId;
        IsTrust = trust;
        ViaDutyFinder = false;
        _registerAttempts = 0;
        _customPartyFielded = false;
        _partyStaged = false;
        Status = DutyEntryStatus.Opening;
        Detail = "Opening Duty Support.";
        _deadline = _world.UtcNow + OpenTimeout;
        _nextAttempt = DateTime.MinValue;
        reason = Detail;
        return true;
    }

    /// <summary>
    /// Starts entering through the Duty Finder, as a party.
    ///
    /// <para>
    /// Only the party leader can queue a party, so only the leader touches the Duty Finder. Every
    /// other box goes straight to waiting: the queue pops for the whole party at once, and a member
    /// pressing Join itself would queue alone. Members answer the same commence prompt the leader
    /// does, which the shared confirmation handling already covers.
    /// </para>
    /// </summary>
    public bool BeginDutyFinder(uint contentFinderConditionId, string dutyName, bool isLeader, out string reason)
    {
        if (_world.IsInDuty)
        {
            reason = "Already in a duty.";
            return false;
        }

        if (_world.IsInCombat)
        {
            reason = "In combat.";
            return false;
        }

        if (!_world.DutyFinderUnlocked)
        {
            reason = "The Duty Finder is not available on this character.";
            return false;
        }

        ContentFinderConditionId = contentFinderConditionId;
        ViaDutyFinder = true;
        IsTrust = false;
        _dutyName = dutyName;
        _isLeader = isLeader;
        _joinedAt = DateTime.MinValue;
        _queueLostSince = DateTime.MaxValue;
        _nextAttempt = DateTime.MinValue;

        if (isLeader && !_world.IsInDutyQueue)
        {
            Status = DutyEntryStatus.Opening;
            Detail = "Opening the Duty Finder.";
            _deadline = _world.UtcNow + OpenTimeout;
        }
        else
        {
            EnterQueued();
        }

        reason = Detail;
        return true;
    }

    public void Cancel(string reason = "Entry cancelled.")
    {
        if (Status is DutyEntryStatus.Idle)
            return;

        Status = DutyEntryStatus.Idle;
        Detail = reason;
        ContentFinderConditionId = 0;
        _deadline = DateTime.MaxValue;
    }

    public void Tick()
    {
        if (!IsActive)
            return;

        // Checked ahead of everything: the goal is being inside, and any stage reaching it is done.
        // The confirmation can be answered by the player, or the zone can start while we are still
        // deciding what to press, and neither of those should read as a failure.
        if (_world.IsInDuty)
        {
            Status = DutyEntryStatus.Entered;
            Detail = "Entered the duty.";
            return;
        }

        // A loading screen swallows every addon, so once it starts there is nothing to do but wait,
        // and the earlier stages' short deadlines would fire in the middle of it.
        if (_world.IsBetweenAreas)
        {
            if (Status != DutyEntryStatus.Loading)
            {
                Status = DutyEntryStatus.Loading;
                Detail = "Loading into the duty.";
                _deadline = _world.UtcNow + LoadTimeout;
            }

            return;
        }

        if (_world.UtcNow > _deadline)
        {
            Fault(Status switch
            {
                DutyEntryStatus.Loading => "Registered, but the duty never loaded.",
                DutyEntryStatus.Queued => "The Duty Finder queue never popped.",
                DutyEntryStatus.Opening when ViaDutyFinder => "The Duty Finder would not open on that duty.",
                _ when ViaDutyFinder => "The Duty Finder would not queue that duty — is it unlocked?",
                DutyEntryStatus.Opening => "Duty Support would not open.",
                _ => "Duty Support did not accept the registration.",
            });
            return;
        }

        // Cutscenes and NPC dialog eat clicks. Waiting costs a deadline, not a wrong press.
        if (_world.IsOccupied)
            return;

        // The confirmation can appear at any point after commence, and answering it is always the
        // right move, so it is handled before the stage switch rather than inside one branch.
        if (_world.IsConfirmVisible)
        {
            if (!Throttled())
            {
                Status = DutyEntryStatus.Confirming;
                Detail = "Confirming.";
                _world.ConfirmQueue();
            }

            return;
        }

        if (ViaDutyFinder)
        {
            TickDutyFinder();
            return;
        }

        switch (Status)
        {
            case DutyEntryStatus.Opening:
                if (_world.IsCompanionWindowReady(IsTrust))
                {
                    Status = DutyEntryStatus.Registering;
                    Detail = "Registering.";
                    _deadline = _world.UtcNow + RegisterTimeout;
                    _nextAttempt = DateTime.MinValue;
                    return;
                }

                if (!Throttled())
                    _world.OpenCompanionWindow(ContentFinderConditionId, IsTrust);

                return;

            case DutyEntryStatus.Registering:
            case DutyEntryStatus.Confirming:
                // The window can close under us — a stray Escape, or the game reopening it — in
                // which case there is nothing to register against and we go back for it.
                if (!_world.IsCompanionWindowReady(IsTrust))
                {
                    Status = DutyEntryStatus.Opening;
                    Detail = "Re-opening Duty Support.";
                    _partyStaged = false; // a fresh window has not seen the party
                    _deadline = _world.UtcNow + OpenTimeout;
                    _nextAttempt = DateTime.MinValue;
                    return;
                }

                if (!Throttled())
                {
                    Status = DutyEntryStatus.Registering;

                    // The window's party is what the commence uses, and the window needs a beat to
                    // take a write — staging and registering in the same instant enters with
                    // whatever party it showed before. So the first pass sets the party and stops;
                    // the commence goes out on the next tick, a full throttle later.
                    if (!_partyStaged)
                    {
                        Detail = "Setting the party.";
                        _world.SelectDuty(ContentFinderConditionId, IsTrust);
                        ChooseCompanions();
                        _partyStaged = true;
                        return;
                    }

                    Detail = "Registering.";

                    // Reaching this line again means the last commence was refused — the game
                    // answers with a chat error and leaves the window open. A hand-picked party is
                    // the usual reason ("Role requirements unmet"), so after a few refusals the
                    // choice goes back to the game rather than slamming into the same wall until
                    // the deadline faults the whole entry.
                    _registerAttempts++;
                    if (_customPartyFielded && _registerAttempts > CustomPartyAttempts)
                    {
                        _customPartyFielded = false;
                        _world.Log("The game refused the chosen Trust party — letting it pick instead.");
                        _world.SetDefaultCompanions(IsTrust);
                        return; // the default party gets its settle tick too
                    }

                    _world.RegisterForDuty(IsTrust);
                }

                return;
        }
    }

    /// <summary>
    /// The leader's Duty Finder sequence: open on the duty, make it the only one ticked, Join, then
    /// wait. Mirrors the order AutoDuty's own queue uses, including clearing a stale selection
    /// before ticking — the window remembers what was queued last, and Join queues all of it.
    /// </summary>
    private void TickDutyFinder()
    {
        switch (Status)
        {
            case DutyEntryStatus.Opening:
                if (_world.IsInDutyQueue)
                {
                    EnterQueued();
                    return;
                }

                if (_world.UtcNow - _joinedAt < JoinSettle)
                    return;

                if (_world.IsDutyFinderReady
                    && _world.DutyFinderHighlightedDuty == ContentFinderConditionId)
                {
                    Status = DutyEntryStatus.Registering;
                    Detail = "Selecting the duty.";
                    _deadline = _world.UtcNow + RegisterTimeout;
                    _nextAttempt = DateTime.MinValue;
                    return;
                }

                if (!Throttled())
                    _world.OpenDutyFinder(ContentFinderConditionId);

                return;

            case DutyEntryStatus.Registering:
                if (_world.IsInDutyQueue)
                {
                    EnterQueued();
                    return;
                }

                // Join was just pressed and the queue flag has not caught up — pressing it again in
                // that gap would queue twice, or toggle straight back out.
                if (_world.UtcNow - _joinedAt < JoinSettle)
                    return;

                if (!_world.IsDutyFinderReady
                    || _world.DutyFinderHighlightedDuty != ContentFinderConditionId)
                {
                    Status = DutyEntryStatus.Opening;
                    Detail = "Re-opening the Duty Finder.";
                    _deadline = _world.UtcNow + OpenTimeout;
                    return;
                }

                if (Throttled())
                    return;

                var ticked = _world.DutyFinderTickedName;
                if (string.IsNullOrWhiteSpace(ticked))
                {
                    Detail = "Selecting the duty.";
                    _world.DutyFinderTickHighlighted();
                }
                else if (!SameDutyName(ticked, _dutyName))
                {
                    Detail = $"Clearing \"{ticked}\" from the Duty Finder.";
                    _world.DutyFinderClearSelection();
                }
                else
                {
                    Detail = "Joining the queue.";
                    _world.DutyFinderJoin();
                    _joinedAt = _world.UtcNow;
                }

                return;

            case DutyEntryStatus.Confirming:
            case DutyEntryStatus.Queued:
                if (Status == DutyEntryStatus.Confirming)
                    EnterQueued();

                // Members have no queue of their own to watch; the leader's pop reaches them as the
                // same commence prompt, handled before this switch.
                if (!_isLeader)
                    return;

                if (_world.IsInDutyQueue)
                {
                    _queueLostSince = DateTime.MaxValue;
                    return;
                }

                // Out of the queue without entering: someone declined the pop, or it was withdrawn.
                // Queue again rather than sit idle — but give the in-queue flag a moment first.
                if (_queueLostSince == DateTime.MaxValue)
                {
                    _queueLostSince = _world.UtcNow;
                }
                else if (_world.UtcNow - _queueLostSince > QueueLostGrace)
                {
                    Status = DutyEntryStatus.Opening;
                    Detail = "Queue dropped — queueing again.";
                    _deadline = _world.UtcNow + OpenTimeout;
                    _queueLostSince = DateTime.MaxValue;
                }

                return;
        }
    }

    private void EnterQueued()
    {
        Status = DutyEntryStatus.Queued;
        Detail = _isLeader ? "In the Duty Finder queue." : "Waiting for the party leader to queue.";
        _deadline = _world.UtcNow + QueueTimeout;
        _queueLostSince = DateTime.MaxValue;
    }

    /// <summary>
    /// Whether the Duty Finder's ticked name is the duty asked for. Compared on letters and digits
    /// only: the addon's text carries formatting codes and swaps hyphens for dashes, and the sheet
    /// name does neither.
    /// </summary>
    internal static bool SameDutyName(string shown, string wanted)
        => Normalize(shown) == Normalize(wanted);

    private static string Normalize(string name)
    {
        var chars = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
                chars.Append(char.ToLowerInvariant(c));
        }

        return chars.ToString();
    }

    /// <summary>
    /// Fills the Trust party before commencing, when the duty lets you choose.
    ///
    /// <para>
    /// Falls back to the game's own default party whenever the requested one cannot be built —
    /// nobody chosen, a companion no longer offered, a party the game rejects. A Trust run with a
    /// party someone did not pick is a small annoyance; a run that will not start because one
    /// companion is locked behind a story step is a stopped fleet.
    /// </para>
    /// </summary>
    private void ChooseCompanions()
    {
        if (!_world.CanChooseCompanions(IsTrust))
            return;

        var wanted = _companions();

        // Only a full hand is worth playing. The commence needs a complete light party, so two
        // picks registered as-is draw "Role requirements unmet" — a partial choice falls back to
        // the game's party rather than entering short-handed.
        if (wanted.Count == PartySlots && _world.SetCompanions(wanted, IsTrust))
        {
            _customPartyFielded = true;
            return;
        }

        if (wanted.Count > 0)
        {
            _world.Log(wanted.Count == PartySlots
                ? "Could not field the chosen Trust party — letting the game pick."
                : $"Trust party has {wanted.Count} of {PartySlots} picks — letting the game pick.");
        }

        _customPartyFielded = false;
        _world.SetDefaultCompanions(IsTrust);
    }

    /// <summary>
    /// True when the last attempt was too recent to repeat. Advances the clock as a side effect,
    /// so a caller that gets false has claimed this attempt.
    /// </summary>
    private bool Throttled()
    {
        var now = _world.UtcNow;
        if (now < _nextAttempt)
            return true;

        _nextAttempt = now + RetryInterval;
        return false;
    }

    private void Fault(string reason)
    {
        Status = DutyEntryStatus.Faulted;
        Detail = reason;
        _deadline = DateTime.MaxValue;
        _world.Log(reason);
    }
}
