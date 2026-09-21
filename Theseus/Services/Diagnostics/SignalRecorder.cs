using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Theseus.Services.Duty;
using Theseus.Services.Frontier;
using Theseus.Services.Ipc;
using Theseus.Services.Run;

namespace Theseus.Services.Diagnostics;

/// <summary>
/// §10 step 1's measurement: the signals the solver is told not to assume, recorded as they happen.
///
/// <para>
/// One JSONL line per event, in the plugin's config directory, and nothing in the run ever reads it.
/// The point is to answer, from a real dungeon rather than from memory, the four questions §9 lists
/// as load-bearing: what the held-item candidates actually do when a key is taken; which condition
/// flags rise on a lift, a teleporter and a walk-in pad, and whether the position jumps in one frame
/// or over several; whether object instance ids match between clients; and whether the boss handler
/// knows a given hostile by DataId.
/// </para>
///
/// <para>
/// It records on change, never on a timer: a quiet dungeon costs nothing, and a run that rides a
/// shuttle writes exactly the frames that mattered. Anything that cannot be written is a missing
/// note, never a reason to interrupt anything — the same rule the gap log runs under.
/// </para>
/// </summary>
public sealed class SignalRecorder
{
    /// <summary>Position delta in one tick that reads as a jump rather than as walking (§4.2).</summary>
    public const float JumpThreshold = 15f;

    /// <summary>Ground covered in half a second with no path running that reads as being carried (§4.2).</summary>
    public const float CarryThreshold = 2f;

    /// <summary>Where the recorder stops writing, so a forgotten run cannot fill a disk.</summary>
    public const int MaxEvents = 20000;

    /// <summary>
    /// The transition flags worth watching (§4.2), plus the state flags that say whether a move was
    /// ours: mounted and falling are what separate a ride from a knockback.
    /// </summary>
    private static readonly string[] WatchedFlags =
    [
        "BetweenAreas", "BetweenAreas51", "OccupiedInCutSceneEvent", "OccupiedInEvent",
        "OccupiedInQuestEvent", "InCombat", "Mounted", "Falling", "Unconscious", "Casting",
    ];

    /// <summary>Addons whose appearance is itself a signal that something is on screen.</summary>
    private static readonly string[] WatchedAddons = ["_ToDoList", "_ToDoList2", "_DutyList", "_DutyInfo"];

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IStepWorld _world;
    private readonly ObjectiveReader _objectives;
    private readonly BossModIpc? _bossMod;
    private readonly MinervaIpc? _minerva;
    private readonly Func<uint> _territory;
    private readonly Func<bool> _inDuty;
    private readonly string _path;
    private readonly Action<string>? _log;

    private readonly List<(DateTime AtUtc, Vector3 Position)> _trail = [];
    private readonly Dictionary<ulong, WorldObject> _objects = [];
    private readonly Dictionary<uint, bool> _moduleAsked = [];
    private readonly HashSet<string> _lastFlags = [];
    private readonly HashSet<string> _lastAddons = [];

    private (int Stage, int CompletedCount, int TotalCount, bool Available) _lastObjective = (-1, -1, -1, false);
    private string _lastObjectSignature = string.Empty;
    private DateTime _lastCarryUtc = DateTime.MinValue;
    private bool _announced;

    public SignalRecorder(
        IStepWorld world,
        ObjectiveReader objectives,
        Func<uint> territory,
        Func<bool> inDuty,
        string path,
        BossModIpc? bossMod = null,
        MinervaIpc? minerva = null,
        Action<string>? log = null)
    {
        _world = world;
        _objectives = objectives;
        _territory = territory;
        _inDuty = inDuty;
        _path = path;
        _bossMod = bossMod;
        _minerva = minerva;
        _log = log;
    }

    /// <summary>Events written. Diagnostic — the file itself is the record.</summary>
    public int Written { get; private set; }

    /// <summary>What the last event was, for the debug window.</summary>
    public string LastKind { get; private set; } = "nothing yet";

    public bool Full => Written >= MaxEvents;

    /// <summary>One line for the debug window.</summary>
    public string Describe()
        => Full
            ? $"full ({MaxEvents} events) · {Path.GetFileName(_path)}"
            : $"recording · {Written} event(s) · last {LastKind} · {Path.GetFileName(_path)}";

    /// <summary>One tick of watching. Cheap: it compares and only writes what changed.</summary>
    public void Tick()
    {
        if (Full || !_inDuty())
        {
            _trail.Clear();
            _announced = false;
            return;
        }

        var now = _world.UtcNow;

        if (!_announced)
        {
            _announced = true;
            Write("session", $"territory {_territory()} entered", _world.PlayerPosition);
        }

        _trail.Add((now, _world.PlayerPosition));
        _trail.RemoveAll(sample => (now - sample.AtUtc).TotalSeconds > 1);

        NoteMove(now);
        NoteFlags(_world.PlayerPosition);
        NoteAddons(_world.PlayerPosition);
        NoteObjects(_world.PlayerPosition);
        NoteObjective(_world.PlayerPosition);
    }

    /// <summary>Whether two clients see the same instance id for the same object's DataId.</summary>
    private void NoteObjects(Vector3 at)
    {
        var nearby = _world.ScanNearby(40f);
        var signature = string.Join('|', nearby.Select(o => o.Id).OrderBy(id => id));

        if (signature == _lastObjectSignature)
            return;

        _lastObjectSignature = signature;

        var gone = _objects.Values.Where(o => nearby.All(n => n.Id != o.Id)).ToList();

        _objects.Clear();
        foreach (var o in nearby)
            _objects[o.Id] = o;

        foreach (var left in gone)
        {
            // §1.3's first candidate, recorded as it happens: an object that leaves the scan while
            // nothing killed it. The objective state is captured beside it, because "despawn + tick"
            // is the definition the solver relies on until something better is measured.
            Write("despawn", $"\"{left.Name}\" ({left.DataId}) left the scan", at,
                dataId: left.DataId, name: left.Name);
        }

        Write("objects", $"{nearby.Count} object(s) ({gone.Count} gone)", at,
            detail: string.Join("; ", nearby.Take(12).Select(DescribeObject)));
    }

