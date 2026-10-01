using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;

namespace Theseus.Tests;

public class ShortcutRecoveryTests
{
    private static readonly Vector3 Entrance = new(-413.4f, 107f, 157.9f);
    private static readonly Vector3 ShortcutAt = new(-410.2f, 107f, 165.2f); // The Ghimlyt Dark's, from its layout
    private static readonly Vector3 Checkpoint = new(-180f, 60f, 70f);

    private static void Frames(ShortcutRecovery recovery, FakeStepWorld world, int count, Action? each = null)
    {
        for (var i = 0; i < count && recovery.Active; i++)
        {
            each?.Invoke();
            recovery.Tick();
            world.Advance(0.2);
        }
    }

    [Fact]
    public void The_shortcut_is_walked_to_used_and_counted_once_it_carries_the_character()
    {
        var world = new FakeStepWorld { PlayerPosition = Entrance };
        world.TargetableDataIds.Add(EventObjectFilter.ShortcutObject);
        world.ObjectPositions[EventObjectFilter.ShortcutObject] = ShortcutAt;
        var recovery = new ShortcutRecovery(world);
        recovery.Begin();

        recovery.Tick();
        Assert.Equal([ShortcutAt], world.MoveRequests); // walking to it

        world.PlayerPosition = ShortcutAt + new Vector3(0.5f, 0f, 0f); // arrived
        Frames(recovery, world, 6);
        Assert.Contains(EventObjectFilter.ShortcutObject, world.Interacted);

        world.VisibleAddons.Add("SelectYesno"); // "Use the shortcut?"
        Frames(recovery, world, 1);
        Assert.Equal([true], world.YesNoAnswers);

        world.VisibleAddons.Clear();
        world.PlayerPosition = Checkpoint; // carried to the last boss
        Assert.True(recovery.Tick());
        Assert.True(recovery.Used);
    }

    [Fact]
    public void No_shortcut_before_the_first_boss_resumes_from_the_entrance()
    {
        var world = new FakeStepWorld { PlayerPosition = Entrance };
        var recovery = new ShortcutRecovery(world);
        recovery.Begin();

        Frames(recovery, world, 40); // eight seconds: past the search window

        Assert.False(recovery.Active);
        Assert.False(recovery.Used);
        Assert.Empty(world.MoveRequests);
    }

    [Fact]
    public void An_attempt_that_never_carries_the_character_gives_up()
    {
        var world = new FakeStepWorld { PlayerPosition = ShortcutAt };
        world.TargetableDataIds.Add(EventObjectFilter.ShortcutObject);
        world.ObjectPositions[EventObjectFilter.ShortcutObject] = ShortcutAt;
        var recovery = new ShortcutRecovery(world);
        recovery.Begin();

        Frames(recovery, world, 200); // forty seconds of interacting and no prompt

        Assert.False(recovery.Active);
        Assert.False(recovery.Used);
    }
}
