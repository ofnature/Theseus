using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Theseus.Services.Solver;

/// <summary>A stretch of mesh that cannot be walked to from here, next to ground that can.</summary>
/// <param name="Frontier">The reachable surface — our side of the edge.</param>
/// <param name="Beyond">The cut-off surface the edge leads to, which is what opens when it unlocks.</param>
public readonly record struct GateCandidate(Vector3 Frontier, Vector3 Beyond);

/// <summary>
/// The reachableCells answer as a grid Theseus computes over: what has been seen, what is left
/// unexplored, and where the walkable ground stops at a height that means a door rather than a
/// stairwell.
///
/// <para>
/// Mnemosyne has no notion of "explored" and should not grow one — it answers which walkable ground
/// is reachable from a point, which is the one question Theseus cannot answer for itself. Everything
/// per-run lives here: the visited set is the reachable <b>surfaces</b> (a column plus a height,
/// because dungeons stack floors in one column) within sight of anywhere the character has stood.
/// The grid is world-aligned, so every answer at the same cell size shares cell boundaries and one
/// visited set spans a run's queries without resampling.
/// </para>
///
/// <para>
/// Only an <c>ok</c> answer becomes a grid. Anything else — <c>meshNotReady</c>,
/// <c>serviceUnavailable</c>, <c>failed</c>, or arrays that cannot be indexed safely — returns null,
/// because an empty grid would read as "no walkable ground here", which is a far more dangerous
/// claim than "I could not answer".
/// </para>
/// </summary>
public sealed class ReachableGrid
{
    private const byte Reachable = 1;
    private const byte CutOff = 2;

    /// <summary>
    /// Two surfaces this close in Y are the same level. It is the nav side's own granularity —
    /// samples less than this apart are merged within a column, and a cut-off surface further than
    /// this from a reachable one is another storey rather than a closed door.
    /// </summary>
    public const float SameLevel = 2f;

    private readonly Vector3[] _surfaces;
    private readonly int[] _columns;
    private readonly byte[] _states;
    private readonly bool[] _explored;

    private ReachableGrid(Vector3 start, Vector2 origin, float cellSize, int width, int depth,
        bool reachableOutside, Vector3[] surfaces, int[] columns, byte[] states)
    {
        Start = start;
        Origin = origin;
        CellSize = cellSize;
        Width = width;
        Depth = depth;
        ReachableOutside = reachableOutside;
        _surfaces = surfaces;
        _columns = columns;
        _states = states;
        _explored = new bool[surfaces.Length];
    }

    /// <summary>The query point the flood ran from — the ground the reachable set is relative to.</summary>
    public Vector3 Start { get; }

    /// <summary>World X/Z of the grid's minimum corner. A cell centre is <c>origin + (index + 0.5) * cellSize</c>.</summary>
    public Vector2 Origin { get; }

    public float CellSize { get; }

    public int Width { get; }

    public int Depth { get; }

    /// <summary>
    /// False only when nothing reachable lies beyond the window. This is what lets "exhausted" mean
    /// exhausted rather than "the grid was too small".
    /// </summary>
    public bool ReachableOutside { get; }

    public int SurfaceCount => _surfaces.Length;

    public int ReachableCount { get; private init; }

    public int CutOffCount { get; private init; }

    /// <summary>Anything reachable left unseen, or ground beyond the window if there is any.</summary>
    public bool Exhausted => UnexploredCount == 0 && !ReachableOutside;

