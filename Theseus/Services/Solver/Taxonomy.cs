using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Theseus.Services.Solver;

/// <summary>
/// What each interactable has been learned to be, by DataId — the solver's memory of "levers are
/// triggers, that key is a pickup".
///
/// <para>
/// Two layers, and the difference between them is who is asserting. A <b>seed</b> is a person
/// writing a class down (shipped, small, hand-authored from field work); a <b>learned</b> entry is
/// an outcome this run observed. A learned class only overrides a seed once it has been confirmed
/// twice, because one observation is a hypothesis and a person's word beats a guess.
/// </para>
///
/// <para>
/// Nothing here reads text: identity is the DataId, which is identical on every client. Object
/// names exist for logs and the run window only.
/// </para>
///
/// <para>
/// An object seen once per run needs two runs before it is known — that is the price of the
/// confirmation rule, and it buys never acting on a coincidence. Probing again costs five seconds.
/// </para>
/// </summary>
public sealed class Taxonomy
{
    /// <summary>Observations of the same class before a learned entry outranks the shipped seed.</summary>
    public const int ConfirmationsToOverrideSeed = 2;

    /// <summary>
    /// The shipped seed. Empty on purpose for now: the entries come from the field work the
    /// architecture doc's §9 lists, and until then everything is learned from the game itself.
    /// </summary>
    private static readonly Dictionary<uint, BehaviourClass> Seeds = [];

    private readonly Dictionary<uint, Entry> _learned = [];
    private readonly IReadOnlyDictionary<uint, BehaviourClass> _seed;
    private readonly Action<string>? _log;

    public Taxonomy(IReadOnlyDictionary<uint, BehaviourClass>? seed = null, Action<string>? log = null)
    {
        _seed = seed ?? Seeds;
        _log = log;
    }

    /// <summary>Entries with at least one observation.</summary>
    public int LearnedCount => _learned.Count;

    /// <summary>Entries that outrank a seed: the ones a run can act on without probing.</summary>
    public int ConfirmedCount => _learned.Values.Count(e => e.Confirmations >= ConfirmationsToOverrideSeed);

    /// <summary>
    /// What this object is, or <see cref="BehaviourClass.Unknown"/> — which is a real answer, not a
    /// failure: an unknown object is acted on by probing, and the outcome teaches it.
    /// </summary>
    public BehaviourClass Classify(uint dataId)
    {
        if (_learned.TryGetValue(dataId, out var learned) && learned.Confirmations >= ConfirmationsToOverrideSeed)
            return learned.Class;

        return _seed.TryGetValue(dataId, out var seeded) ? seeded : BehaviourClass.Unknown;
    }

    /// <summary>
    /// Records what an interaction turned out to be. The same answer twice is a confirmation;
    /// a contradicting answer starts over, because the newest observation is the better hypothesis
    /// and a coin flip must not accumulate its way into a fact.
    /// </summary>
    public void Observe(uint dataId, BehaviourClass observed, DateTime? atUtc = null)
    {
        var now = atUtc ?? DateTime.UtcNow;

        if (!_learned.TryGetValue(dataId, out var entry))
        {
            _learned[dataId] = new Entry { DataId = dataId, Class = observed, Confirmations = 1, LastSeenUtc = now };
            return;
        }

        if (entry.Class == observed)
        {
            entry.Confirmations++;
        }
        else
        {
            entry.Class = observed;
            entry.Confirmations = 1;
        }

        entry.LastSeenUtc = now;
    }

    /// <summary>How many times this object has been observed as its current class. Diagnostic.</summary>
    public int Confirmations(uint dataId)
        => _learned.TryGetValue(dataId, out var entry) ? entry.Confirmations : 0;

    /// <summary>
    /// Loads the learned layer, or starts empty — a missing or unreadable file is not an error, it
    /// is a first run, and the answer is to learn again rather than to refuse to start.
    /// </summary>
    public void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var file = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path));
            if (file is null)
                return;

            _learned.Clear();
            foreach (var entry in file.Learned)
                _learned[entry.DataId] = entry;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Taxonomy could not be read ({ex.GetType().Name}) — starting from what this run learns.");
        }
    }

    /// <summary>
    /// Writes the learned layer. Best-effort: a taxonomy that cannot be saved costs the next run
    /// some probing, and that is never worth interrupting a run over.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            var file = new FileFormat { Learned = [.. _learned.Values.OrderBy(e => e.DataId)] };
            File.WriteAllText(path, JsonSerializer.Serialize(file, SerializerOptions));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Taxonomy could not be written ({ex.GetType().Name}).");
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Entry
    {
        public uint DataId { get; set; }

        public BehaviourClass Class { get; set; }

        public int Confirmations { get; set; }

        public DateTime LastSeenUtc { get; set; }
    }

    private sealed class FileFormat
    {
        public int Version { get; set; } = 1;

        public List<Entry> Learned { get; set; } = [];
    }
}
