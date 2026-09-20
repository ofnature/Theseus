using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Theseus.Services.Paths;

/// <summary>What one import pass did.</summary>
/// <param name="Converted">Files converted this pass.</param>
/// <param name="Skipped">Files already stored at the same source hash and importer version.</param>
/// <param name="Failed">Files that could not be read at all.</param>
/// <param name="Blocked">Paths that converted but cannot be run — <see cref="StepVerb.DutySpecificCode"/>.</param>
public readonly record struct ImportReport(int Converted, int Skipped, int Failed, int Blocked)
{
    public int Total => Converted + Skipped + Failed;

    public override string ToString()
        => $"{Converted} converted, {Skipped} unchanged, {Blocked} unrunnable, {Failed} failed";
}

/// <summary>
/// Persisted route library.
///
/// <para>
/// <b>Converted once, not re-read every launch.</b> AutoDuty's path format is its own internal
/// storage and can change shape without warning — it already has, several times, according to the
/// changelogs inside the files themselves. Converting on every startup would mean a route that
/// worked yesterday silently becomes an empty path today, mid-farm. So conversion is an explicit
/// action, its output is ours, and a stored path keeps working regardless of what happens to the
/// library it came from.
/// </para>
///
/// <para>
/// Paths are keyed by source file rather than by territory: twenty-seven territories in the
/// library have more than one route — variant dungeons reach twelve — so a territory maps to a
/// list, and picking between them is the caller's problem.
/// </para>
/// </summary>
public sealed class PathStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _storeDirectory;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, ThreadPath> _paths = [];

    private bool _loaded;

    public PathStore(string storeDirectory, Action<string>? log = null)
    {
        _storeDirectory = storeDirectory;
        _log = log;
    }

    /// <summary>Every stored path, loading from disk on first use.</summary>
    public IReadOnlyCollection<ThreadPath> All
    {
        get
        {
            EnsureLoaded();
            return _paths.Values;
        }
    }

    /// <summary>
    /// Stored routes for a territory, runnable ones first. Empty when the territory has no path —
    /// which is a normal answer, not an error.
    /// </summary>
    public IReadOnlyList<ThreadPath> ForTerritory(uint territoryId)
    {
        EnsureLoaded();
        return _paths.Values
            .Where(p => p.TerritoryId == territoryId)
            .OrderByDescending(p => p.IsRunnable)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The route to run for a territory: the saved preference when it still exists and is
    /// runnable, otherwise the first runnable route, otherwise nothing.
    ///
    /// <para>
    /// The fallback matters as much as the preference. Twenty-seven territories carry variants —
    /// wall-to-wall pulls for a tank, a gentler route for everyone else, separate exits in the
    /// variant dungeons — and picking one arbitrarily means a healer silently running the tank's
    /// route. But a preference can also go stale when the library is re-imported and a route is
    /// renamed or dropped, and refusing to run at all in that case would strand a fleet on a
    /// spelling change.
    /// </para>
    /// </summary>
    public ThreadPath? Resolve(uint territoryId, string? preferredKey, bool wallToWall = false, bool isTank = false)
    {
        var candidates = ForTerritory(territoryId);
        if (candidates.Count == 0)
            return null;

        // An explicit choice always wins — it is the user overriding us on purpose.
        if (!string.IsNullOrEmpty(preferredKey))
        {
            var preferred = candidates.FirstOrDefault(p => p.Key == preferredKey);
            if (preferred is { IsRunnable: true })
                return preferred;
        }

        foreach (var variant in PreferenceOrder(wallToWall, isTank))
        {
            var match = candidates.FirstOrDefault(p => p.IsRunnable && p.Variant == variant);
            if (match is not null)
                return match;
        }

        return candidates.FirstOrDefault(p => p.IsRunnable) ?? candidates[0];
    }

    /// <summary>
    /// Which route kinds to try, best first, for a character in a given role.
    ///
    /// <para>
    /// The important entry is the one that is <b>absent</b>: a non-tank is never offered
    /// <see cref="RouteVariant.TankWallToWall"/> or the unqualified
    /// <see cref="RouteVariant.WallToWall"/>. Those routes chain packs together with combat stops
    /// disabled, which works because the tank is holding everything — a damage dealer running them
    /// pulls the whole wing onto itself and dies. Falling back to the standard route only means
    /// falling behind the pull, which is recoverable.
    /// </para>
    /// </summary>
    private static IEnumerable<RouteVariant> PreferenceOrder(bool wallToWall, bool isTank)
    {
        if (!wallToWall)
        {
            yield return RouteVariant.Standard;
            yield break;
        }

        if (isTank)
        {
            yield return RouteVariant.TankWallToWall;
            yield return RouteVariant.WallToWall;
            yield return RouteVariant.Standard;
            yield break;
        }

        yield return RouteVariant.OtherWallToWall;
        yield return RouteVariant.Standard;
    }

    /// <summary>
    /// Territories holding more than one route, in territory order. This is what the settings
    /// picker lists — a territory with a single route has nothing to choose.
    /// </summary>
    public IReadOnlyList<IGrouping<uint, ThreadPath>> MultiRouteTerritories()
    {
        EnsureLoaded();
        return _paths.Values
            .GroupBy(p => p.TerritoryId)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key)
            .ToList();
    }

    /// <summary>
    /// Removes a path from the store and from disk. False when nothing was stored under its key.
    ///
    /// <para>
    /// Exists because recordings accumulate: every save under a new name is a new route, and
    /// without this the only way to be rid of a bad one was editing the config directory by hand.
    /// Deleting an imported route is not destructive in the way it sounds — the AutoDuty source is
    /// untouched, so a re-import brings it straight back.
    /// </para>
    /// </summary>
    public bool Delete(ThreadPath path)
    {
        EnsureLoaded();

        var key = KeyFor(path);
        if (!_paths.Remove(key))
            return false;

        try
        {
            var file = Path.Combine(_storeDirectory, key + ".json");
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            // Gone from memory either way; a leftover file just reappears on the next reload,
            // which is annoying but says so rather than silently resurrecting mid-session.
            _log?.Invoke($"Could not delete path file for \"{path.Name}\": {ex.Message}");
        }

        return true;
    }

    /// <summary>Writes a path, replacing any stored under the same key.</summary>
    public void Save(ThreadPath path)
    {
        EnsureLoaded();

        var key = KeyFor(path);
        _paths[key] = path;

        try
        {
            Directory.CreateDirectory(_storeDirectory);
            File.WriteAllText(Path.Combine(_storeDirectory, key + ".json"),
                JsonSerializer.Serialize(path, WriteOptions));
        }
        catch (Exception ex)
        {
            // In-memory copy survives, so the run in progress is unaffected; only persistence
            // across a reload is lost.
            _log?.Invoke($"Could not write path \"{path.Name}\": {ex.Message}");
        }
    }

    /// <summary>
    /// Converts every path in an AutoDuty library directory that we do not already hold at the
    /// same source hash and importer version.
    /// </summary>
    /// <param name="autoDutyPathDirectory">The user's own AutoDuty paths folder.</param>
    /// <param name="force">Re-convert even when the stored copy is already current.</param>
    public ImportReport ImportFrom(string autoDutyPathDirectory, bool force = false)
    {
        EnsureLoaded();

        if (!Directory.Exists(autoDutyPathDirectory))
        {
            _log?.Invoke($"No AutoDuty path folder at \"{autoDutyPathDirectory}\".");
            return default;
        }

        int converted = 0, skipped = 0, failed = 0, blocked = 0;

        foreach (var file in Directory.EnumerateFiles(autoDutyPathDirectory, "*.json"))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                failed++;
                _log?.Invoke($"Could not read \"{Path.GetFileName(file)}\": {ex.Message}");
                continue;
            }

            var name = Path.GetFileName(file);

            // Hand-edited routes are never re-converted, force or not. An imported route can be
            // rebuilt from its source at any time; a hand-fixed one exists nowhere else.
            if (IsEdited(name) || (!force && IsCurrent(name, json)))
            {
                skipped++;
                continue;
            }

            var path = AutoDutyImporter.Import(name, json);
            Save(path);
            converted++;
            if (!path.IsRunnable)
                blocked++;
        }

        var report = new ImportReport(converted, skipped, failed, blocked);
        _log?.Invoke($"AutoDuty import: {report}.");
        return report;
    }

    /// <summary>
    /// Whether the stored conversion of this file is still current. Compares the source hash so an
    /// edited path is picked up, and the importer version so a converter fix invalidates output
    /// produced by the older, wrong one.
    /// </summary>
    /// <summary>Whether the stored conversion of this file has been edited by hand.</summary>
    private bool IsEdited(string fileName)
    {
        var (territoryId, name) = AutoDutyImporter.ParseFileName(fileName);
        return _paths.TryGetValue(Key(territoryId, name), out var stored) && stored.IsEdited;
    }

    private bool IsCurrent(string fileName, string json)
    {
        var (territoryId, name) = AutoDutyImporter.ParseFileName(fileName);
        if (!_paths.TryGetValue(Key(territoryId, name), out var stored))
            return false;

        return stored.ImporterVersion == AutoDutyImporter.Version
               && stored.FormatVersion == ThreadPath.CurrentFormatVersion
               && stored.SourceHash == AutoDutyImporter.Hash(json);
    }

    /// <summary>
    /// Drops the in-memory copies and re-reads from disk. Backs the editor's Revert: edits mutate
    /// the stored objects directly, so discarding them means going back to what was written.
    /// </summary>
    public void Reload()
    {
        _paths.Clear();
        _loaded = false;
        EnsureLoaded();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;

        _loaded = true;

        if (!Directory.Exists(_storeDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(_storeDirectory, "*.json"))
        {
            try
            {
                var path = JsonSerializer.Deserialize<ThreadPath>(File.ReadAllText(file));
                if (path is null)
                    continue;

                // A path written by an older format is not migrated in place — it is simply not
                // loaded, so the next import re-converts it from the source that is still on disk.
                if (path.FormatVersion != ThreadPath.CurrentFormatVersion)
                    continue;

                _paths[Path.GetFileNameWithoutExtension(file)] = path;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Could not read stored path \"{Path.GetFileName(file)}\": {ex.Message}");
            }
        }
    }

    private static string KeyFor(ThreadPath path) => path.Key;

    private static string Key(uint territoryId, string name) => ThreadPath.BuildKey(territoryId, name);

    /// <summary>
    /// Where AutoDuty keeps its paths, derived from our own config folder — both plugins live
    /// under the same <c>pluginConfigs</c> directory, wherever the user put XIVLauncher.
    /// </summary>
    public static string DefaultAutoDutyDirectory(DirectoryInfo pluginConfigDirectory)
        => Path.Combine(pluginConfigDirectory.Parent?.FullName ?? pluginConfigDirectory.FullName,
            "AutoDuty", "paths");
}
