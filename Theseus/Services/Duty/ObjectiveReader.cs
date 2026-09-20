using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.STD;
using UiObjectiveType = FFXIVClientStructs.FFXIV.Client.UI.Arrays.ToDoListNumberArray.ObjectiveType;

namespace Theseus.Services.Duty;

/// <summary>
/// The real reader. Prefers director state and falls back to the HUD's backing arrays.
///
/// <para>
/// <b>Why the order matters.</b> <c>Director.DirectorTodos</c> is owned by the content director,
/// so it is populated whether or not the Duty Information HUD is on screen, and every entry
/// carries an explicit <c>Complete</c> flag. <c>ToDoListNumberArray</c> is the UI mirror: it only
/// exists while the HUD does, and it packs its numbers, so completion has to be inferred. A fleet
/// box with the HUD hidden must still know where it is — hence director first.
/// </para>
///
/// <para>
/// <b>Identity is the slot index.</b> Nothing here matches text. <see cref="DutyObjective.Text"/>
/// is carried for the UI and for logs only. The one place a string is inspected at all is the
/// <see cref="DutyObjective.Hidden"/> flag — the game renders unrevealed objectives as "???" — and
/// that flag drives nothing but how the row is drawn.
/// </para>
/// </summary>
public sealed unsafe class ObjectiveReader : IObjectiveReader
{
    /// <summary>
    /// Sanity bound on the todo vector. A plausible duty has under ten objectives; a count outside
    /// this range means we are reading something that is not a todo vector, and walking it would
    /// dereference arbitrary memory rather than throw.
    /// </summary>
    private const int MaxPlausibleTodos = 64;

    private readonly Action<string>? _logFault;
    private string _lastFault = string.Empty;

    /// <param name="logFault">
    /// Optional sink for read failures, called only when the failure changes — this runs every
    /// frame, so an unconditional log would drown the plugin log in one duty.
    /// </param>
    public ObjectiveReader(Action<string>? logFault = null) => _logFault = logFault;

    public DutyObjectiveSnapshot Read()
    {
        try
        {
            var todos = FindDirectorTodos(out _);
            if (todos != null)
            {
                var count = todos->Count;
                if (count > 0 && count <= MaxPlausibleTodos)
                    return ReadDirector(todos, count);
            }

            var fallback = ReadToDoList();
            if (fallback.Available)
                return fallback;

            // A director that exists but publishes no todos is a real answer — "this duty has no
            // objectives" — and is not the same as having no director to ask.
            return todos != null
                ? DutyObjectiveSnapshot.From([], ObjectiveSource.Director)
                : DutyObjectiveSnapshot.Unavailable;
        }
        catch (Exception ex)
        {
            NoteFault($"{ex.GetType().Name}: {ex.Message}");
            return DutyObjectiveSnapshot.Unavailable;
        }
    }

    // ── Director (primary) ──

    /// <summary>
    /// The active content director's todo vector, or null. Goes through the virtual getter rather
    /// than the field, because some directors override it.
    /// </summary>
    private static StdVector<DirectorTodo>* FindDirectorTodos(out string directorKind)
    {
        var director = DirectorAccess.Current(out directorKind);
        return director != null ? director->GetDirectorTodos() : null;
    }

    private static DutyObjectiveSnapshot ReadDirector(StdVector<DirectorTodo>* todos, int count)
    {
        var objectives = new List<DutyObjective>(count);
        for (var i = 0; i < count; i++)
        {
            var todo = todos->First + i;

            // The vector is a fixed block of slots, not a list sized to the duty. Field-checked in
            // Mistwake: ten slots, of which the first six are enabled and match the HUD's own
            // DutyObjectiveCount of six, with four disabled slots trailing. Counting all ten would
            // report "0/10" for a six-objective dungeon and push every fleet gate off by four.
            //
            // Stopping at the first disabled slot rather than filtering them out is deliberate:
            // filtering would renumber everything after a gap, and the slot index is the identity
            // that checkpoints, gates and step tags are all keyed on.
            if (!todo->Enabled)
                break;

            var (current, total) = SplitDirectorValue(todo);
            var text = ReadText(todo->Text.StringPtr);

            objectives.Add(new DutyObjective(
                Index: i,
                Current: current,
                Total: total,
                // Unrevealed objectives are enabled slots with empty text — they hold a place in
                // the list before the game is willing to say what they are.
                Hidden: LooksHidden(text),
                Text: text,
                ReportedComplete: todo->Complete));
        }

        return DutyObjectiveSnapshot.From(objectives, ObjectiveSource.Director);
    }

    /// <summary>
    /// Pulls the numerator/denominator out of the todo's union, which is interpreted by shape.
    /// Shapes carrying no numbers report 0/0 and lean entirely on the <c>Complete</c> flag.
    /// </summary>
    private static (int Current, int Total) SplitDirectorValue(DirectorTodo* todo) => todo->Type switch
    {
        TodoType.Fraction or TodoType.FractionBar or TodoType.LargeGrayFraction
            => (todo->CurrentCount, todo->NeededCount),
        TodoType.Number or TodoType.LargeGrayNumber
            => (todo->CurrentCount, 0),
        TodoType.Bar or TodoType.LargeBar or TodoType.ColorableBar or TodoType.LongBar
            => (todo->CurrentPercentage, todo->NeededPercentage),
        _ => (0, 0),
    };

    // ── ToDoList arrays (fallback) ──

