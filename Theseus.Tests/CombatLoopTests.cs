using System.Numerics;
using Theseus.Services.Frontier;
using Theseus.Services.Run;
using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The combat loop does not fight. It decides whether there is a fight, walks into range, and
/// targets — the rotation kills the pack because Theseus.IsBusy is true for as long as the loop
/// keeps bidding.
/// </summary>
public sealed class CombatLoopTests
{
    private static WorldModel.Snapshot World(
        bool inCombat = false,
        params WorldModel.Recognised[] objects)
        => new(DateTime.UtcNow, Vector3.Zero, (1314, 1, 103), 0, 0, 3, true, inCombat,
            false, objects, [], null, false);

    private static WorldModel.Recognised Hostile(float distance)
        => new(new WorldObject(99, 99, "wolf", new Vector3(distance, 0f, 0f), WorldObjectKind.Hostile, true),
            null, false, distance);

    [Fact]
    public void It_bids_when_the_pack_is_already_on_us()
    {
        var bid = new CombatLoop().Bid(World(inCombat: true));

        Assert.NotNull(bid);
        Assert.Contains("in combat", bid!.Reason);
    }

    [Fact]
    public void It_bids_on_a_hostile_inside_aggro_range()
    {
        Assert.NotNull(new CombatLoop().Bid(World(objects: [Hostile(CombatLoop.AggroRange - 5f)])));
    }

    [Fact]
    public void A_hostile_across_the_room_is_not_ours_to_start()
    {
        // Walking over to start a fight the route did not ask for is how a run pulls a wing it was
        // meant to walk past.
        Assert.Null(new CombatLoop().Bid(World(objects: [Hostile(CombatLoop.AggroRange + 5f)])));
    }

    [Fact]
    public void Nothing_to_fight_bids_nothing()
    {
        Assert.Null(new CombatLoop().Bid(World()));
    }

    [Fact]
    public void Out_of_range_it_walks_in_without_engaging()
    {
        var game = new FakeStepWorld();

        new CombatLoop().Run(World(objects: [Hostile(12f)]), game);

        Assert.Single(game.MoveRequests);
        Assert.Empty(game.Attacked);
    }

    [Fact]
    public void In_range_it_targets_and_stands_still()
    {
        var game = new FakeStepWorld();

        new CombatLoop().Run(World(objects: [Hostile(2f)]), game);

        // Standing still is deliberate: the rotation owns positioning from here, and a loop that
        // keeps walking fights the boss module for the character's feet.
        Assert.Equal(1, game.StopMovingCalls);
        Assert.Equal([99ul], game.Attacked);
        Assert.Empty(game.MoveRequests);
    }

    [Fact]
    public void With_nothing_left_it_stops_walking()
    {
        var game = new FakeStepWorld();

        new CombatLoop().Run(World(), game);

        Assert.Equal(1, game.StopMovingCalls);
        Assert.Empty(game.Attacked);
    }
}
