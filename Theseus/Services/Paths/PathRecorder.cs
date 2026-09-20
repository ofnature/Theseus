using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Run;

namespace Theseus.Services.Paths;

/// <summary>
/// Writes a route by watching someone run the dungeon.
///
/// <para>
/// The whole run is buffered and turned into steps at the end rather than emitted as it happens.
/// Simplification needs to see the shape of a leg before it can decide which points matter, and a
/// live emitter can only ever look backwards — it would keep every twitch of a corner it had not
/// reached yet.
/// </para>
///
/// <para>
/// Record a tank. A tank's run is the only one that contains the wall-to-wall information: chain
/// pulls show up as ground covered while in combat, which is exactly what a damage dealer never
/// does. Everything else about the route is identical between roles, so one tank recording yields
/// both behaviours — see <see cref="Build"/> for how narrowly that gets tagged.
/// </para>
/// </summary>
public sealed class PathRecorder
{
    /// <summary>How far the character must move before another sample is worth keeping.</summary>
    private const float SampleSpacing = 1.5f;

    /// <summary>
    /// Perpendicular distance from the straight line between its neighbours below which a sample
    /// carries no information. Tight on purpose: too loose and the route cuts corners.
    /// </summary>
    private const float StraightTolerance = 0.8f;

    /// <summary>
    /// Turn angle, in degrees, above which a sample is kept whatever the deviation test says.
    ///
    /// <para>
    /// Corners are where a run snags on scenery, and the fix found by hand was always more
    /// waypoints around them rather than fewer. Simplification that treats a corner as noise
    /// reproduces the exact failure it is supposed to prevent.
    /// </para>
    /// </summary>
    private const float CornerDegrees = 25f;

    /// <summary>Longest gap allowed on a straight, so a long leg still has something to aim at.</summary>
    private const float MaxWaypointGap = 25f;

    /// <summary>
    /// Closest two kept waypoints may sit, corners included.
    ///
    /// <para>
    /// The corner rule is deliberately greedy, and combat is where that bites: BossMod's AI weaves
    /// constantly while a pack is being fought, every zig reads as a corner, and the first field
    /// recording kept fifty-odd waypoints under two yalms apart — fight jitter preserved as if it
    /// were route geometry. Jitter is small by nature, so a spacing floor removes it without
    /// touching real corners, which sit apart from their neighbours.
    /// </para>
    /// </summary>
    private const float MinWaypointSpacing = 2.5f;

    /// <summary>
    /// Ground covered while in combat before a stretch counts as a chain pull rather than a fight.
    ///
    /// <para>
    /// This is the whole wall-to-wall detection. A damage dealer stands where the pack is and kills
    /// it; a tank walks on with it attached. Distance travelled under combat is the difference, and
    /// it needs no knowledge of packs, aggro or the dungeon's layout.
    /// </para>
    /// </summary>
    private const float PullDistance = 15f;

    /// <summary>
    /// Standing still this long marks a fight, and a fight is where a pull ends.
    ///
    /// <para>
    /// Samples only exist while the character moves, so a stationary fight leaves no marks at all —
    /// only a hole in time between two movement samples. The first field recording collapsed an
    /// entire wing into one pull pair because the stop-and-kill at the second pack was exactly such
    /// a hole: the tank dragged pack one to pack two, killed them together, and nothing recorded
    /// that the drag had ended. The gap in the clock is the fight; the <c>true</c> belongs there.
    /// </para>
    /// </summary>
    private static readonly TimeSpan FightHalt = TimeSpan.FromSeconds(6);

    private enum MarkKind
    {
        Step,
        Boss,
        Coffer,
    }

    private readonly record struct Sample(MarkKind Kind, Vector3 Position, bool InCombat, int Objective, DateTime Time);

    private readonly IStepWorld _world;
    private readonly IObjectiveReader _objectives;

    private readonly List<Sample> _marks = [];

    private Vector3 _lastSample;
    private bool _bossSeen;
    private readonly HashSet<ulong> _cofferSeen = [];

    public PathRecorder(IStepWorld world, IObjectiveReader objectives)
    {
        _world = world;
        _objectives = objectives;
    }

    public bool IsRecording { get; private set; }

    public uint TerritoryId { get; private set; }

    /// <summary>Samples taken so far, for the button's label.</summary>
    public int SampleCount => _marks.Count;

    public void Start(uint territoryId)
    {
        _marks.Clear();
        _cofferSeen.Clear();
        _bossSeen = false;
        TerritoryId = territoryId;
        _lastSample = _world.PlayerPosition;
        IsRecording = true;
        Mark(MarkKind.Step);
    }

    /// <summary>Pauses sampling. Marks are kept, so the recording can still be saved.</summary>
    public void Stop() => IsRecording = false;

