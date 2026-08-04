using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Config;
using Theseus.Services.Duty;
using Theseus.Services.Ipc;
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
    private readonly Func<RunState> _runState;
    private readonly Action _openConfig;

    public RunWindow(
        TheseusConfig config,
        PluginPresence presence,
        IObjectiveReader objectives,
        Func<RunState> runState,
        Action openConfig)
        : base("Theseus##TheseusRun", ImGuiWindowFlags.NoCollapse)
    {
        _config = config;
        _presence = presence;
        _objectives = objectives;
        _runState = runState;
        _openConfig = openConfig;

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
        var state = _runState();
        var (color, label) = state switch
        {
            RunState.Idle => (TheseusTheme.StatusGrey, "Idle"),
            RunState.Faulted => (TheseusTheme.StatusRed, "Faulted"),
            RunState.Recover => (TheseusTheme.ThreadCrimson, "Recovering"),
            RunState.BossHandoff => (TheseusTheme.StatusYellow, "Boss — BossMod"),
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
    }

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

        // Start is intentionally inert until the run controller lands (P1) — a button that
        // silently does nothing is worse than one that says why.
        var canRun = _config.Enabled && _presence.CoreReady;
        using (ImRaii.Disabled(!canRun))
        {
            if (ImGui.Button("Start", new Vector2(80, 0)))
            {
                // TODO(P1): RunController.Start() — see the plan doc's Phases.
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80, 0)))
        {
            // TODO(P1): RunController.Stop()
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(90, 0)))
            _openConfig();

        if (!_config.Enabled)
            ImGui.TextColored(TheseusTheme.TextDisabled, "Theseus is disabled in settings.");
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
