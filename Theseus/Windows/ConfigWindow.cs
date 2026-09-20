using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Theseus.Config;
using Theseus.Services.Ipc;
using Theseus.Services.Paths;

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
    private readonly PathStore _pathStore;
    private readonly string _autoDutyDirectory;

    private readonly Func<uint> _currentTerritory;
    private readonly Action _openPathEditor;

    private ConfigSection _currentSection = ConfigSection.General;
    private string _importStatus = string.Empty;
    private bool _showAllPaths;
    private string _pathSearch = string.Empty;

    public ConfigWindow(
        TheseusConfig config,
        Action save,
        PluginPresence presence,
        PathStore pathStore,
        string autoDutyDirectory,
        Func<uint> currentTerritory,
        Action openPathEditor)
        : base("Theseus Settings##TheseusConfig")
    {
        _config = config;
        _save = save;
        _presence = presence;
        _pathStore = pathStore;
        _autoDutyDirectory = autoDutyDirectory;
        _currentTerritory = currentTerritory;
        _openPathEditor = openPathEditor;

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
        TheseusTheme.DependencyChip(_presence.BossHandlerName, _presence.BossHandler);
        ImGui.SameLine(0f, 12f);
        TheseusTheme.DependencyChip("Daedalus", _presence.Daedalus);

        // Only when it is the selected source: otherwise it is an optional extra, and this line is
        // for what a run needs as configured.
        if (_config.NavSource == NavSource.Ariadne)
        {
            ImGui.SameLine(0f, 12f);
            TheseusTheme.DependencyChip("Ariadne", _presence.Ariadne);
        }
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

        var matchRole = _config.MatchRouteToRole;
        if (ImGui.Checkbox("Match the route to my role", ref matchRole))
        {
            _config.MatchRouteToRole = matchRole;
            _save();
        }

        TheseusTheme.HelpMarker(
            "Read at the start of each run, as AutoDuty does it. A tank gets the wall-to-wall " +
            "route and runs its W2W-tagged steps, merging packs; everyone else skips those steps " +
            "and kills each pack where it stands.");

        var autoStart = _config.AutoStartInDuty;
        if (ImGui.Checkbox("Start automatically when a duty begins", ref autoStart))
        {
            _config.AutoStartInDuty = autoStart;
            _save();
        }

        TheseusTheme.HelpMarker(
            "However you got there — roulette, manual queue, party finder. A territory with no " +
            "usable route stays idle and says so rather than erroring.");

        var explore = _config.ExploreUnmappedZones;
        if (ImGui.Checkbox("Explore zones with no recorded route", ref explore))
        {
            _config.ExploreUnmappedZones = explore;
            _save();
        }

        TheseusTheme.HelpMarker(
            "A fallback, and a much worse run than a recorded route: it fights what it can see and " +
            "walks to the map's own labels when a room is quiet, knowing nothing about the " +
            "dungeon's shape. What it buys is that new content works on the day it ships. Zones " +
            "that already have a route are unaffected.");

        var solver = _config.SolverDrives;
        if (ImGui.Checkbox("Let the solver drive zones with no recorded route", ref solver))
        {
            _config.SolverDrives = solver;
            _save();
        }

        TheseusTheme.HelpMarker(
            "Picks the solver's own route through the dungeon instead of the frontier navigator's: " +
            "it fights what is in the way, takes what is worth taking, discovers the edges the " +
            "mesh refuses and walks to ground nobody has looked at yet. It hands the run back to " +
            "the frontier navigator when it runs out of ideas, so the worst case is a slower walk. " +
            "Needs Ariadne answering with a reachable grid, and only ever applies to territories " +
            "with no recorded route — everything with one runs exactly as it does today.");

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("Pathfinding source", _config.NavSource == NavSource.Ariadne ? "Ariadne" : "vnavmesh"))
        {
            foreach (var source in Enum.GetValues<NavSource>())
            {
                var label = source == NavSource.Ariadne ? "Ariadne" : "vnavmesh";
                if (ImGui.Selectable(label, _config.NavSource == source))
                {
                    _config.NavSource = source;
                    _save();
                }
            }

            ImGui.EndCombo();
        }

        TheseusTheme.HelpMarker(
            "Who computes paths. Both are normally installed and whichever is not selected is the " +
            "fallback: a move the selected one cannot answer is handed to the other, once, so a run " +
            "never stalls on its source. Ariadne routes out of process, so a zone is routable about " +
            "a tenth of a second after zoning in instead of after vnavmesh's build.");

        // The migration is flipped one territory at a time, so the state worth reporting is the one
        // that would silently leave runs on the other source.
        if (_config.NavSource == NavSource.Ariadne && !_presence.Ariadne)
        {
            ImGui.TextColored(TheseusTheme.StatusYellow,
                "Ariadne is not loaded — runs fall back to vnavmesh.");
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("Boss handling", _presence.BossHandlerName))
        {
            foreach (var handler in Enum.GetValues<BossHandler>())
            {
                var label = handler == BossHandler.Minerva ? "Minerva" : "BossMod Reborn";
                if (ImGui.Selectable(label, _config.BossHandler == handler))
                {
                    _config.BossHandler = handler;
                    _save();
                }
            }

            ImGui.EndCombo();
        }

        TheseusTheme.HelpMarker(
            "Which plugin runs boss mechanics. Theseus switches its AI on at the start of a run and " +
            "never off. Only the selected one needs to be installed.");

        if (_config.BossHandler == BossHandler.Minerva)
        {
            var preset = _config.MinervaPreset;
            ImGui.SetNextItemWidth(180f);
            if (ImGui.InputText("Minerva preset", ref preset, 64))
            {
                _config.MinervaPreset = preset;
                _save();
            }

            TheseusTheme.HelpMarker(
                "Minerva has no command to turn its AI on — Theseus claims this dodge preset instead. " +
                "Create it in Minerva with auto-dodge enabled; its built-in Default preset has it off.");
        }

        var loot = _config.LootChests;
        if (ImGui.Checkbox("Loot treasure coffers", ref loot))
        {
            _config.LootChests = loot;
            _save();
        }

        // Hidden rather than greyed out while looting is off: it is a refinement of a choice that
        // has not been made yet, and a disabled control still asks to be read.
        if (_config.LootChests)
        {
            ImGui.Indent(20f);

            var bossOnly = _config.LootBossChestsOnly;
            if (ImGui.Checkbox("Boss treasure only", ref bossOnly))
            {
                _config.LootBossChestsOnly = bossOnly;
                _save();
            }

            TheseusTheme.HelpMarker(
                "A boss chest is free — it is in the room you are already standing in when the " +
                "fight ends. A route chest costs a detour, and routes tag the whole excursion, so " +
                "skipping them makes the run go straight past rather than stand at an unopened " +
                "coffer.");

            ImGui.Unindent(20f);
        }

        var leave = _config.LeaveWhenComplete;
        if (ImGui.Checkbox("Leave the duty when it is cleared", ref leave))
        {
            _config.LeaveWhenComplete = leave;
            _save();
        }

        TheseusTheme.HelpMarker(
            "Waits a few seconds for loot, then leaves. Turn off while proving a route so the run " +
            "stops inside and waits for you.");

        var limit = _config.RunLimit;
        if (ImGui.InputInt("Run limit", ref limit))
        {
            _config.RunLimit = Math.Max(0, limit);
            _save();
        }
        TheseusTheme.HelpMarker("Stop after this many completed runs. 0 = keep going until you stop it.");

        TheseusTheme.SectionHeader("DEPENDENCIES");
        TheseusTheme.DependencyChip("vnavmesh — pathfinding and movement", _presence.Vnavmesh);
        TheseusTheme.DependencyChip("Ariadne — alternative pathfinding (optional)", _presence.Ariadne);
        TheseusTheme.DependencyChip($"{_presence.BossHandlerName} — boss mechanics", _presence.BossHandler);
        TheseusTheme.DependencyChip("Daedalus — rotation and LAN relay", _presence.Daedalus);
        TheseusTheme.DependencyChip("Charon — equip upgrades (optional)", _presence.Charon);
    }

    private void DrawDungeonsSection()
    {
        TheseusTheme.SectionHeader("PATH LIBRARY");

        var stored = _pathStore.All;
        var runnable = 0;
        foreach (var path in stored)
        {
            if (path.IsRunnable)
                runnable++;
        }

        ImGui.TextColored(TheseusTheme.TextPrimary, $"{stored.Count} routes stored, {runnable} runnable.");
        if (stored.Count > runnable)
        {
            ImGui.TextColored(TheseusTheme.StatusYellow,
                $"{stored.Count - runnable} need a recorded path — they use AutoDuty's own hardcoded logic.");
        }

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Routes are converted from the AutoDuty library already on this machine, once, and " +
            "kept in our own format. Nothing is downloaded and nothing is redistributed — " +
            "AutoDuty's files stay where they are and never leave your PC.");

        ImGui.Spacing();
        if (ImGui.Button("Open path editor", new Vector2(190, 0)))
            _openPathEditor();
        TheseusTheme.HelpMarker(
            "Inspect a route step by step and repair it. Hand-edited routes are never overwritten " +
            "by an import.");

        ImGui.Spacing();
        if (ImGui.Button("Import from AutoDuty", new Vector2(190, 0)))
            _importStatus = _pathStore.ImportFrom(_autoDutyDirectory).ToString();

        ImGui.SameLine();
        if (ImGui.Button("Re-convert all", new Vector2(130, 0)))
            _importStatus = _pathStore.ImportFrom(_autoDutyDirectory, force: true).ToString();

        TheseusTheme.HelpMarker(
            "Re-convert only after a Theseus update changes the converter. A normal import already " +
            "picks up any path file you have edited.");

        if (_importStatus.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(TheseusTheme.AccentPatina, _importStatus);
        }

        ImGui.Spacing();
        ImGui.TextColored(TheseusTheme.TextDisabled, _autoDutyDirectory);

        DrawPaths();
    }

    /// <summary>
    /// Route browser: everything in the library, or just the territory you are standing in.
    ///
    /// <para>
    /// Route choice is not cosmetic. Twenty-seven territories carry variants — wall-to-wall pulls
    /// meant for a tank, a gentler route for everyone else, separate exits in the variant dungeons
    /// — so letting the runner pick means a healer quietly running the tank's pulls. The current
    /// view is the one you want mid-farm; the full list is for setting a fleet up beforehand.
    /// </para>
    /// </summary>
    private void DrawPaths()
    {
        TheseusTheme.SectionHeader("PATHS");

        var territory = _currentTerritory();

        if (ImGui.RadioButton("Current territory", !_showAllPaths))
            _showAllPaths = false;
        ImGui.SameLine(0f, 16f);
        if (ImGui.RadioButton("All paths", _showAllPaths))
            _showAllPaths = true;

        if (_showAllPaths)
        {
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##pathSearch", "filter by dungeon name or territory id...", ref _pathSearch, 64);
        }

        var groups = _pathStore.All
            .GroupBy(p => p.TerritoryId)
            .Where(g => _showAllPaths ? MatchesSearch(g) : g.Key == territory)
            .OrderBy(g => g.Key)
            .ToList();

        ImGui.Spacing();

        if (groups.Count == 0)
        {
            ImGui.TextColored(TheseusTheme.TextDisabled, _showAllPaths
                ? "Nothing matches that filter."
                : territory == 0
                    ? "Not in a duty."
                    : $"No route stored for territory {territory}.");
            return;
        }

        ImGui.BeginChild("##pathList", new Vector2(0, 240f), true);
        try
        {
            foreach (var group in groups)
                DrawTerritoryRoutes(group, territory);
        }
        finally
        {
            ImGui.EndChild();
        }
    }

    private bool MatchesSearch(IGrouping<uint, ThreadPath> group)
    {
        if (string.IsNullOrWhiteSpace(_pathSearch))
            return true;

        var needle = _pathSearch.Trim();
        return group.Key.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase)
               || group.Any(p => p.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawTerritoryRoutes(IGrouping<uint, ThreadPath> group, uint currentTerritory)
    {
        var routes = group.OrderBy(p => p.Name.Length).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // The shortest name is the plain dungeon; every variant decorates it.
        var heading = routes[0].Name;
        var isHere = group.Key == currentTerritory;

        ImGui.TextColored(isHere ? TheseusTheme.AccentPatina : TheseusTheme.TextSecondary,
            $"{heading}  ({group.Key})" + (isHere ? "  — you are here" : string.Empty));

        var selectedKey = _config.PreferredRoutes.GetValueOrDefault(group.Key);
        var selected = routes.FirstOrDefault(p => p.Key == selectedKey && p.IsRunnable)
                       ?? routes.FirstOrDefault(p => p.IsRunnable)
                       ?? routes[0];

        ImGui.Indent(12f);
        foreach (var route in routes)
        {
            var chosen = route.Key == selected.Key;

            // Only meaningful where there is a choice — a lone route needs no marker arguing
            // that it was selected.
            if (routes.Count > 1)
            {
                ImGui.TextColored(chosen ? TheseusTheme.AccentPatina : TheseusTheme.TextDisabled, chosen ? "●" : "○");
                ImGui.SameLine(0f, 6f);
            }

            var color = route.IsRunnable ? TheseusTheme.TextPrimary : TheseusTheme.StatusYellow;
            ImGui.TextColored(color, route.Name);

            if (routes.Count > 1 && route.IsRunnable && ImGui.IsItemClicked())
            {
                _config.PreferredRoutes[group.Key] = route.Key;
                _save();
            }

            ImGui.SameLine();
            ImGui.TextColored(
                route.Variant == RouteVariant.Standard ? TheseusTheme.TextDisabled : TheseusTheme.AccentPatina,
                route.Variant switch
                {
                    RouteVariant.TankWallToWall => "· tank W2W",
                    RouteVariant.OtherWallToWall => "· non-tank W2W",
                    RouteVariant.WallToWall => "· W2W",
                    _ => "· standard",
                });

            ImGui.SameLine();
            ImGui.TextColored(TheseusTheme.TextDisabled,
                $"· {route.Steps.Count} steps" +
                (route.IsObjectiveTagged ? " · objective-tagged" : string.Empty) +
                (route.IsRunnable ? string.Empty : " · needs a recorded path"));
        }

        ImGui.Unindent(12f);
        ImGui.Spacing();
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
