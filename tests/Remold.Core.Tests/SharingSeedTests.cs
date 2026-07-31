using System;
using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The shipped measurement as the app takes it: the name-free key, the catalog fingerprint that decides
/// what a pass has to re-read, which file the load adopts, and the one line the status bar shows while a
/// background pass runs.
/// </summary>
public class SharingSeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-seed-" + Guid.NewGuid().ToString("N"));

    public SharingSeedTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string At(string name) => Path.Combine(_root, name);

    // ---- the key ----------------------------------------------------------------------------------

    [Fact]
    public void The_key_is_the_same_every_time_and_different_per_string()
    {
        Assert.Equal(NameKey.Of("vesna|vesnassr01"), NameKey.Of("vesna|vesnassr01"));
        Assert.NotEqual(NameKey.Of("vesna|vesnassr01"), NameKey.Of("vesna|vesnassr02"));
        Assert.NotEqual(NameKey.Of("A"), NameKey.Of("a"));      // callers normalize, the key does not
        Assert.Equal(16, NameKey.Of("vesna|vesnassr01").Length);
    }

    // ---- the fingerprint --------------------------------------------------------------------------

    private static Outfit Outfit(string stem) => new(10, stem, OutfitKind.Base);

    private static CatalogIndex Catalog(string[] deps) => CatalogIndex.ForTest(
        new[] { (GameVfs.PrefabAddress("Character/Player", "VesnaSSR01"), deps[0]) },
        new[] { (GameVfs.PrefabAddress("Character/Player", "VesnaSSR01"), deps) },
        new[] { (deps[0], "aaaa.bundle") });

    [Fact]
    public void The_fingerprint_is_stable_for_an_unchanged_catalog()
    {
        var deps = new[] { "prefab.bundle", "mat.bundle" };
        Assert.Equal(SubjectFingerprint.For(Catalog(deps), Outfit("VesnaSSR01")),
            SubjectFingerprint.For(Catalog(deps), Outfit("VesnaSSR01")));
    }

    [Fact]
    public void The_fingerprint_moves_when_the_subjects_bundle_set_does()
    {
        string before = SubjectFingerprint.For(
            Catalog(new[] { "prefab.bundle", "mat.bundle" }), Outfit("VesnaSSR01"));
        // a dependency added
        Assert.NotEqual(before, SubjectFingerprint.For(
            Catalog(new[] { "prefab.bundle", "mat.bundle", "extra.bundle" }), Outfit("VesnaSSR01")));
        // the SAME bundle set, renamed by content
        Assert.NotEqual(before, SubjectFingerprint.For(
            Catalog(new[] { "prefab2.bundle", "mat.bundle" }), Outfit("VesnaSSR01")));
    }

    [Fact]
    public void The_fingerprint_ignores_what_the_app_derives_for_itself()
    {
        // It answers "has the game moved under this subject". A mesh prefix is this code's own reading of
        // the subject, and the schema version is what invalidates a measurement when that reading changes.
        var deps = new[] { "prefab.bundle", "mat.bundle" };
        Assert.Equal(
            SubjectFingerprint.For(Catalog(deps), Outfit("VesnaSSR01")),
            SubjectFingerprint.For(Catalog(deps),
                new Outfit(10, "VesnaSSR01", OutfitKind.Base) { MeshPrefixOverride = "c_Other_" }));
    }

    [Fact]
    public void The_fingerprint_moves_when_the_internal_id_behind_a_bundle_does()
    {
        // The two bundle namespaces are independent, so a bundle whose content moved is caught by
        // whichever of them the game re-minted.
        var address = GameVfs.PrefabAddress("Character/Player", "VesnaSSR01");
        CatalogIndex WithInternalId(string internalId) => CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle" }) },
            new[] { ("prefab.bundle", internalId) });
        Assert.NotEqual(SubjectFingerprint.For(WithInternalId("aaaa.bundle"), Outfit("VesnaSSR01")),
            SubjectFingerprint.For(WithInternalId("bbbb.bundle"), Outfit("VesnaSSR01")));
    }

    // ---- adoption ---------------------------------------------------------------------------------

    private static SharingPopulation OneSubject() => SharingPopulation.Of(new[]
    {
        new Character(1, "Vesna", "SSR", 10, 1099,
            new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
    });

    private static SharingIndex Measured(string catalogVersion) => SharingIndex.FromMeasurements(
        catalogVersion, new[] { new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null) },
        new Dictionary<string, int[]> { ["11111111"] = new[] { 0 } },
        new Dictionary<string, int[]> { ["aaaaaaaa"] = new[] { 0 } },
        new Dictionary<int, string[]>());

    [Fact]
    public void With_nothing_cached_the_seed_becomes_the_base()
    {
        Measured("25180").Save(At("seed.json"));
        var found = MainWindowViewModel.LoadSharingBase(At("cache.json"), At("seed.json"), "25180", OneSubject());
        Assert.True(found.FromSeed);
        Assert.True(found.Index!.Covers("Vesna", "VesnaSSR01"));
        Assert.Equal("25180", found.Index.CatalogVersion);
    }

    [Fact]
    public void A_seed_from_an_older_catalog_is_still_the_base()
    {
        // It is what the delta repairs — refusing it would cost the whole population a read.
        Measured("25180").Save(At("seed.json"));
        var found = MainWindowViewModel.LoadSharingBase(At("cache.json"), At("seed.json"), "25200", OneSubject());
        Assert.True(found.FromSeed);
        Assert.Equal("25180", found.Index!.CatalogVersion);
    }

    [Fact]
    public void A_cache_from_another_catalog_is_not_the_base_the_seed_is()
    {
        // The cache path is per-catalog, so this is the file a schema the app no longer reads left behind.
        Measured("25180").Save(At("cache.json"));
        Measured("25180").Save(At("seed.json"));
        Assert.True(MainWindowViewModel.LoadSharingBase(
            At("cache.json"), At("seed.json"), "25200", OneSubject()).FromSeed);
    }

    [Fact]
    public void The_installs_own_cache_wins_over_the_seed()
    {
        Measured("25200").Save(At("cache.json"));
        Measured("25180").Save(At("seed.json"));
        var found = MainWindowViewModel.LoadSharingBase(At("cache.json"), At("seed.json"), "25200", OneSubject());
        Assert.False(found.FromSeed);
        Assert.Equal("25200", found.Index!.CatalogVersion);
    }

    [Fact]
    public void With_no_seed_and_no_cache_there_is_no_base()
    {
        Assert.Null(MainWindowViewModel.LoadSharingBase(At("cache.json"), At("seed.json"), "25180", OneSubject()).Index);
    }

    [Fact]
    public void A_seed_that_joins_to_nothing_is_no_base()
    {
        // Adopting it would read as "everything measured, nothing shared" and ship every edit unscoped.
        Measured("25180").Save(At("seed.json"));
        var strangers = SharingPopulation.Of(new[]
        {
            new Character(9, "Mirel", "SSR", 90, 9099,
                new List<Remold.Core.Model.Outfit> { new(90, "MirelSSR01", OutfitKind.Base) }),
        });
        Assert.Null(MainWindowViewModel.LoadSharingBase(At("cache.json"), At("seed.json"), "25180", strangers).Index);
    }

    [Fact]
    public void An_unversioned_game_adopts_nothing()
    {
        Measured("25180").Save(At("seed.json"));
        Assert.Null(MainWindowViewModel.LoadSharingBase(
            At("cache.json"), At("seed.json"), GameInfo.UnknownVersion, OneSubject()).Index);
    }

    // ---- the shipped file -------------------------------------------------------------------------

    [Fact]
    public void The_shipped_seed_is_current_schema_and_carries_rows()
    {
        var seed = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "sharing_seed.json")))!;
        Assert.Equal(4, seed["SchemaVersion"]!.GetValue<int>());
        Assert.NotEmpty(seed["CatalogVersion"]!.GetValue<string>());
        Assert.NotEmpty(seed["Outfits"]!.AsArray());
    }

    [Fact]
    public void The_shipped_seed_carries_nothing_but_keys_and_hashes()
    {
        // The invariant that lets a measurement taken on one install ship to every other one. It holds for
        // the shape the writer produces; this pins it for the artifact actually committed — and walks the
        // document STRUCTURALLY rather than by known field, so a field added later that carried a game name
        // fails here instead of shipping.
        var seed = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "sharing_seed.json")))!;
        int strings = 0;
        void Walk(System.Text.Json.Nodes.JsonNode? node, string path)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    foreach (var (name, child) in obj) Walk(child, $"{path}.{name}");
                    break;
                case System.Text.Json.Nodes.JsonArray arr:
                    for (int i = 0; i < arr.Count; i++) Walk(arr[i], $"{path}[{i}]");
                    break;
                case System.Text.Json.Nodes.JsonValue value
                    when value.GetValueKind() == System.Text.Json.JsonValueKind.String:
                    // the catalog version is the one string that names the GAME rather than a subject, and
                    // the whole file is keyed to it
                    if (path == ".CatalogVersion") break;
                    strings++;
                    Assert.Matches("^[0-9a-fA-F]*$", value.GetValue<string>());
                    break;
            }
        }
        Walk(seed, "");
        Assert.True(strings > 0, "the seed carries no string values at all");
    }

    // ---- the background-work line -----------------------------------------------------------------

    [Fact]
    public void Nothing_running_shows_no_line() =>
        Assert.Equal("", MainWindowViewModel.BackgroundWorkLine(null, prewarming: false));

    [Fact]
    public void The_floor_pass_and_the_delta_read_differently()
    {
        Assert.Equal("Measuring asset sharing… 3/506",
            MainWindowViewModel.BackgroundWorkLine(new SharingProgress(3, 506, Delta: false), false));
        Assert.Equal("Updating asset sharing… 1/4",
            MainWindowViewModel.BackgroundWorkLine(new SharingProgress(1, 4, Delta: true), false));
    }

    [Fact]
    public void Speculative_preparation_gets_the_line_when_no_pass_is_running()
    {
        Assert.Equal("Preparing outfits…", MainWindowViewModel.BackgroundWorkLine(null, prewarming: true));
        // one line: the measurement is the one a build waits on, so it comes first
        Assert.Equal("Measuring asset sharing… 0/2",
            MainWindowViewModel.BackgroundWorkLine(new SharingProgress(0, 2, Delta: false), prewarming: true));
    }

    [Fact]
    public void An_explicit_action_waiting_on_speculative_work_names_the_same_noun_the_cell_does()
    {
        Assert.Equal("Finishing outfit preparation…", MainWindowViewModel.PrewarmWaitLine(cancelling: false));
        Assert.Equal("Stopping outfit preparation…", MainWindowViewModel.PrewarmWaitLine(cancelling: true));
    }

    // ---- the cell's endings -----------------------------------------------------------------------

    [Fact]
    public void A_running_pass_carries_the_cells_line_and_says_what_it_is_for()
    {
        var facet = MainWindowViewModel.BackgroundFacet(
            new SharingProgress(12, 38, Delta: true), prewarming: false, sharingFailed: false);
        // the same rule the Build footer streams while it waits — one wording for one piece of work
        Assert.Equal(MainWindowViewModel.BackgroundWorkLine(new SharingProgress(12, 38, Delta: true), false),
            facet.Text);
        Assert.Equal("Updating asset sharing… 12/38", facet.Text);
        Assert.Equal("", facet.Glyph);
        Assert.Equal(MainWindowViewModel.SharingCellTip, facet.Detail);
    }

    [Fact]
    public void A_failed_pass_ends_on_the_cell_rather_than_in_silence()
    {
        var facet = MainWindowViewModel.BackgroundFacet(null, prewarming: false, sharingFailed: true);
        Assert.Equal("⚠", facet.Glyph);
        Assert.Equal("Asset sharing unmeasured", facet.Text);
        Assert.Equal("Edits ship unscoped. Rescan game files to retry.", facet.Detail);
    }

    [Fact]
    public void A_pass_that_ends_with_nothing_wrong_leaves_the_cell_blank()
    {
        Assert.Equal(StatusFacet.None.Text,
            MainWindowViewModel.BackgroundFacet(null, prewarming: false, sharingFailed: false).Text);
        // and a new pass over the failure's ground outranks it: work in flight is the newer answer
        Assert.Equal("Measuring asset sharing… 0/9", MainWindowViewModel.BackgroundFacet(
            new SharingProgress(0, 9, Delta: false), prewarming: false, sharingFailed: true).Text);
    }

    // ---- what a completed pass writes back --------------------------------------------------------

    [Fact]
    public void A_pass_that_changed_nothing_does_not_rewrite_the_cache()
    {
        // The pass runs every launch now, so a result identical to the file it started from must not churn
        // that file once a session.
        var cached = Measured("25180");
        Assert.False(MainWindowViewModel.ShouldWriteSharingCache(
            cached, new MainWindowViewModel.SharingBase(cached, FromSeed: false)));
    }

    [Fact]
    public void An_adopted_seed_is_always_written_as_this_installs_own_cache()
    {
        var seed = Measured("25180");
        Assert.True(MainWindowViewModel.ShouldWriteSharingCache(
            seed, new MainWindowViewModel.SharingBase(seed, FromSeed: true)));
    }

    [Fact]
    public void A_result_that_moved_is_written()
    {
        var cached = Measured("25180");
        var moved = SharingIndex.FromMeasurements("25180",
            new[] { new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null) },
            new Dictionary<string, int[]> { ["11111111"] = new[] { 0 } },
            new Dictionary<string, int[]> { ["dddddddd"] = new[] { 0 } },
            new Dictionary<int, string[]>());
        Assert.True(MainWindowViewModel.ShouldWriteSharingCache(
            moved, new MainWindowViewModel.SharingBase(cached, FromSeed: false)));
        // and so is a pass under a newer catalog than the base it repaired
        Assert.True(MainWindowViewModel.ShouldWriteSharingCache(
            Measured("25200"), new MainWindowViewModel.SharingBase(cached, FromSeed: false)));
    }

    [Fact]
    public void A_run_that_failed_most_of_the_population_is_not_cached()
    {
        // Typically the game holding its bundles open after a Launch — a fact about the run, not the
        // catalog, and caching it would serve those outfits as uncovered until the next game update.
        var mostly = SharingIndex.FromMeasurements("25180",
            new[] { new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null) },
            new Dictionary<string, int[]>(), new Dictionary<string, int[]>(),
            new Dictionary<int, string[]>(),
            failedOutfits: new[] { "a|a", "b|b", "c|c", "d|d", "e|e" });
        Assert.False(MainWindowViewModel.ShouldWriteSharingCache(mostly, default));
    }

    // ---- the tab filter's reach -------------------------------------------------------------------

    /// <summary>A playable outfit and two enemy doors: one an exact twin of it, one carrying a mesh of its
    /// own.</summary>
    private static SharingIndex Doors() => SharingIndex.FromMeasurements("25180",
        new[]
        {
            new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null),
            new SharingIndex.Wearer("Door", null, "DoorTwin", null),
            new SharingIndex.Wearer("Door", null, "DoorOwn", null),
        },
        new Dictionary<string, int[]>(),
        new Dictionary<string, int[]>
        {
            ["aaaaaaaa"] = new[] { 0, 1, 2 },
            ["bbbbbbbb"] = new[] { 0, 1 },
            ["cccccccc"] = new[] { 2 },
        },
        new Dictionary<int, string[]>(), enemyCharacters: new[] { "Door" });

    [Fact]
    public void A_filtered_door_leaves_the_tab_and_stays_resolvable()
    {
        // The whole point of the split. A door picked on a fresh install — before any measurement called it
        // one — is a subject in someone's mod, and dropping it from the roster is what makes the next launch
        // refuse to build it.
        var (resolvable, listed) = MainWindowViewModel.SplitDuplicateDoors(
            Doors(), "Door", isEnemy: true, new[] { "DoorTwin", "DoorOwn" }, s => s);
        Assert.Equal(new[] { "DoorTwin", "DoorOwn" }, resolvable);
        Assert.Equal(new[] { "DoorOwn" }, listed);
    }

    [Fact]
    public void The_filter_touches_neither_the_character_tab_nor_an_install_with_no_measurement()
    {
        var both = new[] { "DoorTwin", "DoorOwn" };
        // a playable character's outfits are never doors
        Assert.Equal(both, MainWindowViewModel.SplitDuplicateDoors(
            Doors(), "Door", isEnemy: false, both, s => s).Listed);
        // and with nothing measured yet, every door is listed
        Assert.Equal(both, MainWindowViewModel.SplitDuplicateDoors(
            null, "Door", isEnemy: true, both, s => s).Listed);
    }

    // ---- the Pick tabs' dead ends -----------------------------------------------------------------

    [Fact]
    public void A_search_that_matched_nothing_says_so()
    {
        Assert.Equal("No match for \"zzz\".", MainWindowViewModel.NoMatchLine("zzz", shown: 0));
        Assert.Equal("No match for \"zzz\".", MainWindowViewModel.NoMatchLine("  zzz  ", shown: 0));
        Assert.Equal("", MainWindowViewModel.NoMatchLine("zzz", shown: 3));
        // an empty list with nothing searched is the tab's own empty state, not a dead-ended search
        Assert.Equal("", MainWindowViewModel.NoMatchLine("", shown: 0));
        Assert.Equal("", MainWindowViewModel.NoMatchLine(null, shown: 0));
        Assert.Equal("", MainWindowViewModel.NoMatchLine("   ", shown: 0));
    }

    [Fact]
    public void The_enemies_tab_says_where_a_filtered_door_went()
    {
        // The filter is otherwise invisible: an enemy the modder can name is simply not in the list, and a
        // hole with no explanation reads as a missing feature.
        Assert.Equal("Enemies that reuse a character's meshes are listed under that character.",
            MainWindowViewModel.EnemyDoorNote);
    }
}
