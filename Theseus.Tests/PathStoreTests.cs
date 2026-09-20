using Theseus.Services.Paths;

namespace Theseus.Tests;

public class PathStoreTests : IDisposable
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
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    private void WriteSource(string fileName, string verb = "MoveTo")
    {
        Directory.CreateDirectory(SourceDir);
        File.WriteAllText(Path.Combine(SourceDir, fileName), $$"""
            {"Actions":[{"Tag":"None","Name":"{{verb}}","Position":{"X":1.0,"Y":2.0,"Z":3.0},
             "Arguments":[],"Conditions":[],"Note":""}]}
            """);
    }

    [Fact]
    public void Importing_twice_converts_once()
    {
        // The whole point of the store: AutoDuty's format is not a contract, so conversion is an
        // explicit action whose output we keep, not something redone on every launch.
        WriteSource("(1036) Sastasha.json");
        var store = new PathStore(StoreDir);

        var first = store.ImportFrom(SourceDir);
        var second = store.ImportFrom(SourceDir);

        Assert.Equal(1, first.Converted);
        Assert.Equal(0, first.Skipped);
        Assert.Equal(0, second.Converted);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public void An_edited_source_file_is_reconverted()
    {
        WriteSource("(1036) Sastasha.json");
        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        WriteSource("(1036) Sastasha.json", verb: "Boss");

        Assert.Equal(1, store.ImportFrom(SourceDir).Converted);
        Assert.Equal(StepVerb.Boss, store.ForTerritory(1036)[0].Steps[0].Verb);
    }

    [Fact]
    public void Stored_paths_survive_a_reload()
    {
        WriteSource("(1036) Sastasha.json");
        new PathStore(StoreDir).ImportFrom(SourceDir);

        // A fresh store reads what the last one wrote — this is what makes the conversion durable
        // when the source library changes underneath us.
        var reloaded = new PathStore(StoreDir);

        var path = Assert.Single(reloaded.ForTerritory(1036));
        Assert.Equal("Sastasha", path.Name);
        Assert.Equal(new PathPoint(1f, 2f, 3f), path.Steps[0].Position);
    }

    [Fact]
    public void A_territory_with_several_routes_keeps_all_of_them()
    {
        // Twenty-seven territories in the real library have more than one route; variant dungeons
        // reach twelve. Keying the store by territory alone would silently keep only the last.
        WriteSource("(1137) Mount Rokkon - Exit 12 - Middle.json");
        WriteSource("(1137) Mount Rokkon - Exit 21 - Left.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        Assert.Equal(2, store.ForTerritory(1137).Count);
    }

    [Fact]
    public void Unrunnable_paths_are_stored_but_sort_last()
    {
        // Keeping them matters: the UI has to be able to say "this duty needs a recorded path"
        // rather than behaving as though the territory were simply unknown.
        Directory.CreateDirectory(SourceDir);
        WriteSource("(1036) Sastasha A.json");
        File.WriteAllText(Path.Combine(SourceDir, "(1036) Sastasha B.json"), """
            {"Actions":[{"Tag":"None","Name":"DutySpecificCode","Position":{"X":0,"Y":0,"Z":0},
             "Arguments":["1"],"Conditions":[],"Note":""}]}
            """);

        var store = new PathStore(StoreDir);
        var report = store.ImportFrom(SourceDir);

        Assert.Equal(1, report.Blocked);
        var paths = store.ForTerritory(1036);
        Assert.Equal(2, paths.Count);
        Assert.True(paths[0].IsRunnable);
        Assert.False(paths[1].IsRunnable);
    }

    [Fact]
    public void The_chosen_route_variant_wins()
    {
        // Real names from the library: a plain route beside wall-to-wall variants meant for a tank
        // and for everyone else. Picking arbitrarily means a healer running the tank's pulls.
        WriteSource("(1064) Sohm Al.json");
        WriteSource("(1064) 「Tank W2W - タンクまとめ」 Sohm Al.json", verb: "Boss");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        var tankRoute = store.ForTerritory(1064).Single(p => p.Name.Contains("Tank W2W"));

        Assert.Equal(tankRoute.Key, store.Resolve(1064, tankRoute.Key)!.Key);
    }

    [Fact]
    public void A_route_key_survives_a_reimport()
    {
        // A saved preference points at a key, so converting the same file twice must produce the
        // same one — otherwise every re-import silently forgets which variant the user chose.
        WriteSource("(1064) 「Tank W2W - タンクまとめ」 Sohm Al.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);
        var before = store.ForTerritory(1064)[0].Key;

        store.ImportFrom(SourceDir, force: true);

        Assert.Equal(before, store.ForTerritory(1064)[0].Key);
    }

    [Fact]
    public void A_stale_preference_falls_back_instead_of_refusing_to_run()
    {
        // Preferences go stale when the library is re-imported and a route is renamed or dropped.
        // Refusing to run would strand a fleet on a spelling change.
        WriteSource("(1064) Sohm Al.json");
        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        var resolved = store.Resolve(1064, "1064-a-route-that-no-longer-exists");

        Assert.NotNull(resolved);
        Assert.Equal("Sohm Al", resolved.Name);
    }

    [Fact]
    public void An_unrunnable_route_is_never_chosen_even_when_preferred()
    {
        Directory.CreateDirectory(SourceDir);
        WriteSource("(1036) Sastasha Good.json");
        File.WriteAllText(Path.Combine(SourceDir, "(1036) Sastasha Blocked.json"), """
            {"Actions":[{"Tag":"None","Name":"DutySpecificCode","Position":{"X":0,"Y":0,"Z":0},
             "Arguments":["1"],"Conditions":[],"Note":""}]}
            """);

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);
        var blocked = store.ForTerritory(1036).Single(p => !p.IsRunnable);

        var resolved = store.Resolve(1036, blocked.Key);

        Assert.True(resolved!.IsRunnable);
    }

    [Fact]
    public void Only_territories_with_a_choice_are_offered_one()
    {
        WriteSource("(1064) Sohm Al.json");
        WriteSource("(1064) 「Tank W2W - タンクまとめ」 Sohm Al.json");
        WriteSource("(1036) Sastasha.json");

        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        var multi = store.MultiRouteTerritories();

        Assert.Equal(1064u, Assert.Single(multi).Key);
    }

    [Fact]
    public void A_hand_edited_route_survives_re_convert_all()
    {
        // The one destructive mistake the editor makes possible. An imported route can always be
        // rebuilt from its source file; a hand-fixed one exists nowhere else, so losing an evening
        // of waypoint repairs to a button press is not recoverable.
        WriteSource("(1036) Sastasha.json");
        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        var path = store.ForTerritory(1036)[0];
        path.Steps[0].Position = new PathPoint(42f, 42f, 42f);
        path.IsEdited = true;
        store.Save(path);

        store.ImportFrom(SourceDir, force: true);

        Assert.Equal(new PathPoint(42f, 42f, 42f), store.ForTerritory(1036)[0].Steps[0].Position);
    }

    [Fact]
    public void Reverting_discards_unsaved_edits()
    {
        // Edits mutate the stored objects directly, so Revert has to go back to disk.
        WriteSource("(1036) Sastasha.json");
        var store = new PathStore(StoreDir);
        store.ImportFrom(SourceDir);

        store.ForTerritory(1036)[0].Steps[0].Position = new PathPoint(99f, 99f, 99f);
        store.Reload();

        Assert.Equal(new PathPoint(1f, 2f, 3f), store.ForTerritory(1036)[0].Steps[0].Position);
    }

    [Fact]
    public void A_missing_source_folder_is_not_an_error()
    {
        var report = new PathStore(StoreDir).ImportFrom(Path.Combine(_root, "nowhere"));

        Assert.Equal(0, report.Total);
    }
    [Fact]
    public void DeleteRemovesTheRouteAndItsFile()
    {
        var store = new PathStore(StoreDir);
        var path = new ThreadPath { TerritoryId = 1314, Name = "Recorded route" };
        path.Steps.Add(new ThreadStep { Verb = StepVerb.MoveTo, Position = new PathPoint(1, 2, 3) });
        store.Save(path);

        Assert.True(store.Delete(path));
        Assert.Empty(store.ForTerritory(1314));
        Assert.False(store.Delete(path)); // already gone

        // Gone from disk too, so it does not resurrect on the next reload.
        var reloaded = new PathStore(StoreDir);
        Assert.Empty(reloaded.ForTerritory(1314));
    }

}
