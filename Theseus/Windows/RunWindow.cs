using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Config;
using Theseus.Services.Duty;
using Theseus.Services.Ipc;
using Theseus.Services.Paths;
using Theseus.Services.Run;

namespace Theseus.Windows;

/// <summary>
/// The window you actually watch while a run is going: what state the run is in, which duty
/// objective the game says you are on, and where the rest of the fleet is.
///
/// <para>
/// The objective list is deliberately front and centre — it is both the thing the user reads to
/// know how far along a run is, and the signal the whole resume system is built on, so showing
/// it verbatim makes the automation's reasoning legible instead of magic.
/// </para>
/// </summary>
public sealed class RunWindow : Window
{
    private readonly TheseusConfig _config;
    private readonly PluginPresence _presence;
    private readonly IObjectiveReader _objectives;
    private readonly RunController _run;
    private readonly DutyCatalog _catalog;
    private readonly PathStore _paths;
    private readonly Action _openConfig;
    private readonly Action _save;

    /// <summary>Companions a light party holds beside the player.</summary>
    private const int PartySlots = 3;

    private string _dutyFilter = string.Empty;
    private string _routeName = string.Empty;
    private int _hiddenCount;

    public RunWindow(
        TheseusConfig config,
        PluginPresence presence,
        IObjectiveReader objectives,
        RunController run,
        DutyCatalog catalog,
        PathStore paths,
        Action openConfig,
        Action save)
        : base("Theseus##TheseusRun")
    {
        _config = config;
        _presence = presence;
        _objectives = objectives;
        _run = run;
        _catalog = catalog;
        _paths = paths;
        _openConfig = openConfig;
        _save = save;

        Size = new Vector2(340, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 260),
            MaximumSize = new Vector2(600, 900),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawStatus();
            ImGui.Separator();

            if (_run.CanEnterDuty)
                DrawEntry();
            else
                DrawObjectives();

            ImGui.Separator();
            DrawControls();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void DrawStatus()
    {
        var state = _run.State;
        var (color, label) = state switch
        {
            RunState.Idle => (TheseusTheme.StatusGrey, "Idle"),
            RunState.Faulted => (TheseusTheme.StatusRed, "Faulted"),
            RunState.Recover => (TheseusTheme.ThreadCrimson, "Recovering"),
            RunState.BossHandoff => (TheseusTheme.StatusYellow, $"Boss — {_presence.BossHandlerName}"),
            RunState.Chores => (TheseusTheme.TextSecondary, "Chores"),
            _ => (TheseusTheme.AccentPatina, state.ToString()),
        };

        ImGui.TextColored(color, "●");
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(color, label);

        var missing = _presence.MissingSummary();
        if (missing.Length > 0)
        {
            ImGui.SameLine(0f, 12f);
            ImGui.TextColored(TheseusTheme.StatusRed, "⚠ " + missing);
        }

        ImGui.TextWrapped(_run.Status);

        if (_run.ActivePath is { } path)
        {
            ImGui.TextColored(TheseusTheme.TextSecondary,
                $"{path.Name} — step {_run.CurrentStepIndex + 1}/{path.Steps.Count}" +
                (path.IsObjectiveTagged ? " · objective-tagged" : " · learning objectives"));
        }

        var (players, slot, waiting) = _run.FleetState;
        if (players > 1)
        {
            // Slot is shown because it is the role: every box derives its duties from this number,
            // and a wrong one is the difference between fetching a key and waiting for it.
            ImGui.TextColored(
                waiting > 0 ? TheseusTheme.StatusYellow : TheseusTheme.PeerOnline,
                waiting > 0
                    ? $"Fleet {players} · slot {slot} · waiting for {waiting}"
                    : $"Fleet {players} · slot {slot} · together");
        }

        if (_run.ActivePath is null && _run.IsExploring)
        {
            // Says plainly that this is the fallback: without a route the run has no plan, and a
            // silent unmapped run reads like a route that is going badly.
            var (landmarks, objects) = _run.ExplorationProgress;
            ImGui.TextColored(TheseusTheme.StatusYellow,
                $"Exploring — no recorded route · {landmarks} landmarks, {objects} objects");
        }
    }

    /// <summary>
    /// The duty picker, shown in place of the objective list while standing in the field.
    ///
    /// <para>
    /// Alone, entry is Trust or Duty Support; in a party, the Duty Finder. The list is filtered to
    /// duties with a route by default, because entering content Theseus cannot run just leaves you
    /// standing in it.
    /// </para>
    /// </summary>
    private void DrawEntry()
    {
        TheseusTheme.SectionHeader("ENTER A DUTY");

        DrawExpansionFilter();

        var selected = _catalog.ByCondition(_config.LastDutyCondition);
        var hidden = 0;

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##duty", selected?.Label ?? "Pick a duty"))
        {
            try
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##dutyFilter", "Filter", ref _dutyFilter, 64);

                foreach (var duty in _catalog.Dungeons)
                {
                    if (!MatchesExpansion(duty))
                        continue;

                    if (_dutyFilter.Length > 0 &&
                        !duty.Name.Contains(_dutyFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!_config.ShowAllDutiesInPicker && !HasRoute(duty))
                    {
                        hidden++;
                        continue;
                    }

                    // Name left, the numbers tucked against the right edge — a hundred rows of
                    // "(Lv X, ilvl Y, Trust)" is noise; aligned columns read at a glance.
                    if (ImGui.Selectable($"{duty.Name}##cfc{duty.ContentFinderConditionId}",
                            duty.ContentFinderConditionId == _config.LastDutyCondition))
                    {
                        _config.LastDutyCondition = duty.ContentFinderConditionId;
                        _save();
                    }

                    var meta = $"Lv {duty.LevelRequired} · {duty.ItemLevelRequired}";
                    ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(meta).X - 24f);
                    ImGui.TextColored(TheseusTheme.TextSecondary, meta);
                }
            }
            finally
            {
                ImGui.EndCombo();
            }

            // Counted while drawing, so it only reports on the list you were just looking at.
            _hiddenCount = hidden;
        }

        if (_hiddenCount > 0 && !_config.ShowAllDutiesInPicker)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled,
                $"{_hiddenCount} dungeon(s) hidden — no Theseus route for them.");
        }

        var showAll = _config.ShowAllDutiesInPicker;
        if (ImGui.Checkbox("Show duties without a route", ref showAll))
        {
            _config.ShowAllDutiesInPicker = showAll;
            _save();
        }

        // A party queues through the Duty Finder, so there are no companions to choose.
        if (_run.InGroup)
        {
            DrawGroupEntry();
        }
        else
        {
            DrawCompanionMode(selected);
            DrawCompanions(selected);
        }
        DrawEnterButton(selected);
    }

    /// <summary>
    /// Which Trust companions to bring.
    ///
    /// <para>
    /// The roster comes from the game's sheets rather than the Trust window, so it is here whether
    /// or not the window has ever been opened — no loading, no reloading, and nothing to go stale
    /// when you switch expansion. A companion the duty will not accept is refused at entry, and the
    /// party falls back to the game's own choice rather than half-forming.
    /// </para>
    /// </summary>
    private void DrawCompanions(DutyListing? selected)
    {
        if (selected is not { HasDutySupport: true } || !UsesTrust(selected))
            return;

        var companions = _run.ReadCompanions(selected.ContentFinderConditionId, trust: true);
        if (companions.Count == 0)
            return;

        // Drop picks the current duty does not offer, so the count below never lies.
        var chosenHere = _config.TrustParty.Where(k => companions.Any(c => c.Key == k)).ToList();

        ImGui.Spacing();
        ImGui.TextColored(TheseusTheme.TextSecondary, "Trust party");
        ImGui.SameLine(110f);
        // A partial hand is worse than none: the commence needs a full light party, so one or two
        // picks would be refused with "Role requirements unmet" and fall back anyway. Say so here
        // rather than letting the game say it in red.
        var (countColor, countLabel) = chosenHere.Count switch
        {
            0 => (TheseusTheme.TextPrimary, "the game will pick"),
            PartySlots => (TheseusTheme.AccentPatina, $"{PartySlots}/{PartySlots} chosen"),
            _ => (TheseusTheme.StatusYellow, $"{chosenHere.Count}/{PartySlots} — pick {PartySlots} or none"),
        };
        ImGui.TextColored(countColor, countLabel);
        if (chosenHere.Count is > 0 and < PartySlots && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "The duty needs a full party. A partial pick is ignored and the game chooses instead.");
        }

        if (chosenHere.Count > 0)
        {
            ImGui.SameLine(0f, 10f);
            if (ImGui.SmallButton("clear"))
            {
                _config.TrustParty.Clear();
                _save();
            }
        }

        ImGui.Spacing();

        // Two columns of role-dotted cards. The dot is the game's own role colour, so a party
        // that will not fit — two tanks, no healer — is visible before the game refuses it.
        var columnWidth = (ImGui.GetContentRegionAvail().X - 8f) / 2f;

        for (var i = 0; i < companions.Count; i++)
        {
            var companion = companions[i];
            var chosen = chosenHere.Contains(companion.Key);
            var slotsFull = !chosen && chosenHere.Count >= PartySlots;

            if (i % 2 == 1)
                ImGui.SameLine(columnWidth + 12f);

            ImGui.TextColored(
                slotsFull ? TheseusTheme.TextDisabled : TheseusTheme.RoleColor(companion.Role),
                chosen ? "●" : "○");
            ImGui.SameLine(0f, 5f);

            using (ImRaii.Disabled(slotsFull))
            {
                ImGui.PushStyleColor(ImGuiCol.Text,
                    chosen ? TheseusTheme.AccentPatina : TheseusTheme.TextPrimary);
                var clicked = ImGui.Selectable(
                    $"{companion.Name}##trust{companion.Key}", chosen,
                    ImGuiSelectableFlags.None, new Vector2(columnWidth - 70f, 0f));
                ImGui.PopStyleColor();

                if (clicked)
                {
                    if (chosen)
                        _config.TrustParty.Remove(companion.Key);
                    else
                        _config.TrustParty.Add(companion.Key);

                    _save();
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{companion.ClassName} · {companion.Role}");
        }
    }

    private void DrawExpansionFilter()
    {
        var current = _config.DutyPickerExpansion;
        var label = current < 0
            ? "All expansions"
            : _catalog.Expansions.FirstOrDefault(e => e.Id == (uint)current).Name is { Length: > 0 } selectedName
                ? selectedName
                : "All expansions";

        ImGui.SetNextItemWidth(-1f);
        if (!ImGui.BeginCombo("##expansion", label))
            return;

        try
        {
            if (ImGui.Selectable("All expansions", current < 0))
            {
                _config.DutyPickerExpansion = -1;
                _save();
            }

            foreach (var (id, name) in _catalog.Expansions)
            {
                if (ImGui.Selectable(name, current == (int)id))
                {
                    _config.DutyPickerExpansion = (int)id;
                    _save();
                }
            }
        }
        finally
        {
            ImGui.EndCombo();
        }
    }

    /// <summary>
    /// Trust or Duty Support, where the duty has both.
    ///
    /// <para>
    /// They are separate systems and the choice is a real one. Duty Support brings the story's own
    /// cast and asks nothing of you, which is what an MSQ dungeon wants; Trust brings companions
    /// you pick and level. Only offered where both exist — everything before Shadowbringers has
    /// Duty Support alone, so there is nothing to choose and the row says so rather than showing a
    /// control that cannot move.
    /// </para>
    /// </summary>
    private void DrawCompanionMode(DutyListing? selected)
    {
        if (selected is not { HasDutySupport: true })
            return;

        ImGui.Spacing();

        ImGui.TextColored(TheseusTheme.TextSecondary, "Companions");
        ImGui.SameLine(110f);

        if (!selected.SupportsTrust)
        {
            ImGui.TextColored(TheseusTheme.TextPrimary, "Duty Support");
            TheseusTheme.HelpMarker("This duty predates Trust — the story's own cast comes along.");
            return;
        }

        var dutySupport = _config.PreferDutySupport;

        if (ImGui.RadioButton("Trust", !dutySupport))
        {
            _config.PreferDutySupport = false;
            _save();
        }

        ImGui.SameLine(0f, 12f);
        if (ImGui.RadioButton("Duty Support", dutySupport))
        {
            _config.PreferDutySupport = true;
            _save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The story's own cast, with nothing to pick — what an MSQ dungeon wants.");
    }

    /// <summary>Grouped: who queues, and that the Duty Finder is the door.</summary>
    private void DrawGroupEntry()
    {
        var (players, _, _) = _run.FleetState;
        ImGui.Spacing();
        ImGui.TextColored(TheseusTheme.TextSecondary, "Entry");
        ImGui.SameLine(110f);
        ImGui.TextColored(TheseusTheme.TextPrimary, $"Duty Finder · party of {players}");
        ImGui.TextColored(TheseusTheme.TextSecondary,
            _run.IsPartyLeader
                ? "You lead the party — this box queues."
                : "The party leader queues — this box waits and commences.");
    }

    private void DrawEnterButton(DutyListing? selected)
    {
        var grouped = _run.InGroup;

        // Alone, Duty Support is the only door, so a duty without one cannot be entered at all —
        // that outranks the route warning. A party queues through the Duty Finder instead.
        var blocked = selected is null || (!grouped && !selected.HasDutySupport);

        var label = !grouped ? "Enter and run"
            : _run.IsPartyLeader ? "Queue and run" : "Wait for the queue and run";

        ImGui.Spacing();
        using (ImRaii.Disabled(blocked || !_config.Enabled || !_presence.CoreReady))
        {
            if (TheseusTheme.AccentButton(label) && selected is not null)
                _run.Enter(selected, UsesTrust(selected));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(grouped
                ? "Queues the party through the Duty Finder, commences, and starts the route once inside."
                : "Opens the companion window, commences, and starts the route once you are inside.");
        }

        if (selected is null)
            return;

        if (!grouped && !selected.HasDutySupport)
        {
            ImGui.TextColored(TheseusTheme.StatusRed,
                "No Duty Support — Theseus cannot enter this one solo.");
        }
        else if (!HasRoute(selected))
        {
            ImGui.TextColored(TheseusTheme.StatusYellow,
                "No route for this duty — it will enter, then sit idle.");
        }
    }

    private bool HasRoute(DutyListing duty) => _paths.ForTerritory(duty.TerritoryId).Count > 0;

    /// <summary>Which door this duty will be entered through, given what it offers and what you chose.</summary>
    private bool UsesTrust(DutyListing duty) => duty.SupportsTrust && !_config.PreferDutySupport;

    private bool MatchesExpansion(DutyListing duty)
        => _config.DutyPickerExpansion < 0 || duty.ExpansionId == (uint)_config.DutyPickerExpansion;

    private void DrawObjectives()
    {
        TheseusTheme.SectionHeader("DUTY OBJECTIVES");

        var snapshot = _objectives.Read();
        if (!snapshot.Available)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "No objective data.");
            ImGui.TextWrapped("Resume will fall back to position only.");
            return;
        }

        if (snapshot.TotalCount == 0)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, "No objectives for this duty.");
            return;
        }

        foreach (var objective in snapshot.Objectives)
        {
            var label = objective.Hidden
                ? "???"
                : objective.Total > 1
                    ? $"{objective.Text}  {objective.Current}/{objective.Total}"
                    : objective.Text;

            TheseusTheme.ObjectiveRow(label, objective.IsComplete, objective.Index == snapshot.CurrentIndex);
        }

        ImGui.Spacing();
        ImGui.TextColored(TheseusTheme.TextSecondary,
            $"{snapshot.CompletedCount}/{snapshot.TotalCount} complete");
    }

    private void DrawControls()
    {
        ImGui.Spacing();

        var canRun = _config.Enabled && _presence.CoreReady;
        using (ImRaii.Disabled(!canRun))
        {
            // Crimson is the Thread's colour and this is the Thread's button — the only control in
            // the window that picks the run back up from where it stopped.
            ImGui.PushStyleColor(ImGuiCol.Button, TheseusTheme.ThreadDim);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, TheseusTheme.ThreadCrimson);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, TheseusTheme.ThreadCrimson);
            var resume = ImGui.Button("Resume", new Vector2(80, 0));
            ImGui.PopStyleColor(3);
            if (resume)
                _run.Start();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Picks up at the nearest step to where you are standing.");

        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80, 0)))
            _run.Stop();

        ImGui.SameLine();
        using (ImRaii.Disabled(!canRun))
        {
            if (ImGui.Button("Restart", new Vector2(80, 0)))
                _run.Restart();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Runs from step 0. Only correct at the dungeon entrance — partway in, this walks " +
                "back through everything already cleared, and past a one-way drop it cannot.");
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(90, 0)))
            _openConfig();

        ImGui.Spacing();

        // Here rather than only in settings: this is the switch you reach for on the way out the
        // door, and digging through a settings window to flip it defeats the point.
        var autoStart = _config.AutoStartInDuty;
        if (ImGui.Checkbox("Start automatically on entering a duty", ref autoStart))
        {
            _config.AutoStartInDuty = autoStart;
            _save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "However you arrive — roulette, manual queue, party finder. Content with no usable " +
                "route stays idle and says so rather than erroring.");
        }

        if (!_config.Enabled)
            ImGui.TextColored(TheseusTheme.TextDisabled, "Theseus is disabled in settings.");

        DrawRecorder();
    }

    /// <summary>
    /// Recording a route by playing the dungeon.
    ///
    /// <para>
    /// Run it as a tank. A tank's run is the only one that carries the wall-to-wall information —
    /// chain pulls show up as ground covered while in combat, which a damage dealer never does — and
    /// the recorder tags only the combat-mode switch, so the same route still runs plainly for
    /// everyone else.
    /// </para>
    /// </summary>
    private void DrawRecorder()
    {
        if (_run.CanEnterDuty)
            return;

        ImGui.Spacing();

        if (!_run.IsRecording)
        {
            // A recording paused by leaving the duty is still worth its marks — offer the save
            // from out here rather than silently holding data the user cannot reach.
            if (_run.HasPendingRecording)
            {
                ImGui.TextColored(TheseusTheme.StatusYellow,
                    $"Recording paused — {_run.RecordedSamples} marks held.");

                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("##routeName", "Route name", ref _routeName, 64);

                if (ImGui.Button("Save recording", new Vector2(-70f, 0f)))
                    _run.StopRecording(_routeName.Length > 0 ? _routeName : "Recorded route");

                ImGui.SameLine();
                if (ImGui.Button("Discard"))
                    _run.DiscardRecording();

                return;
            }

            if (ImGui.Button("Record route", new Vector2(-1f, 0f)))
            {
                _routeName = _routeName.Length > 0 ? _routeName : "Recorded route";
                _run.StartRecording();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Watches you play and writes the route out when you stop. Run it as a tank so " +
                    "the wall-to-wall pulls get recorded.");
            }

            return;
        }

        ImGui.TextColored(TheseusTheme.ThreadCrimson, $"● Recording — {_run.RecordedSamples} marks");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##routeName", "Route name", ref _routeName, 64);

        if (ImGui.Button("Stop and save", new Vector2(-1f, 0f)))
            _run.StopRecording(_routeName.Length > 0 ? _routeName : "Recorded route");
    }
}

/// <summary>Minimal scoped-disable helper (Dalamud's ImRaii is not exposed by this binding).</summary>
internal static class ImRaii
{
    public static DisabledScope Disabled(bool disabled) => new(disabled);

    internal readonly struct DisabledScope : IDisposable
    {
        private readonly bool _active;

        public DisabledScope(bool active)
        {
            _active = active;
            if (_active)
                ImGui.BeginDisabled();
        }

        public void Dispose()
        {
            if (_active)
                ImGui.EndDisabled();
        }
    }
}
