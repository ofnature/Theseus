using System.Numerics;
using Theseus.Services.Paths;
using Theseus.Services.Run;

namespace Theseus.Tests;

/// <summary>
/// The trash hold: the route does not walk on while a pack is fought. Tanks and melee stop the navmesh
/// every frame as before; the backline stops its route walk once and then leaves the navmesh to Minerva,
/// whose trash dodges go through it (Holminster Switch, 2026-10-04: an Astrologian stood in every trash
/// AoE because the per-frame stop cancelled each dodge, and her rotation held every damage cast).
/// </summary>
public class CombatHoldTests
{
    private static ThreadPath Path(params ThreadStep[] steps)
    {
        var path = new ThreadPath { TerritoryId = 837, Name = "Holminster Switch" };
        path.Steps.AddRange(steps);
        return path;
    }

    private static (FakeStepWorld World, StepExecutor Executor, int Baseline) Fight(bool melee, bool steering = false)
    {
        var world = new FakeStepWorld { InCombat = true, PlayerPosition = Vector3.Zero, IsMelee = melee, DodgeSteering = steering };
        var executor = new StepExecutor(world);
        executor.Start(Path(new ThreadStep { Verb = StepVerb.MoveTo, Position = new PathPoint(100, 0, 0) }));
        return (world, executor, world.StopMovingCalls);
    }

    private static void Ticks(StepExecutor executor, int n)
    {
        for (var i = 0; i < n; i++)
            executor.Tick();
    }

    /// <summary>Unchanged for tanks and melee: a tank that drifts on drags the next pack in.</summary>
    [Fact]
    public void Melee_stop_every_frame()
    {
        var (world, executor, baseline) = Fight(melee: true);
        Ticks(executor, 10);
        Assert.Equal(baseline + 10, world.StopMovingCalls);
        Assert.Empty(world.MoveRequests);
    }

    [Fact]
    public void Backline_stops_the_route_once()
    {
        var (world, executor, baseline) = Fight(melee: false);
        Ticks(executor, 10);
        Assert.Equal(baseline + 1, world.StopMovingCalls);
        Assert.Empty(world.MoveRequests); // the route still holds
    }

    /// <summary>A dodge under way is never stopped; the hold's one stop waits for it to end.</summary>
    [Fact]
    public void Backline_never_stops_a_dodge()
    {
        var (world, executor, baseline) = Fight(melee: false, steering: true);
        Ticks(executor, 10);
        Assert.Equal(baseline, world.StopMovingCalls);

        world.DodgeSteering = false;
        Ticks(executor, 5);
        Assert.Equal(baseline + 1, world.StopMovingCalls);
    }

    /// <summary>Each fight gets its own stop: the next pack's hold stops the route again.</summary>
    [Fact]
    public void Backline_stops_again_in_the_next_fight()
    {
        var (world, executor, baseline) = Fight(melee: false);
        Ticks(executor, 3);
        Assert.Equal(baseline + 1, world.StopMovingCalls);

        world.InCombat = false;
        executor.Tick();                 // the route walks on
        var afterWalk = world.StopMovingCalls;

        world.InCombat = true;
        Ticks(executor, 3);
        Assert.Equal(afterWalk + 1, world.StopMovingCalls);
    }
}
