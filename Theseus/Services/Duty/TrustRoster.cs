using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Theseus.Services.Duty;

/// <summary>One Trust companion, across every form the game knows them in.</summary>
/// <param name="MemberIds">
/// Every id this companion appears under in the companion agent's member entries, one per
/// incarnation — Alphinaud is 0 as an Academician and 6 as a Sage.
/// </param>
public sealed record TrustCompanion(string Name, IReadOnlyList<byte> MemberIds, string ClassName, string Role = "")
{
    /// <summary>Stable handle for a companion — what a saved party stores.</summary>
    public byte Key => MemberIds[0];

    public string Label => ClassName.Length > 0 ? $"{Name} · {ClassName}" : Name;
}

/// <summary>
/// The Trust cast, and the id table that names the companion agent's entries.
///
/// <para>
/// The table is the part that cannot be derived, and mistaking that cost most of an evening. The
/// agent's <c>MemberId</c> values and the <c>DawnMemberUIParam</c> row ids are two different id
/// spaces: in the agent, 4 is Y'shtola and 8 is G'raha; in the sheet, those rows are Minfilia and
/// Lyna. Naming agent entries through the sheet shifted every label one companion over, which made
/// a correct roster read as the wrong expansion's cast — and sent the search off after sheets that
/// were never wrong so much as never involved.
/// </para>
///
/// <para>
/// The pairs below are field-verified against the live agent (2026-08-07): each id was read from a
/// duty whose roster is known, with the class jobs confirming the identification — 0/Scholar and
/// 6/Sage are both Alphinaud, 5 carries Ryne's Rogue, 103 Zero's Reaper, 11 Krile's Pictomancer.
/// </para>
/// </summary>
public sealed class TrustRoster
{
    /// <summary>Agent member ids per companion, paired with their DawnMemberUIParam row for naming.</summary>
    private static readonly (uint UiRow, byte[] AgentIds)[] Cast =
    [
        (UiRow: 1, AgentIds: [0, 6]),      // Alphinaud — Academician, then Sage
        (UiRow: 2, AgentIds: [1]),         // Alisaie
        (UiRow: 3, AgentIds: [2]),         // Thancred
        (UiRow: 5, AgentIds: [3]),         // Urianger
        (UiRow: 6, AgentIds: [4]),         // Y'shtola
        (UiRow: 7, AgentIds: [5]),         // Ryne
        (UiRow: 12, AgentIds: [7]),        // Estinien
        (UiRow: 10, AgentIds: [8, 9, 10]), // G'raha Tia — one per expansion
        (UiRow: 41, AgentIds: [103]),      // Zero
        (UiRow: 60, AgentIds: [11]),       // Krile
    ];

    private readonly Dictionary<byte, TrustCompanion> _byAgentId = [];
    private readonly Dictionary<uint, string> _jobNames = [];
    private readonly Dictionary<uint, string> _jobRoles = [];

    public TrustRoster(IDataManager data, Action<string> log)
    {
        var companions = new List<TrustCompanion>();

        try
        {
            var members = data.GetExcelSheet<DawnMemberUIParam>();

            foreach (var (uiRow, agentIds) in Cast)
            {
                var name = members.TryGetRow(uiRow, out var ui) ? ui.Name.ExtractText() : string.Empty;
                var className = members.TryGetRow(uiRow, out ui) ? ui.ClassSingular.ExtractText() : string.Empty;
                if (name.Length == 0)
                    continue;

                var companion = new TrustCompanion(name, agentIds, className);
                companions.Add(companion);
                foreach (var id in agentIds)
                    _byAgentId[id] = companion;
            }
        }
        catch (Exception ex)
        {
            log($"Could not read the Trust roster names: {ex.Message}");
        }

        try
        {
            // Job names and roles, for labelling live entries — the entry's ClassJob says what a
            // companion is playing in this duty, which beats a static class name.
            foreach (var row in data.GetExcelSheet<ClassJob>())
            {
                // The sheet spells jobs lowercase ("red mage"); the picker is a proper noun context.
                var jobName = row.Name.ExtractText();
                _jobNames[row.RowId] = string.Join(' ',
                    jobName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
                _jobRoles[row.RowId] = row.Role switch
                {
                    1 => "Tank",
                    4 => "Healer",
                    2 or 3 => "DPS",
                    _ => string.Empty,
                };
            }
        }
        catch (Exception ex)
        {
            log($"Could not read job names: {ex.Message}");
        }

        Companions = companions;
    }

    /// <summary>The full cast, in introduction order. The fallback when no live roster is readable.</summary>
    public IReadOnlyList<TrustCompanion> Companions { get; }

    /// <summary>The companion an agent member entry belongs to, or null for a non-Trust entry.</summary>
    public TrustCompanion? ByAgentId(byte agentMemberId) => _byAgentId.GetValueOrDefault(agentMemberId);

    public TrustCompanion? ByKey(byte key) => Companions.FirstOrDefault(c => c.Key == key);

    public string JobName(uint classJobId) => _jobNames.GetValueOrDefault(classJobId, string.Empty);

    public string JobRole(uint classJobId) => _jobRoles.GetValueOrDefault(classJobId, "DPS");

    /// <summary>Diagnostic — the id table as loaded, so a wrong pairing is visible rather than silent.</summary>
    public string Describe()
        => Companions.Count == 0
            ? "no Trust companions loaded"
            : string.Join("\n", Companions.Select(c =>
                $"{c.Name} · {c.ClassName} · key {c.Key} · agent ids [{string.Join(", ", c.MemberIds)}]"));
}
