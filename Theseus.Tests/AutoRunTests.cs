using Theseus.Services.Run;

namespace Theseus.Tests;

/// <summary>
/// Auto-run follows the camera in Legacy movement mode and the character in Standard, and every
/// route was recorded under AutoDuty forcing Standard for the length of the run.
/// </summary>
public class AutoRunTests
{
    private const uint Standard = 0;
    private const uint Legacy = 1;

    private sealed class Game
    {
        public uint? MoveMode = Standard;
        public bool Unreadable;
        public readonly List<uint> Writes = [];
        public readonly List<string> Commands = [];

        public AutoRun AutoRun { get; }

        public Game()
        {
            AutoRun = new AutoRun(
                () => Unreadable ? throw new InvalidOperationException() : MoveMode,
                mode =>
                {
                    Writes.Add(mode);
                    MoveMode = mode;
                },
                Commands.Add);
        }
    }

    [Fact]
    public void Legacy_is_switched_to_standard_for_the_run_and_put_back_after()
    {
        var game = new Game { MoveMode = Legacy };

        game.AutoRun.Set(true);
        Assert.Equal(Standard, game.MoveMode);
        Assert.Equal(["/automove on"], game.Commands);

        game.AutoRun.Set(false);
        Assert.Equal(Legacy, game.MoveMode);
        Assert.Equal(["/automove on", "/automove off"], game.Commands);
    }

    [Fact]
    public void Standard_is_left_exactly_as_it_was()
    {
        var game = new Game { MoveMode = Standard };

        game.AutoRun.Set(true);
        game.AutoRun.Set(false);

        Assert.Empty(game.Writes); // the setting is the user's; nothing to change, nothing to restore
    }

    [Fact]
    public void Asking_twice_does_not_forget_that_legacy_is_owed_back()
    {
        // The second "on" reads Standard because the first one set it. Taking that at face value
        // would leave the user in Standard mode for good.
        var game = new Game { MoveMode = Legacy };

        game.AutoRun.Set(true);
        game.AutoRun.Set(true);
        game.AutoRun.Set(false);

        Assert.Equal(Legacy, game.MoveMode);
    }

    [Fact]
    public void Stopping_without_having_started_changes_nothing()
    {
        // Every stop path releases forward movement, including runs that never held it.
        var game = new Game { MoveMode = Legacy };

        game.AutoRun.Set(false);

        Assert.Empty(game.Writes);
        Assert.Equal(["/automove off"], game.Commands);
    }

    [Fact]
    public void A_mode_that_cannot_be_read_still_runs()
    {
        var game = new Game { Unreadable = true };

        game.AutoRun.Set(true);
        game.AutoRun.Set(false);

        Assert.Empty(game.Writes);
        Assert.Equal(["/automove on", "/automove off"], game.Commands);
    }
}
