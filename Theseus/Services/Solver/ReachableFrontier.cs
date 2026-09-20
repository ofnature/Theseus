using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Theseus.Services.Ipc;

namespace Theseus.Services.Solver;

/// <summary>
/// The reachable frontier, kept up to date: Mnemosyne's answer to "which walkable ground can I get
/// to from here", re-asked only when it has to be.
///
/// <para>
/// The query is a pipe round trip over a whole wing — 120 yalms of radius at 2 y cells, which the
/// nav side measured at 6 ms warm and about 40 KB on the wire. That is cheap enough to re-ask, and
/// far too expensive to re-ask every tick, so this holds both halves of the contract: ask when
/// nothing has been asked yet, when the character has crossed half the radius, or when something
/// says the world changed underneath (a gate opened, a transit landed).
/// </para>
///
/// <para>
/// A degraded answer never throws the frontier away. <c>meshNotReady</c> and
/// <c>serviceUnavailable</c> mean the question could not be answered, not that there is no ground
/// to walk on, and the last good grid stays in use with a line in the log. The visited set is
/// carried across answers, so re-asking does not re-explore the zone.
/// </para>
///
/// <para>
/// Without Ariadne — or an older one without this op — this returns null forever and the caller
/// degrades to landmarks and overrides, which is the frontier mode as it works today.
/// </para>
/// </summary>
public sealed class ReachableFrontier
{
    /// <summary>A dungeon wing. The radius is half the window's side, so the grid is 121 cells wide.</summary>
    public const float Radius = 120f;

    /// <summary>Two yalms: the architected size for exploration, and the size the visited set assumes.</summary>
    public const float CellSize = 2f;

    /// <summary>How far the character may drift before the window is re-centred.</summary>
    private const float MoveTolerance = Radius * 0.5f;

    private readonly AriadneIpc _ariadne;
    private readonly Func<Vector3> _position;
    private readonly Action<string>? _log;

    private Task<(string Result, Vector3 Start, Vector2 Origin, float CellSize, int Width, int Depth,
        int[] Columns, float[] Heights, byte[] States, bool ReachableOutside)>? _pending;

    private bool _asked;
    private bool _stale;
    private Vector3 _askedFrom;
    private bool _warned;

    public ReachableFrontier(AriadneIpc ariadne, Func<Vector3> position, Action<string>? log = null)
    {
        _ariadne = ariadne;
        _position = position;
        _log = log;
    }

    /// <summary>The grid in hand, or null while nothing has answered yet.</summary>
    public ReachableGrid? Grid { get; private set; }

    /// <summary>The last result the nav side gave. Diagnostic, and what the run window reports.</summary>
    public string LastResult { get; private set; } = "nothing asked";

    /// <summary>
    /// The frontier as it stands, asking for a fresh answer when one is due. Cheap to call every
    /// tick: the pipe is only touched when it has to be, and an answer in flight is picked up here
    /// on the frame it lands.
    /// </summary>
    public ReachableGrid? Refresh()
    {
        Collect();

        var here = _position();
        var due = _pending is null
                  && (!_asked || _stale || Vector3.Distance(here, _askedFrom) > MoveTolerance);

        if (!due)
            return Grid;

        _asked = true;
        _stale = false;
        _askedFrom = here;

        // No height band: a dungeon wing's floors are all worth knowing about, and the stacked
        // surfaces are how the grid tells a lift shaft from a locked door.
        _pending = _ariadne.ReachableCells(here, Radius, CellSize, float.NaN, float.NaN);
        return Grid;
    }

    /// <summary>
    /// The world changed underneath the grid: a gate opened, a transit landed, an override was
    /// added. The next refresh asks again instead of waiting for the character to walk far enough.
    /// </summary>
    public void Invalidate() => _stale = true;

    private void Collect()
    {
        var pending = _pending;
        if (pending is null || !pending.IsCompleted)
            return;

        _pending = null;

        if (pending.IsFaulted)
        {
            Warn("the query failed");
            return;
        }

        var (result, start, origin, cellSize, width, depth, columns, heights, states, reachableOutside) = pending.Result;
        LastResult = result;

        if (ReachableGrid.TryFrom(result, start, origin, cellSize, width, depth, columns, heights, states,
                reachableOutside) is not { } grid)
        {
            // An empty grid would read as "no walkable ground here", which is a different and much
            // more dangerous claim than "I could not answer".
            Warn(result);
            return;
        }

        if (Grid is { } previous)
            grid.AdoptExploredFrom(previous);

        Grid = grid;
        _warned = false;
    }

    private void Warn(string reason)
    {
        if (_warned)
            return;

        _warned = true;
        _log?.Invoke($"Reachable frontier unavailable ({reason}) — exploring by landmarks until it answers.");
    }
}
