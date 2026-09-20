using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Theseus.Services.Solver;

/// <summary>What the solver could not classify. The vocabulary the gap log is read by.</summary>
public enum GapKind
{
    /// <summary>An object nothing in the taxonomy could name, logged the first time it is probed.</summary>
    UnknownInteractable,

    /// <summary>Interacting did nothing, twice — learned as inert.</summary>
    Inert,

    /// <summary>A gate whose unlock condition never became true.</summary>
    UnknownGate,

    /// <summary>A transit that never landed inside its bound.</summary>
    TransitTimeout,

    /// <summary>A probe that could not be answered at all.</summary>
    ProbeFailed,

    /// <summary>A hostile that would not engage inside its bound.</summary>
    EngageTimeout,
}

/// <summary>One nearby object, as the gap log records it for whoever reads the line later.</summary>
public readonly record struct GapNeighbour(uint DataId, string Kind, float Distance);

/// <summary>
/// The append-only record of everything the solver could not classify.
///
/// <para>
/// One JSONL line per event, per character, in the plugin's config directory. Nothing in the
/// real-time loop ever reads it: it exists for the offline pass — a person, a script, or a model
/// reading traversal history to notice that three runs stalled in the same doorway and proposing a
/// taxonomy entry, a zone override, or a mesh link. Together with Mnemosyne's per-zone evidence it
/// is the entire input to that pass.
/// </para>
///
/// <para>
/// Recording is best-effort throughout: a gap log that cannot be written is a missing note, never a
/// reason to interrupt a run.
/// </para>
/// </summary>
public sealed class GapLog
{
    private readonly string _path;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;
    private bool _warned;

    /// <param name="path">Where the lines go — <c>gaps.jsonl</c> in the config directory.</param>
    public GapLog(string path, Func<DateTime>? clock = null, Action<string>? log = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    /// <summary>Lines written by this instance. Diagnostic — the file itself is the record.</summary>
    public int Written { get; private set; }

    public void Append(
        GapKind kind,
        string runId,
        uint territory,
        string cacheKey,
        int stage,
        Vector3 position,
        uint? dataId = null,
        string? name = null,
        IReadOnlyList<GapNeighbour>? nearby = null,
        string detail = "")
    {
        var line = new Line
        {
            Ts = _clock(),
            RunId = runId,
            Territory = territory,
            CacheKey = cacheKey,
            Stage = stage,
            Kind = kind.ToString(),
            X = position.X,
            Y = position.Y,
            Z = position.Z,
            DataId = dataId,
            Name = name,
            Nearby = nearby is { Count: > 0 } ? [.. nearby] : null,
            Detail = detail,
        };

        try
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(line, SerializerOptions) + Environment.NewLine);
            Written++;
        }
        catch (Exception ex)
        {
            if (_warned)
                return;

            _warned = true;
            _log?.Invoke($"Gap log could not be written ({ex.GetType().Name}) — the run continues without it.");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The on-disk shape, flattened so a line reads without a JSON viewer: position as x/y/z and the
    /// kind as a name rather than a number.
    /// </summary>
    private sealed class Line
    {
        public DateTime Ts { get; set; }

        public string RunId { get; set; } = string.Empty;

        public uint Territory { get; set; }

        public string CacheKey { get; set; } = string.Empty;

        public int Stage { get; set; }

        public string Kind { get; set; } = string.Empty;

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        public uint? DataId { get; set; }

        public string? Name { get; set; }

        public List<GapNeighbour>? Nearby { get; set; }

        public string Detail { get; set; } = string.Empty;
    }
}
