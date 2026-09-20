using Theseus.Services.Duty;

namespace Theseus.Tests;

public class DutyEntryTests
{
    private const uint Mistwake = 1036;

    private static (DutyEntry Entry, FakeDutyEntryWorld World) Begun(Action<FakeDutyEntryWorld>? setup = null)
    {
        var world = new FakeDutyEntryWorld();
        setup?.Invoke(world);

        var entry = new DutyEntry(world);
        entry.Begin(Mistwake, trust: true, out _);
        return (entry, world);
    }

    // ── Preconditions ──

    [Fact]
    public void RefusesWhileInsideADuty()
    {
        var world = new FakeDutyEntryWorld { IsInDuty = true };
        var entry = new DutyEntry(world);

        Assert.False(entry.Begin(Mistwake, trust: true, out var reason));
        Assert.Equal(DutyEntryStatus.Idle, entry.Status);
        Assert.Contains("Already in a duty", reason);
    }

    [Fact]
    public void RefusesInCombat()
    {
        var world = new FakeDutyEntryWorld { IsInCombat = true };
        Assert.False(new DutyEntry(world).Begin(Mistwake, trust: true, out var reason));
        Assert.Contains("combat", reason);
    }

    [Fact]
    public void RefusesWhenDutySupportIsLocked()
    {
        var world = new FakeDutyEntryWorld { DutySupportUnlocked = false };
        Assert.False(new DutyEntry(world).Begin(Mistwake, trust: true, out var reason));
        Assert.Contains("not unlocked", reason);
    }

    [Fact]
    public void RefusesContentWithoutDutySupport()
    {
        var world = new FakeDutyEntryWorld { DawnSupported = false };
        Assert.False(new DutyEntry(world).Begin(Mistwake, trust: true, out var reason));
        Assert.Contains("no Duty Support", reason);
    }

    // ── The happy path ──

    [Fact]
    public void OpensThenRegistersThenConfirmsThenEnters()
    {
        var (entry, world) = Begun();

        // Window not up yet: it keeps asking.
        world.Run(entry, 2);
        Assert.Equal(DutyEntryStatus.Opening, entry.Status);
        Assert.Equal(2, world.Opens);

        world.CompanionWindowReady = true;
        world.Run(entry, 3); // transition, stage the party, then commence
        Assert.Equal(1, world.Registers);

        world.IsConfirmVisible = true;
        world.Run(entry, 1);
        Assert.Equal(1, world.Confirms);

        world.IsConfirmVisible = false;
        world.IsBetweenAreas = true;
        world.Run(entry, 1);
        Assert.Equal(DutyEntryStatus.Loading, entry.Status);

        world.IsBetweenAreas = false;
        world.IsInDuty = true;
        entry.Tick();

        Assert.Equal(DutyEntryStatus.Entered, entry.Status);
        Assert.False(entry.IsActive);
        Assert.Equal(["open:1036:trust", "open:1036:trust", "select:1036", "register:trust", "confirm"], world.Calls);
    }

    [Fact]
    public void EnteringByAnyMeansCountsAsSuccess()
    {
        // The player can commence by hand mid-attempt. Ending up inside is the goal, whoever
        // pressed the button.
        var (entry, world) = Begun();
        world.IsInDuty = true;
        entry.Tick();

        Assert.Equal(DutyEntryStatus.Entered, entry.Status);
    }

    // ── Retries and throttling ──

    [Fact]
    public void ThrottlesRepeatedOpens()
    {
        var (entry, world) = Begun();

        for (var i = 0; i < 10; i++)
        {
            entry.Tick();
            world.Advance(0.1);
        }

        // One second of throttle over one second of ticks: the first attempt plus at most one more.
        Assert.InRange(world.Opens, 1, 2);
    }

    [Fact]
    public void DoesNothingWhileOccupied()
    {
        var (entry, world) = Begun(w => w.IsOccupied = true);

        world.Run(entry, 5);

        Assert.Equal(0, world.Opens);
        Assert.Equal(DutyEntryStatus.Opening, entry.Status);
    }

