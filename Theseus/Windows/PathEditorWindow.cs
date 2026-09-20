using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Services.Duty;
using Theseus.Services.Paths;
using Theseus.Services.Run;
using Theseus.Services.Thread;

namespace Theseus.Windows;

/// <summary>
/// Reads and repairs a stored route, step by step.
///
/// <para>
/// Imported routes are someone else's work, converted automatically, and some of them are simply
/// wrong — the library already contains a jump off the end of a path whose own note admits its
/// purpose is unknown. Without a way to look at the steps, a route that strands a character is a
/// dead end; with one it is a two-minute fix. Everything here writes to <b>our</b> stored copy;
/// the AutoDuty files it was converted from are never touched.
/// </para>
/// </summary>
public sealed class PathEditorWindow : Window
{
    private static readonly StepVerb[] Verbs = Enum.GetValues<StepVerb>();
    private static readonly StepTag[] Tags =
        [StepTag.Synced, StepTag.Unsynced, StepTag.Comment, StepTag.Treasure, StepTag.Revival, StepTag.W2W];

    private readonly PathStore _paths;
    private readonly Func<uint> _currentTerritory;
    private readonly Func<Vector3> _playerPosition;
    private readonly IObjectiveReader _objectives;
    private readonly Action<int> _stepOnce;
    private readonly Func<bool> _isRunActive;

    private ThreadPath? _path;
    private int _selected = -1;
    private bool _dirty;
    private bool _scrollToNearest;
    private string _filter = string.Empty;
    private string _arguments = string.Empty;
    private string _note = string.Empty;
    private int _argumentsForStep = -1;

