namespace Theseus.Services.Run;

/// <summary>
/// Movement constants that can only really be settled by watching a character walk.
///
/// <para>
/// These are constants rather than settings because the right value is a property of the game's
/// geometry and vnavmesh's behaviour, not of anything reasoning can reach: too loose and the run
/// re-paths from off-route and clips scenery, too tight and it re-paths forever at a doorway it
/// cannot stand in. Both were found by watching a character walk Mistwake and are now settled.
/// </para>
///
/// <para>
/// The seam remains so tests can vary them, and so a future dungeon that needs different numbers
/// has somewhere to put them — but they are deliberately not user-facing. What looked at first
/// like a tolerance problem turned out to be waypoint density: a route whose steps stop short of a
/// corner leaves vnavmesh to round it, and no tolerance fixes that. Extra waypoints do.
/// </para>
/// </summary>
/// <param name="ArrivalTolerance">How close counts as having reached a waypoint.</param>
/// <param name="ArrivalSlack">
/// How far short we will settle for after repeated attempts, rather than stalling the run on a
/// waypoint that cannot be stood on.
/// </param>
public readonly record struct StepTuning(float ArrivalTolerance, float ArrivalSlack)
{
    /// <summary>
    /// The current best guess, and what every caller that does not tune gets.
    ///
    /// <para>
    /// Spelled out rather than relying on default parameter values. This is a record <i>struct</i>,
    /// so it keeps an implicit parameterless constructor that zero-initialises and never runs the
    /// primary constructor — writing <c>new()</c> here silently produced a tolerance of zero, which
    /// no character can ever satisfy, so every waypoint would re-path forever.
    /// </para>
    /// </summary>
    public static StepTuning Default { get; } = new(ArrivalTolerance: 1.5f, ArrivalSlack: 4f);
}
