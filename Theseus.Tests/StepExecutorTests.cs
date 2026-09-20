using System.Numerics;
using Theseus.Services.Duty;
using Theseus.Services.Paths;
using Theseus.Services.Run;

namespace Theseus.Tests;

public class StepExecutorTests
{
    private static ThreadPath Path(params ThreadStep[] steps)
    {
        var path = new ThreadPath { TerritoryId = 1252, Name = "Mistwake" };
        path.Steps.AddRange(steps);
        return path;
    }

    private static ThreadStep Step(StepVerb verb, PathPoint position = default, params string[] arguments)
        => new() { Verb = verb, Position = position, Arguments = arguments };

    private static ThreadStep Tagged(StepTag tag, StepVerb verb, PathPoint position = default,
        params string[] arguments)
        => new() { Verb = verb, Position = position, Arguments = arguments, Tag = tag };

    /// <summary>Runs until the executor stops advancing, with a hard cap so a bug cannot hang the suite.</summary>
    private static void RunToCompletion(StepExecutor executor, FakeStepWorld world, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(0.1);
        }
    }

    [Fact]
    public void A_straight_path_runs_to_the_end()
    {
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);

        executor.Start(Path(
            Step(StepVerb.Comment),
            Step(StepVerb.StopForCombat, arguments: "false"),
            Step(StepVerb.Rotation, arguments: "On")));
        RunToCompletion(executor, world);

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
        Assert.Equal([true], world.RotationCalls);
    }

    [Fact]
    public void Moving_stops_once_the_destination_is_reached()
    {
        var world = new FakeStepWorld { PlayerPosition = new Vector3(0, 0, 0) };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        executor.Tick();
        Assert.Single(world.MoveRequests);
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.PlayerPosition = new Vector3(100, 0, 0);
        executor.Tick();

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void A_position_is_a_waypoint_whatever_the_verb_is()
    {
        // Only a third of positioned steps in the library are MoveTo. Mistwake walks its entire
        // first wing on positions attached to StopForCombat and Wait — ignoring them does not
        // stall the run, it silently cuts the corner over whatever the navmesh thinks is walkable.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.StopForCombat, new PathPoint(174, 47, 598), "false"),
            Step(StepVerb.Wait, new PathPoint(174, 47, 558), "0"),
            Step(StepVerb.StopForCombat, new PathPoint(207, 46, 468), "true")));

        executor.Tick();
        Assert.Equal(new Vector3(174, 47, 598), Assert.Single(world.MoveRequests));

        world.PlayerPosition = new Vector3(174, 47, 598);
        executor.Tick(); // arrives, toggle applies, advances
        executor.Tick(); // second step walks to its own waypoint

        Assert.Equal(new Vector3(174, 47, 558), world.MoveRequests[^1]);
    }

    [Fact]
    public void A_wait_step_walks_to_its_waypoint_before_waiting()
    {
        // The wall-to-wall pattern: move onto the pack, then wait for the AoE to land.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Wait, new PathPoint(100, 0, 0), "5000")));

        executor.Tick();
        world.Advance(10); // the wait must not have started while still walking
        executor.Tick();

        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.PlayerPosition = new Vector3(100, 0, 0);
        executor.Tick();
        world.Advance(6);
        executor.Tick();

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void Flow_control_never_walks_anywhere()
    {
        // A handful of ModifyIndex and ConditionAction steps carry positions. They are decisions,
        // not places, and walking to one would stall a branch behind a pointless journey.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.Wait, new PathPoint(1, 0, 0), "0"),
            Step(StepVerb.ModifyIndex, new PathPoint(9000, 0, 0), "-1")));

        world.PlayerPosition = new Vector3(1, 0, 0);
        executor.Tick(); // Wait — already there
        executor.Tick(); // ModifyIndex — must not path to (9000,0,0)

        Assert.DoesNotContain(new Vector3(9000, 0, 0), world.MoveRequests);
    }

    [Fact]
    public void A_non_tank_skips_the_chain_pull_steps()
    {
        // Mistwake's opening, verbatim. Eighteen of its thirty steps are tagged W2W; skipping them
        // leaves MoveTo -> Boss -> coffers with combat stops never disabled, which is a character
        // killing each pack where it stands.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, IsTank = false };
        var executor = new StepExecutor(world, () => false);
        executor.Start(Path(
            Tagged(StepTag.W2W, StepVerb.StopForCombat, new PathPoint(174, 47, 598), "false"),
            Tagged(StepTag.W2W, StepVerb.Wait, new PathPoint(174, 47, 558), "2500"),
            Tagged(StepTag.W2W, StepVerb.StopForCombat, new PathPoint(207, 46, 468), "true"),
            Step(StepVerb.MoveTo, new PathPoint(206, 47, 470))));

        executor.Tick();

        // Straight past all three tagged steps to the plain waypoint.
        Assert.Equal(new Vector3(206, 47, 470), Assert.Single(world.MoveRequests));
    }

    [Fact]
    public void A_tank_runs_the_chain_pull_steps()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, IsTank = true };
        var executor = new StepExecutor(world, () => true);
        executor.Start(Path(
            Tagged(StepTag.W2W, StepVerb.StopForCombat, new PathPoint(174, 47, 598), "false"),
            Step(StepVerb.MoveTo, new PathPoint(206, 47, 470))));

        executor.Tick();

        Assert.Equal(new Vector3(174, 47, 598), Assert.Single(world.MoveRequests));
    }

    [Fact]
    public void Skipping_chain_pull_steps_leaves_combat_stops_on()
    {
        // The safety consequence: the "StopForCombat false" that would chain packs is never
        // executed, so a damage dealer holds for the fight instead of dragging the pack along.
        var world = new FakeStepWorld { InCombat = true, PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, () => false);
        executor.Start(Path(
            Tagged(StepTag.W2W, StepVerb.StopForCombat, arguments: "false"),
            Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        executor.Tick();
        executor.Tick();

        Assert.Empty(world.MoveRequests);
    }

    [Fact]
    public void Arrival_is_not_declared_from_several_yalms_short()
    {
        // Field-observed cause of clipping: arriving early re-paths the next leg from a point the
        // route never passed through, so vnavmesh plots a fresh line from off-route and the
        // character catches on scenery getting back to it. Across thirty waypoints it compounds.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(2.5f, 0, 0))));

        executor.Tick();

        Assert.Equal(ExecutorStatus.Running, executor.Status);
    }

    [Fact]
    public void A_waypoint_that_cannot_be_stood_on_is_eventually_accepted()
    {
        // The other half of the trade. Some waypoints sit where the character will not go — a lip,
        // a doorway — and insisting forever turns a rounded corner into a stalled run.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(3f, 0, 0))));

        for (var i = 0; i < 10 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(2); // movement finishes each time, still short
        }

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
        Assert.True(world.MoveRequests.Count is >= 2 and <= 4, $"retried {world.MoveRequests.Count} times");
    }

    [Fact]
    public void Settling_for_a_near_miss_does_not_cost_seconds()
    {
        // A near miss and a blocked door look the same but need opposite treatment. Paying the
        // door's one-second patience at every waypoint left the character standing still for three
        // seconds a time — most of a minute across a thirty-waypoint route.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(3f, 0, 0))));

        var started = world.UtcNow;
        for (var i = 0; i < 40 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(0.1);
        }

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
        Assert.True((world.UtcNow - started).TotalSeconds < 1.5,
            $"settled after {(world.UtcNow - started).TotalSeconds:0.0}s");
    }

    [Fact]
    public void A_blocked_destination_still_retries_patiently()
    {
        // The door case must keep its slow cadence: a full pathfind every frame for as long as it
        // stays shut is exactly what the throttle exists to prevent.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(500, 0, 0))));

        for (var i = 0; i < 50; i++)
        {
            executor.Tick();
            world.Advance(0.1); // five seconds in total
        }

        Assert.True(world.MoveRequests.Count <= 6, $"{world.MoveRequests.Count} pathfinds in 5s");
    }

    [Fact]
    public void An_unreachable_waypoint_faults_in_seconds_not_minutes()
    {
        // "Use my position" records wherever you stand, including a ledge or a step the navmesh
        // does not cover. vnavmesh reports the empty pathfind instantly; waiting three minutes for
        // the progress watchdog to reach the same conclusion helps nobody.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = 0 };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(500, 0, 0))));

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
        Assert.Contains("off the navmesh", executor.FaultReason);
    }

    [Fact]
    public void An_unreadable_waypoint_count_never_faults_a_run()
    {
        // -1 means "cannot tell" — a missing vnavmesh must not look like an unreachable waypoint,
        // or every run would fault the moment the plugin was absent.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = -1 };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(500, 0, 0))));

        for (var i = 0; i < 30; i++)
        {
            executor.Tick();
            // Walking normally throughout: this is about the waypoint count being unreadable, not
            // about a character that cannot move.
            world.PlayerPosition = new Vector3(world.PlayerPosition.X + 10f, 0, 0);
            world.Advance(1);
        }

        Assert.NotEqual(ExecutorStatus.Faulted, executor.Status);
    }

    [Fact]
    public void An_unreachable_chest_is_shrugged_off_rather_than_ending_the_dungeon()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = 0 };
        world.Coffers[42] = new Vector3(30, 0, 0);

        var executor = new StepExecutor(world, lootBossChests: () => true);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        executor.Tick();
        world.BossModuleActive = false;

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void A_waypoint_far_out_of_reach_is_never_quietly_accepted()
    {
        // Slack is for near misses only. Accepting a waypoint the character never got near would
        // skip whole sections of a route.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(50, 0, 0))));

        for (var i = 0; i < 20 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(2);
        }

        Assert.NotEqual(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void Turning_off_looting_removes_the_whole_detour()
    {
        // The Treasure tag covers the walk over and whatever guards the chest, not just the
        // opening — so skipping it must not leave the character standing at an unopened coffer.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, lootRouteChests: () => false);
        executor.Start(Path(
            Tagged(StepTag.Treasure, StepVerb.MoveTo, new PathPoint(50, 0, 0)),
            Tagged(StepTag.Treasure, StepVerb.TreasureCoffer, new PathPoint(55, 0, 0)),
            Step(StepVerb.MoveTo, new PathPoint(200, 0, 0))));

        executor.Tick();

        Assert.Equal(new Vector3(200, 0, 0), Assert.Single(world.MoveRequests));
        Assert.Empty(world.CoffersOpened);
    }

    [Fact]
    public void Looting_on_takes_the_detour()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, lootRouteChests: () => true);
        executor.Start(Path(
            Tagged(StepTag.Treasure, StepVerb.MoveTo, new PathPoint(50, 0, 0)),
            Step(StepVerb.MoveTo, new PathPoint(200, 0, 0))));

        executor.Tick();

        Assert.Equal(new Vector3(50, 0, 0), Assert.Single(world.MoveRequests));
    }

    [Fact]
    public void Chest_and_chain_pull_filters_are_independent()
    {
        // A tank with looting off still runs its W2W steps, and vice versa.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, wallToWall: () => true, lootRouteChests: () => false);
        executor.Start(Path(
            Tagged(StepTag.Treasure, StepVerb.TreasureCoffer, new PathPoint(50, 0, 0)),
            Tagged(StepTag.W2W, StepVerb.MoveTo, new PathPoint(90, 0, 0))));

        executor.Tick();

        Assert.Equal(new Vector3(90, 0, 0), Assert.Single(world.MoveRequests));
    }

    [Fact]
    public void A_step_that_walks_you_off_its_own_waypoint_is_not_dragged_back()
    {
        // Field-observed on Mistwake's slide. AutoMoveFor exists to leave its own position — off a
        // ledge, onto a one-way drop — so re-checking the distance afterwards paths the character
        // straight back up the cliff it just came down.
        var world = new FakeStepWorld { PlayerPosition = new Vector3(106, 36, 267) };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.AutoMoveFor, new PathPoint(106, 36, 267), "2000")));

        executor.Tick();                 // arrives (already there) and starts running forward
        Assert.Equal([true], world.ForwardMovement);

        world.PlayerPosition = new Vector3(115, -109, -16); // the slide happened
        executor.Tick();

        Assert.Empty(world.MoveRequests); // must not path back to the top
    }

    [Fact]
    public void Combat_holds_movement_but_not_the_clock()
    {
        // Walking away from a pack the rotation is mid-fight with pulls the next one too. The
        // step must hold — and must not count the fight against its own stuck-step timeout.
        var world = new FakeStepWorld { InCombat = true, PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        for (var i = 0; i < 50; i++)
        {
            executor.Tick();
            world.Advance(30); // far past the stuck-step timeout
        }

        Assert.Equal(ExecutorStatus.Running, executor.Status);
        Assert.Empty(world.MoveRequests);

        world.InCombat = false;
        executor.Tick();
        Assert.Single(world.MoveRequests);
    }

    [Fact]
    public void StopForCombat_off_keeps_the_run_walking_through_a_fight()
    {
        var world = new FakeStepWorld { InCombat = true, PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.StopForCombat, arguments: "FALSE"),
            Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        executor.Tick(); // StopForCombat
        executor.Tick(); // MoveTo — must not be gated

        Assert.Single(world.MoveRequests);
    }

    [Fact]
    public void ModifyIndex_jumps_backwards_and_reruns_the_step()
    {
        // The library's retry loops are almost all short backward jumps: "is it targetable yet?
        // no — step back and look again".
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.Wait, arguments: "0"),
            Step(StepVerb.Target, arguments: "1234"),
            Step(StepVerb.ModifyIndex, arguments: "-1")));

        executor.Tick(); // Wait
        executor.Tick(); // Target
        executor.Tick(); // ModifyIndex -1
        executor.Tick(); // Target again

        Assert.Equal([1234u, 1234u], world.Targeted);
    }

    [Fact]
    public void A_condition_that_holds_takes_the_branch()
    {
        var world = new FakeStepWorld();
        world.TargetableDataIds.Add(1016994);

        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.ConditionAction, arguments: "ObjectData;1016994;IsTargetable;true&ModifyIndex;+2"),
            Step(StepVerb.Target, arguments: "1"),
            Step(StepVerb.Target, arguments: "2")));
        RunToCompletion(executor, world);

        // Jumped straight to the third step; the second never ran.
        Assert.Equal([2u], world.Targeted);
    }

    [Fact]
    public void A_condition_that_fails_falls_through()
    {
        var world = new FakeStepWorld(); // nothing targetable
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.ConditionAction, arguments: "ObjectData;1016994;IsTargetable;true&ModifyIndex;+2"),
            Step(StepVerb.Target, arguments: "1"),
            Step(StepVerb.Target, arguments: "2")));
        RunToCompletion(executor, world);

        Assert.Equal([1u, 2u], world.Targeted);
    }

    [Fact]
    public void A_jump_off_the_end_faults_instead_of_landing_somewhere_wrong()
    {
        // The real library contains one of these. Clamping would silently resume at the wrong
        // step; stopping tells the user which route needs fixing.
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.ModifyIndex, arguments: "+51"),
            Step(StepVerb.MoveTo, new PathPoint(1, 0, 0))));

        executor.Tick();

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
        Assert.Contains("outside the path", executor.FaultReason);
    }

    [Fact]
    public void A_stuck_step_faults_rather_than_hanging_forever()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, IsMoving = true };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(10);
        }

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
    }

    [Fact]
    public void Resuming_past_a_combat_toggle_keeps_the_mode_the_route_asked_for()
    {
        // Mistwake's opening: combat stops off at step 0, back on at step 2, and the gap between
        // them is the chain pull. Auto-start resumes at step 1, so step 0 never runs — and the run
        // fought each pack where it stood while looking like it was ignoring its wall-to-wall tags.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, InCombat = true };
        var executor = new StepExecutor(world);
        executor.Start(
            Path(
                Step(StepVerb.StopForCombat, arguments: "false"),
                Step(StepVerb.Wait, new PathPoint(100, 0, 0), "2500"),
                Step(StepVerb.StopForCombat, arguments: "true")),
            fromStep: 1);

        executor.Tick();

        // Combat stops are off, so a fight does not hold the walk to the pull point.
        Assert.True(world.MoveRequests.Count > 0, "should still be travelling while in combat");
    }

    [Fact]
    public void Resuming_does_not_replay_toggles_the_character_would_have_skipped()
    {
        // A tank-only toggle must not take effect for a character that would never have run it.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, InCombat = true };
        var executor = new StepExecutor(world, wallToWall: () => false);
        executor.Start(
            Path(
                Tagged(StepTag.W2W, StepVerb.StopForCombat, arguments: "false"),
                Step(StepVerb.Wait, new PathPoint(100, 0, 0), "2500")),
            fromStep: 1);

        executor.Tick();

        Assert.Empty(world.MoveRequests);
    }

    [Fact]
    public void The_navigator_is_told_to_stop_closer_than_we_require()
    {
        // Found in the field: vnavmesh was configured to stop six yalms out, so it halted short of
        // every waypoint in the route while we held out for 1.5 — each one re-pathed forever. The
        // two tolerances have to agree in this direction or waypoints are unreachable by design.
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        Assert.NotNull(world.MoveTolerance);
        Assert.True(world.MoveTolerance < StepTuning.Default.ArrivalTolerance,
            "the navigator must get closer than the executor accepts");
    }

    [Fact]
    public void A_hop_too_short_for_vnavmesh_is_taken_rather_than_stalling()
    {
        // Mistwake's steps 2 and 3 sit 1.85 yalms apart. vnavmesh treats itself as already arrived,
        // returns a path, and never takes a step — while the twenty-yalm hops either side walk fine.
        // Insisting on the tolerance stalls the run forever on a waypoint it is effectively standing on.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = 3 };
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.MoveTo, new PathPoint(2, 0, 0)),
            Step(StepVerb.MoveTo, new PathPoint(500, 0, 0))));

        for (var i = 0; i < 20 && executor.CurrentStepIndex == 0; i++)
        {
            executor.Tick();
            world.Advance(1.1);
        }

        Assert.Equal(ExecutorStatus.Running, executor.Status);
        Assert.Equal(1, executor.CurrentStepIndex);
        Assert.Equal(0, world.Jumps); // no jumping about for a waypoint we are standing on
    }

    [Fact]
    public void A_walkable_path_the_character_never_walks_faults_in_seconds()
    {
        // Mistwake, six yalms from a waypoint approached from the wrong side: vnavmesh returned a
        // three-waypoint path and reported the pathfind complete, every second, for three minutes,
        // while the character stood still. Every signal said success — a full path, no empty
        // pathfind — so nothing short of the watchdog noticed. Only the position told the truth.
        // Far enough out that "close enough" does not apply — this is a genuine failure to move.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = 3 };
        var startedAt = world.UtcNow;
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(60, 0, 0))));

        for (var i = 0; i < 30 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1.1);
        }

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
        Assert.Contains("has not moved", executor.FaultReason);

        // A jump is tried before giving up — wedged in scenery is the common cause and it clears it.
        Assert.True(world.Jumps > 0, "should have tried to jump free before faulting");

        // Seconds, not the three-minute progress watchdog.
        Assert.True(world.UtcNow < startedAt.AddMinutes(1), "should fault long before the watchdog");
    }

    [Fact]
    public void Movement_that_is_working_is_never_called_stuck()
    {
        // The detector must key on the character actually moving, not on how many requests were
        // issued: a long leg re-issues a move every second and is perfectly healthy.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, PathWaypointCount = 3 };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(500, 0, 0))));

        for (var i = 0; i < 30 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.PlayerPosition = new Vector3(world.PlayerPosition.X + 10f, 0, 0);
            world.Advance(1.1);
        }

        Assert.NotEqual(ExecutorStatus.Faulted, executor.Status);
    }

    [Fact]
    public void A_long_leg_does_not_fault_while_the_character_is_still_covering_ground()
    {
        // Mistwake crosses the whole dungeon in four MoveTo legs, so one step can legitimately run
        // for many minutes. A fixed per-step deadline would kill a run that is walking along fine.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, IsMoving = true };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(5000, 0, 0))));

        for (var i = 1; i <= 120 && executor.Status == ExecutorStatus.Running; i++)
        {
            world.PlayerPosition = new Vector3(i * 20f, 0, 0); // still moving
            executor.Tick();
            world.Advance(10); // twenty minutes in total
        }

        Assert.Equal(ExecutorStatus.Running, executor.Status);
    }

    [Fact]
    public void Drifting_on_the_spot_still_counts_as_stuck()
    {
        // Sub-epsilon jitter must not keep resetting the watchdog, or nothing ever faults.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, IsMoving = true };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(100, 0, 0))));

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            world.PlayerPosition = new Vector3(i % 2 == 0 ? 0.4f : 0f, 0, 0);
            executor.Tick();
            world.Advance(10);
        }

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
    }

    [Fact]
    public void The_boss_ai_is_switched_on_once_for_the_whole_run()
    {
        // The fleet runs BossMod's AI permanently, and it costs nothing to leave on — BossMod
        // suppresses its own movement while a navmesh path is running, so it cannot fight Theseus
        // for the character. Toggling it per boss left every stretch of trash without mechanics.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss), Step(StepVerb.Boss)));

        Assert.Equal([true], world.BossAiCalls);

        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();

        world.BossModuleActive = false;
        world.InCombat = false;
        executor.Tick();

        Assert.Equal([true], world.BossAiCalls);
    }

    [Fact]
    public void A_boss_ends_when_its_module_does()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.BossModuleActive = false;
        world.InCombat = false;

        // Not finished the instant combat drops: the room is watched briefly in case a chest is
        // still spawning. Checking once and moving on is what missed them in the field.
        executor.Tick();
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        RunToCompletion(executor, world, maxTicks: 800);
        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    /// <summary>One "Defeat the boss" objective at the given progress, the way a trial reports it.</summary>
    private static DutyObjectiveSnapshot BossObjective(int current)
        => DutyObjectiveSnapshot.From(
            [new DutyObjective(0, current, 1, Hidden: false, Text: "Defeat the Ultima Weapon")],
            ObjectiveSource.Director);

    [Fact]
    public void A_boss_step_waits_through_a_phase_break_until_the_objective_advances()
    {
        // The Ultima Weapon: two modules in one encounter. Between them the module is gone, combat
        // drops, and no chest appears — and phase two comes up ~10s later, which raced the loot
        // grace. Three runs in nine advanced to the next waypoint as phase two began, and the boss
        // handler would not dodge across a path it had not issued.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var progress = 0;
        var executor = new StepExecutor(world, lootBossChests: () => true, objectives: () => BossObjective(progress));
        executor.Start(Path(Step(StepVerb.Boss), Step(StepVerb.Wait, new PathPoint(50, 0, 0), "1000")));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();

        // Phase one ends. Nothing for twenty seconds — twice the loot grace.
        world.BossModuleActive = false;
        world.InCombat = false;
        for (var i = 0; i < 200; i++)
        {
            executor.Tick();
            world.Advance(0.1);
        }

        Assert.Equal(0, executor.CurrentStepIndex);
        Assert.Empty(world.MoveRequests); // never set off toward the Wait point

        // Phase two. The kill advances the objective; only then is the encounter over.
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();
        world.BossModuleActive = false;
        world.InCombat = false;
        progress = 1;
        world.Coffers[9] = new Vector3(1, 0, 0);
        RunToCompletion(executor, world, maxTicks: 400);

        Assert.Equal([9ul], world.OpenedCofferIds);
        Assert.Equal(new Vector3(50, 0, 0), world.MoveRequests[0]); // set off for the Wait point only now
    }

    [Fact]
    public void A_boss_whose_objective_never_ticks_still_finishes_after_the_phase_grace()
    {
        // Not every boss the library marks is counted by the game. Waiting on an objective that
        // will never move would turn every one of those into a twenty-minute watchdog fault.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, lootBossChests: () => true, objectives: () => BossObjective(0));
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();
        world.BossModuleActive = false;
        world.InCombat = false;

        for (var i = 0; i < 50 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Running, executor.Status); // 50s: still inside the grace

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
        Assert.Contains(world.Logs, l => l.Contains("treating the fight as over"));
    }

    [Fact]
    public void A_dead_character_does_not_run_the_phase_clock_down()
    {
        // A wipe reads exactly like a phase break — module gone, objective unchanged. Dead, the
        // step must wait for the raise and the re-pull, not count sixty seconds and walk on.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, lootBossChests: () => true, objectives: () => BossObjective(0));
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();
        world.BossModuleActive = false;
        world.InCombat = false;
        world.IsDead = true;

        for (var i = 0; i < 180; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Running, executor.Status);
        Assert.Equal(0, executor.CurrentStepIndex);
    }

    [Fact]
    public void A_chest_that_appears_a_moment_after_the_kill_is_still_taken()
    {
        // Measured in Mistwake: the module unloads, combat drops, and the coffer materialises a
        // beat later. A single check at that instant finds an empty room.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world, lootBossChests: () => true);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        executor.Tick();
        world.BossModuleActive = false;

        executor.Tick();
        world.Advance(2);
        world.Coffers[7] = new Vector3(1, 0, 0); // spawns late

        RunToCompletion(executor, world, maxTicks: 400);

        Assert.Equal([7ul], world.OpenedCofferIds);
    }

    [Fact]
    public void A_boss_step_does_not_complete_before_the_pull()
    {
        // A boss step is reached before the fight starts, so "no module active" is the normal
        // state on arrival. Reading that as "the fight is over" walks the run past a live boss.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss), Step(StepVerb.Target, arguments: "99")));

        for (var i = 0; i < 20; i++)
        {
            executor.Tick();
            world.Advance(0.5);
        }

        Assert.Empty(world.Targeted);
        Assert.Equal(ExecutorStatus.Running, executor.Status);
    }

    [Fact]
    public void A_boss_marker_with_no_module_behind_it_gives_up_and_moves_on()
    {
        // Not every Boss marker has a module. Waiting on one forever would stall the whole route
        // on the step timeout instead of finishing the dungeon.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss)));

        for (var i = 0; i < 60 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void A_boss_step_pulls_by_acquiring_a_target()
    {
        // Nothing pulls a boss on its own — trash aggroes on proximity, a boss just stands there.
        // Field-confirmed: giving the rotation something to point at is the whole pull.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, EnemiesInRange = 5 };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();

        Assert.Equal(4, world.EnemiesInRange); // one acquisition attempt made
    }

    [Fact]
    public void A_boss_that_will_not_engage_stops_the_run_rather_than_being_walked_past()
    {
        // The expensive failure: a boss that never engages, the run walking on past it, and a
        // ninety-minute farm loop quietly expiring. Better to stop and name the step.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, EnemiesInRange = 10_000 };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Boss), Step(StepVerb.Target, arguments: "77")));

        for (var i = 0; i < 200 && executor.Status == ExecutorStatus.Running; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
        Assert.Contains("would not engage", executor.FaultReason);
        Assert.Empty(world.Targeted); // never reached the following step
    }

    [Fact]
    public void A_dead_boss_leaves_a_chest_that_gets_taken()
    {
        // Routes do not cover these: 215 of 309 end on a Boss step and 153 have no coffer step at
        // all, so the chest a boss drops — including the one at the end of the dungeon — is never
        // in the path data.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Coffers[42] = new Vector3(1, 0, 0); // within arrival range; the fake never walks

        var executor = new StepExecutor(world, lootBossChests: () => true);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();

        world.BossModuleActive = false;
        world.InCombat = false;
        RunToCompletion(executor, world);

        Assert.Equal([42ul], world.OpenedCofferIds);
        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void A_boss_chest_is_left_alone_when_looting_is_off()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Coffers[42] = new Vector3(1, 0, 0); // within arrival range; the fake never walks

        var executor = new StepExecutor(world, lootBossChests: () => false);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        world.InCombat = true;
        executor.Tick();

        world.BossModuleActive = false;
        world.InCombat = false;
        RunToCompletion(executor, world);

        Assert.Empty(world.OpenedCofferIds);
    }

    [Fact]
    public void A_looted_chest_is_not_opened_twice()
    {
        // A looted coffer lingers in the object table, so without remembering it the pass would
        // re-open the same one until its timeout.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Coffers[42] = new Vector3(1.0f, 0, 0);
        world.Coffers[43] = new Vector3(1.2f, 0, 0);

        var executor = new StepExecutor(world, lootBossChests: () => true);
        executor.Start(Path(Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        executor.Tick();
        world.BossModuleActive = false;
        RunToCompletion(executor, world);

        Assert.Equal([42ul, 43ul], world.OpenedCofferIds);
    }

    [Fact]
    public void Boss_treasure_only_takes_the_free_chest_and_skips_the_detours()
    {
        // The two costs differ: a boss chest is in the room you are already standing in, a route
        // chest is a detour. This is the combination that buys one without the other.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Coffers[42] = new Vector3(1, 0, 0);

        var executor = new StepExecutor(world, lootBossChests: () => true, lootRouteChests: () => false);
        executor.Start(Path(
            Tagged(StepTag.Treasure, StepVerb.TreasureCoffer, new PathPoint(80, 0, 0)),
            Step(StepVerb.Boss)));

        executor.Tick();
        world.BossModuleActive = true;
        executor.Tick();
        world.BossModuleActive = false;
        RunToCompletion(executor, world);

        Assert.Empty(world.CoffersOpened);      // the route's detour was skipped
        Assert.Equal([42ul], world.OpenedCofferIds); // the boss chest was not
    }

    [Fact]
    public void Trash_fought_on_the_way_in_is_not_the_boss_encounter()
    {
        // Field-observed: arriving at a boss step already in combat with trash marked the step
        // engaged, so when that trash died the step opened its loot pass in an empty room, waited
        // out the grace, and advanced past a boss it had never pulled.
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero, InCombat = true, EnemiesInRange = 5 };
        var executor = new StepExecutor(world, lootBossChests: () => true);
        executor.Start(Path(Step(StepVerb.Boss, new PathPoint(200, 0, 0)), Step(StepVerb.Target, arguments: "9")));

        // Trash dies before the boss is ever reached.
        for (var i = 0; i < 20; i++)
        {
            executor.Tick();
            world.Advance(1);
        }

        world.InCombat = false;
        for (var i = 0; i < 20; i++)
        {
            executor.Tick();
            // Closing on the boss at a walk, so this stays a test about the approach rather than
            // about a character that has stopped moving.
            world.PlayerPosition = new Vector3(world.PlayerPosition.X + 5f, 0, 0);
            world.Advance(1);
        }

        // Still on the boss step, still trying to get there — not finished, not looting.
        Assert.Equal(ExecutorStatus.Running, executor.Status);
        Assert.Empty(world.Targeted);
        Assert.DoesNotContain(world.Logs, l => l.Contains("encounter over"));
    }

    [Fact]
    public void Stopping_never_switches_the_boss_ai_off()
    {
        // Stopping hands the character back to a user who then most needs mechanics handling.
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.MoveTo, new PathPoint(1, 0, 0))));

        executor.Tick();
        executor.Stop();

        Assert.Equal([true], world.BossAiCalls);
        Assert.Equal(ExecutorStatus.Idle, executor.Status);
        Assert.True(world.StopMovingCalls > 0);
    }

    [Fact]
    public void A_BossMod_off_step_in_a_route_is_ignored()
    {
        // Imported routes contain them; obeying one would disable the AI mid-dungeon.
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.BossMod, arguments: "off")));
        RunToCompletion(executor, world);

        Assert.Equal([true], world.BossAiCalls);
    }

    [Fact]
    public void Waiting_holds_for_the_stated_duration()
    {
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Wait, arguments: "5000")));

        executor.Tick();
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.Advance(6);
        executor.Tick();
        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void WaitFor_understands_the_vocabulary_the_library_uses()
    {
        var world = new FakeStepWorld { IsReady = false };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.WaitFor, arguments: "IsReady")));

        executor.Tick();
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.IsReady = true;
        executor.Tick();
        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void A_revival_step_waits_while_dead()
    {
        var world = new FakeStepWorld { IsDead = true };
        var executor = new StepExecutor(world);
        executor.Start(Path(Step(StepVerb.Revival)));

        executor.Tick();
        Assert.Equal(ExecutorStatus.Running, executor.Status);

        world.IsDead = false;
        executor.Tick();
        Assert.Equal(ExecutorStatus.Finished, executor.Status);
    }

    [Fact]
    public void Step_starts_are_reported_once_each_for_the_objective_mapper()
    {
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        var started = new List<int>();
        executor.StepStarted += started.Add;

        executor.Start(Path(
            Step(StepVerb.Wait, arguments: "0"),
            Step(StepVerb.Wait, arguments: "0"),
            Step(StepVerb.Wait, arguments: "0")));
        RunToCompletion(executor, world);

        Assert.Equal([0, 1, 2], started);
    }

    [Fact]
    public void An_unrunnable_path_faults_at_the_start_with_its_reason()
    {
        var path = Path(Step(StepVerb.MoveTo));
        path.Blockers.Add("Uses DutySpecificCode ×3.");

        var executor = new StepExecutor(new FakeStepWorld());
        executor.Start(path);

        Assert.Equal(ExecutorStatus.Faulted, executor.Status);
        Assert.Contains("DutySpecificCode", executor.FaultReason);
    }

    [Fact]
    public void A_resume_starts_partway_in()
    {
        // What the Thread does with a relocalized step index.
        var world = new FakeStepWorld();
        var executor = new StepExecutor(world);
        executor.Start(Path(
            Step(StepVerb.Target, arguments: "1"),
            Step(StepVerb.Target, arguments: "2"),
            Step(StepVerb.Target, arguments: "3")), fromStep: 2);
        RunToCompletion(executor, world);

        Assert.Equal([3u], world.Targeted);
    }
}