    public int UnexploredCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < _surfaces.Length; i++)
            {
                if (_states[i] == Reachable && !_explored[i])
                    count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Builds the grid from a gate answer, or null when the answer is not <c>ok</c> or cannot be
    /// indexed safely — mismatched arrays, a cell size that cannot be a size, or a column index
    /// outside the declared grid. A grid that silently dropped bad entries would understate the
    /// world, which is exactly the failure the nav side refuses to produce.
    /// </summary>
    public static ReachableGrid? TryFrom(string result, Vector3 start, Vector2 origin, float cellSize,
        int width, int depth, int[] columns, float[] heights, byte[] states, bool reachableOutside)
    {
        if (result != "ok" || cellSize <= 0f || width <= 0 || depth <= 0)
            return null;

        if (columns.Length != heights.Length || heights.Length != states.Length)
            return null;

        if ((long)width * depth > 65536)
            return null;

        var surfaces = new Vector3[columns.Length];
        var reachable = 0;
        var cutOff = 0;

        for (var i = 0; i < columns.Length; i++)
        {
            var column = columns[i];
            if (column < 0 || column >= width * depth)
                return null;

            surfaces[i] = Position(origin, cellSize, width, column, heights[i]);

            if (states[i] == Reachable)
                reachable++;
            else if (states[i] == CutOff)
                cutOff++;
        }

        return new ReachableGrid(start, origin, cellSize, width, depth, reachableOutside, surfaces, columns, states)
        {
            ReachableCount = reachable,
            CutOffCount = cutOff,
        };
    }

    /// <summary>
    /// Marks every reachable surface within sight of a point as explored. Called with the
    /// character's position as it moves, which is what makes "visited" mean "seen from somewhere I
    /// have stood" rather than "the flood said I could walk there".
    /// </summary>
    public void MarkExplored(Vector3 position, float sightRadius)
    {
        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (_states[i] == Reachable && !_explored[i] && Vector3.Distance(_surfaces[i], position) <= sightRadius)
                _explored[i] = true;
        }
    }

    /// <summary>
    /// Carries what has been seen from an earlier answer.
    ///
    /// <para>
    /// A fresh query re-centres the window, so the columns are not the same columns — but the grid
    /// is world-aligned, and every cell centre sits on the same lattice at the same cell size
    /// whatever the origin. So the two grids are matched on <i>where the surface is</i>, which is
    /// what lets one visited set span a whole run instead of restarting every time the frontier is
    /// re-asked.
    /// </para>
    /// </summary>
    public void AdoptExploredFrom(ReachableGrid previous)
    {
        if (Math.Abs(previous.CellSize - CellSize) > 0.001f)
            return;

        var seen = new HashSet<(long X, long Y, long Z)>();
        for (var i = 0; i < previous._surfaces.Length; i++)
        {
            if (previous._explored[i])
                seen.Add(Key(previous._surfaces[i]));
        }

        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (seen.Contains(Key(_surfaces[i])))
                _explored[i] = true;
        }
    }

    /// <summary>A surface's identity across queries: its world position, to the precision the wire carries.</summary>
    private static (long X, long Y, long Z) Key(Vector3 surface)
        => ((long)MathF.Round(surface.X * 10f), (long)MathF.Round(surface.Y * 10f), (long)MathF.Round(surface.Z * 10f));

    /// <summary>
    /// The nearest ground nobody has looked at, scored the way the exploration loop scores it:
    /// straight-line distance first, with a small pull toward ground farther from the entrance so
    /// that two equally close regions resolve forward rather than backward.
    ///
    /// <para>
    /// The distance is straight-line rather than path-length. The flood already guarantees the
    /// surface is reachable, so this is a ranking rather than a route, and a route is a pathfind
    /// the loop fires when it commits to the pick.
    /// </para>
    /// </summary>
    public Vector3? NearestUnexplored(Vector3 from, Vector3? entrance = null, float progressionWeight = 0.25f)
    {
        Vector3? best = null;
        var bestScore = float.MaxValue;

        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (_states[i] != Reachable || _explored[i])
                continue;

            var score = Vector3.Distance(from, _surfaces[i]);

            if (entrance is { } origin)
                score -= progressionWeight * Vector3.Distance(origin, _surfaces[i]);

            if (score >= bestScore)
                continue;

            bestScore = score;
            best = _surfaces[i];
        }

        return best;
    }

    /// <summary>
    /// Ground the mesh says is walkable but cut off from here, sitting beside ground that is
    /// reachable at the same level — a door, a lip, a seam. Ordered nearest first.
    ///
    /// <para>
    /// The height filter is the whole trick: a cut-off floor thirty yalms above a reachable one in
    /// the next column is the other end of a lift shaft, not a locked door, and treating it as a
    /// gate would have the run probing its own ceiling forever. Same level means within
    /// <see cref="SameLevel"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<GateCandidate> GateCandidates(Vector3 from)
    {
        var byColumn = new Dictionary<int, List<int>>();
        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (_states[i] != CutOff)
                continue;

            if (!byColumn.TryGetValue(_columns[i], out var list))
                byColumn[_columns[i]] = list = [];

            list.Add(i);
        }

        if (byColumn.Count == 0)
            return [];

        var found = new List<GateCandidate>();
        var seen = new HashSet<(long X, long Y, long Z)>();

        for (var i = 0; i < _surfaces.Length; i++)
        {
            if (_states[i] != Reachable)
                continue;

            var column = _columns[i];
            var xi = column % Width;
            var zi = column / Width;

            for (var dz = -1; dz <= 1; dz++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = xi + dx;
                    var nz = zi + dz;
                    if (nx < 0 || nx >= Width || nz < 0 || nz >= Depth)
                        continue;

                    if (!byColumn.TryGetValue(nz * Width + nx, out var candidates))
                        continue;

                    foreach (var other in candidates)
                    {
                        if (MathF.Abs(_surfaces[other].Y - _surfaces[i].Y) > SameLevel)
                            continue;

                        // One doorway spans several cells; the ledger wants one entry per edge.
                        var beyond = _surfaces[other];
                        var key = (Quantise(beyond.X), Quantise(beyond.Y), Quantise(beyond.Z));
                        if (!seen.Add(key))
                            continue;

                        found.Add(new GateCandidate(_surfaces[i], beyond));
                    }
                }
            }
        }

        return [.. found.OrderBy(g => Vector3.Distance(from, g.Beyond))];
    }

    private static long Quantise(float value) => (long)MathF.Round(value);

    private static Vector3 Position(Vector2 origin, float cellSize, int width, int column, float height)
        => new(origin.X + ((column % width) + 0.5f) * cellSize, height, origin.Y + ((column / width) + 0.5f) * cellSize);
}
