namespace Theseus.Services.Duty;

/// <summary>
/// Reads the live duty objective list — the backbone of resume, fleet gating and wipe
/// detection (see the plan doc, finding #7).
///
/// <para>
/// <b>Implementation order (P0):</b> prefer
/// <c>FFXIVClientStructs.FFXIV.Client.Game.Event.Director.DirectorTodos</c> (via the inherited
/// <c>EventHandler.GetDirectorTodos()</c>) — that is director state and is populated whether or
/// not the HUD is visible. Fall back to the UI array
/// <c>FFXIVClientStructs.FFXIV.Client.UI.Arrays.ToDoListNumberArray</c>
/// (<c>DutyObjectiveTypes[]</c>, <c>DutyObjectiveValue[]</c>, <c>ObjectiveProgress</c>) with
/// <c>ToDoListStringArray</c> for display text.
/// </para>
///
/// <para>
/// <b>Hard rules.</b> Key off index + completion value, NEVER the text — it is localized and
/// carries dynamic counts. Still-hidden "???" slots must be counted, not skipped, so the total
/// is knowable before the names are. Return <see cref="DutyObjectiveSnapshot.Unavailable"/>
/// rather than an empty-but-available snapshot when nothing can be read: "unknown" and "none
/// complete" must never be confused, because the first means fall back to position-only resume
/// and the second means we are at the start of the dungeon.
/// </para>
/// </summary>
public interface IObjectiveReader
{
    /// <summary>Current objective state. Cheap enough to call once per frame; never throws.</summary>
    DutyObjectiveSnapshot Read();
}

/// <summary>
/// Stand-in until the real reader lands in P0. Always reports unavailable, which makes every
/// consumer take its documented fallback path — so the framework runs end-to-end without
/// pretending to know anything it does not.
/// </summary>
public sealed class NullObjectiveReader : IObjectiveReader
{
    public DutyObjectiveSnapshot Read() => DutyObjectiveSnapshot.Unavailable;
}
