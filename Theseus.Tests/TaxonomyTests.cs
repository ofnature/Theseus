using Theseus.Services.Solver;

namespace Theseus.Tests;

/// <summary>
/// The taxonomy's whole job is to be slow to believe: one observation is a hypothesis, a
/// contradiction starts over, and a person's seed entry outranks a machine's guess until the
/// machine has seen the same thing twice.
/// </summary>
public class TaxonomyTests
{
    private const uint Lever = 2001234;
    private const uint Door = 2005678;

    [Fact]
    public void One_observation_is_not_yet_a_class()
    {
        var taxonomy = new Taxonomy();

        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);

        Assert.Equal(BehaviourClass.Unknown, taxonomy.Classify(Lever));
        Assert.Equal(1, taxonomy.Confirmations(Lever));
        Assert.Equal(1, taxonomy.LearnedCount);
        Assert.Equal(0, taxonomy.ConfirmedCount);
    }

    [Fact]
    public void The_second_observation_confirms_it()
    {
        var taxonomy = new Taxonomy();

        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);
        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);

        Assert.Equal(BehaviourClass.DirectTrigger, taxonomy.Classify(Lever));
        Assert.Equal(1, taxonomy.ConfirmedCount);
    }

    [Fact]
    public void A_shipped_seed_beats_one_observation_and_loses_to_two()
    {
        var taxonomy = new Taxonomy(new Dictionary<uint, BehaviourClass> { [Door] = BehaviourClass.TurnIn });

        // A person said so; one run's guess does not overrule it.
        taxonomy.Observe(Door, BehaviourClass.DirectTrigger);
        Assert.Equal(BehaviourClass.TurnIn, taxonomy.Classify(Door));

        // Two runs' guesses do.
        taxonomy.Observe(Door, BehaviourClass.DirectTrigger);
        Assert.Equal(BehaviourClass.DirectTrigger, taxonomy.Classify(Door));
    }

    [Fact]
    public void A_contradiction_starts_over_rather_than_accumulating()
    {
        var taxonomy = new Taxonomy();
        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);
        taxonomy.Observe(Lever, BehaviourClass.DirectTrigger);
        Assert.Equal(BehaviourClass.DirectTrigger, taxonomy.Classify(Lever));

        // The lever turned out to be something else after all. The old confidence is gone: the
        // newest observation is the better hypothesis, and a coin flip must not add its way into a
        // fact.
        taxonomy.Observe(Lever, BehaviourClass.PickupHold);

        Assert.Equal(BehaviourClass.Unknown, taxonomy.Classify(Lever));
        Assert.Equal(1, taxonomy.Confirmations(Lever));

        taxonomy.Observe(Lever, BehaviourClass.PickupHold);
        Assert.Equal(BehaviourClass.PickupHold, taxonomy.Classify(Lever));
    }

    [Fact]
    public void What_a_run_learns_survives_into_the_next_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"theseus-taxonomy-{Guid.NewGuid():N}.json");

        try
        {
            var first = new Taxonomy();
            first.Observe(Lever, BehaviourClass.DirectTrigger);
            first.Observe(Lever, BehaviourClass.DirectTrigger);
            first.Observe(Door, BehaviourClass.TurnIn);
            first.Save(path);

            var second = new Taxonomy();
            second.Load(path);

            Assert.Equal(BehaviourClass.DirectTrigger, second.Classify(Lever)); // confirmed
            Assert.Equal(BehaviourClass.Unknown, second.Classify(Door));        // still a hypothesis
            Assert.Equal(2, second.LearnedCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_or_unreadable_file_is_a_first_run_not_a_failure()
    {
        var warnings = new List<string>();
        var taxonomy = new Taxonomy(log: warnings.Add);

        taxonomy.Load(Path.Combine(Path.GetTempPath(), $"theseus-missing-{Guid.NewGuid():N}.json"));
        Assert.Equal(0, taxonomy.LearnedCount);
        Assert.Empty(warnings);

        var corrupt = Path.Combine(Path.GetTempPath(), $"theseus-corrupt-{Guid.NewGuid():N}.json");
        File.WriteAllText(corrupt, "{ not json at all");
        try
        {
            taxonomy.Load(corrupt);

            Assert.Single(warnings);
            Assert.Equal(0, taxonomy.LearnedCount); // and it still runs
        }
        finally
        {
            File.Delete(corrupt);
        }
    }
}
