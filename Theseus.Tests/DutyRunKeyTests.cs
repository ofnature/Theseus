using Theseus.Services.Duty;

namespace Theseus.Tests;

public class DutyRunKeyTests
{
    [Fact]
    public void A_differing_start_timestamp_separates_two_runs()
    {
        // Where the game does populate it — timed content — the timestamp does the job.
        var first = new DutyRunKey(1252, 1036, 1_754_300_000);
        var second = new DutyRunKey(1252, 1036, 1_754_301_500);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_dungeon_requeue_is_currently_indistinguishable()
    {
        // Known gap, measured in Mistwake: DirectorStartTimestamp is 0 for a plain dungeon, so two
        // separate runs of the same duty produce the same key. Pinned rather than hidden — resume
        // reconciles against live objective state and the game always beats the stored ledger, so
        // this degrades precision rather than correctness, but the Thread needs a real
        // discriminator before it ships.
        Assert.Equal(new DutyRunKey(1252, 1036, 0), new DutyRunKey(1252, 1036, 0));
    }

    [Fact]
    public void The_same_instance_read_twice_matches()
    {
        // This is the test a checkpoint runs after a crash: re-reading the live director must
        // reproduce the key that was persisted, or no resume is ever offered.
        Assert.Equal(
            new DutyRunKey(1252, 1036, 1_754_300_000),
            new DutyRunKey(1252, 1036, 1_754_300_000));
    }

    [Fact]
    public void A_director_that_has_not_published_yet_is_not_valid()
    {
        // The director exists for a few frames before it is populated. Treating that as a real key
        // would make every entry look like a different run and discard every resume.
        Assert.False(DutyRunKey.None.IsValid);
        Assert.False(new DutyRunKey(1252, 0, 0).IsValid);
    }

    [Fact]
    public void A_dungeon_with_no_start_timestamp_is_still_a_valid_key()
    {
        // Validity keys off the content id, not the timestamp. Requiring the timestamp left the
        // run key permanently "none" in a dungeon, which also meant RunController read territory 0
        // and refused to start any route at all.
        Assert.True(new DutyRunKey(1252, 1036, 0).IsValid);
        Assert.Equal(1252u, new DutyRunKey(1252, 1036, 0).TerritoryId);
    }
}