    /// <summary>Throws the recording away.</summary>
    public void Discard()
    {
        IsRecording = false;
        _marks.Clear();
    }

    /// <summary>Called every frame while recording. Cheap: most frames add nothing.</summary>
    public void Tick()
    {
        if (!IsRecording)
            return;

        var here = _world.PlayerPosition;

        // A boss module starting is the only reliable "this is the boss" signal there is — the
        // alternative is inferring it from a health bar or a name, both of which have been wrong.
        var bossActive = _world.BossModuleActive;
        if (bossActive && !_bossSeen)
        {
            _bossSeen = true;
            Mark(MarkKind.Boss);
        }
        else if (!bossActive)
        {
            _bossSeen = false;
        }

        // A coffer that was standing there and is now gone was opened by whoever we are recording.
        if (_world.NearestCoffer(6f, _cofferSeen) is { } coffer)
        {
            _cofferSeen.Add(coffer.Id);
            Mark(MarkKind.Coffer, coffer.Position);
        }

        if (Vector3.Distance(here, _lastSample) < SampleSpacing)
            return;

        _lastSample = here;
        Mark(MarkKind.Step);
    }

    private void Mark(MarkKind kind, Vector3? at = null)
        => _marks.Add(new Sample(
            kind,
            at ?? _world.PlayerPosition,
            _world.InCombat,
            _objectives.Read().CurrentIndex,
            _world.UtcNow));

    /// <summary>
    /// Turns the recording into a route.
    ///
    /// <para>
    /// Tagging is deliberately narrow. Waypoints are left untagged so every role walks them —
    /// tagging travel <c>W2W</c> is what makes an imported route collapse for a damage dealer,
    /// because skipping a step skips its position too. Only the step that turns combat stops
    /// <i>off</i> carries the tag, and its matching <c>true</c> does not: a character that skipped
    /// the first one then runs a redundant no-op rather than depending on a step it never ran.
    /// </para>
    /// </summary>
    public ThreadPath Build(string name)
    {
        // Variant is derived from the tags, so it settles itself once the pull steps are in.
        var path = new ThreadPath { TerritoryId = TerritoryId, Name = name };
        if (_marks.Count == 0)
            return path;

        var pulling = false;

        foreach (var leg in Legs())
        {
            switch (leg.Kind)
            {
                case MarkKind.Step:
                    pulling = EmitTravel(path, leg.Samples, pulling);
                    break;

                case MarkKind.Boss:
                    // A boss is never chain-pulled into; close the mode before the fight.
                    pulling = ClosePull(path, pulling, leg.Samples[0]);
                    path.Steps.Add(Step(StepVerb.Boss, leg.Samples[0].Position, leg.Samples[0].Objective, StepTag.None));
                    break;

                case MarkKind.Coffer:
                    pulling = ClosePull(path, pulling, leg.Samples[0]);
                    path.Steps.Add(Step(StepVerb.TreasureCoffer, leg.Samples[0].Position, leg.Samples[0].Objective, StepTag.Treasure));
                    break;
            }
        }

        // Never leave the route in chain-pull mode: whatever follows would run with combat stops off.
        if (pulling && path.Steps.Count > 0)
        {
            var last = path.Steps[^1];
            path.Steps.Add(Step(StepVerb.StopForCombat, last.Position.ToVector3(), last.ObjectiveIndex,
                StepTag.None, "true"));
        }

        return path;
    }

    /// <summary>
    /// Emits one travel leg: waypoints, with pull toggles interleaved where the run's own rhythm
    /// put them.
    ///
    /// <para>
    /// A wing is not one pull. The recorded pattern is pull → stop and kill → pull again, and the
    /// stops are invisible in the positions — the character is stationary, so there are no samples,
    /// only a hole in the clock. Each combat stretch that covered real ground opens a pull at the
    /// point combat began, and the first long halt (or combat simply ending) after it closes the
    /// pull where the fight actually happened.
    /// </para>
    /// </summary>
    private static bool EmitTravel(ThreadPath path, List<Sample> samples, bool pulling)
    {
        var toggles = new List<(int Index, string Value, StepTag Tag)>();
        var combatStart = -1;
        var combatDistance = 0f;

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];

            if (sample.InCombat)
            {
                if (combatStart < 0)
                {
                    combatStart = i;
                    combatDistance = 0f;
                }
                else if (i > 0)
                {
                    combatDistance += Vector3.Distance(samples[i - 1].Position, sample.Position);
                }
            }

            if (!pulling && combatStart >= 0 && combatDistance >= PullDistance)
            {
                toggles.Add((combatStart, "false", StepTag.W2W));
                pulling = true;
            }