    private static DutyObjectiveSnapshot ReadToDoList()
    {
        var numbers = ToDoListNumberArray.Instance();
        if (numbers == null)
            return DutyObjectiveSnapshot.Unavailable;

        var types = numbers->DutyObjectiveTypes;
        var values = numbers->DutyObjectiveValue;

        var count = Math.Min(numbers->DutyObjectiveCount, Math.Min(types.Length, values.Length));
        if (count <= 0)
            return DutyObjectiveSnapshot.Unavailable;

        // Confirmed in Mistwake: a bitmask, not a count. With objectives 0 and 1 complete it read
        // 0x3 — a count would have read 2, and reading a count as a bitmask would have marked
        // objective 1 done and objective 0 not.
        var completedMask = numbers->DutyCompletedObjectives;

        var strings = ToDoListStringArray.Instance();
        var objectives = new List<DutyObjective>(count);
        for (var i = 0; i < count; i++)
        {
            var (current, total) = SplitToDoListValue(types[i], values[i]);
            var text = strings != null ? ReadText(strings->DutyObjectives[i]) : string.Empty;

            objectives.Add(new DutyObjective(
                Index: i,
                Current: current,
                Total: total,
                Hidden: LooksHidden(text),
                Text: text,
                ReportedComplete: i < 32 && (completedMask & (1u << i)) != 0));
        }

        return DutyObjectiveSnapshot.From(objectives, ObjectiveSource.ToDoList);
    }

    /// <summary>
    /// Reads the HUD array's objective value, which is a <b>completion percentage</b> rather than
    /// a count.
    ///
    /// <para>
    /// Measured in Mistwake: two objectives the HUD showed as "1/1" both carried <c>0x64</c> — 100
    /// — while a "0/1" carried <c>0</c>. The counts themselves appear only inside the localized
    /// display string, so the earlier guess that the int packed a numerator and denominator into
    /// two halves was wrong. Unused slots hold <c>-1</c>.
    /// </para>
    ///
    /// <para>
    /// Reporting progress out of 100 means a finished objective satisfies
    /// <see cref="DutyObjective.IsComplete"/> on its own, which matters because this is the source
    /// a box falls back to when the director cannot be read.
    /// </para>
    /// </summary>
    internal static (int Current, int Total) SplitToDoListValue(UiObjectiveType type, int raw)
    {
        // -1 is the sentinel the game leaves in slots it is not using. Taken at face value it is
        // an objective at four billion percent.
        if (raw is < 0 or > 100)
            return (0, 0);

        return type switch
        {
            UiObjectiveType.Fraction or UiObjectiveType.FractionBar or UiObjectiveType.LargeGrayFraction
                or UiObjectiveType.Bar or UiObjectiveType.ProgressBar or UiObjectiveType.ColorableBar
                or UiObjectiveType.LongBar
                => (raw, 100),
            _ => (0, 0),
        };
    }

    // ── Diagnostics ──

    /// <summary>
    /// Raw values from both sources, undecoded. Drives the debug window; nothing else reads it.
    /// </summary>
    internal ObjectiveDiagnostics ReadDiagnostics()
    {
        try
        {
            var directorRows = new List<DirectorTodoRow>();
            var todos = FindDirectorTodos(out var directorKind);
            var todoCount = todos != null ? todos->Count : 0;

            if (todos != null && todoCount > 0 && todoCount <= MaxPlausibleTodos)
            {
                for (var i = 0; i < todoCount; i++)
                {
                    var todo = todos->First + i;
                    directorRows.Add(new DirectorTodoRow(
                        Index: i,
                        Enabled: todo->Enabled,
                        Shape: todo->Type.ToString(),
                        Complete: todo->Complete,
                        // Printed straight from the union rather than through the shape switch, so
                        // a shape we mapped wrongly shows up as a mismatch instead of hiding.
                        RawCurrent: todo->CurrentCount,
                        RawNeeded: todo->NeededCount,
                        Text: ReadText(todo->Text.StringPtr)));
                }
            }

            var uiRows = new List<ToDoListRow>();
            var numbers = ToDoListNumberArray.Instance();
            var uiCount = 0;
            uint completedRaw = 0;

            if (numbers != null)
            {
                var types = numbers->DutyObjectiveTypes;
                var values = numbers->DutyObjectiveValue;
                completedRaw = numbers->DutyCompletedObjectives;
                uiCount = numbers->DutyObjectiveCount;

                var strings = ToDoListStringArray.Instance();
                var shown = Math.Min(uiCount, Math.Min(types.Length, values.Length));
                for (var i = 0; i < shown; i++)
                {
                    var (current, total) = SplitToDoListValue(types[i], values[i]);
                    uiRows.Add(new ToDoListRow(
                        Index: i,
                        Shape: types[i].ToString(),
                        RawValue: values[i],
                        DecodedCurrent: current,
                        DecodedTotal: total,
                        Text: strings != null ? ReadText(strings->DutyObjectives[i]) : string.Empty));
                }
            }

            return new ObjectiveDiagnostics(
                directorKind,
                todoCount,
                directorRows,
                numbers != null,
                uiCount,
                completedRaw,
                uiRows);
        }
        catch (Exception ex)
        {
            NoteFault($"{ex.GetType().Name}: {ex.Message}");
            return ObjectiveDiagnostics.Empty;
        }
    }

    // ── Shared ──

    /// <summary>
    /// Whether a slot is still showing as unrevealed. Presentation only — an unrevealed objective
    /// is still a counted slot, which is what makes "objective 3 of 6" answerable before the names
    /// are known.
    /// </summary>
    internal static bool LooksHidden(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        foreach (var c in text)
        {
            if (c != '?' && !char.IsWhiteSpace(c))
                return false;
        }

        return true;
    }

    private static string ReadText(InteropGenerator.Runtime.CStringPointer pointer)
        => pointer.HasValue ? pointer.ToString() ?? string.Empty : string.Empty;

    private void NoteFault(string fault)
    {
        if (fault == _lastFault)
            return;

        _lastFault = fault;
        _logFault?.Invoke(fault);
    }
}
