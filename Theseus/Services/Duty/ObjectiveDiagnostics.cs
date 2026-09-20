using System.Collections.Generic;

namespace Theseus.Services.Duty;

/// <summary>One director todo, flattened to renderable values.</summary>
internal readonly record struct DirectorTodoRow(
    int Index,
    bool Enabled,
    string Shape,
    bool Complete,
    int RawCurrent,
    int RawNeeded,
    string Text);

/// <summary>One HUD-array objective slot, showing both the packed int and how we unpacked it.</summary>
internal readonly record struct ToDoListRow(
    int Index,
    string Shape,
    int RawValue,
    int DecodedCurrent,
    int DecodedTotal,
    string Text);

/// <summary>
/// Both objective sources, side by side and undecoded, for the debug window.
///
/// <para>
/// This exists to be looked at once in a live dungeon. The reader's own preference order hides
/// which source answered and what the other one said; the two assumptions worth confirming with
/// eyes on a real duty are that unrevealed "???" slots are present and countable in the director
/// vector, and that the HUD array's packed fraction is low-half-current — see
/// <see cref="ObjectiveReader.SplitToDoListValue"/>.
/// </para>
/// </summary>
internal readonly record struct ObjectiveDiagnostics(
    string DirectorKind,
    int DirectorTodoCount,
    IReadOnlyList<DirectorTodoRow> DirectorRows,
    bool ToDoListPresent,
    int ToDoListObjectiveCount,
    uint ToDoListCompletedRaw,
    IReadOnlyList<ToDoListRow> ToDoListRows)
{
    public static ObjectiveDiagnostics Empty { get; } =
        new("none", 0, [], false, 0, 0, []);
}
