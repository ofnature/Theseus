using System;
using System.Collections.Generic;

namespace Theseus.Services.Duty;

/// <summary>
/// Everything <see cref="DutyEntry"/> needs from the game, behind one seam.
///
/// <para>
/// Same reasoning as <see cref="Run.IStepWorld"/>: the ordering, the retries and the timeouts are
/// the part that goes wrong, and they are the only part testable without a client. The real
/// implementation is a translation layer with no decisions in it.
/// </para>
/// </summary>
public interface IDutyEntryWorld
{
    DateTime UtcNow { get; }

    /// <summary>Already inside instanced content. Entering is a no-op — and must never restart one.</summary>
    bool IsInDuty { get; }

    /// <summary>Zoning. Nothing can be clicked and no addon means anything.</summary>
    bool IsBetweenAreas { get; }

    /// <summary>Mid-something — a cutscene, an NPC, an occupied flag. Wait rather than click through it.</summary>
    bool IsOccupied { get; }

    bool IsInCombat { get; }

    /// <summary>
    /// The Duty Support main command is available on this character. Unlocked partway through
    /// A Realm Reborn, so a fresh alt genuinely does not have it.
    /// </summary>
    bool DutySupportUnlocked { get; }

    /// <summary>This duty can be run with Duty Support at all. Asked of the game, not inferred.</summary>
    bool SupportsDutySupport(uint contentFinderConditionId);

    /// <summary>
    /// Opens the companion window on a specific duty.
    ///
    /// <para>
    /// <paramref name="trust"/> picks the door. Duty Support and Trust are separate systems with
    /// separate agents, and entering a Trust duty through the Duty Support one brings its fixed
    /// story cast with no companion choice at all — which looks exactly like the party picker
    /// being ignored.
    /// </para>
    /// </summary>
    void OpenCompanionWindow(uint contentFinderConditionId, bool trust);

    /// <summary>
    /// Selects a duty inside an open companion window.
    ///
    /// <para>
    /// Opening the window does not choose what it is showing — it comes up on whatever was selected
    /// last. Everything read from it afterwards, the roster included, belongs to that duty rather
    /// than the one asked for, and registering commences it. The symptom is a companion list that
    /// never changes when you switch expansion.
    /// </para>
    /// </summary>
    bool SelectDuty(uint contentFinderConditionId, bool trust);

    /// <summary>The companion window is up and will accept a registration.</summary>
    bool IsCompanionWindowReady(bool trust);

    /// <summary>Presses "Duty Commence" — registers for whatever the window has selected.</summary>
    void RegisterForDuty(bool trust);

    // ── Trust ──

    /// <summary>
    /// This duty lets you choose companions, rather than fixing them. Duty Support content does
    /// not; Trust content does, once the story has unlocked enough of the roster.
    /// </summary>
    bool CanChooseCompanions(bool trust);

    /// <summary>
    /// The companions this duty is offering, or empty when the window is not open.
    ///
    /// <para>
    /// Only readable while the Trust window is up, which is why the picker caches it. The list is
    /// the game's own answer to who is available, so companions still locked behind story are
    /// absent rather than present-and-refused — there is no separate unlock check to get wrong.
    /// </para>
    /// </summary>
    IReadOnlyList<TrustCompanion> ReadCompanions(uint contentFinderConditionId, bool trust);

    /// <summary>
    /// Sets the Trust party to exactly these companions. False when the game refused any of them,
    /// which is the signal to fall back rather than register a half-filled party.
    /// </summary>
    bool SetCompanions(IReadOnlyList<byte> companionKeys, bool trust);

    /// <summary>Lets the game pick a valid party, as the window's own default button does.</summary>
    void SetDefaultCompanions(bool trust);

    /// <summary>
    /// The raw companion arrays, for the debug window.
    ///
    /// <para>
    /// Here because the grouping has been guessed at wrongly twice: the member indices and the
    /// selected expansion both looked like the group selector and both returned a plausible roster
    /// from the wrong offset. Printing the counts and the first entries of every group is the only
    /// way to see which index actually addresses which cast.
    /// </para>
    /// </summary>
    string DescribeCompanionData(bool trust);

    // ── Duty Finder ──

    /// <summary>The Duty Finder main command is available on this character.</summary>
    bool DutyFinderUnlocked { get; }

    /// <summary>Queued in the Duty Finder right now.</summary>
    bool IsInDutyQueue { get; }

    /// <summary>Opens the Duty Finder with a duty highlighted.</summary>
    void OpenDutyFinder(uint contentFinderConditionId);

    /// <summary>The Duty Finder window is up and will take clicks.</summary>
    bool IsDutyFinderReady { get; }

    /// <summary>The duty the Duty Finder has highlighted — not necessarily ticked for queueing.</summary>
    uint DutyFinderHighlightedDuty { get; }

    /// <summary>Name of the duty currently ticked for queueing, or empty when none is.</summary>
    string DutyFinderTickedName { get; }

    /// <summary>Ticks the highlighted duty.</summary>
    void DutyFinderTickHighlighted();

    /// <summary>Unticks everything.</summary>
    void DutyFinderClearSelection();

    /// <summary>Presses Join.</summary>
    void DutyFinderJoin();

    /// <summary>The commence confirmation dialog is showing.</summary>
    bool IsConfirmVisible { get; }

    /// <summary>Accepts the commence confirmation.</summary>
    void ConfirmQueue();

    void Log(string message);
}
