using Theseus.Services.Paths;

namespace Theseus.Tests;

/// <summary>
/// Route variants are three different plans, not three spellings of one. A tank route chains packs
/// together with combat stops disabled; the matching non-tank route stops and kills at each pack.
/// Handing the wrong one to a character is not an efficiency question.
/// </summary>
public class RouteVariantTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "theseus-tests", Guid.NewGuid().ToString("N"));

    private string StoreDir => Path.Combine(_root, "store");

    private string SourceDir => Path.Combine(_root, "autoduty");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSource(string fileName)
    {
        Directory.CreateDirectory(SourceDir);
        File.WriteAllText(Path.Combine(SourceDir, fileName), """
            {"Actions":[{"Tag":"None","Name":"MoveTo","Position":{"X":1.0,"Y":2.0,"Z":3.0},
             "Arguments":[],"Conditions":[],"Note":""}]}
            """);
    }

    private PathStore StoreWithSohmAlVariants()
    {
        WriteSource("(1064) Sohm Al.json");
        WriteSource("(1064) 「Tank W2W - タンクまとめ」 Sohm Al.json");
        WriteSource("(1064) 「Other W2W - タンク以外まとめ」 Sohm Al.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);
        return store;
    }

    [Theory]
    [InlineData("Sohm Al", RouteVariant.Standard)]
    [InlineData("「Tank W2W - タンクまとめ」 Sohm Al", RouteVariant.TankWallToWall)]
    [InlineData("「Other W2W - タンク以外まとめ」 Sohm Al", RouteVariant.OtherWallToWall)]
    [InlineData("「W2W-まとめ」 Alexandria", RouteVariant.WallToWall)]
    public void Variants_are_read_from_the_route_name(string name, RouteVariant expected)
    {
        var path = new ThreadPath { TerritoryId = 1064, Name = name };

        Assert.Equal(expected, path.Variant);
    }

    [Fact]
    public void A_tank_gets_the_tank_route()
    {
        var store = StoreWithSohmAlVariants();

        var chosen = store.Resolve(1064, null, wallToWall: true, isTank: true);

        Assert.Equal(RouteVariant.TankWallToWall, chosen!.Variant);
    }

    [Fact]
    public void Everyone_else_gets_the_non_tank_route()
    {
        var store = StoreWithSohmAlVariants();

        var chosen = store.Resolve(1064, null, wallToWall: true, isTank: false);

        Assert.Equal(RouteVariant.OtherWallToWall, chosen!.Variant);
    }

    [Fact]
    public void A_non_tank_is_never_given_the_tank_route()
    {
        // The safety property. With only a tank route available, a damage dealer must fall back to
        // the standard route — falling behind a pull is recoverable, chain-pulling a wing is not.
        WriteSource("(1064) Sohm Al.json");
        WriteSource("(1064) 「Tank W2W - タンクまとめ」 Sohm Al.json");
        WriteSource("(1199) 「W2W-まとめ」 Alexandria.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        Assert.Equal(RouteVariant.Standard, store.Resolve(1064, null, wallToWall: true, isTank: false)!.Variant);
    }

    [Fact]
    public void A_pull_route_is_used_as_a_last_resort_when_it_is_the_only_one()
    {
        // One territory in the real library ships only an unqualified wall-to-wall route. Refusing
        // to run would be worse than running it — in a party the tank is holding everything anyway
        // — but the run status names the variant so a solo character can see what they are about
        // to do before it pulls a wing.
        WriteSource("(1199) 「W2W-まとめ」 Alexandria.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        Assert.Equal(RouteVariant.WallToWall, store.Resolve(1199, null, wallToWall: true, isTank: false)!.Variant);
    }

    [Fact]
    public void An_unqualified_wall_to_wall_route_serves_a_tank_when_there_is_no_tank_route()
    {
        WriteSource("(1199) Alexandria.json");
        WriteSource("(1199) 「W2W-まとめ」 Alexandria.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        Assert.Equal(RouteVariant.WallToWall, store.Resolve(1199, null, wallToWall: true, isTank: true)!.Variant);
    }

    [Fact]
    public void Role_matching_off_forces_the_standard_route_for_everyone()
    {
        var store = StoreWithSohmAlVariants();

        Assert.Equal(RouteVariant.Standard, store.Resolve(1064, null, wallToWall: false, isTank: true)!.Variant);
        Assert.Equal(RouteVariant.Standard, store.Resolve(1064, null, wallToWall: false, isTank: false)!.Variant);
    }

    [Fact]
    public void An_explicit_choice_beats_role_matching()
    {
        // The user overriding us on purpose — including a tank deliberately running the plain route.
        var store = StoreWithSohmAlVariants();
        var plain = store.ForTerritory(1064).Single(p => p.Variant == RouteVariant.Standard);

        var chosen = store.Resolve(1064, plain.Key, wallToWall: true, isTank: true);

        Assert.Equal(RouteVariant.Standard, chosen!.Variant);
    }
}
