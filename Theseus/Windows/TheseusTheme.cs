using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Theseus.Windows;

/// <summary>
/// Theseus palette + shared draw helpers.
///
/// <para>
/// Same dark Greek-pantheon chassis as Daedalus / Charon / Caduceus — identical background
/// layers, text ramp and status colours, so the family reads as one suite — but Theseus owns
/// its own accent so you can tell at a glance which plugin's window you are looking at.
/// </para>
///
/// <para>
/// <b>Accent: verdigris.</b> The patina bronze takes with age — the colour of the labyrinth
/// itself rather than the gold Daedalus is signed in. It is also maximally distinct from the
/// family gold, and critically it leaves red / amber / green free to mean only what they mean
/// everywhere else: error, warning, good. An accent that collides with your status ramp is an
/// accent that makes a broken run look decorative.
/// </para>
///
/// <para>
/// <b>Crimson is reserved.</b> <see cref="ThreadCrimson"/> is Ariadne's thread and is used for
/// exactly one thing: the resume system — checkpoints, the recovered step, the trail back. It
/// is never used for ordinary chrome, so wherever crimson appears in the UI it means "this is
/// what we know about where you were". See the Thread section of the plan doc.
/// </para>
/// </summary>
internal static class TheseusTheme
{
    // ── Background layers (shared with the family) ──
    public static readonly Vector4 BgDeep = new(0.08f, 0.08f, 0.10f, 1.00f);
    public static readonly Vector4 BgPanel = new(0.12f, 0.12f, 0.15f, 1.00f);
    public static readonly Vector4 BgRow = new(0.15f, 0.15f, 0.18f, 0.60f);

    // ── Accent — verdigris / aged bronze (Theseus identity) ──
    public static readonly Vector4 AccentPatina = new(0.29f, 0.74f, 0.69f, 1.00f);
    public static readonly Vector4 AccentDim = new(0.18f, 0.45f, 0.42f, 1.00f);
    public static readonly Vector4 AccentWash = new(0.29f, 0.74f, 0.69f, 0.10f);

    // ── The Thread (resume system only — never ordinary chrome) ──
    public static readonly Vector4 ThreadCrimson = new(0.86f, 0.31f, 0.34f, 1.00f);
    public static readonly Vector4 ThreadDim = new(0.52f, 0.19f, 0.21f, 1.00f);
    public static readonly Vector4 ThreadWash = new(0.86f, 0.31f, 0.34f, 0.10f);

    // ── Status (shared with the family — semantics stay put) ──
    public static readonly Vector4 StatusGreen = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 StatusYellow = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 StatusRed = new(0.85f, 0.25f, 0.20f, 1.00f);
    public static readonly Vector4 StatusGrey = new(0.45f, 0.45f, 0.50f, 1.00f);

    // ── Text ramp (shared with the family) ──
    public static readonly Vector4 TextPrimary = new(0.92f, 0.90f, 0.85f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.60f, 0.58f, 0.55f, 1.00f);
    public static readonly Vector4 TextDisabled = new(0.35f, 0.35f, 0.38f, 1.00f);

    // ── Party roles (FFXIV's own conventions, used only as role dots) ──
    public static readonly Vector4 RoleTank = new(0.36f, 0.55f, 0.89f, 1.00f);
    public static readonly Vector4 RoleHealer = new(0.35f, 0.72f, 0.42f, 1.00f);
    public static readonly Vector4 RoleDps = new(0.78f, 0.38f, 0.38f, 1.00f);

    public static Vector4 RoleColor(string role) => role switch
    {
        "Tank" => RoleTank,
        "Healer" => RoleHealer,
        "DPS" => RoleDps,
        _ => TextSecondary,
    };

    // ── Run-state colours (objective progress, fleet rows) ──
    public static readonly Vector4 ObjectiveDone = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 ObjectiveCurrent = new(0.29f, 0.74f, 0.69f, 1.00f);
    public static readonly Vector4 ObjectiveHidden = new(0.35f, 0.35f, 0.38f, 1.00f);
    public static readonly Vector4 PeerOnline = new(0.20f, 0.75f, 0.35f, 1.00f);
    public static readonly Vector4 PeerStale = new(0.85f, 0.75f, 0.10f, 1.00f);
    public static readonly Vector4 PeerGone = new(0.45f, 0.45f, 0.50f, 1.00f);

    /// <summary>
    /// Accent-coloured section header. This Dalamud ImGui binding has no SeparatorText, so it is
    /// hand-drawn: coloured label with a hairline continuing to the right edge (family pattern).
    /// </summary>
    public static void SectionHeader(string label) => SectionHeader(label, AccentPatina);

    /// <summary>Section header in an explicit colour — pass <see cref="ThreadCrimson"/> for Thread panels.</summary>
    public static void SectionHeader(string label, Vector4 color)
    {
        ImGui.Spacing();
        ImGui.TextColored(color, label);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var lineY = (min.Y + max.Y) / 2f;
        var lineStart = new Vector2(max.X + 8f, lineY);
        var lineEnd = new Vector2(
            ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X, lineY);
        if (lineEnd.X > lineStart.X)
            ImGui.GetWindowDrawList().AddLine(lineStart, lineEnd,
                ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.20f, 0.24f, 1f)), 1f);
        ImGui.Spacing();
    }

    /// <summary>Coloured status dot + label.</summary>
    public static void StatusDot(bool active, string activeLabel = "Active", string inactiveLabel = "Idle")
    {
        ImGui.TextColored(active ? StatusGreen : StatusGrey, "●");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(active ? StatusGreen : TextSecondary, active ? activeLabel : inactiveLabel);
    }

    /// <summary>Dependency chip — green when the plugin is present, red when it is missing.</summary>
    public static void DependencyChip(string label, bool available)
    {
        ImGui.TextColored(available ? StatusGreen : StatusRed, available ? "●" : "○");
        ImGui.SameLine(0f, 4f);
        ImGui.TextColored(available ? TextPrimary : StatusRed, label);
    }

    /// <summary>
    /// Full-width call-to-action button in the accent. The one thing on the panel you press to make
    /// something happen gets the identity colour; everything else stays quiet around it.
    /// </summary>
    public static bool AccentButton(string label, float height = 30f)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, AccentDim);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentPatina);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentPatina);
        ImGui.PushStyleColor(ImGuiCol.Text, TextPrimary);
        try
        {
            return ImGui.Button(label, new Vector2(-1f, height));
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    /// <summary>Hover "(?)" tooltip for a non-obvious control.</summary>
    public static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    /// <summary>
    /// One duty-objective row: filled done / hollow current / dim still hidden by the game.
    /// Mirrors the in-game Duty Information panel, which is the source this reads from.
    ///
    /// <para>
    /// Glyphs are restricted to ones the game's own font actually carries. Checkmarks and
    /// triangles render as tofu here, which made every objective row look identical in a live
    /// duty — so shape and colour both carry the state, and neither is decorative.
    /// </para>
    /// </summary>
    public static void ObjectiveRow(string label, bool done, bool current)
    {
        var (glyph, color) = done
            ? ("●", ObjectiveDone)
            : current ? ("○", ObjectiveCurrent) : ("·", ObjectiveHidden);

        ImGui.TextColored(color, glyph);
        ImGui.SameLine(0f, 6f);
        ImGui.TextColored(done ? TextSecondary : current ? TextPrimary : ObjectiveHidden, label);
    }
}
