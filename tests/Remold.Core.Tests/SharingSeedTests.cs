using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
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
        // The two bundle namespaces are independent, so a bundle that re-minted in either one moves the
        // fingerprint. Content rewritten in place under names that both survive is the read record's
        // question, not this one.
        var address = GameVfs.PrefabAddress("Character/Player", "VesnaSSR01");
        CatalogIndex WithInternalId(string internalId) => CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle" }) },
            new[] { ("prefab.bundle", internalId) });
        Assert.NotEqual(SubjectFingerprint.For(WithInternalId("aaaa.bundle"), Outfit("VesnaSSR01")),
            SubjectFingerprint.For(WithInternalId("bbbb.bundle"), Outfit("VesnaSSR01")));
    }

    // ---- the read record --------------------------------------------------------------------------

    /// <summary>A catalog naming two mesh bundles, each joined to the manifest internalId given.</summary>
    private static CatalogIndex ReadCatalog(string vInternalId = "v-1", string kInternalId = "k-1") =>
        CatalogIndex.ForTest(
            new[] { ("Assets/X/a.mesh", "vmesh.bundle"), ("Assets/X/b.mesh", "kmesh.bundle") },
            null,
            new[] { ("vmesh.bundle", vInternalId), ("kmesh.bundle", kInternalId) });

    /// <summary>A content-hash lookup over exactly these internalIds; anything else resolves to no
    /// hash, the way an internalId the manifest does not name does.</summary>
    private static Func<string, string?> Content(params (string InternalId, string ContentHash)[] files) =>
        id => files.FirstOrDefault(f => f.InternalId == id).ContentHash;

    [Fact]
    public void The_read_record_is_one_triple_per_bundle_and_current_in_the_world_it_was_taken_in()
    {
        var catalog = ReadCatalog();
        var content = Content(("v-1", "vhash"), ("k-1", "khash"));
        string reads = BundleReads.Of(catalog, content, new[] { "vmesh.bundle", "kmesh.bundle" });

        // bundle key, internalId key, content-hash key — per bundle, and nothing but hex
        Assert.Equal(2 * 3 * 16, reads.Length);
        Assert.Matches("^[0-9A-F]+$", reads);
        Assert.True(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, content), reads));
        // a bundle read twice is one triple: the record is over the SET the measurement depended on
        Assert.Equal(3 * 16,
            BundleReads.Of(catalog, content, new[] { "vmesh.bundle", "vmesh.bundle" }).Length);
    }

    [Fact]
    public void A_bundles_content_moving_under_an_unchanged_name_and_internal_id_reads_as_moved()
    {
        // The one thing the name namespaces cannot report: a bundle rewritten where it stands. Both names
        // are held constant here — the catalog is the same object on both sides — so the content behind
        // one of them is all that moved, and the row that read it has to measure again.
        var catalog = ReadCatalog();
        string reads = BundleReads.Of(catalog, Content(("v-1", "vhash"), ("k-1", "khash")),
            new[] { "vmesh.bundle", "kmesh.bundle" });

        Assert.False(BundleReads.StillCurrent(
            BundleReads.CurrentKeys(catalog, Content(("v-1", "vhash-2"), ("k-1", "khash"))), reads));
        // …and the row that read only the unmoved bundle is untouched
        Assert.True(BundleReads.StillCurrent(
            BundleReads.CurrentKeys(catalog, Content(("v-1", "vhash-2"), ("k-1", "khash"))),
            BundleReads.Of(catalog, Content(("k-1", "khash")), new[] { "kmesh.bundle" })));
    }

    [Fact]
    public void A_read_record_in_any_other_key_shape_reads_as_moved()
    {
        // A record that is not a whole number of triples was written by something else, and nothing can be
        // said about which keys are which inside it — so the row measures again rather than being trusted.
        var catalog = ReadCatalog();
        var keys = BundleReads.CurrentKeys(catalog, Content(("v-1", "vhash")));
        string nameAndInternalIdOnly = NameKey.Of("vmesh.bundle") + NameKey.Of("v-1");

        Assert.Equal(32, nameAndInternalIdOnly.Length);
        Assert.False(BundleReads.StillCurrent(keys, nameAndInternalIdOnly));
        // an empty record records no bundles at all, which is current by definition
        Assert.True(BundleReads.StillCurrent(keys, ""));
    }

    [Fact]
    public void A_bundle_no_manifest_entry_names_records_the_same_absent_marker_on_both_sides()
    {
        // Absence is a fact like any other. A bundle whose internalId the manifest does not name, and a
        // bundle the catalog does not name at all, both have to round-trip as a state rather than as a
        // skipped half — otherwise a row that read one can never be current.
        var catalog = ReadCatalog();
        var located = Content(("v-1", "vhash"));
        var unlocated = Content();

        Assert.True(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, unlocated),
            BundleReads.Of(catalog, unlocated, new[] { "vmesh.bundle" })));
        Assert.True(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, unlocated),
            BundleReads.Of(catalog, unlocated, new[] { "stranger.bundle" })));
        // and the file arriving is a move like any other
        Assert.False(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, located),
            BundleReads.Of(catalog, unlocated, new[] { "vmesh.bundle" })));
    }

    [Fact]
    public void The_content_lookup_answers_from_the_manifests_own_stub()
    {
        // The join the app hands the measurement: internalId → the content hash the VFS stub carries for
        // it, read out of the manifest image with no bundle opened.
        string dir = At("gff");
        Directory.CreateDirectory(dir);
        string manifestPath = Path.Combine(dir, GffManifest.ManifestHash + ".bundle");
        string physHash = new string('a', 32);
        FakeGff.Write(manifestPath, ("v-1.bundle", FakeGff.Stub(physHash, 0, 0, subSeed: 0x5A)));

        var lookup = BundleReads.ContentHashLookup(GffManifest.Read(manifestPath));
        // the stub's 16 content bytes, hex: this fixture writes all sixteen as 0x5A
        Assert.Equal(string.Concat(Enumerable.Repeat("5a", 16)), lookup("v-1.bundle"));
        Assert.Equal(lookup("v-1.bundle"), lookup("v-1"));   // the catalog spells internalIds both ways
        Assert.Null(lookup("k-1.bundle"));                   // an internalId this manifest does not name
        // and it is the CONTENT half of the stub, not the physical filename in the same 40 bytes
        Assert.NotEqual(physHash, lookup("v-1.bundle"));
    }

    [Fact]
    public void A_single_file_bundles_content_hash_is_the_only_thing_a_rewrite_in_place_moves()
    {
        // Why the record cannot key on the physical filename. For a single-file bundle the manifest entry
        // name IS physHash + ".bundle" — the live install's rule for all 7,258 of them — so the filename
        // key can only ever restate the internalId key sitting beside it. Two manifests for the same
        // single, differing only in what the bundle CONTAINS, are what a rewrite in place looks like.
        string dir = At("singles");
        Directory.CreateDirectory(dir);
        string phys = new string('a', 32);
        string Manifest(string name, byte content)
        {
            string p = Path.Combine(dir, name);
            FakeGff.Write(p, (phys + ".bundle", FakeGff.Stub(phys, 0, 0, content)));
            return p;
        }
        var before = GffManifest.Read(Manifest("before.bundle", 1));
        var after = GffManifest.Read(Manifest("after.bundle", 2));

        // the entry name restates the physical file, and neither moved
        Assert.Equal(phys + ".bundle", Assert.Single(before.Names));
        Assert.Equal(before.Names, after.Names);
        Assert.Equal(phys, before.Locate(phys + ".bundle").Stub.PhysHash);
        Assert.Equal(phys, after.Locate(phys + ".bundle").Stub.PhysHash);

        var catalog = CatalogIndex.ForTest(new[] { ("Assets/X/a.mesh", "vmesh.bundle") }, null,
            new[] { ("vmesh.bundle", phys + ".bundle") });
        string reads = BundleReads.Of(catalog, BundleReads.ContentHashLookup(before),
            new[] { "vmesh.bundle" });
        Assert.True(BundleReads.StillCurrent(
            BundleReads.CurrentKeys(catalog, BundleReads.ContentHashLookup(before)), reads));
        Assert.False(BundleReads.StillCurrent(
            BundleReads.CurrentKeys(catalog, BundleReads.ContentHashLookup(after)), reads));
    }

    [Fact]
    public void A_bundle_the_catalog_does_not_name_takes_the_absent_marker_whatever_the_lookup_says()
    {
        // Symmetry between the two sides of the check. StillCurrent compares a bundle missing from the
        // current map against a fixed absent marker, so the record must write that same marker rather than
        // asking the lookup about an empty internalId — a lookup that answered anything for "" would write
        // rows no check could ever call current again.
        var catalog = ReadCatalog();
        Func<string, string?> talkative = id => id.Length == 0 ? "something" : "vhash";

        string reads = BundleReads.Of(catalog, talkative, new[] { "stranger.bundle" });
        Assert.True(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, talkative), reads));
        // and it is the same record a well-behaved lookup writes for that bundle
        Assert.Equal(BundleReads.Of(catalog, Content(), new[] { "stranger.bundle" }), reads);
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
        // The twin of the writer-side pin in SharingIndexTests: the shipped artifact and the code that
        // writes it must agree, or the seed is refused at load on every install.
        Assert.Equal(6, seed["SchemaVersion"]!.GetValue<int>());
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

    /// <summary>A superseded pass writes nothing. Cancellation normally comes out of the build as an
    /// exception, but a pass that finished in the same instant reaches the write with its token already
    /// down — and what cancels it is a rescan, which may have just swept the cache folder. Writing then
    /// resurrects the file the modder asked to clear, holding rows measured before the sweep.</summary>
    [Fact]
    public void A_cancelled_pass_writes_no_cache()
    {
        var seed = Measured("25180");
        var adopted = new MainWindowViewModel.SharingBase(seed, FromSeed: true);   // the always-write case
        using var cts = new CancellationTokenSource();

        Assert.True(MainWindowViewModel.ShouldWriteSharingCache(seed, adopted, cts.Token));
        cts.Cancel();
        Assert.False(MainWindowViewModel.ShouldWriteSharingCache(seed, adopted, cts.Token));
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
