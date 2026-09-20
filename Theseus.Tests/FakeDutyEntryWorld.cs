using System;
using System.Collections.Generic;
using System.Linq;
using Theseus.Services.Duty;

namespace Theseus.Tests;

/// <summary>Scriptable <see cref="IDutyEntryWorld"/> — every fact a field, every call recorded.</summary>
internal sealed class FakeDutyEntryWorld : IDutyEntryWorld
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public bool IsInDuty { get; set; }
    public bool IsBetweenAreas { get; set; }
    public bool IsOccupied { get; set; }
    public bool IsInCombat { get; set; }
    public bool DutySupportUnlocked { get; set; } = true;
    public bool DawnSupported { get; set; } = true;
    public bool CompanionWindowReady { get; set; }
    public bool IsConfirmVisible { get; set; }

    public List<string> Calls { get; } = [];
    public List<string> Logs { get; } = [];

    public int Opens { get; private set; }
    public int Registers { get; private set; }
    public int Confirms { get; private set; }

    public bool SupportsDutySupport(uint contentFinderConditionId) => DawnSupported;

    /// <summary>Which door each call went through, so a Trust duty opened as Duty Support shows up.</summary>
    public List<bool> OpenedAsTrust { get; } = [];

    public void OpenCompanionWindow(uint contentFinderConditionId, bool trust)
    {
        Opens++;
        OpenedAsTrust.Add(trust);
        Calls.Add($"open:{contentFinderConditionId}:{(trust ? "trust" : "support")}");
    }

    /// <summary>Duties the window was pointed at, in order.</summary>
    public List<uint> Selected { get; } = [];

    public bool SelectDuty(uint contentFinderConditionId, bool trust)
    {
        Selected.Add(contentFinderConditionId);
        Calls.Add($"select:{contentFinderConditionId}");
        return true;
    }

    public bool IsCompanionWindowReady(bool trust) => CompanionWindowReady;

    public void RegisterForDuty(bool trust)
    {
        Registers++;
        Calls.Add($"register:{(trust ? "trust" : "support")}");
    }

    public void ConfirmQueue()
    {
        Confirms++;
        Calls.Add("confirm");
    }

    public void Log(string message) => Logs.Add(message);

    // ── Trust ──

    public bool ChooseCompanionsAllowed { get; set; }

    public bool CanChooseCompanions(bool trust) => trust && ChooseCompanionsAllowed;

    public List<TrustCompanion> Companions { get; } = [];

    /// <summary>Member ids the game will accept. Anything else fails the party, as a story lock would.</summary>
    public HashSet<byte> Fieldable { get; } = [];

    public List<byte> PartySet { get; private set; } = [];

    public int DefaultPartyCalls { get; private set; }

    public IReadOnlyList<TrustCompanion> ReadCompanions(uint contentFinderConditionId, bool trust)
        => trust ? Companions : [];

    public bool SetCompanions(IReadOnlyList<byte> companionKeys, bool trust)
    {
        if (companionKeys.Count == 0 || !companionKeys.All(Fieldable.Contains))
            return false;

        PartySet = [.. companionKeys];
        return true;
    }

    public void SetDefaultCompanions(bool trust) => DefaultPartyCalls++;

    public string DescribeCompanionData(bool trust) => string.Empty;

    // ── Duty Finder ──

    public bool DutyFinderUnlocked { get; set; } = true;

    public bool IsInDutyQueue { get; set; }

    public bool IsDutyFinderReady { get; set; }

    public uint DutyFinderHighlightedDuty { get; set; }

    public string DutyFinderTickedName { get; set; } = string.Empty;

    /// <summary>What ticking the highlighted row puts in the ticked-name slot.</summary>
    public string HighlightedName { get; set; } = string.Empty;

    public int DutyFinderOpens { get; private set; }

    public int Joins { get; private set; }

    public int Clears { get; private set; }

    public void OpenDutyFinder(uint contentFinderConditionId)
    {
        DutyFinderOpens++;
        Calls.Add($"df-open:{contentFinderConditionId}");
    }

    public void DutyFinderTickHighlighted()
    {
        Calls.Add("df-tick");
        DutyFinderTickedName = HighlightedName;
    }

    public void DutyFinderClearSelection()
    {
        Clears++;
        Calls.Add("df-clear");
        DutyFinderTickedName = string.Empty;
    }

    public void DutyFinderJoin()
    {
        Joins++;
        Calls.Add("df-join");
    }

    public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);

    /// <summary>Ticks an entry <paramref name="times"/> times, a second apart — past the retry throttle.</summary>
    public void Run(DutyEntry entry, int times, double secondsPerTick = 1.1)
    {
        for (var i = 0; i < times; i++)
        {
            entry.Tick();
            Advance(secondsPerTick);
        }
    }
}
