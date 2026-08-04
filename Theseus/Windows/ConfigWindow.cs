using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Config;
using Theseus.Services.Ipc;

namespace Theseus.Windows;

/// <summary>Navigation sections in the settings sidebar.</summary>
internal enum ConfigSection
{
    General,
    Dungeons,
    Thread,
    Fleet,
    Chores,
    Debug,
}

/// <summary>
/// Theseus settings window. Same layout as the rest of the suite — master toggle in the header,
/// left sidebar with dim small-cap group headers and an accent bar on the selected row, bordered
/// content pane, footer status line — carrying the verdigris accent instead of the family gold.
/// </summary>
public sealed class ConfigWindow : Window
{
    private const float SidebarWidth = 150f;

    private readonly TheseusConfig _config;
    private readonly Action _save;
    private readonly PluginPresence _presence;

    private ConfigSection _currentSection = ConfigSection.General;

    public ConfigWindow(TheseusConfig config, Action save, PluginPresence presence)
        : base("Theseus Settings##TheseusConfig", ImGuiWindowFlags.NoCollapse)
    {
        _config = config;
        _save = save;
        _presence = presence;

        Size = new Vector2(620, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 400),
            MaximumSize = new Vector2(900, 900),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        try
        {
            DrawHeader();
            ImGui.Separator();
            ImGui.Spacing();
            DrawMainLayout();
            ImGui.Spacing();
            ImGui.Separator();
            DrawFooter();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    // ------------------------------------------------------------------ header / footer

    private void DrawHeader()
    {
        var enabled = _config.Enabled;
        if (ImGui.Checkbox("Enable Theseus", ref enabled))
        {
            _config.Enabled = enabled;
            _save();
        }

        ImGui.SameLine(0f, 20f);
        var missing = _presence.MissingSummary();
        if (missing.Length == 0)
            TheseusTheme.StatusDot(_config.Enabled, "Ready", "Disabled");
        else
            ImGui.TextColored(TheseusTheme.StatusRed, "⚠ " + missing);

        ImGui.TextDisabled("Runs dungeons for the fleet — and picks up where it left off.");
    }

    private void DrawFooter()
    {
        ImGui.TextColored(TheseusTheme.TextSecondary, $"Theseus v{TheseusPlugin.PluginVersion}");

        ImGui.SameLine(0f, 20f);
        TheseusTheme.DependencyChip("vnavmesh", _presence.Vnavmesh);
        ImGui.SameLine(0f, 12f);
        TheseusTheme.DependencyChip("BossMod", _presence.BossMod);
        ImGui.SameLine(0f, 12f);
        TheseusTheme.DependencyChip("Daedalus", _presence.Daedalus);
    }

    // ------------------------------------------------------------------ layout

    private void DrawMainLayout()
    {
        var availableHeight = ImGui.GetContentRegionAvail().Y - 32f; // reserve the footer line

        ImGui.BeginChild("##SidebarContainer", new Vector2(SidebarWidth + 10f, availableHeight), false);
        DrawSidebar();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("##ContentArea", new Vector2(0, availableHeight), true);
        DrawCurrentSection();
        ImGui.EndChild();
    }

    private void DrawSidebar()
    {
        ImGui.BeginChild("##ConfigSidebar", new Vector2(SidebarWidth, 0), true);

        DrawCategoryHeader("RUN");
        DrawNavItem("General", ConfigSection.General);
        DrawNavItem("Dungeons", ConfigSection.Dungeons);
        ImGui.Spacing();

        DrawCategoryHeader("RECOVERY");
        // The Thread gets its own crimson so the resume system is identifiable on sight.
        DrawNavItem("The Thread", ConfigSection.Thread, TheseusTheme.ThreadCrimson);
        ImGui.Spacing();

        DrawCategoryHeader("FLEET");
        DrawNavItem("Coordination", ConfigSection.Fleet);
        ImGui.Spacing();

        DrawCategoryHeader("BETWEEN RUNS");
        DrawNavItem("Chores", ConfigSection.Chores);
        ImGui.Spacing();

        DrawCategoryHeader("SYSTEM");
        DrawNavItem("Debug", ConfigSection.Debug);

        ImGui.EndChild();
    }

    private static void DrawCategoryHeader(string label)
        => ImGui.TextColored(TheseusTheme.StatusGrey, label);

    private void DrawNavItem(string label, ConfigSection section, Vector4? color = null)
    {
        var isSelected = _currentSection == section;
        var rowAccent = color ?? TheseusTheme.AccentPatina;
        var rowWash = color is null ? TheseusTheme.AccentWash : TheseusTheme.ThreadWash;

        // Selection: faint wash + 2px accent bar on the left edge (suite identity).
        if (isSelected)
        {
            var cursorPos = ImGui.GetCursorScreenPos();
            var regionAvail = ImGui.GetContentRegionAvail();
            var drawList = ImGui.GetWindowDrawList();
            var rowMax = new Vector2(cursorPos.X + regionAvail.X,
                cursorPos.Y + ImGui.GetTextLineHeightWithSpacing());
            drawList.AddRectFilled(cursorPos, rowMax, ImGui.GetColorU32(rowWash));
            drawList.AddRectFilled(cursorPos, new Vector2(cursorPos.X + 2f, rowMax.Y),
                ImGui.GetColorU32(rowAccent));
        }

        ImGui.Indent(10);

        var textColor = isSelected ? rowAccent : color ?? TheseusTheme.TextSecondary;
        ImGui.PushStyleColor(ImGuiCol.Text, textColor);
        ImGui.PushStyleColor(ImGuiCol.Header, rowWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, rowWash);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, rowWash);

        var clicked = ImGui.Selectable($"  {label}##{section}", isSelected, ImGuiSelectableFlags.None,
            new Vector2(SidebarWidth - 25, 0));

        ImGui.PopStyleColor(4);
        ImGui.Unindent(10);

        if (clicked)
            _currentSection = section;
    }

    // ------------------------------------------------------------------ sections

    private void DrawCurrentSection()
    {
        switch (_currentSection)
        {
            case ConfigSection.General: DrawGeneralSection(); break;
            case ConfigSection.Dungeons: DrawDungeonsSection(); break;
            case ConfigSection.Thread: DrawThreadSection(); break;
            case ConfigSection.Fleet: DrawFleetSection(); break;
            case ConfigSection.Chores: DrawChoresSection(); break;
            case ConfigSection.Debug: DrawDebugSection(); break;
        }
    }

    private void DrawGeneralSection()
    {
        TheseusTheme.SectionHeader("GENERAL");

        var limit = _config.RunLimit;
        if (ImGui.InputInt("Run limit", ref limit))
        {
            _config.RunLimit = Math.Max(0, limit);
            _save();
        }
        TheseusTheme.HelpMarker("Stop after this many completed runs. 0 = keep going until you stop it.");

        TheseusTheme.SectionHeader("DEPENDENCIES");
        TheseusTheme.DependencyChip("vnavmesh — pathfinding and movement", _presence.Vnavmesh);
        TheseusTheme.DependencyChip("BossMod Reborn — boss mechanics", _presence.BossMod);
        TheseusTheme.DependencyChip("Daedalus — rotation and LAN relay", _presence.Daedalus);
        TheseusTheme.DependencyChip("Charon — equip upgrades (optional)", _presence.Charon);
    }

    private void DrawDungeonsSection()
    {
        TheseusTheme.SectionHeader("DUNGEONS");
        ImGui.TextWrapped(
            "Route selection and the path library land in P1. Routes are imported from your own " +
            "installed AutoDuty path folder and converted on this machine; the in-game recorder " +
            "comes later for fixes and new content.");
    }

    private void DrawThreadSection()
    {
        TheseusTheme.SectionHeader("THE THREAD", TheseusTheme.ThreadCrimson);
        ImGui.TextWrapped(
            "If a run is interrupted, Theseus resumes where it stopped instead of restarting. " +
            "The live duty objective list decides which part of the dungeon you are in; your " +
            "position decides the exact step within it.");
        ImGui.Spacing();

        var resume = _config.EnableResume;
        if (ImGui.Checkbox("Resume interrupted runs", ref resume))
        {
            _config.EnableResume = resume;
            _save();
        }

        var confirm = _config.ConfirmBeforeResume;
        if (ImGui.Checkbox("Ask before resuming", ref confirm))
        {
            _config.ConfirmBeforeResume = confirm;
            _save();
        }
        TheseusTheme.HelpMarker(
            "Off = resume automatically whenever the objective is unambiguous and your position " +
            "is confidently placed. Theseus always asks when it is not sure.");
    }

    private void DrawFleetSection()
    {
        TheseusTheme.SectionHeader("COORDINATION");
        ImGui.TextWrapped(
            "Every box runs the route itself and syncs on duty objectives, so a box that lags, " +
            "dies, or restarts rejoins where the fleet actually is.");
        ImGui.Spacing();

        var gates = _config.EnableFleetGates;
        if (ImGui.Checkbox("Hold at objective boundaries for the fleet", ref gates))
        {
            _config.EnableFleetGates = gates;
            _save();
        }

        var stale = _config.PeerStaleSeconds;
        if (ImGui.SliderFloat("Peer timeout (s)", ref stale, 5f, 60f, "%.0f"))
        {
            _config.PeerStaleSeconds = stale;
            _save();
        }
        TheseusTheme.HelpMarker(
            "A box unheard from for this long stops holding the gate, so one crashed client " +
            "cannot freeze the whole fleet.");
    }

    private void DrawChoresSection()
    {
        TheseusTheme.SectionHeader("BETWEEN RUNS");

        var repair = _config.EnableRepair;
        if (ImGui.Checkbox("Repair gear", ref repair))
        {
            _config.EnableRepair = repair;
            _save();
        }

        var threshold = _config.RepairThresholdPercent;
        if (ImGui.SliderInt("Repair below (%)", ref threshold, 5, 95))
        {
            _config.RepairThresholdPercent = threshold;
            _save();
        }

        var materia = _config.EnableMateriaExtraction;
        if (ImGui.Checkbox("Extract materia", ref materia))
        {
            _config.EnableMateriaExtraction = materia;
            _save();
        }
        TheseusTheme.HelpMarker("Pulls materia from spiritbond-100 gear. Runs on every box, not just one.");

        var charon = _config.EnableCharonUpgrades;
        if (ImGui.Checkbox("Equip upgrades via Charon", ref charon))
        {
            _config.EnableCharonUpgrades = charon;
            _save();
        }
        TheseusTheme.HelpMarker("Off by default — this changes your equipped gear.");
    }

    private void DrawDebugSection()
    {
        TheseusTheme.SectionHeader("DIAGNOSTICS");

        var debug = _config.DebugMode;
        if (ImGui.Checkbox("Debug mode", ref debug))
        {
            _config.DebugMode = debug;
            _save();
        }
        TheseusTheme.HelpMarker("Verbose run logging plus the live objective readout on the run window.");
    }
}