    private string DescribeObject(WorldObject o)
    {
        var module = string.Empty;

        if (o.Kind == WorldObjectKind.Hostile)
        {
            // §9's boss-detection question, answered for real: whether the boss handler knows this
            // hostile by DataId, asked once per object so the file stays readable.
            var known = _moduleAsked.TryGetValue(o.DataId, out var cached)
                ? cached
                : _moduleAsked[o.DataId] = _bossMod?.HasModuleForDataId(o.DataId) ?? false;

            module = $" bmrModule={known}";
        }

        return $"{o.Kind} \"{o.Name}\" id={o.Id} data={o.DataId}{module}";
    }

    private void NoteMove(DateTime now)
    {
        if (_trail.Count < 2)
            return;

        var previous = _trail[^2].Position;
        var at = _trail[^1].Position;
        var step = Vector3.Distance(previous, at);

        // §4.2's observed jump: one frame, a long way, and not because anything we did moved us.
        if (step >= JumpThreshold)
        {
            Write("jump", $"moved {step:0.#}y in one tick", at,
                detail: $"from ({previous.X:0.#}, {previous.Y:0.#}, {previous.Z:0.#}) " +
                        $"mounted={Has("Mounted")} falling={Has("Falling")} inCombat={_world.InCombat} " +
                        $"pathRunning={_world.IsMoving} waypoints={_world.PathWaypointCount}");
            return;
        }

        // §4.2's observed carry: ground covered with no path running and nothing we asked for. A
        // moving platform looks exactly like this and like nothing else the solver does.
        var half = _trail.FirstOrDefault(sample => (now - sample.AtUtc).TotalSeconds >= 0.5);
        var covered = Vector3.Distance(half.Position, at);

        if (covered < CarryThreshold || _world.IsMoving || Has("Mounted") || Has("Falling"))
            return;

        if ((now - _lastCarryUtc).TotalSeconds < 2)
            return;

        _lastCarryUtc = now;
        Write("carry", $"covered {covered:0.#}y in half a second with no path running", at,
            detail: $"inCombat={_world.InCombat} navReady={_world.NavmeshReady} " +
                    $"moving={_world.IsMoving} jumping={_world.IsJumping}");
    }

    private void NoteFlags(Vector3 at)
    {
        var flags = WatchedFlags.Where(Has).ToHashSet();

        if (flags.SetEquals(_lastFlags))
            return;

        var rose = flags.Except(_lastFlags).ToList();
        var fell = _lastFlags.Except(flags).ToList();

        _lastFlags.Clear();
        foreach (var flag in flags)
            _lastFlags.Add(flag);

        Write("flags",
            $"rose [{string.Join(' ', rose)}] fell [{string.Join(' ', fell)}]",
            at,
            detail: $"now [{string.Join(' ', flags.Order())}]");
    }

    private void NoteAddons(Vector3 at)
    {
        var visible = WatchedAddons.Where(_world.IsAddonVisible).ToHashSet();

        if (visible.SetEquals(_lastAddons))
            return;

        _lastAddons.Clear();
        foreach (var addon in visible)
            _lastAddons.Add(addon);

        Write("addons", $"visible [{string.Join(' ', visible.Order())}]", at);
    }

    private void NoteObjective(Vector3 at)
    {
        var objectives = _objectives.Read();
        var state = (objectives.Stage, objectives.CompletedCount, objectives.TotalCount, objectives.Available);

        if (state == _lastObjective)
            return;

        var previous = _lastObjective;
        _lastObjective = state;

        // §1.3's second candidate, and the one the solver leans on: does the objective move when
        // something is taken, and how? Recorded together with the raw list so the shape is visible.
        var diagnostics = _objectives.ReadDiagnostics();

        var todo = diagnostics.ToDoListRows
            .Select(r => $"#{r.Index} shape={r.Shape} raw={r.RawValue} {r.DecodedCurrent}/{r.DecodedTotal} \"{r.Text}\"");

        var director = diagnostics.DirectorRows
            .Select((r, i) => $"#{i} done={r.Complete} {r.RawCurrent}/{r.RawNeeded} \"{r.Text}\"");

        Write("objective",
            $"stage {previous.Stage}→{state.Stage}, done {previous.CompletedCount}→{state.CompletedCount}/" +
            $"{state.TotalCount}, readable {state.Available}",
            at,
            detail: $"director {diagnostics.DirectorKind} x{diagnostics.DirectorTodoCount}: " +
                    $"{string.Join("; ", director)} || hud x{diagnostics.ToDoListObjectiveCount} " +
                    $"completed={diagnostics.ToDoListCompletedRaw}: {string.Join("; ", todo)}");
    }

    private bool Has(string flag) => _world.HasConditionFlag(flag);

    private void Write(string kind, string summary, Vector3 at, uint? dataId = null, string? name = null, string? detail = null)
    {
        LastKind = kind;

        if (Full)
            return;

        var line = new
        {
            ts = DateTime.UtcNow,
            territory = _territory(),
            kind,
            summary,
            x = MathF.Round(at.X, 2),
            y = MathF.Round(at.Y, 2),
            z = MathF.Round(at.Z, 2),
            dataId,
            name,
            detail,
        };

        try
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(line, Options) + Environment.NewLine);
            Written++;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Signal recorder could not write ({ex.Message}).");
        }
    }
}