    [Fact]
    public void GoesBackForTheWindowIfItCloses()
    {
        var (entry, world) = Begun();
        world.CompanionWindowReady = true;
        world.Run(entry, 2);
        Assert.Equal(DutyEntryStatus.Registering, entry.Status);

        world.CompanionWindowReady = false;
        entry.Tick();

        Assert.Equal(DutyEntryStatus.Opening, entry.Status);
    }

    // ── Deadlines ──

    [Fact]
    public void FaultsWhenTheWindowNeverOpens()
    {
        var (entry, world) = Begun();

        world.Run(entry, 25);

        Assert.Equal(DutyEntryStatus.Faulted, entry.Status);
        Assert.Contains("would not open", entry.Detail);
    }

    [Fact]
    public void FaultsWhenRegistrationIsNeverAccepted()
    {
        var (entry, world) = Begun(w => w.CompanionWindowReady = true);

        world.Run(entry, 35);

        Assert.Equal(DutyEntryStatus.Faulted, entry.Status);
        Assert.Contains("did not accept", entry.Detail);
    }

    [Fact]
    public void ALoadingScreenGetsItsOwnLongerDeadline()
    {
        var (entry, world) = Begun();

        // Well past the 20s open budget, but a loading screen is not a stalled menu.
        world.IsBetweenAreas = true;
        world.Run(entry, 60);

        Assert.Equal(DutyEntryStatus.Loading, entry.Status);
    }

    [Fact]
    public void FaultsWhenTheLoadNeverFinishes()
    {
        var (entry, world) = Begun();
        world.IsBetweenAreas = true;
        world.Run(entry, 1);

        world.IsBetweenAreas = false;
        world.Advance(130);
        entry.Tick();

        Assert.Equal(DutyEntryStatus.Faulted, entry.Status);
        Assert.Contains("never loaded", entry.Detail);
    }

    // ── Trust companions ──

    private static DutyEntry BegunWithParty(FakeDutyEntryWorld world, params byte[] party)
    {
        var entry = new DutyEntry(world, () => party);
        entry.Begin(Mistwake, trust: true, out _);
        return entry;
    }

    [Fact]
    public void ADutySupportDutyGoesThroughTheDutySupportDoor()
    {
        // Duty Support and Trust are separate systems with separate agents. Opening a Trust duty
        // through the Duty Support door entered it with the fixed story cast and no companion
        // choice at all, which looked exactly like the picker being ignored.
        var world = new FakeDutyEntryWorld();
        var entry = new DutyEntry(world);
        entry.Begin(Mistwake, trust: false, out _);

        world.Run(entry, 1);
        world.CompanionWindowReady = true;
        world.Run(entry, 3);

        Assert.Equal([false], world.OpenedAsTrust);
        Assert.Contains("register:support", world.Calls);
    }

    [Fact]
    public void DutySupportOnATrustDutyBringsNoCompanionChoice()
    {
        // Choosing Duty Support on content that also has Trust is a real option, not a fallback:
        // the story's own cast with nothing to pick is what an MSQ dungeon wants. The party the
        // picker holds must not leak into it.
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };
        world.Fieldable.UnionWith([1, 2, 3]);

        var entry = new DutyEntry(world, () => new byte[] { 1, 2, 3 });
        entry.Begin(Mistwake, trust: false, out _);
        world.Run(entry, 3);