    public PathEditorWindow(
        PathStore paths,
        Func<uint> currentTerritory,
        Func<Vector3> playerPosition,
        IObjectiveReader objectives,
        Action<int> stepOnce,
        Func<bool> isRunActive)
        : base("Theseus — Paths##TheseusPaths")
    {
        _paths = paths;
        _currentTerritory = currentTerritory;
        _playerPosition = playerPosition;
        _objectives = objectives;
        _stepOnce = stepOnce;
        _isRunActive = isRunActive;

        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 420),
            MaximumSize = new Vector2(1600, 1400),
        };
    }

    /// <summary>
    /// Selects the route for the current territory when the window opens.
    ///
    /// <para>
    /// Editing is nearly always about the dungeon you are standing in — you open this because
    /// something just went wrong in front of you. Only applied on open, and never over a selection
    /// with unsaved edits or one you chose yourself, so it cannot yank a route out from under you
    /// mid-edit.
    /// </para>
    /// </summary>
    public override void OnOpen()
    {
        if (_dirty)
            return;

        var territory = _currentTerritory();
        if (territory == 0 || _path?.TerritoryId == territory)
            return;

        var here = _paths.ForTerritory(territory).FirstOrDefault();
        if (here is null)
            return;

        _path = here;
        Select(-1);
    }

    public override void Draw()
    {
        DrawPathPicker();

        if (_path is null)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "Pick a route to inspect.");
            return;
        }

        // Deliberately the same call the runner makes, so the step highlighted here is exactly the
        // step Resume would pick up at — a second, subtly different idea of "where am I" would be
        // worse than none, because it would look authoritative while disagreeing.
        var nearest = _path.TerritoryId == _currentTerritory()
            ? PathRelocalizer.Find(_path, _playerPosition(), _objectives.Read())
            : null;

        DrawHeader(_path);

        // The header can delete the route out from under the rest of the frame — its confirmation
        // modal nulls _path on the spot. Returning from the header alone was not enough: the first
        // deletion in the field crashed here, drawing steps for a route that no longer existed.
        if (_path is null)
            return;

        DrawHereLine(nearest);
        ImGui.Separator();
        DrawSteps(_path, nearest?.StepIndex ?? -1);
        ImGui.Separator();
        DrawStepEditor(_path);
    }

    private void DrawHereLine(ResumePoint? nearest)
    {
        // Always shown, even for a route belonging to somewhere else: reading off coordinates is
        // half of what this window is for, and standing in the wrong zone is a normal way to work.
        var position = _playerPosition();
        var coordinates = $"{position.X:0.00}, {position.Y:0.00}, {position.Z:0.00}";

        ImGui.TextColored(TheseusTheme.TextSecondary, "You:");
        ImGui.SameLine();
        ImGui.TextColored(TheseusTheme.TextPrimary, coordinates);

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy"))
            ImGui.SetClipboardText(coordinates);

        if (nearest is null)
        {
            ImGui.SameLine();
            ImGui.TextColored(TheseusTheme.TextDisabled, "· not in this route's territory");
            return;
        }

        ImGui.TextColored(TheseusTheme.AccentPatina, "●");
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(TheseusTheme.TextPrimary,
            $"You are at step {nearest.Value.StepIndex} " +
            $"({nearest.Value.Distance:0} yalms" +
            $"{(nearest.Value.UsedObjectives ? ", objective-matched" : string.Empty)})" +
            (nearest.Value.IsConfident ? string.Empty : " — nothing close by, so this is a guess"));

        ImGui.SameLine();
        if (ImGui.Button("Scroll to"))
            _scrollToNearest = true;

        ImGui.SameLine();
        if (ImGui.Button("Edit that step"))
            Select(nearest.Value.StepIndex);
    }

    // ── Route selection ──

    private void DrawPathPicker()
    {
        var territory = _currentTerritory();

        ImGui.SetNextItemWidth(240f);
        ImGui.InputTextWithHint("##editorFilter", "filter routes...", ref _filter, 64);

        ImGui.SameLine();
        if (ImGui.Button("Here", new Vector2(60, 0)) && territory != 0)
        {
            _path = _paths.ForTerritory(territory).FirstOrDefault();
            Select(-1);
        }

        var routes = _paths.All
            .Where(p => string.IsNullOrWhiteSpace(_filter)
                        || p.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                        || p.TerritoryId.ToString().Contains(_filter))
            .OrderBy(p => p.TerritoryId)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##editorRoute", _path is null ? "Select a route" : $"{_path.Name} ({_path.TerritoryId})"))
        {
            foreach (var route in routes)
            {
                if (ImGui.Selectable($"{route.Name}  ({route.TerritoryId})##{route.Key}", route.Key == _path?.Key))
                {
                    _path = route;
                    Select(-1);
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawHeader(ThreadPath path)
    {
        ImGui.TextColored(TheseusTheme.TextSecondary,
            $"{path.Steps.Count} steps · {(path.IsObjectiveTagged ? "objective-tagged" : "objectives not learned yet")}");

        if (path.IsEdited)
        {
            ImGui.SameLine();
            ImGui.TextColored(TheseusTheme.AccentPatina, "· edited");
            TheseusTheme.HelpMarker("Hand-edited routes are never overwritten by an import, including \"Re-convert all\".");
        }

        if (_dirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(TheseusTheme.StatusYellow, "· unsaved");
        }

        using (ImRaii.Disabled(!_dirty))
        {
            if (ImGui.Button("Save", new Vector2(80, 0)))
            {
                path.IsEdited = true;
                PathValidator.Validate(path);
                _paths.Save(path);
                _dirty = false;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert", new Vector2(80, 0)))
        {
            _paths.Reload();
            _path = _paths.All.FirstOrDefault(p => p.Key == path.Key);
            Select(-1);
            _dirty = false;
        }

        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, TheseusTheme.StatusRed with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TheseusTheme.StatusRed);
        if (ImGui.Button("Delete route", new Vector2(110, 0)))
            ImGui.OpenPopup("Delete route?##deleteRoute");
        ImGui.PopStyleColor(2);

        if (DrawDeleteConfirmation(path))
            return;

        foreach (var blocker in path.Blockers)
            ImGui.TextColored(TheseusTheme.StatusRed, "⚠ " + blocker);

        foreach (var warning in path.Warnings)
            ImGui.TextColored(TheseusTheme.StatusYellow, "· " + warning);
    }

    /// <summary>
    /// The delete confirmation. Returns true when the route was deleted, so the caller stops
    /// drawing a path that no longer exists.
    ///
    /// <para>
    /// A modal rather than a modifier-key gate, because the cost is not symmetrical across routes:
    /// an import can be re-imported, but a recording is the only copy of a run someone actually
    /// walked, and one click should never be able to end it. The modal says exactly that before
    /// the red button does anything.
    /// </para>
    /// </summary>
    private bool DrawDeleteConfirmation(ThreadPath path)
    {
        var open = true;
        if (!ImGui.BeginPopupModal("Delete route?##deleteRoute", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return false;

        try
        {
            ImGui.TextColored(TheseusTheme.TextPrimary, $"Delete \"{path.Name}\"?");
            ImGui.Spacing();
            ImGui.TextColored(TheseusTheme.StatusRed,
                "This is permanent. The route is removed from disk and cannot be recovered.");
            ImGui.TextColored(TheseusTheme.TextSecondary,
                path.IsEdited
                    ? "This route carries hand edits that exist nowhere else."
                    : "An imported route can be brought back by re-importing from AutoDuty; a recording cannot.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.PushStyleColor(ImGuiCol.Button, TheseusTheme.StatusRed with { W = 0.75f });
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TheseusTheme.StatusRed);
            var confirmed = ImGui.Button("Delete permanently", new Vector2(150, 0));
            ImGui.PopStyleColor(2);

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)))
                ImGui.CloseCurrentPopup();

            if (!confirmed)
                return false;

            ImGui.CloseCurrentPopup();
            if (!_paths.Delete(path))
                return false;

            _path = null;
            Select(-1);
            _dirty = false;
            return true;
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    // ── Step list ──

    private void DrawSteps(ThreadPath path, int nearestStep)
    {
        ImGui.BeginChild("##stepList", new Vector2(0, 250f), true);
        try
        {
            if (!ImGui.BeginTable("##steps", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
                return;

            try
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 34f);
                ImGui.TableSetupColumn("Verb", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Position", ImGuiTableColumnFlags.WidthFixed, 160f);
                ImGui.TableSetupColumn("Arguments", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn("Obj", ImGuiTableColumnFlags.WidthFixed, 34f);
                ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                for (var i = 0; i < path.Steps.Count; i++)
                {
                    var step = path.Steps[i];
                    var isHere = i == nearestStep;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();

                    if (isHere)
                        ImGui.PushStyleColor(ImGuiCol.Text, TheseusTheme.AccentPatina);

                    if (ImGui.Selectable($"{(isHere ? "●" : " ")}{i}##step{i}", _selected == i,
                            ImGuiSelectableFlags.SpanAllColumns))
                        Select(i);

                    if (isHere)
                    {
                        ImGui.PopStyleColor();

                        if (_scrollToNearest)
                        {
                            ImGui.SetScrollHereY(0.5f);
                            _scrollToNearest = false;
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextColored(
                        step.Verb == StepVerb.Comment ? TheseusTheme.TextDisabled : TheseusTheme.TextPrimary,
                        step.Verb == StepVerb.Unknown ? step.RawVerb : step.Verb.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextColored(TheseusTheme.TextSecondary,
                        step.Position.IsSet ? step.Position.ToString() : "—");

                    ImGui.TableNextColumn();

                    // Arguments run to 75 characters in the real library and the column is far
                    // narrower, so the cell is a preview and the tooltip is the actual value.
                    var arguments = string.Join(" | ", step.Arguments);
                    ImGui.TextColored(TheseusTheme.TextSecondary, arguments);
                    if (arguments.Length > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip(arguments);

                    ImGui.TableNextColumn();
                    ImGui.TextColored(TheseusTheme.ThreadCrimson,
                        step.ObjectiveIndex == ThreadPath.UnknownObjective ? "—" : step.ObjectiveIndex.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextColored(TheseusTheme.TextDisabled, step.Note);
                }
            }
            finally
            {
                ImGui.EndTable();
            }
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    // ── Single-step editor ──

    private void DrawStepEditor(ThreadPath path)
    {
        if (_selected < 0 || _selected >= path.Steps.Count)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "Select a step to edit it.");
            return;
        }

        var step = path.Steps[_selected];
        TheseusTheme.SectionHeader($"STEP {_selected}");

        // Verb
        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo("Verb", step.Verb.ToString()))
        {
            foreach (var verb in Verbs)
            {
                if (ImGui.Selectable(verb.ToString(), verb == step.Verb))
                {
                    step.Verb = verb;
                    step.RawVerb = verb.ToString();
                    MarkDirty(path);
                }
            }

            ImGui.EndCombo();
        }

        // Position
        var position = new Vector3(step.Position.X, step.Position.Y, step.Position.Z);
        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputFloat3("Position", ref position))
        {
            step.Position = new PathPoint(position.X, position.Y, position.Z);
            MarkDirty(path);
        }

        ImGui.SameLine();
        DrawStepRunControls();

        ImGui.SameLine();
        if (ImGui.Button("Use mine"))
        {
            var here = _playerPosition();
            step.Position = new PathPoint(here.X, here.Y, here.Z);
            MarkDirty(path);
        }
        TheseusTheme.HelpMarker("Writes your current position into this step — the quickest way to fix a waypoint that leads somewhere unreachable.");

        // Arguments and note are buffered so typing does not rewrite the step on every keystroke.
        if (_argumentsForStep != _selected)
        {
            _arguments = string.Join(" | ", step.Arguments);
            _note = step.Note;
            _argumentsForStep = _selected;
        }

        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("Arguments", ref _arguments, 256))
        {
            step.Arguments = _arguments
                .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            MarkDirty(path);
        }
        TheseusTheme.HelpMarker("Separate multiple arguments with |  —  e.g. ObjectData;1016994;IsTargetable;false | ModifyIndex;-1");

        ImGui.SetNextItemWidth(300f);
        if (ImGui.InputText("Note", ref _note, 256))
        {
            step.Note = _note;
            MarkDirty(path);
        }

        var objective = step.ObjectiveIndex;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Objective", ref objective))
        {
            step.ObjectiveIndex = Math.Max(ThreadPath.UnknownObjective, objective);
            MarkDirty(path);
        }
        TheseusTheme.HelpMarker("Which duty objective this step belongs to. Learned automatically on a clean run; -1 means not yet known.");

        DrawTags(step, path);
        DrawStepActions(path);
    }

    /// <summary>
    /// Runs just the selected step, then stops.
    ///
    /// <para>
    /// The other half of editing by hand. A repaired waypoint is guesswork until something walks
    /// it, and the alternative — restart the route and wait to arrive at that step again — is slow
    /// enough that it discourages fixing anything. Refused while a run is active, because two
    /// things steering one character produces nonsense that looks like a pathfinding bug.
    /// </para>
    /// </summary>
    private void DrawStepRunControls()
    {
        var running = _isRunActive();

        using (ImRaii.Disabled(running))
        {
            if (ImGui.Button("Run this step"))
                _stepOnce(_selected);
        }

        if (running && ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop the run first — Theseus is already steering.");
    }

    private void DrawTags(ThreadStep step, ThreadPath path)
    {
        ImGui.TextColored(TheseusTheme.TextSecondary, "Tags");
        foreach (var tag in Tags)
        {
            var set = step.Tag.HasFlag(tag);
            if (ImGui.Checkbox(tag.ToString(), ref set))
            {
                step.Tag = set ? step.Tag | tag : step.Tag & ~tag;
                MarkDirty(path);
            }

            if (tag != Tags[^1])
                ImGui.SameLine();
        }
    }

    private void DrawStepActions(ThreadPath path)
    {
        ImGui.Spacing();

        using (ImRaii.Disabled(_selected <= 0))
        {
            if (ImGui.Button("Move up", new Vector2(80, 0)))
                Swap(path, _selected, _selected - 1);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_selected >= path.Steps.Count - 1))
        {
            if (ImGui.Button("Move down", new Vector2(90, 0)))
                Swap(path, _selected, _selected + 1);
        }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate", new Vector2(90, 0)))
        {
            var source = path.Steps[_selected];
            path.Steps.Insert(_selected + 1, new ThreadStep
            {
                Verb = source.Verb,
                RawVerb = source.RawVerb,
                Position = source.Position,
                Arguments = [.. source.Arguments],
                Tag = source.Tag,
                Note = source.Note,
                ObjectiveIndex = source.ObjectiveIndex,
            });
            Select(_selected + 1);
            MarkDirty(path);
        }

        ImGui.SameLine();
        if (ImGui.Button("Delete", new Vector2(70, 0)))
        {
            path.Steps.RemoveAt(_selected);
            Select(Math.Min(_selected, path.Steps.Count - 1));
            MarkDirty(path);
        }

        // Inserts get their own row: which side of the selected step a new waypoint lands on is
        // the whole question when routing around something, and burying one of the two at the end
        // of a row of unrelated buttons makes it easy to pick the wrong one.
        if (ImGui.Button("Insert before", new Vector2(110, 0)))
            InsertStep(path, _selected);

        ImGui.SameLine();
        if (ImGui.Button("Insert after", new Vector2(110, 0)))
            InsertStep(path, _selected + 1);
    }

    /// <summary>
    /// Adds a waypoint beside the selected step, seeded with its position so "Use mine" is the only
    /// edit usually needed.
    /// </summary>
    private void InsertStep(ThreadPath path, int index)
    {
        // Read before inserting: an insert-before shifts the selected step out from under us.
        var seed = path.Steps[_selected].Position;

        path.Steps.Insert(index, new ThreadStep
        {
            Verb = StepVerb.MoveTo,
            RawVerb = nameof(StepVerb.MoveTo),
            Position = seed,
        });

        Select(index);
        MarkDirty(path);
    }

    // ── Helpers ──

    private void Swap(ThreadPath path, int a, int b)
    {
        (path.Steps[a], path.Steps[b]) = (path.Steps[b], path.Steps[a]);
        Select(b);
        MarkDirty(path);
    }

    /// <summary>
    /// Marks the route changed and re-checks it.
    ///
    /// <para>
    /// Re-validating on every edit is the point, not politeness. Jumps are <b>relative</b>, so
    /// inserting or deleting a step silently re-points every jump that crosses it — the warning
    /// list is the only place that becomes visible before a run walks into it.
    /// </para>
    /// </summary>
    private void MarkDirty(ThreadPath path)
    {
        // Deliberately does NOT reset the text buffers. Editing the Arguments field calls this,
        // and resetting here re-ran split-then-rejoin on every keystroke: typing a trailing "|"
        // to begin a second argument had it deleted again before the next character arrived, so a
        // multi-argument step could not be typed at all. The buffers belong to the selection, and
        // only changing the selection may refill them.
        _dirty = true;
        PathValidator.Validate(path);
    }

    private void Select(int index)
    {
        _selected = index;
        _argumentsForStep = -1;
    }
}
