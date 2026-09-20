using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Theseus.Services.Paths;

/// <summary>
/// Converts a path from the user's installed AutoDuty library into Theseus's own format.
///
/// <para>
/// <b>Licensing.</b> AutoDuty carries no licence, so all rights are reserved by default. Theseus
/// therefore reads only the copy already on the user's machine, converts it there, and never
/// redistributes those files or anything derived from them. Routes produced by our own recorder
/// are ours and ship freely. This is a hard constraint, not a preference.
/// </para>
///
/// <para>
/// <b>The format is not a contract.</b> It is AutoDuty's internal storage and can change without
/// warning, so conversion happens once into a persisted path rather than on every launch, and
/// unrecognised input is reported rather than skipped.
/// </para>
/// </summary>
public static class AutoDutyImporter
{
    /// <summary>
    /// Bumped whenever conversion changes meaning. Persisted onto each path so a converter fix can
    /// invalidate output produced by the older, wrong one.
    /// </summary>
    public const int Version = 1;

    /// <summary>Library filenames lead with the territory id: <c>(1036) Sastasha.json</c>.</summary>
    private static readonly Regex TerritoryFromFileName =
        new(@"^\s*\((?<id>\d+)\)\s*(?<name>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // Six of the library's files spell every key in lower case. Without this they deserialize
        // to an empty action list and import as a silently empty path.
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Converts one file's contents. Never throws for bad input: malformed JSON produces a path
    /// carrying a blocker, because a route that refuses to run with a reason beats one that runs
    /// halfway.
    /// </summary>
    /// <param name="fileName">Source file name, used for the territory id and the display name.</param>
    /// <param name="json">File contents.</param>
    public static ThreadPath Import(string fileName, string json)
    {
        var (territoryId, name) = ParseFileName(fileName);
        var path = new ThreadPath
        {
            TerritoryId = territoryId,
            Name = name,
            Source = $"AutoDuty import: {fileName}",
            SourceHash = Hash(json),
            ImporterVersion = Version,
        };

        AutoDutyFile? file;
        try
        {
            file = JsonSerializer.Deserialize<AutoDutyFile>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            path.Blockers.Add($"Source file is not valid JSON: {ex.Message}");
            return path;
        }

        if (file?.Actions is not { Count: > 0 })
        {
            path.Blockers.Add("Source file contains no actions.");
            return path;
        }

        foreach (var action in file.Actions)
            path.Steps.Add(Convert(action));

        // Validation rewrites the warning list from the route's contents, so import-time notes are
        // added after it rather than before.
        PathValidator.Validate(path);

        if (territoryId == 0)
            path.Warnings.Add($"Could not read a territory id from the file name \"{fileName}\".");

        return path;
    }

    private static ThreadStep Convert(AutoDutyAction action)
    {
        var rawVerb = action.Name?.Trim() ?? string.Empty;
        var verb = ParseVerb(rawVerb);

        return new ThreadStep
        {
            Verb = verb,
            RawVerb = rawVerb,
            Position = action.Position,
            Arguments = StepArguments.Meaningful(action.Arguments),
            Tag = ParseTag(action.Tag),
            Note = action.Note?.Trim() ?? string.Empty,
        };
    }

    /// <summary>
    /// Maps a verb name onto the enum.
    ///
    /// <para>
    /// Case-insensitive on purpose: the library contains both <c>BossMod</c> and <c>Bossmod</c>,
    /// and an exact-match table would drop the second spelling and leave the boss handoff out of
    /// two routes without saying so. Comments are written as the literal text
    /// <c>&lt;-- Comment --&gt;</c> rather than a word, which is why they need their own case.
    /// </para>
    /// </summary>
    public static StepVerb ParseVerb(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return StepVerb.Unknown;

        var trimmed = name.Trim();
        if (trimmed.Contains("Comment", StringComparison.OrdinalIgnoreCase))
            return StepVerb.Comment;

        return Enum.TryParse<StepVerb>(trimmed, ignoreCase: true, out var verb) && verb != StepVerb.Unknown
            ? verb
            : StepVerb.Unknown;
    }

    /// <summary>
    /// Reads the tag field, which is comma-combinable ("Synced, W2W") and occasionally written as
    /// a bare number instead of a name.
    /// </summary>
    public static StepTag ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return StepTag.None;

        // A numeric tag is the enum's underlying value, and 0 — by far the most common — is None.
        if (int.TryParse(tag.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return (StepTag)numeric;

        var result = StepTag.None;
        foreach (var part in tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse<StepTag>(part, ignoreCase: true, out var parsed))
                result |= parsed;
        }

        return result;
    }

    /// <summary>Pulls the territory id and display name out of a library file name.</summary>
    public static (uint TerritoryId, string Name) ParseFileName(string fileName)
    {
        var stem = fileName;
        var slash = stem.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            stem = stem[(slash + 1)..];
        if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^5];

        var match = TerritoryFromFileName.Match(stem);
        if (!match.Success)
            return (0, stem.Trim());

        return uint.TryParse(match.Groups["id"].Value, out var id)
            ? (id, match.Groups["name"].Value.Trim())
            : (0, stem.Trim());
    }

    /// <summary>
    /// Fingerprint of a source file, so a re-import can tell an unchanged file from an edited one
    /// without converting it first.
    /// </summary>
    public static string Hash(string content)
        => System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    // ── Source shape ──

    private sealed class AutoDutyFile
    {
        public List<AutoDutyAction>? Actions { get; set; }
    }

    private sealed class AutoDutyAction
    {
        public string? Name { get; set; }

        [JsonConverter(typeof(LenientStringConverter))]
        public string? Tag { get; set; }

        public PathPoint Position { get; set; }

        public List<string>? Arguments { get; set; }

        public string? Note { get; set; }
    }

    /// <summary>
    /// Reads a field that is usually a string but is sometimes written as a bare number. The tag
    /// field is; a strict string converter throws on those files and loses the whole path.
    /// </summary>
    private sealed class LenientStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.GetInt64().ToString(CultureInfo.InvariantCulture),
                JsonTokenType.Null => null,
                _ => null,
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}