        Assert.Empty(world.PartySet);
        Assert.Equal(0, world.DefaultPartyCalls);
        Assert.Contains("register:support", world.Calls);
    }

    [Fact]
    public void TheDutyIsSelectedInTheWindowBeforeCommencing()
    {
        // The window opens on whatever was selected last, not on what was asked for. Without this
        // the roster read from it belongs to a different duty and commencing enters that one.
        var world = new FakeDutyEntryWorld { CompanionWindowReady = true };
        var entry = new DutyEntry(world);
        entry.Begin(Mistwake, trust: true, out _);

        world.Run(entry, 3);

        Assert.Contains(Mistwake, world.Selected);
        Assert.True(
            world.Calls.IndexOf($"select:{Mistwake}") < world.Calls.FindIndex(c => c.StartsWith("register")),
            "the duty must be selected before commencing");
    }

    [Fact]
    public void TheDoorIsChosenOnceAndStaysChosen()
    {
        // Sticky for the whole attempt: opening one window and registering against the other would
        // commence the duty nobody asked for.
        var world = new FakeDutyEntryWorld { CompanionWindowReady = true };
        var entry = new DutyEntry(world);
        entry.Begin(Mistwake, trust: true, out _);

        world.Run(entry, 4);

        Assert.True(entry.IsTrust);
        Assert.All(world.OpenedAsTrust, opened => Assert.True(opened));
        Assert.DoesNotContain("register:support", world.Calls);
    }

    [Fact]
    public void TheChosenTrustPartyIsFieldedBeforeCommencing()
    {
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };
        world.Fieldable.UnionWith([1, 2, 3]);

        var entry = BegunWithParty(world, 1, 2, 3);
        world.Run(entry, 3);

        Assert.Equal([1, 2, 3], world.PartySet);
        Assert.Equal(0, world.DefaultPartyCalls);
        Assert.Equal(1, world.Registers);
    }

    [Fact]
    public void ACompanionLockedBehindStoryFallsBackRatherThanFailing()
    {
        // Member 9 is not offered on this character. Registering with a half-built party would be
        // worse than the game's own choice, and refusing to enter at all would stop the fleet.
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };
        world.Fieldable.UnionWith([1, 2]);

        var entry = BegunWithParty(world, 1, 2, 9);
        world.Run(entry, 3);

        Assert.Empty(world.PartySet);
        Assert.Equal(1, world.DefaultPartyCalls);
        Assert.Equal(1, world.Registers);
        Assert.Contains(world.Logs, l => l.Contains("Could not field"));
    }

    [Fact]
    public void APartialPartyFallsBackRatherThanEnteringShortHanded()
    {
        // Two picks fielded as-is draw "Role requirements unmet" from the game — the commence
        // needs a complete light party, so anything less hands the choice back.
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };
        world.Fieldable.UnionWith([1, 2]);

        var entry = BegunWithParty(world, 1, 2);
        world.Run(entry, 3);

        Assert.Empty(world.PartySet);
        Assert.Equal(1, world.DefaultPartyCalls);
        Assert.Contains(world.Logs, l => l.Contains("2 of 3"));
    }

    [Fact]
    public void ARefusedCustomPartyEventuallyHandsTheChoiceBack()
    {
        // The game can refuse a comp CanAddMember tolerated — it answers in chat and leaves the
        // window open, so registration just repeats. After a few refusals the party goes back to
        // the game's own choice instead of slamming into the same wall until the deadline.
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };
        world.Fieldable.UnionWith([1, 2, 3]);

        var entry = BegunWithParty(world, 1, 2, 3);
        world.Run(entry, 8);

        Assert.True(world.Registers >= 4);
        Assert.Equal(1, world.DefaultPartyCalls);
        Assert.Contains(world.Logs, l => l.Contains("refused the chosen Trust party"));
    }

    [Fact]
    public void NoChoiceLetsTheGamePick()
    {
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = true, CompanionWindowReady = true };

        var entry = BegunWithParty(world);
        world.Run(entry, 3);

        Assert.Equal(1, world.DefaultPartyCalls);
        Assert.Equal(1, world.Registers);
    }

    [Fact]
    public void DutySupportContentIsLeftAlone()
    {
        // No companion choice on offer means nothing to set — touching the party here would be
        // writing to a window that does not have one.
        var world = new FakeDutyEntryWorld { ChooseCompanionsAllowed = false, CompanionWindowReady = true };

        var entry = BegunWithParty(world, 1, 2, 3);
        world.Run(entry, 3);

        Assert.Empty(world.PartySet);
        Assert.Equal(0, world.DefaultPartyCalls);
        Assert.Equal(1, world.Registers);
    }

    // ── Duty Finder (grouped) ──

    private const string MistwakeName = "Mistwake";

    private static FakeDutyEntryWorld FinderOnMistwake() => new()
    {
        IsDutyFinderReady = true,
        DutyFinderHighlightedDuty = Mistwake,
        HighlightedName = MistwakeName,
    };

    [Fact]
    public void TheLeaderTicksTheDutyThenJoins()
    {
        var world = FinderOnMistwake();
        var entry = new DutyEntry(world);
        Assert.True(entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: true, out _));

        world.Run(entry, 4);

        Assert.Equal(1, world.Joins);
        Assert.True(world.Calls.IndexOf("df-tick") < world.Calls.IndexOf("df-join"),
            "the duty must be ticked before Join");
    }

    [Fact]
    public void AStaleSelectionIsClearedBeforeJoining()
    {
        // The Duty Finder remembers what was queued last, and Join queues all of it.
        var world = FinderOnMistwake();
        world.DutyFinderTickedName = "Sastasha";
        var entry = new DutyEntry(world);
        entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: true, out _);

        world.Run(entry, 5);

        Assert.Equal(1, world.Clears);
        Assert.True(world.Calls.IndexOf("df-clear") < world.Calls.IndexOf("df-join"));
    }

    [Fact]
    public void AMemberNeverTouchesTheDutyFinder()
    {
        // Only the leader can queue a party; a member pressing Join would queue alone.
        var world = FinderOnMistwake();
        var entry = new DutyEntry(world);
        entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: false, out _);

        world.Run(entry, 10);

        Assert.Equal(DutyEntryStatus.Queued, entry.Status);
        Assert.Equal(0, world.DutyFinderOpens);
        Assert.Equal(0, world.Joins);
    }

    [Fact]
    public void AMemberCommencesWhenTheLeadersQueuePops()
    {
        var world = FinderOnMistwake();
        var entry = new DutyEntry(world);
        entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: false, out _);

        world.Run(entry, 2);
        world.IsConfirmVisible = true;
        world.Run(entry, 1);

        Assert.Equal(1, world.Confirms);
    }

    [Fact]
    public void BeingInTheQueueIsWaitedOutNotReQueued()
    {
        var world = FinderOnMistwake();
        var entry = new DutyEntry(world);
        entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: true, out _);
        world.Run(entry, 4);
        Assert.Equal(1, world.Joins);

        world.IsInDutyQueue = true;
        world.IsDutyFinderReady = false; // the window closes on Join
        world.Run(entry, 60);

        Assert.Equal(DutyEntryStatus.Queued, entry.Status);
        Assert.Equal(1, world.Joins);
    }

    [Fact]
    public void ADroppedQueueIsQueuedAgain()
    {
        // A declined pop takes the whole party out of the queue.
        var world = FinderOnMistwake();
        world.IsInDutyQueue = true;
        var entry = new DutyEntry(world);
        entry.BeginDutyFinder(Mistwake, MistwakeName, isLeader: true, out _);
        Assert.Equal(DutyEntryStatus.Queued, entry.Status);

        world.IsInDutyQueue = false;
        world.DutyFinderTickedName = MistwakeName;
        world.Run(entry, 20);

        Assert.True(world.Joins >= 1, "the leader should queue again");
    }

    [Fact]
    public void ALockedDutyFinderRefusesToStart()
    {
        var world = new FakeDutyEntryWorld { DutyFinderUnlocked = false };
        Assert.False(new DutyEntry(world).BeginDutyFinder(Mistwake, MistwakeName, true, out var reason));
        Assert.Contains("Duty Finder", reason);
    }

    [Theory]
    [InlineData("the Tam–Tara Deepcroft", "The Tam-Tara Deepcroft")]
    [InlineData("Mistwake", "Mistwake")]
    public void DutyNamesMatchDespiteFormattingCodesAndDashes(string shown, string wanted)
        => Assert.True(DutyEntry.SameDutyName(shown, wanted));

    [Fact]
    public void DifferentDutiesDoNotMatch()
        => Assert.False(DutyEntry.SameDutyName("Sastasha", "Sastasha (Hard)"));

    // ── Cancelling ──

    [Fact]
    public void CancelStopsEverything()
    {
        var (entry, world) = Begun();
        entry.Cancel();

        world.Run(entry, 5);

        Assert.Equal(DutyEntryStatus.Idle, entry.Status);
        Assert.Equal(0, world.Opens);
        Assert.Equal(0u, entry.ContentFinderConditionId);
    }
}
