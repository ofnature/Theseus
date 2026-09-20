using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Theseus.Services.Duty;

/// <summary>
/// The real <see cref="IDutyEntryWorld"/>. Translation only — every decision lives in
/// <see cref="DutyEntry"/>.
///
/// <para>
/// Duty Support and Trust are two systems, not one. They have separate agents with identical
/// shapes — <c>AgentDawnStory</c> and <c>AgentDawn</c> — and separate openers, and using the wrong
/// one enters the duty with the other system's party: a Trust duty opened through the Duty Support
/// door comes with its fixed story cast and no companion choice at all. That is why every call
/// here takes which system the duty uses rather than assuming.
/// </para>
/// </summary>
public sealed unsafe class GameDutyEntryWorld : IDutyEntryWorld
{
    /// <summary>Main command id for Duty Support. Its enabled state is the unlock check.</summary>
    private const uint DutySupportMainCommand = 91;

    /// <summary>
    /// Callback value the commence confirmation reads as "yes". The dialog is not a SelectYesno,
    /// so the ordinary yes/no path does not reach it.
    /// </summary>
    private const int ConfirmCommenceValue = 8;

    private const string ConfirmAddon = "ContentsFinderConfirm";

    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly DutyLifecycle _lifecycle;
    private readonly TrustRoster _roster;
    private readonly Action<string> _log;

