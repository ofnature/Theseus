using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Services.Duty;

namespace Theseus.Windows;

/// <summary>
/// Raw objective plumbing, both sources at once.
///
/// <para>
/// The reader prefers director state and silently falls back to the HUD array, which is exactly
/// the behaviour you want in a run and exactly the behaviour that makes a wrong assumption
/// invisible. This window undoes that: it shows what each source actually holds, side by side,
/// including the values the reader threw away.
/// </para>
///
/// <para>
/// It exists to be read once in a live dungeon (Mistwake — a visible multi-objective list) to
/// confirm the two things P0 could not settle from the game structs alone: that unrevealed "???"
/// objectives occupy counted slots in the director vector, and that the HUD array packs its
/// fraction low-half-first.
/// </para>
/// </summary>
public sealed class DebugWindow : Window
{
    private readonly ObjectiveReader _reader;
    private readonly DutyLifecycle _lifecycle;
    private readonly Func<string> _describeNearby;
    private readonly Func<string> _describeVnav;
    private readonly Func<string> _describeAriadne;
    private readonly Func<string> _describeShadow;
    private readonly Func<string> _describeRun;
    private readonly Func<string> _describeCompanions;

    public DebugWindow(
        ObjectiveReader reader,
        DutyLifecycle lifecycle,
        Func<string> describeNearby,
        Func<string> describeVnav,
        Func<string> describeAriadne,
        Func<string> describeShadow,
        Func<string> describeRun,
        Func<string> describeCompanions)
        : base("Theseus — Debug##TheseusDebug")
    {
        _reader = reader;
        _lifecycle = lifecycle;
        _describeNearby = describeNearby;
        _describeVnav = describeVnav;
        _describeAriadne = describeAriadne;
        _describeShadow = describeShadow;
        _describeRun = describeRun;
        _describeCompanions = describeCompanions;

        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 300),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        var diagnostics = _reader.ReadDiagnostics();
        DrawMovement();
        DrawShadow();
        DrawCompanionData();
        DrawNearby();
        DrawLifecycle();
        DrawSnapshot();
        DrawDirector(diagnostics);
        DrawToDoList(diagnostics);
    }

    /// <summary>
    /// What the solver's perception has learned — the in-game reading of the shadow driver's exit
    /// criterion: on a farm run the counts climb and the gates appear; the run itself is unchanged.
    /// </summary>
    private void DrawShadow()
    {
        TheseusTheme.SectionHeader("SOLVER (shadow)");
        ImGui.TextWrapped(_describeShadow());

        if (ImGui.Button("Copy solver"))
            ImGui.SetClipboardText(_describeShadow());
    }

    /// <summary>
    /// What is standing around you, by object kind and id.
    ///
    /// <para>
    /// Here because treasure coffers turned out not to be one thing: the Thundergust Griffin drops
    /// a <c>Treasure</c>-kind object, and the Treno Catoblepas drops something that is not one at
    /// all. Guessing at the difference was wrong three times; reading it off the object standing in
    /// front of you is not.
    /// </para>
    /// </summary>
    /// <summary>
    /// Who is moving the character, and whether vnavmesh thinks it is.
    ///
    /// <para>
    /// Built for one failure that inference could not crack: the run asks vnavmesh to move, the
    /// pathfind completes with a full set of waypoints, and the character does not shift a single
    /// yalm — once a second, indefinitely. Nothing in either plugin's log distinguishes "the move
    /// was refused" from "the move was accepted and abandoned instantly", and both were guessed at
    /// wrongly. These two lines, read while it is happening, settle it.
    /// </para>
    ///
    /// <para>
    /// The Ariadne row is the nav migration's own first check, and it is readable here before
    /// anything consumes it: in a duty it should say <c>connected True · zone 2 or 3 · ready True</c>
    /// within a fraction of a second of zoning in — while vnavmesh is still building.
    /// </para>
    /// </summary>
    private void DrawMovement()
    {
        TheseusTheme.SectionHeader("MOVEMENT");

        ImGui.TextColored(TheseusTheme.TextSecondary, "Run");
        ImGui.SameLine(110f);
        ImGui.TextWrapped(_describeRun());

        ImGui.TextColored(TheseusTheme.TextSecondary, "vnavmesh");
        ImGui.SameLine(110f);
        ImGui.TextWrapped(_describeVnav());

        ImGui.TextColored(TheseusTheme.TextSecondary, "Ariadne");
        ImGui.SameLine(110f);
        ImGui.TextWrapped(_describeAriadne());

        if (ImGui.Button("Copy movement"))
            ImGui.SetClipboardText($"{_describeRun()}\n{_describeVnav()}\n{_describeAriadne()}");
    }

    /// <summary>
    /// The Trust roster arrays, exactly as the game holds them.
    ///
    /// <para>
    /// Which index addresses which expansion's cast has now been guessed at twice and been wrong
    /// twice — both times returning a plausible eight-companion list assembled from the wrong
    /// offset, which is the hardest kind of wrong to notice. This prints every group's count and
    /// members so the answer can be read rather than inferred.
    /// </para>
    /// </summary>
    private void DrawCompanionData()
    {
        TheseusTheme.SectionHeader("TRUST ROSTER (raw)");
        ImGui.TextWrapped(_describeCompanions());

        if (ImGui.Button("Copy roster"))
            ImGui.SetClipboardText(_describeCompanions());
    }

    private void DrawNearby()
    {
        TheseusTheme.SectionHeader("NEARBY OBJECTS");
        ImGui.TextWrapped(_describeNearby());

        if (ImGui.Button("Copy"))
            ImGui.SetClipboardText(_describeNearby());
    }

    private void DrawLifecycle()
    {
        TheseusTheme.SectionHeader("DUTY LIFECYCLE");

        ImGui.TextColored(TheseusTheme.TextSecondary, "Phase");
        ImGui.SameLine(110f);
        ImGui.TextColored(
            _lifecycle.IsInDuty ? TheseusTheme.AccentPatina : TheseusTheme.TextDisabled,
            _lifecycle.Phase.ToString());

        // Crimson: this is the run identity a checkpoint is matched against, so it belongs to the
        // Thread and nothing else in this window may borrow the colour.
        ImGui.TextColored(TheseusTheme.TextSecondary, "Run key");
        ImGui.SameLine(110f);
        ImGui.TextColored(TheseusTheme.ThreadCrimson, _lifecycle.RunKey.ToString());
        TheseusTheme.HelpMarker(
            "territory / content id @ director start timestamp. The timestamp reads 0 in a plain " +
            "dungeon, so this cannot yet tell a re-queue from the original run — the raw fields " +
            "below are here to pick a replacement discriminator from.");

        var identity = DirectorAccess.Identity();
        ImGui.TextColored(TheseusTheme.TextSecondary, "Director");
        ImGui.SameLine(110f);
        ImGui.TextColored(TheseusTheme.TextPrimary,
            $"content {identity.ContentId} · event 0x{identity.EventId:X} · seq {identity.Sequence} · " +
            $"flags 0x{identity.Flags:X2} · start {identity.Start} · end {identity.End}");

        var (left, max) = DirectorAccess.ContentTime();
        ImGui.TextColored(TheseusTheme.TextSecondary, "Instance clock");
        ImGui.SameLine(110f);
        ImGui.TextColored(TheseusTheme.TextPrimary,
            left is null || max is null
                ? "—"
                : $"{left:0}s left of {max:0}s · {max - left:0}s elapsed");
        TheseusTheme.HelpMarker(
            "Elapsed is the leading candidate to replace the run key's dead start timestamp: it " +
            "resets on a fresh entry, which is exactly what tells a re-queue from the run before it.");
    }

    private void DrawSnapshot()
    {
        TheseusTheme.SectionHeader("READER OUTPUT");

        var snapshot = _reader.Read();
        if (!snapshot.Available)
        {
            ImGui.TextColored(TheseusTheme.StatusRed, "Unavailable — no source could be read.");
            return;
        }

        ImGui.TextColored(TheseusTheme.TextSecondary, "Source");
        ImGui.SameLine(110f);
        ImGui.TextColored(
            snapshot.Source == ObjectiveSource.Director ? TheseusTheme.StatusGreen : TheseusTheme.StatusYellow,
            snapshot.Source.ToString());
        if (snapshot.Source == ObjectiveSource.ToDoList)
            TheseusTheme.HelpMarker("Fell back to the HUD array — objectives will vanish if the Duty Information element is hidden.");

        ImGui.TextColored(TheseusTheme.TextSecondary, "Progress");
        ImGui.SameLine(110f);
        ImGui.TextColored(TheseusTheme.TextPrimary,
            $"{snapshot.CompletedCount}/{snapshot.TotalCount} complete, current index {snapshot.CurrentIndex}");
    }

    private static void DrawDirector(ObjectiveDiagnostics diagnostics)
    {
        TheseusTheme.SectionHeader("DIRECTOR TODOS (primary)");
        ImGui.TextColored(TheseusTheme.TextSecondary,
            $"director: {diagnostics.DirectorKind}    todos: {diagnostics.DirectorTodoCount}");

        if (diagnostics.DirectorRows.Count == 0)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "No director todos.");
            return;
        }

        if (!ImGui.BeginTable("##directorTodos", 7, TableFlags))
            return;

        try
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24f);
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Done", ImGuiTableColumnFlags.WidthFixed, 40f);
            ImGui.TableSetupColumn("Cur", ImGuiTableColumnFlags.WidthFixed, 44f);
            ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, 44f);
            ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var row in diagnostics.DirectorRows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextSecondary, row.Index.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(row.Enabled ? TheseusTheme.StatusGreen : TheseusTheme.TextDisabled,
                    row.Enabled ? "●" : "○");

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, row.Shape);

                ImGui.TableNextColumn();
                ImGui.TextColored(row.Complete ? TheseusTheme.ObjectiveDone : TheseusTheme.TextDisabled,
                    row.Complete ? "●" : "–");

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, row.RawCurrent.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, row.RawNeeded.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(
                    ObjectiveReader.LooksHidden(row.Text) ? TheseusTheme.ObjectiveHidden : TheseusTheme.TextPrimary,
                    ObjectiveReader.LooksHidden(row.Text) ? "(unrevealed)" : row.Text);
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        ImGui.TextColored(TheseusTheme.TextSecondary,
            "Slots are a fixed block; only the leading run of enabled ones belongs to this duty. " +
            "Unrevealed objectives are enabled and counted, which is what makes the total knowable.");
        ImGui.TextColored(TheseusTheme.TextSecondary,
            "Done is the game's own flag and has been seen staying false on completed objectives — " +
            "Cur/Need is what actually moves.");
    }

    private static void DrawToDoList(ObjectiveDiagnostics diagnostics)
    {
        TheseusTheme.SectionHeader("TODOLIST ARRAYS (fallback)");
        if (!diagnostics.ToDoListPresent)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "Number array not allocated.");
            return;
        }

        ImGui.TextColored(TheseusTheme.TextSecondary,
            $"DutyObjectiveCount: {diagnostics.ToDoListObjectiveCount}    " +
            $"DutyCompletedObjectives: 0x{diagnostics.ToDoListCompletedRaw:X8}");
        TheseusTheme.HelpMarker(
            "DutyCompletedObjectives is a bitmask — confirmed in Mistwake, where it read 0x3 with " +
            "objectives 0 and 1 done (a count would have read 2). The fallback uses it for " +
            "completion. Raw values are progress percentages, not counts: 100 means done.");

        if (diagnostics.ToDoListRows.Count == 0)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "No duty objectives in the HUD array.");
            return;
        }

        if (!ImGui.BeginTable("##todoList", 5, TableFlags))
            return;

        try
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 24f);
            ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Raw", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Decoded", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Text", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            foreach (var row in diagnostics.ToDoListRows)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextSecondary, row.Index.ToString());

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, row.Shape);

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, $"0x{row.RawValue:X8}");

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, $"{row.DecodedCurrent}/{row.DecodedTotal}");

                ImGui.TableNextColumn();
                ImGui.TextColored(TheseusTheme.TextPrimary, row.Text);
            }
        }
        finally
        {
            ImGui.EndTable();
        }

        ImGui.TextColored(TheseusTheme.TextSecondary,
            "If Decoded reads backwards against the in-game list, the packed halves are swapped.");
    }

    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit;
}
