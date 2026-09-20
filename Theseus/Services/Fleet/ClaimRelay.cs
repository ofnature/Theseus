using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theseus.Services.Frontier;

namespace Theseus.Services.Fleet;

/// <summary>
/// One frame on the <c>theseus.claim</c> channel (§5.4).
///
/// <para>
/// Extend-only JSON with unknown fields ignored, so an older box keeps working against a newer one
/// and vice versa. The key is the object's DataId plus its position rounded to a yalm — never the
/// instance id, which has never been verified to match between clients, and a claim on the wrong
/// object is worse than no claim at all.
/// </para>
///
/// <para>
/// <b>Nothing here is ever acted on directly.</b> A frame says what a peer believes; the local scan
/// is what says whether the object is there. An id from the network is used to look something up in
/// the world this client can see, and if it is not there the frame is a note about a peer.
/// </para>
/// </summary>
public sealed class ClaimMessage
{
    /// <summary>Character that sent the frame.</summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>The run this claim belongs to — the duty's own nonce, so stale claims die with it.</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>One of <see cref="ClaimRelay.ActClaim"/>, <see cref="ClaimRelay.ActDone"/>,
    /// <see cref="ClaimRelay.ActHeld"/>, <see cref="ClaimRelay.ActBeyond"/>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>DataId and rounded position of the object, or the gate's id for <c>beyond</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Party-list slot of the sender, which is what makes arbitration leaderless.</summary>
    [JsonPropertyName("slot")]
    public int Slot { get; set; } = -1;

    /// <summary>Unix milliseconds, identical across the three repeats of a one-shot.</summary>
    [JsonPropertyName("at")]
    public long At { get; set; }

    /// <summary>For <c>done</c>: resolved, failed, or transit.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>For <c>held</c>: which kind of item, as the taxonomy names it.</summary>
    [JsonPropertyName("itemKind")]
    public string ItemKind { get; set; } = string.Empty;
}

/// <summary>Codec for the claim channel. Parse never throws; serialize is the only writer.</summary>
public static class ClaimRelay
{
    /// <summary>I am taking this object.</summary>
    public const string ActClaim = "claim";

    /// <summary>That object is finished with, and how.</summary>
    public const string ActDone = "done";

    /// <summary>Group-wide item state — somebody is holding a dungeon key.</summary>
    public const string ActHeld = "held";

    /// <summary>I am past this gate, so it is open for everybody.</summary>
    public const string ActBeyond = "beyond";

    /// <summary>
    /// How many times a one-shot is repeated. The relay's dedup ring makes identical frames
    /// idempotent, and the repeats are what makes a single dropped packet not matter.
    /// </summary>
    public const int Repeats = 3;

    /// <summary>
    /// The claim key for an object: DataId plus its position rounded to a yalm. Rounded rather than
    /// exact because two clients see the same object at very slightly different floats.
    /// </summary>
    public static string KeyFor(WorldObject target)
        => $"{target.DataId}@{MathF.Round(target.Position.X)}:" +
           $"{MathF.Round(target.Position.Y)}:{MathF.Round(target.Position.Z)}";

    /// <summary>The DataId inside a key, or 0 when it is not an object key.</summary>
    public static uint DataIdOf(string key)
    {
        var at = key.IndexOf('@');
        var text = at < 0 ? key : key[..at];
        return uint.TryParse(text, out var dataId) ? dataId : 0;
    }

    /// <summary>The key for a gate this box is past: its id plus where it leads, rounded.</summary>
    public static string GateKeyFor(string gateId, Vector3 beyond)
        => $"gate:{gateId}@{MathF.Round(beyond.X)}:{MathF.Round(beyond.Y)}:{MathF.Round(beyond.Z)}";

    /// <summary>
    /// Reads a gate key back. Returns null when the frame is not a gate key at all — a peer's gate
    /// that this box cannot name is a note, not something to act on.
    /// </summary>
    public static (string GateId, Vector3 Beyond)? ParseGateKey(string key)
    {
        if (!key.StartsWith("gate:", StringComparison.Ordinal))
            return null;

        var at = key.IndexOf('@');
        if (at < 0)
            return (key[5..], Vector3.Zero);

        var parts = key[(at + 1)..].Split(':');
        if (parts.Length != 3
            || !float.TryParse(parts[0], out var x)
            || !float.TryParse(parts[1], out var y)
            || !float.TryParse(parts[2], out var z))
        {
            return (key[5..at], Vector3.Zero);
        }

        return (key[5..at], new Vector3(x, y, z));
    }

    public static string Serialize(ClaimMessage message) => JsonSerializer.Serialize(message);

    /// <summary>Parses a frame. Null on anything malformed or incomplete — never throws.</summary>
    public static ClaimMessage? Parse(string json)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ClaimMessage>(json);

            if (message is null || message.Kind.Length == 0 || message.Key.Length == 0)
                return null;

            // A frame with no run is a frame from a run that is over. Dropping it here keeps the
            // rest of the code from having to ask.
            return message.RunId.Length == 0 ? null : message;
        }
        catch
        {
            return null;
        }
    }
}