    public GameDutyEntryWorld(
        ICondition condition,
        IGameGui gameGui,
        DutyLifecycle lifecycle,
        TrustRoster roster,
        Action<string> log)
    {
        _condition = condition;
        _gameGui = gameGui;
        _lifecycle = lifecycle;
        _roster = roster;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public bool IsInDuty => _lifecycle.IsInDuty;

    public bool IsBetweenAreas
        => _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    public bool IsOccupied
        => _condition[ConditionFlag.Occupied]
           || _condition[ConditionFlag.Occupied30]
           || _condition[ConditionFlag.Occupied33]
           || _condition[ConditionFlag.Occupied38]
           || _condition[ConditionFlag.Occupied39]
           || _condition[ConditionFlag.OccupiedInCutSceneEvent]
           || _condition[ConditionFlag.OccupiedInQuestEvent]
           || _condition[ConditionFlag.WatchingCutscene]
           || _condition[ConditionFlag.WatchingCutscene78];

    public bool IsInCombat => _condition[ConditionFlag.InCombat];

    public bool DutySupportUnlocked
        => Guard(() =>
        {
            var hud = AgentHUD.Instance();
            return hud is not null && hud->IsMainCommandEnabled(DutySupportMainCommand);
        });

    public bool SupportsDutySupport(uint contentFinderConditionId)
        => Guard(() =>
        {
            var atk = RaptureAtkModule.Instance();
            return atk is not null && atk->IsDawnSupported(contentFinderConditionId) != 0;
        });

    public void OpenCompanionWindow(uint contentFinderConditionId, bool trust)
        => Guard(() =>
        {
            var atk = RaptureAtkModule.Instance();
            if (atk is null)
                return false;

            if (trust)
                atk->OpenDawn(contentFinderConditionId);
            else
                atk->OpenDawnStory(contentFinderConditionId);

            return true;
        });

    public bool SelectDuty(uint contentFinderConditionId, bool trust)
        => Guard(() =>
        {
            var content = ContentDataOf(trust);
            if (content is null)
                return false;

            var entries = content->ContentEntries;
            for (var i = 0; i < entries.Length; i++)
            {
                ref var entry = ref entries[i];
                if (entry.ContentFinderConditionId != contentFinderConditionId)
                    continue;

                if (content->SelectedContentEntry != entry.Index)
                {
                    if (!trust)
                        AgentDawnStory.Instance()->SelectContentEntry(entry.Index);
                    else
                        AgentDawn.Instance()->SelectContentEntry(entry.Index);
                }

                // Selecting a duty does not refill the member data when the duty belongs to a
                // different expansion tab — the arrays keep whatever tab was last browsed, so the
                // roster read afterwards is another expansion's cast. Asking the agent to rebuild
                // its addon is the cheap half of the fix; driving the tab itself is the other half.
                if (trust)
                    AgentDawn.Instance()->UpdateAddon();
                else
                    AgentDawnStory.Instance()->UpdateAddon();

                return true;
            }

            return false;
        });

    public bool IsCompanionWindowReady(bool trust)
        => Guard(() =>
        {
            if (!trust)
            {
                var story = AgentDawnStory.Instance();
                return story is not null && story->IsAddonReady();
            }

            var dawn = AgentDawn.Instance();
            return dawn is not null && dawn->IsAddonReady();
        });

    public void RegisterForDuty(bool trust)
        => Guard(() =>
        {
            if (!trust)
            {
                var story = AgentDawnStory.Instance();
                if (story is null)
                    return false;

                story->RegisterForDuty();
                return true;
            }

            var dawn = AgentDawn.Instance();
            if (dawn is null)
                return false;

            dawn->RegisterForDuty();
            return true;
        });

    // ── Trust ──

    public bool CanChooseCompanions(bool trust)
        => trust && Guard(() =>
        {
            var content = ContentDataOf(true);
            if (content is null)
                return false;

            var entries = content->ContentEntries;
            var index = content->SelectedContentEntry;
            if (index >= entries.Length)
                return false;

            ref var entry = ref entries[index];
            return entry.IsMemberSelectionAvailable && !entry.IsNotYetSupported;
        });

    /// <summary>
    /// The duty's own roster, read from the agent's member group and named through the id table.
    ///
    /// <para>
    /// The agent held the right answer the whole time — groups are per duty, and each lists exactly
    /// the companions that duty offers, Ryne in Shadowbringers and Krile in Dawntrail included. It
    /// only ever looked wrong because its member ids were being named through the wrong id space.
    /// When the agent has no data (window never opened), the full cast is the fallback: offering
    /// too many costs a refused pick that already falls back, hiding someone does not recover.
    /// </para>
    /// </summary>
    public IReadOnlyList<TrustCompanion> ReadCompanions(uint contentFinderConditionId, bool trust)
    {
        if (!trust)
            return [];

        var live = new List<TrustCompanion>();

        Guard(() =>
        {
            var entries = DutyGroup(contentFinderConditionId, out var count);
            if (entries is null)
                return false;

            for (var i = 0; i < count; i++)
            {
                var entry = &entries[i];
                if (_roster.ByAgentId(entry->MemberId) is not { } companion || live.Contains(companion))
                    continue;

                // The entry's job is what the companion plays in this duty, so the label follows it
                // rather than a static class name.
                var jobName = _roster.JobName(entry->ClassJob);
                live.Add(companion with
                {
                    ClassName = jobName.Length > 0 ? jobName : companion.ClassName,
                    Role = _roster.JobRole(entry->ClassJob),
                });
            }

            return live.Count > 0;
        });

        return live.Count > 0 ? live : _roster.Companions;
    }

    /// <summary>
    /// Fields the chosen companions from the duty's own member group.
    ///
    /// <para>
    /// Two hard-won facts shape this. <c>AddMember</c>'s first argument is the companion's roster
    /// <i>slot</i> — the position of their entry in the group, 0 for Alphinaud through 7 —
    /// not their member id; passing ids returned success and wrote nonsense. And the party the
    /// commence actually uses is the <i>window's</i>, so every mutation ends with
    /// <c>UpdateAddon</c> and the caller must give the addon a beat before registering — writes
    /// followed by an immediate commence enter with whatever party the window already showed.
    /// </para>
    /// </summary>
    public bool SetCompanions(IReadOnlyList<byte> keys, bool trust)
        => trust && Guard(() =>
        {
            var party = PartyDataOf(true);
            var dawn = AgentDawn.Instance();
            if (party is null || dawn is null || keys.Count == 0)
                return false;

            var entries = DutyGroup(SelectedCfc(), out var count);
            if (entries is null)
                return false;

            var wanted = keys
                .Select(_roster.ByKey)
                .Where(c => c is not null)
                .Cast<TrustCompanion>()
                .ToList();

            party->ClearParty();

            var added = 0;
            for (byte slot = 0; slot < count; slot++)
            {
                var entry = &entries[slot];
                if (!wanted.Any(c => c.MemberIds.Contains(entry->MemberId)))
                    continue;

                if (party->AddMember(slot, entry))
                    added++;
            }

            // Commit whatever happened — a cleared party must not linger in the window either way.
            dawn->UpdateAddon();

            if (added == wanted.Count && added == keys.Count)
                return true;

            _log($"Trust party not fielded ({added}/{keys.Count} accepted by slot).");
            party->ClearParty();
            dawn->UpdateAddon();
            return false;
        });

    /// <summary>The member entries for a duty, by its content entry index — groups are per duty.</summary>
    private AgentDawnInterface.DawnMemberEntry* DutyGroup(uint contentFinderConditionId, out byte count)
    {
        count = 0;

        var data = MemberDataOf(true);
        var content = ContentDataOf(true);
        if (data is null || content is null)
            return null;

        // The duty's own index when it can be found, else whatever the window has current.
        var group = content->SelectedContentEntry;
        var entries = content->ContentEntries;
        for (var i = 0; i < entries.Length; i++)
        {
            if (entries[i].ContentFinderConditionId == contentFinderConditionId)
            {
                group = entries[i].Index;
                break;
            }
        }

        if (group >= data->MemberEntriesCount || data->GetMemberCount(group) == 0)
            group = data->CurrentMembersIndex;

        if (group >= data->MemberEntriesCount)
            return null;

        count = data->GetMemberCount(group);
        return data->GetMembers(group);
    }

    private uint SelectedCfc()
    {
        var content = ContentDataOf(true);
        if (content is null)
            return 0;

        var entries = content->ContentEntries;
        var index = content->SelectedContentEntry;
        return index < entries.Length ? entries[index].ContentFinderConditionId : 0;
    }

    public void SetDefaultCompanions(bool trust)
        => Guard(() =>
        {
            if (!trust)
                return false;

            var dawn = AgentDawn.Instance();
            if (dawn is null)
                return false;

            dawn->SetupDefaultParty();
            dawn->UpdateAddon();
            return true;
        });

    public string DescribeCompanionData(bool trust)
    {
        var text = new System.Text.StringBuilder();

        Guard(() =>
        {
            var data = MemberDataOf(trust);
            var content = ContentDataOf(trust);
            if (data is null || content is null)
                return false;

            text.AppendLine(
                $"expansion sel={content->SelectedExpansion}/{content->ExpansionCount} · " +
                $"content sel={content->SelectedContentEntry}/{content->ContentEntryCount} · " +
                $"groups={data->MemberEntriesCount} cur={data->CurrentMembersIndex} disp={data->DisplayMemberIndex}");

            text.AppendLine($"chosen group = {GroupIndex(data, trust)?.ToString() ?? "none"}");

            // Which duty the window believes is selected, so a roster can be matched to it.
            var picked = content->ContentEntries;
            if (content->SelectedContentEntry < picked.Length)
            {
                ref var entry = ref picked[content->SelectedContentEntry];
                text.AppendLine(
                    $"selected duty: cfc={entry.ContentFinderConditionId} lvl={entry.Level} " +
                    $"ex={entry.ExVersion} idx={entry.Index} members={entry.MemberCount} " +
                    $"trustAvail={entry.IsTrustAvailable} pick={entry.IsMemberSelectionAvailable}");
            }

            for (byte g = 0; g < data->MemberEntriesCount && g < 64; g++)
            {
                var count = data->GetMemberCount(g);
                var entries = data->GetMembers(g);
                text.Append($"[{g}] n={count}: ");

                if (entries is null)
                {
                    text.AppendLine("(null)");
                    continue;
                }

                for (var i = 0; i < count && i < 10; i++)
                {
                    var e = &entries[i];
                    text.Append($"{_roster.ByAgentId(e->MemberId)?.Name ?? "?"}#{e->MemberId}/j{e->ClassJob} ");
                }

                text.AppendLine();
            }

            return true;
        });

        text.AppendLine("derived roster:");
        text.AppendLine(_roster.Describe());
        return text.ToString();
    }

    // ── Duty Finder ──
    //
    // The sequence and callback values below follow AutoDuty's own regular-duty queue, read from
    // its decompiled QueueRegular rather than inferred: the callback numbers are not documented
    // anywhere, and guessing at addon callbacks has a long record of doing the wrong thing quietly.

    /// <summary>Main command id for the Duty Finder. Its enabled state is the unlock check.</summary>
    private const uint DutyFinderMainCommand = 33;

    private const string DutyFinderAddon = "ContentsFinder";

    /// <summary>AtkValue slot holding the name of the duty ticked for queueing.</summary>
    private const int TickedNameValue = 18;

    public bool DutyFinderUnlocked
        => Guard(() =>
        {
            var hud = AgentHUD.Instance();
            return hud is not null && hud->IsMainCommandEnabled(DutyFinderMainCommand);
        });

    public bool IsInDutyQueue => _condition[ConditionFlag.InDutyQueue];

    public void OpenDutyFinder(uint contentFinderConditionId)
        => Guard(() =>
        {
            var agent = AgentContentsFinder.Instance();
            if (agent is null)
                return false;

            agent->OpenRegularDuty(contentFinderConditionId, false);
            return true;
        });

    public bool IsDutyFinderReady => DutyFinder() is not null;

    public uint DutyFinderHighlightedDuty
    {
        get
        {
            try
            {
                var agent = AgentContentsFinder.Instance();
                return agent is null ? 0 : agent->SelectedDuty.Id;
            }
            catch (Exception ex)
            {
                _log($"Duty Finder read failed: {ex.Message}");
                return 0;
            }
        }
    }

    public string DutyFinderTickedName
    {
        get
        {
            try
            {
                var addon = DutyFinder();
                if (addon is null || addon->AtkValuesCount <= TickedNameValue)
                    return string.Empty;

                return addon->AtkValues[TickedNameValue].GetValueAsString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _log($"Duty Finder read failed: {ex.Message}");
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Ticks the highlighted row. The argument is AutoDuty's: the count of category header rows
    /// above the highlighted row, plus one — header rows carry a first UInt value of 0 or 1.
    /// </summary>
    public void DutyFinderTickHighlighted()
        => Guard(() =>
        {
            var addon = DutyFinder();
            if (addon is null || addon->DutyList is null)
                return false;

            var list = addon->DutyList;
            var selected = list->AtkComponentList.SelectedItemIndex;
            var headers = 0;

            for (var i = 0; i < selected && i < list->Items.Count; i++)
            {
                var item = list->Items[i].Value;
                if (item is not null && item->UIntValues.Count > 0 && item->UIntValues[0] is 0 or 1)
                    headers++;
            }

            FireInts((AtkUnitBase*)addon, 3, headers + 1);
            return true;
        });

    public void DutyFinderClearSelection()
        => Guard(() =>
        {
            var addon = DutyFinder();
            if (addon is null)
                return false;

            FireInts((AtkUnitBase*)addon, 12, 1);
            return true;
        });

    public void DutyFinderJoin()
        => Guard(() =>
        {
            var addon = DutyFinder();
            if (addon is null)
                return false;

            FireInts((AtkUnitBase*)addon, 12, 0);
            return true;
        });

    private AddonContentsFinder* DutyFinder()
    {
        var addon = _gameGui.GetAddonByName(DutyFinderAddon);
        if (addon.IsNull || !addon.IsVisible)
            return null;

        var unit = (AtkUnitBase*)addon.Address;
        return unit->IsReady ? (AddonContentsFinder*)unit : null;
    }

    private static void FireInts(AtkUnitBase* addon, int first, int second)
    {
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue { Type = AtkValueType.Int, Int = first };
        values[1] = new AtkValue { Type = AtkValueType.Int, Int = second };
        addon->FireCallback(2, values, true);
    }

    public bool IsConfirmVisible
    {
        get
        {
            var addon = _gameGui.GetAddonByName(ConfirmAddon);
            return !addon.IsNull && addon.IsVisible;
        }
    }

    public void ConfirmQueue()
        => Guard(() =>
        {
            var addon = _gameGui.GetAddonByName(ConfirmAddon);
            if (addon.IsNull || !addon.IsVisible)
                return false;

            var value = new AtkValue { Type = AtkValueType.Int, Int = ConfirmCommenceValue };
            ((AtkUnitBase*)addon.Address)->FireCallback(1, &value, true);
            return true;
        });

    public void Log(string message) => _log(message);

    // ── Agent plumbing ──

    private static AgentDawnInterface.DawnContentData* ContentDataOf(bool trust)
    {
        if (!trust)
        {
            var story = AgentDawnStory.Instance();
            return story is null || story->Data is null ? null : &story->Data->ContentData;
        }

        var dawn = AgentDawn.Instance();
        return dawn is null || dawn->Data is null ? null : &dawn->Data->ContentData;
    }

    private static AgentDawnInterface.DawnMemberData* MemberDataOf(bool trust)
    {
        if (!trust)
        {
            var story = AgentDawnStory.Instance();
            return story is null || story->Data is null ? null : &story->Data->MemberData;
        }

        var dawn = AgentDawn.Instance();
        return dawn is null || dawn->Data is null ? null : &dawn->Data->MemberData;
    }

    private static AgentDawnInterface.DawnPartyData* PartyDataOf(bool trust)
    {
        if (!trust)
        {
            var story = AgentDawnStory.Instance();
            return story is null || story->Data is null ? null : &story->Data->PartyData;
        }

        var dawn = AgentDawn.Instance();
        return dawn is null || dawn->Data is null ? null : &dawn->Data->PartyData;
    }

    /// <summary>
    /// The member group for the duty on screen.
    ///
    /// <para>
    /// Two indices are on offer and the game's own naming does not settle which is authoritative,
    /// so this takes the current group and falls back to the displayed one when it is empty. Both
    /// are bounds-checked: a wrong index here would read past the group into another expansion's
    /// cast, which is exactly what listing the whole game's roster turned out to be.
    /// </para>
    /// </summary>
    /// <summary>
    /// The member group for the duty on screen.
    ///
    /// <para>
    /// Groups are per <i>duty</i>, not per expansion: the count matches the content list exactly,
    /// and <c>CurrentMembersIndex</c> tracks <c>SelectedContentEntry</c>. Reading the selected
    /// expansion as a group index is what produced a roster from an unrelated dungeon — with
    /// thirty-three groups it was always in range and never empty, so every bounds check passed and
    /// the wrong cast came back looking entirely reasonable.
    /// </para>
    /// </summary>
    private static byte? GroupIndex(AgentDawnInterface.DawnMemberData* data, bool trust)
    {
        if (Usable(data, data->CurrentMembersIndex))
            return data->CurrentMembersIndex;

        var content = ContentDataOf(trust);
        if (content is not null && Usable(data, content->SelectedContentEntry))
            return content->SelectedContentEntry;

        return Usable(data, data->DisplayMemberIndex) ? data->DisplayMemberIndex : null;

        static bool Usable(AgentDawnInterface.DawnMemberData* data, byte index)
            => index < data->MemberEntriesCount && data->GetMemberCount(index) > 0;
    }

    /// <summary>
    /// Every game touch here reads or pokes a raw pointer, and a null one during a zone is normal
    /// rather than exceptional. Failing to false keeps that a "not yet" for the state machine's
    /// retry loop instead of an unhandled exception in the framework tick.
    /// </summary>
    private bool Guard(Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _log($"Duty entry step failed: {ex.Message}");
            return false;
        }
    }
}