            // The fight: a hole in the clock while in combat, or combat simply ending. Either says
            // the drag is over and this is where the packs were killed.
            var halted = i + 1 < samples.Count && samples[i + 1].Time - sample.Time > FightHalt;
            if (pulling && ((halted && sample.InCombat) || !sample.InCombat))
            {
                toggles.Add((i, "true", StepTag.None));
                pulling = false;
                combatStart = sample.InCombat ? i : -1;
                combatDistance = 0f;
            }

            if (!sample.InCombat)
                combatStart = -1;
        }

        // Slice the leg at the toggles, simplify each slice, and interleave.
        var cursor = 0;
        foreach (var (index, value, tag) in toggles)
        {
            EmitWaypoints(path, samples, cursor, index);
            path.Steps.Add(Step(StepVerb.StopForCombat, samples[index].Position, samples[index].Objective, tag, value));
            cursor = index;
        }

        EmitWaypoints(path, samples, cursor, samples.Count - 1);
        return pulling;
    }

    private static void EmitWaypoints(ThreadPath path, List<Sample> samples, int from, int to)
    {
        if (to <= from)
            return;

        var points = new List<Vector3>(to - from + 1);
        for (var i = from; i <= to; i++)
            points.Add(samples[i].Position);

        foreach (var point in Simplify(points))
            path.Steps.Add(Step(StepVerb.MoveTo, point, samples[from].Objective, StepTag.None));
    }

    private static bool ClosePull(ThreadPath path, bool pulling, Sample at)
    {
        if (pulling)
            path.Steps.Add(Step(StepVerb.StopForCombat, at.Position, at.Objective, StepTag.None, "true"));

        return false;
    }

    private readonly record struct Leg(MarkKind Kind, List<Sample> Samples);

    /// <summary>Groups the marks into runs of travel, broken by the events that become their own steps.</summary>
    private IEnumerable<Leg> Legs()
    {
        var run = new List<Sample>();

        foreach (var mark in _marks)
        {
            if (mark.Kind != MarkKind.Step)
            {
                if (run.Count > 0)
                {
                    yield return new Leg(MarkKind.Step, run);
                    run = [];
                }

                yield return new Leg(mark.Kind, [mark]);
                continue;
            }

            run.Add(mark);
        }

        if (run.Count > 0)
            yield return new Leg(MarkKind.Step, run);
    }

    /// <summary>
    /// Reduces a travelled line to the points worth aiming at.
    ///
    /// <para>
    /// Not plain line-fitting: a corner is kept whatever its deviation says, because rounding
    /// corners is precisely how a run ends up clipping scenery, and the hand fix has always been to
    /// add waypoints there rather than remove them. Straights are capped instead, so a long leg
    /// still has something to walk towards — and a spacing floor thins the jitter combat weaving
    /// leaves behind, which the corner rule would otherwise keep en masse.
    /// </para>
    /// </summary>
    internal static List<Vector3> Simplify(IReadOnlyList<Vector3> points)
    {
        if (points.Count <= 2)
            return [.. points];

        var kept = new List<Vector3> { points[0] };

        for (var i = 1; i < points.Count - 1; i++)
        {
            var anchor = kept[^1];
            var current = points[i];
            var next = points[i + 1];

            if (Vector3.Distance(anchor, current) < MinWaypointSpacing)
                continue;

            if (Vector3.Distance(anchor, current) >= MaxWaypointGap
                || TurnDegrees(anchor, current, next) >= CornerDegrees
                || DistanceToSegment(current, anchor, next) >= StraightTolerance)
                kept.Add(current);
        }

        kept.Add(points[^1]);
        return kept;
    }

    private static float TurnDegrees(Vector3 from, Vector3 at, Vector3 to)
    {
        var a = at - from;
        var b = to - at;
        if (a.LengthSquared() < 1e-4f || b.LengthSquared() < 1e-4f)
            return 0f;

        var cos = Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f);
        return float.RadiansToDegrees(MathF.Acos(cos));
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var line = end - start;
        var lengthSquared = line.LengthSquared();
        if (lengthSquared < 1e-4f)
            return Vector3.Distance(point, start);

        var t = Math.Clamp(Vector3.Dot(point - start, line) / lengthSquared, 0f, 1f);
        return Vector3.Distance(point, start + (line * t));
    }

    private static ThreadStep Step(StepVerb verb, Vector3 position, int objective, StepTag tag,
        params string[] arguments)
        => new()
        {
            Verb = verb,
            RawVerb = verb.ToString(),
            Position = new PathPoint(position.X, position.Y, position.Z),
            Arguments = arguments,
            Tag = tag,
            ObjectiveIndex = objective,
        };
}
