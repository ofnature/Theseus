using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Theseus.Services.Solver;

/// <summary>
/// Who drives a territory. This is the migration table of §8, as data rather than as a build: a
/// territory keeps behaving exactly as it does today until a record — not a release — says
/// otherwise, which is what makes the solver additive instead of a rewrite with a flag on it.
/// </summary>
public enum DriverKind
{
    /// <summary>No route and no solver: today's frontier navigation, unchanged.</summary>
    Route,

    /// <summary>A route exists and the territory is not promoted: the route drives, the solver watches.</summary>
    RouteAndShadow,

    /// <summary>Promoted, with a route file still on disk to fall back into when the solver faults.</summary>
    SolverWithRouteFallback,

    /// <summary>Promoted or unrouted with nothing to fall back on: the solver drives, and faults go to the person.</summary>
    Solver,
}

/// <summary>
/// What is known about one territory's migration, and nothing else.
///
/// <para>
/// The counts here are the promotion decision's inputs (§8.2): how many clean route runs the shadow
/// agreed with the route on, and how many runs the solver drove without falling back. They are
/// written from things that actually happened — a disagreement is a gap log entry, not a strike.
/// </para>
/// </summary>
public sealed class TerritoryRecord
{
    public uint Territory { get; set; }

    /// <summary>Route runs the shadow watched and agreed with, since the last disagreement.</summary>
    public int ShadowAgreements { get; set; }

    /// <summary>Route runs the shadow watched but disagreed with the route on.</summary>
    public int ShadowDisagreements { get; set; }

    /// <summary>Runs the solver drove through to the end without falling back.</summary>
    public int SolverRuns { get; set; }

    /// <summary>Runs where the solver handed control back mid-run.</summary>
    public int SolverFallbacks { get; set; }

    public bool Promoted { get; set; }

    public bool RouteRetired { get; set; }

    public DateTime LastUtc { get; set; }
}

/// <summary>
/// The per-territory record, in the plugin's config directory beside the taxonomy and the gap log.
///
/// <para>
/// Reading it is what makes the driver decision data-driven; writing it happens at the end of a run
/// and never mid-run. A record that cannot be read is an empty record: the worst case is a
/// territory that keeps running the way it already runs, which is the safe direction to fail in.
/// </para>
/// </summary>
public sealed class SolverRecordStore
{
    /// <summary>Clean route runs the shadow must agree with before a territory may be promoted.</summary>
    public const int AgreementsToPromote = 3;

    /// <summary>
    /// Clean solver runs before an imported route is even a candidate for retirement. Deliberately
    /// more than the promotion count: driving a dungeon is a bigger claim than agreeing with one.
    /// </summary>
    public const int SolverRunsToRetire = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly Dictionary<uint, TerritoryRecord> _records = [];
    private readonly Action<string>? _log;

    public SolverRecordStore(Action<string>? log = null) => _log = log;

    public IReadOnlyCollection<TerritoryRecord> Records => _records.Values;

    public TerritoryRecord For(uint territory)
    {
        if (_records.TryGetValue(territory, out var record))
            return record;

        record = new TerritoryRecord { Territory = territory };
        _records[territory] = record;
        return record;
    }

    public bool IsPromoted(uint territory)
        => _records.TryGetValue(territory, out var record) && record.Promoted;

    /// <summary>
    /// §8's table. The inputs are facts about the world, not settings: whether a route resolves for
    /// this territory, whether the record has promoted it, and whether the solver can actually
    /// drive right now (a connected Ariadne with a grid answer — without those it degrades to
    /// vnavmesh, which is the frontier navigator's world, not the solver's).
    /// </summary>
    public DriverKind Decide(uint territory, bool hasRoute, bool solverReady)
    {
        if (!solverReady)
            return hasRoute ? DriverKind.RouteAndShadow : DriverKind.Route;

        if (IsPromoted(territory))
            return hasRoute ? DriverKind.SolverWithRouteFallback : DriverKind.Solver;

        // Unrouted territory with a working solver: this is the case the solver exists for, and the
        // frontier navigator stays underneath as the hand-back.
        return hasRoute ? DriverKind.RouteAndShadow : DriverKind.Solver;
    }

    /// <summary>A promotion is only ever written once the counts earn it.</summary>
    public bool Promote(uint territory)
    {
        var record = For(territory);

        if (record.ShadowAgreements < AgreementsToPromote)
            return false;

        record.Promoted = true;
        return true;
    }

    public void Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Options);

            foreach (var record in file?.Territories ?? [])
                _records[record.Territory] = record;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Solver record could not be read ({ex.Message}) — starting from nothing.");
        }
    }

    public void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(
                new FileShape { Territories = [.. _records.Values.OrderBy(r => r.Territory)] }, Options));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Solver record could not be written ({ex.Message}).");
        }
    }

    private sealed class FileShape
    {
        public int Version { get; set; } = 1;

        public List<TerritoryRecord> Territories { get; set; } = [];
    }
}
