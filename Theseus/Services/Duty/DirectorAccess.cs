using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace Theseus.Services.Duty;

/// <summary>
/// Finds the content director currently driving the instance. One call site, because "which
/// director is live" is a question the objective reader and the duty lifecycle both ask and must
/// never answer differently.
/// </summary>
internal static unsafe class DirectorAccess
{
    /// <summary>
    /// The active director, or null when not in directed content. Dungeons, trials and raids are
    /// instance content; Bozja and Eureka are public content; the plain content director covers
    /// the rest. First one that answers wins.
    /// </summary>
    internal static Director* Current(out string kind)
    {
        kind = "none";

        var framework = EventFramework.Instance();
        if (framework == null)
            return null;

        var instance = framework->GetInstanceContentDirector();
        if (instance != null)
        {
            kind = "InstanceContent";
            return (Director*)instance;
        }

        var publicContent = framework->GetPublicContentDirector();
        if (publicContent != null)
        {
            kind = "PublicContent";
            return (Director*)publicContent;
        }

        var content = framework->GetContentDirector();
        if (content != null)
        {
            kind = "Content";
            return (Director*)content;
        }

        return null;
    }

    /// <summary>
    /// The active director's content id and start timestamp, or zeroes when there is no director.
    /// See <see cref="DutyRunKey"/> for why these two in particular.
    /// </summary>
    internal static (uint ContentId, long StartTimestamp) CurrentRunIdentity()
    {
        var director = Current(out _);
        return director != null
            ? (director->ContentId, director->DirectorStartTimestamp)
            : (0u, 0L);
    }

    /// <summary>
    /// Every field on the director that could plausibly separate one run of a duty from the next.
    /// Printed raw in the debug window because the obvious candidate — the start timestamp — turned
    /// out to be 0 in a normal dungeon, so the replacement has to be chosen by looking.
    /// </summary>
    internal static (uint ContentId, long Start, long End, byte Sequence, byte Flags, uint EventId) Identity()
    {
        var director = Current(out _);
        if (director == null)
            return (0u, 0L, 0L, 0, 0, 0u);

        return (
            director->ContentId,
            director->DirectorStartTimestamp,
            director->DirectorEndTimestamp,
            director->Sequence,
            director->ContentFlags,
            director->Info.EventId.Id);
    }

    /// <summary>
    /// The instance's own clock, in seconds, or nulls outside instance content.
    ///
    /// <para>
    /// Here as a candidate discriminator, not as a countdown. <see cref="DutyRunKey"/> still cannot
    /// separate a re-queue into the same dungeon from the original run, and max-minus-left is
    /// elapsed time inside the instance — which resets on a fresh entry where every other field
    /// stays identical. Displayed raw until it has been watched across a re-queue.
    /// </para>
    /// </summary>
    internal static (float? Left, float? Max) ContentTime()
    {
        var framework = EventFramework.Instance();
        if (framework == null)
            return (null, null);

        var instance = framework->GetInstanceContentDirector();
        return instance != null
            ? (instance->ContentTimeLeft, instance->ContentTimeMax)
            : (null, null);
    }
}
