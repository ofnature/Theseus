namespace Theseus.Services.Duty;

/// <summary>
/// Identity of one duty instance — the "is this still the same run?" test the Thread performs
/// before offering to resume.
///
/// <para>
/// <b>Why not a nonce we mint ourselves.</b> A resume has to survive the client dying, and a
/// random id we generated lives only in our own memory: after a crash there is nothing left to
/// compare a checkpoint against. These values are the game's, so they can simply be asked for
/// again.
/// </para>
///
/// <para>
/// <b>Known gap.</b> <paramref name="DirectorStart"/> was meant to separate two runs of the same
/// duty, but measured in Mistwake it is <b>0</b> for a plain dungeon — the game appears to
/// populate it only for timed content. Identity is therefore currently zone + duty, which cannot
/// tell a re-queue from the original run. That is a P2 problem rather than a P0 one: resume
/// reconciles against live objective state and the game always wins over the stored ledger, so a
/// stale checkpoint is corrected rather than trusted. It still needs a real discriminator before
/// the Thread ships, and the debug window prints the director's raw identity fields so one can be
/// chosen from evidence.
/// </para>
/// </summary>
/// <param name="TerritoryId">Zone the instance runs in.</param>
/// <param name="ContentId">Director content id — which duty this is.</param>
/// <param name="DirectorStart">
/// Director start timestamp (Unix seconds), or 0 for content that does not set one.
/// </param>
public readonly record struct DutyRunKey(uint TerritoryId, uint ContentId, long DirectorStart)
{
    /// <summary>Not in a duty, or the director has not published itself yet.</summary>
    public static DutyRunKey None => default;

    /// <summary>
    /// Whether this key identifies an instance. Keyed off the content id, because a director
    /// exists for a few frames before it is populated and answering "yes" too early would make
    /// every entry look like a different run.
    /// </summary>
    public bool IsValid => ContentId != 0;

    public override string ToString()
        => IsValid ? $"t{TerritoryId}/c{ContentId}@{DirectorStart}" : $"none (t{TerritoryId})";
}
