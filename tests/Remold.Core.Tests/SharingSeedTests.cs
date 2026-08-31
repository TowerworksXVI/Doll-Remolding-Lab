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
    public void The_fingerprint_ignores_the_internal_id_behind_a_bundle()
    {
        // An internalId is minted by the packer — for a single-file bundle it IS the physical content hash
        // of the file — so every one of them re-mints when the game is repacked, with nothing about the
        // subject having changed. A fingerprint that joined to them would die on every patch. Whether the
        // CONTENT behind the bundle moved is the read record's question, not this one.
        var address = GameVfs.PrefabAddress("Character/Player", "VesnaSSR01");
        CatalogIndex WithInternalId(string internalId) => CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle" }) },
            new[] { ("prefab.bundle", internalId) });
        Assert.Equal(SubjectFingerprint.For(WithInternalId("aaaa.bundle"), Outfit("VesnaSSR01")),
            SubjectFingerprint.For(WithInternalId("bbbb.bundle"), Outfit("VesnaSSR01")));
    }

    [Fact]
    public void The_fingerprint_reads_the_scope_as_a_set()
    {
        // Membership is the catalog fact; the ORDER of a dependency array is the packer's, and reordering
        // it is packaging churn like a re-mint. So the same bundles listed the other way round are the
        // same scope, while a bundle arriving or leaving is not.
        // the hit bundle leads the scope either way, so the two dependencies BEHIND it are what carries
        // the order difference
        string forward = SubjectFingerprint.For(
            Catalog(new[] { "prefab.bundle", "mat.bundle", "extra.bundle" }), Outfit("VesnaSSR01"));
        string swapped = SubjectFingerprint.For(
            Catalog(new[] { "prefab.bundle", "extra.bundle", "mat.bundle" }), Outfit("VesnaSSR01"));
        Assert.Equal(forward, swapped);
        // …and a bundle leaving the closure is not a reordering
        Assert.NotEqual(forward, SubjectFingerprint.For(
            Catalog(new[] { "prefab.bundle", "mat.bundle" }), Outfit("VesnaSSR01")));
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
    public void The_read_record_is_one_content_pair_per_bundle_and_current_in_the_world_it_was_taken_in()
    {
        var catalog = ReadCatalog();
        var content = Content(("v-1", "vhash"), ("k-1", "khash"));
        string reads = BundleReads.Of(catalog, content, new[] { "vmesh.bundle", "kmesh.bundle" });

        // logical bundle key, content-hash key — per bundle, and nothing but hex. The internalId is NOT
        // among them: it is minted by the packer, so keying on it would invalidate every row of a repack.
        Assert.Equal(2 * 2 * 16, reads.Length);
        Assert.Matches("^[0-9A-F]+$", reads);
        Assert.True(BundleReads.StillCurrent(BundleReads.CurrentKeys(catalog, content), reads));
        // a bundle read twice is one pair: the record is over the SET the measurement depended on
        Assert.Equal(2 * 16,
            BundleReads.Of(catalog, content, new[] { "vmesh.bundle", "vmesh.bundle" }).Length);
    }

    [Fact]
    public void A_bundle_that_only_re_minted_is_still_current()
    {
        // The repack, in the smallest shape that shows it: the same logical bundle, the same content, a
        // brand-new internalId (a single-file bundle's internalId IS its physical filename, so a repack
        // re-mints every one of them). The row that read it must survive, or a patch that moved nothing
        // costs the whole population a re-measure.
        string reads = BundleReads.Of(ReadCatalog("v-1", "k-1"),
            Content(("v-1", "vhash"), ("k-1", "khash")), new[] { "vmesh.bundle", "kmesh.bundle" });

        Assert.True(BundleReads.StillCurrent(
            BundleReads.CurrentKeys(ReadCatalog("v-2", "k-2"),
                Content(("v-2", "vhash"), ("k-2", "khash"))), reads));
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
        // A record that is not a whole number of pairs was written by something else, and nothing can be
        // said about which keys are which inside it — so the row measures again rather than being trusted.
        // The shape used here is the schema-6 one this code replaced: bundle, internalId, content hash.
        var catalog = ReadCatalog();
        var keys = BundleReads.CurrentKeys(catalog, Content(("v-1", "vhash")));
        string oldTripleShape = NameKey.Of("vmesh.bundle") + NameKey.Of("v-1") + NameKey.Of("vhash");

        Assert.Equal(48, oldTripleShape.Length);
        Assert.False(BundleReads.StillCurrent(keys, oldTripleShape));
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
        // so is the bundle LEAVING the catalog, even though no content hash was resolvable on either
        // side: the two absences are deliberately different keys, or a departure could read as current
        var without = CatalogIndex.ForTest(new[] { ("Assets/X/b.mesh", "kmesh.bundle") }, null,
            new[] { ("kmesh.bundle", "k-1") });
        Assert.False(BundleReads.StillCurrent(BundleReads.CurrentKeys(without, unlocated),
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

    [Fact]
    public void The_absent_catalog_marker_is_the_key_every_persisted_row_already_carries()
    {
        // A VALUE pin, not a behaviour one. The marker is a constant this code writes into every row that
        // recorded a bundle the catalog does not name — the shipped seed's rows included — so its spelling
        // in the source is a persisted-file contract: move it by one byte and every such row stops matching
        // and the whole population re-measures, silently. The expectation is computed outside this code
        // (SHA-256 of the marker's UTF-8 bytes, first 64 bits, hex) rather than read back off the constant.
        var catalog = ReadCatalog();
        string reads = BundleReads.Of(catalog, Content(), new[] { "stranger.bundle" });

        Assert.Equal(32, reads.Length);                          // bundle key, then the marker
        Assert.Equal("3E4ED681F925E599", reads.Substring(16));
        // …and it is still not the OTHER absence — a bundle the catalog names whose hash would not mint
        Assert.NotEqual(NameKey.Of(""), reads.Substring(16));
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

    private static void SaveCompleteLocal(string path, string catalogVersion)
    {
        Measured(catalogVersion).Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var row in json["Outfits"]!.AsArray())
            row!["R"] = "00112233445566778899aabbccddeeff";
        File.WriteAllText(path, json.ToJsonString());
    }

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
    public void Newest_complete_prior_local_cache_is_selected_before_the_seed()
    {
        string current = At("sharing_25200.json");
        string prior = At("sharing_25180.json");
        string seed = At("seed.json");
        SaveCompleteLocal(prior, "25180");
        MainWindowViewModel.WriteSharingInstallContext(prior, "install-A");
        Measured("25000").Save(seed);

        var found = MainWindowViewModel.LoadSharingBase(current, seed, "25200", OneSubject(),
            installIdentity: "install-A");

        Assert.False(found.FromSeed);
        Assert.Equal("25180", found.Index!.CatalogVersion);
    }

    [Fact]
    public void Wrong_schema_prior_local_cache_is_rejected_in_favor_of_the_seed()
    {
        string current = At("sharing_25200.json");
        string prior = At("sharing_25180.json");
        string seed = At("seed.json");
        SaveCompleteLocal(prior, "25180");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(prior))!;
        json["SchemaVersion"] = SharingIndex.SchemaVersion - 1;
        File.WriteAllText(prior, json.ToJsonString());
        MainWindowViewModel.WriteSharingInstallContext(prior, "install-A");
        Measured("25000").Save(seed);

        var found = MainWindowViewModel.LoadSharingBase(current, seed, "25200", OneSubject(),
            installIdentity: "install-A");

        Assert.True(found.FromSeed);
        Assert.Equal("25000", found.Index!.CatalogVersion);
    }

    [Fact]
    public void Seed_load_logs_schema_acceptance_and_row_join_counts()
    {
        string seed = At("logged-seed.json");
        Measured("25180").Save(seed);
        var lines = new List<(string Context, string Detail)>();

        var found = MainWindowViewModel.LoadSharingBase(At("absent-cache.json"), seed, "25180",
            OneSubject(), (context, detail) => lines.Add((context, detail)));

        Assert.True(found.FromSeed);
        var line = Assert.Single(lines);
        Assert.Equal("Asset sharing seed", line.Context);
        Assert.Contains($"schema {SharingIndex.SchemaVersion} accepted", line.Detail);
        Assert.Contains("rows loaded 1, joined 1, dropped 0", line.Detail);
    }

    [Fact]
    public void Seed_load_logs_schema_refusal_and_dropped_row_counts()
    {
        string seed = At("refused-seed.json");
        Measured("25180").Save(seed);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(seed))!;
        json["SchemaVersion"] = SharingIndex.SchemaVersion - 1;
        File.WriteAllText(seed, json.ToJsonString());
        var lines = new List<(string Context, string Detail)>();

        var found = MainWindowViewModel.LoadSharingBase(At("absent-cache-2.json"), seed, "25180",
            OneSubject(), (context, detail) => lines.Add((context, detail)));

        Assert.Null(found.Index);
        var line = Assert.Single(lines);
        Assert.Contains($"schema {SharingIndex.SchemaVersion - 1} refused", line.Detail);
        Assert.Contains("rows loaded 1, joined 0, dropped 1", line.Detail);
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

    /// <summary>The committed seed is a real minted artifact (see
    /// <see cref="Remold.Core.LabPaths.SharingSeedFile"/> for the procedure) and every install adopts it as
    /// its base. This is the re-mint tripwire in its armed state: a schema bump lands, this fails on the
    /// stale number the file still states, and the release step is minting a fresh pair — never teaching
    /// the loader the old schema.</summary>
    [Fact]
    public void The_shipped_seed_is_the_current_schema_and_becomes_the_base()
    {
        var seed = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "sharing_seed.json")))!;
        Assert.Equal(SharingIndex.SchemaVersion, seed["SchemaVersion"]!.GetValue<int>());
        Assert.NotEmpty(seed["CatalogVersion"]!.GetValue<string>());
        Assert.NotEmpty(seed["Outfits"]!.AsArray());
        // …and the app's own route over it: a fresh install joins the seed to its roster and starts from
        // it instead of measuring the whole population. The subject is a REAL measured pair — the join is
        // by name key, so only a pair the live roster actually holds can vouch that the artifact joins.
        var sharkry = SharingPopulation.Of(new[]
        {
            new Character(1, "Sharkry", "SR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "SharkrySR01", OutfitKind.Base) }),
        });
        var found = MainWindowViewModel.LoadSharingBase(At("cache.json"),
            Path.Combine(AppContext.BaseDirectory, "data", "sharing_seed.json"),
            seed["CatalogVersion"]!.GetValue<string>(), sharkry);
        Assert.True(found.FromSeed);
        Assert.True(found.Index!.Covers("Sharkry", "SharkrySR01"));
    }

    [Fact]
    public void The_shipped_observation_memo_matches_the_current_measurement_schema()
    {
        // The seed's other half, minted from the same pass. BOTH numbers are pinned: the memo's own shape,
        // and the sharing schema its VALUES were computed under — a bump to either fails here until the
        // pair is re-minted together. A full store, never entry-less: the memo is what spares a fresh
        // install the reads behind any row a game update invalidates.
        var memo = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "asset_hashes_seed.json")))!;
        Assert.Equal(AssetHashMemo.SchemaVersion, memo["SchemaVersion"]!.GetValue<int>());
        Assert.Equal(SharingIndex.SchemaVersion, memo["SharingSchemaVersion"]!.GetValue<int>());
        Assert.NotEmpty(memo["Entries"]!.AsObject());
    }

    // ---- the release pack's guard on the pair ------------------------------------------------------

    /// <summary>The publish layout the pack reads and the app reads back: the pair under <c>data\</c>.
    /// The seed is written by the real writer at whatever schema it currently produces; the memo is
    /// hand-stated so a schema can be moved one number at a time.</summary>
    private string ShippedFolder(string name, int? memoSchema = null, int? memoSharingSchema = null,
        string? rawSeed = null, string? rawMemo = null)
    {
        string dir = At(name);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        string seed = Path.Combine(dir, "data", "sharing_seed.json");
        if (rawSeed is null)
        {
            Measured("25180").Save(seed);
            // FromMeasurements deliberately writes a non-reusable empty R; a shipped pair comes from a
            // real pass, whose writer uses the same row shape with a nonempty read record.
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(seed))!;
            foreach (var row in json["Outfits"]!.AsArray()) row!["R"] = "0011223344556677";
            File.WriteAllText(seed, json.ToJsonString());
        }
        else File.WriteAllText(seed, rawSeed);
        File.WriteAllText(Path.Combine(dir, "data", "asset_hashes_seed.json"), rawMemo
            ?? $"{{\"SchemaVersion\":{memoSchema ?? 1},"
            + $"\"SharingSchemaVersion\":{memoSharingSchema ?? SharingIndex.SchemaVersion},"
            + "\"Entries\":{\"0011223344556677\":\"89abcdef\"}}");
        return dir;
    }

    private static void RemoveSeedRowField(string dir, string field)
    {
        string path = Path.Combine(dir, "data", "sharing_seed.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Outfits"]!.AsArray()[0]!.AsObject().Remove(field);
        File.WriteAllText(path, json.ToJsonString());
    }

    [Fact]
    public void The_pack_refuses_a_seed_the_app_would_refuse_at_load()
    {
        // The gap this closes: a seed one schema behind is refused SILENTLY at load — the install just
        // measures the whole population, which is indistinguishable from a fresh one — so every gate in
        // the release flow stays green while the release ships a file no install reads.
        string stale = ShippedFolder("stale", rawSeed:
            "{\"SchemaVersion\":6,\"CatalogVersion\":\"26109\",\"Outfits\":[],\"Failed\":[]}");
        var refusal = ShippedMeasurement.Refusal(stale);

        Assert.NotNull(refusal);
        Assert.Contains("sharing_seed.json", refusal!);
        Assert.Contains("SchemaVersion 6", refusal);

        // …and the same folder with a seed this build writes itself packs
        Assert.Null(ShippedMeasurement.Refusal(ShippedFolder("current")));
    }

    [Fact]
    public void The_pack_refuses_a_memo_from_before_a_sharing_schema_bump_or_of_another_shape()
    {
        // Half a current pair is still refused: the memo's values are only as good as the measurement
        // rules that produced them, and a memo whose own shape moved is not readable entry by entry.
        var bumped = ShippedMeasurement.Refusal(
            ShippedFolder("bumped", memoSharingSchema: SharingIndex.SchemaVersion - 1));
        Assert.NotNull(bumped);
        Assert.Contains("asset_hashes_seed.json", bumped!);

        var reshaped = ShippedMeasurement.Refusal(ShippedFolder("reshaped", memoSchema: 99));
        Assert.NotNull(reshaped);
        Assert.Contains("asset_hashes_seed.json", reshaped!);

        // a memo that predates the coupling states no such number at all, and is refused on that
        string old = At("old");
        Directory.CreateDirectory(Path.Combine(old, "data"));
        Measured("25180").Save(Path.Combine(old, "data", "sharing_seed.json"));
        File.WriteAllText(Path.Combine(old, "data", "asset_hashes_seed.json"),
            "{\"SchemaVersion\":1,\"Entries\":{}}");
        Assert.NotNull(ShippedMeasurement.Refusal(old));

        // and a missing half is not a pass either
        string half = At("half");
        Directory.CreateDirectory(Path.Combine(half, "data"));
        Measured("25180").Save(Path.Combine(half, "data", "sharing_seed.json"));
        Assert.NotNull(ShippedMeasurement.Refusal(half));
    }

    [Fact]
    public void The_pack_refuses_a_schema_valid_seed_with_zero_rows()
    {
        string dir = ShippedFolder("zero-rows", rawSeed:
            $"{{\"SchemaVersion\":{SharingIndex.SchemaVersion},\"CatalogVersion\":\"26109\","
            + "\"Outfits\":[],\"Failed\":[]}");

        var refusal = ShippedMeasurement.Refusal(dir);

        Assert.NotNull(refusal);
        Assert.Contains("no outfit measurements", refusal!);
    }

    [Theory]
    [InlineData("R")]
    [InlineData("A")]
    public void The_pack_refuses_a_schema_valid_seed_row_missing_a_writer_field(string field)
    {
        string dir = ShippedFolder("missing-" + field);
        RemoveSeedRowField(dir, field);

        var refusal = ShippedMeasurement.Refusal(dir);

        Assert.NotNull(refusal);
        Assert.Contains(field, refusal!);
    }

    [Fact]
    public void The_pack_refuses_a_schema_valid_empty_asset_hash_memo()
    {
        string dir = ShippedFolder("empty-memo", rawMemo:
            $"{{\"SchemaVersion\":{AssetHashMemo.SchemaVersion},"
            + $"\"SharingSchemaVersion\":{SharingIndex.SchemaVersion},\"Entries\":{{}}}}");

        var refusal = ShippedMeasurement.Refusal(dir);

        Assert.NotNull(refusal);
        Assert.Contains("no measured asset hashes", refusal!);
    }

    [Fact]
    public void The_pack_accepts_a_well_formed_measurement_pair()
    {
        Assert.Null(ShippedMeasurement.Refusal(ShippedFolder("well-formed")));
    }

    /// <summary>The committed pair, through the pack's own guard: a release packed from this tree carries
    /// a measurement every install reads. Beside
    /// <see cref="The_shipped_seed_is_the_current_schema_and_becomes_the_base"/>, this is the tripwire's
    /// pack-side half — a schema bump fails it until the pair is re-minted.</summary>
    [Fact]
    public void The_pack_accepts_the_committed_pair()
    {
        Assert.Null(ShippedMeasurement.Refusal(AppContext.BaseDirectory));
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
        Assert.Equal("", MainWindowViewModel.BackgroundWorkLine(null));

    [Fact]
    public void The_floor_pass_and_the_delta_read_differently()
    {
        Assert.Equal("Checking assets… 3/506",
            MainWindowViewModel.BackgroundWorkLine(new SharingProgress(3, 506, Delta: false)));
        Assert.Equal("Updating assets… 1/4",
            MainWindowViewModel.BackgroundWorkLine(new SharingProgress(1, 4, Delta: true)));
    }

    // ---- the cell's endings -----------------------------------------------------------------------

    [Fact]
    public void A_running_pass_carries_the_cells_line_and_says_what_it_is_for()
    {
        var facet = MainWindowViewModel.BackgroundFacet(
            new SharingProgress(12, 38, Delta: true), sharingFailed: false);
        Assert.Equal(MainWindowViewModel.BackgroundWorkLine(new SharingProgress(12, 38, Delta: true)),
            facet.Text);
        Assert.Equal("Updating assets… 12/38", facet.Text);
        Assert.Equal("", facet.Glyph);
        Assert.Equal(MainWindowViewModel.SharingCellTip, facet.Detail);
    }

    [Fact]
    public void A_failed_pass_ends_on_the_cell_rather_than_in_silence()
    {
        var facet = MainWindowViewModel.BackgroundFacet(null, sharingFailed: true);
        Assert.Equal("⚠", facet.Glyph);
        Assert.Equal("Shared assets not checked", facet.Text);
        Assert.Equal("Edits may also change other outfits that share the same textures or meshes. "
            + "Use Tools · Rescan game files to try again.", facet.Detail);
    }

    // ---- what a width-capped cell's tooltip carries -----------------------------------------------
    //
    // Route: StatusFacet.Tip is what MainWindow.axaml's three capped cells — background-work, notice and
    // launch — bind their ToolTip.Tip to. A capped label ellipsizes from the end, so the tooltip is where
    // the whole label has to survive.

    [Fact]
    public void A_capped_cells_tooltip_leads_with_the_whole_label()
    {
        // Deliberately not app sentences: the rule under test is the composition, not any one string.
        Assert.Equal("Label\nDetail sentence.", StatusFacet.Warn("Label", "Detail sentence.").Tip);
    }

    [Fact]
    public void A_facet_with_only_one_half_tips_with_that_half()
    {
        Assert.Equal("Label", StatusFacet.Loading("Label").Tip);
        Assert.Equal("Detail sentence.", new StatusFacet("", StatusFacet.Ok, "", "Detail sentence.").Tip);
        Assert.Equal("", StatusFacet.None.Tip);
    }

    /// <summary>The counts are the only live thing on the background-work line, and the 180px cap eats
    /// exactly them. They survive in the tooltip, above the sentence saying what the pass is for.</summary>
    [Fact]
    public void The_running_lines_counts_survive_in_the_tooltip()
    {
        var progress = new SharingProgress(12, 38, Delta: true);
        var facet = MainWindowViewModel.BackgroundFacet(progress, sharingFailed: false);
        Assert.Equal(MainWindowViewModel.BackgroundWorkLine(progress) + "\n"
            + MainWindowViewModel.SharingCellTip, facet.Tip);
        Assert.Contains("12/38", facet.Tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_passs_tooltip_carries_its_label_and_its_remedy()
    {
        var facet = MainWindowViewModel.BackgroundFacet(null, sharingFailed: true);
        Assert.Equal(MainWindowViewModel.SharingUnmeasured + "\n"
            + MainWindowViewModel.SharingUnmeasuredDetail, facet.Tip);
    }

    [Fact]
    public void A_pass_that_ends_with_nothing_wrong_leaves_the_cell_blank()
    {
        Assert.Equal(StatusFacet.None.Text,
            MainWindowViewModel.BackgroundFacet(null, sharingFailed: false).Text);
        // and a new pass over the failure's ground outranks it: work in flight is the newer answer
        Assert.Equal("Checking assets… 0/9", MainWindowViewModel.BackgroundFacet(
            new SharingProgress(0, 9, Delta: false), sharingFailed: true).Text);
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

    /// <summary>The pass publishes a PAIR — this install's index and the observation memo beside it — and
    /// one read of the token decides both. Read twice, a cancellation landing between the two writes
    /// publishes one file and not the other, into a folder the rescan that cancelled the pass may have just
    /// swept. The decision is a value, so the halves cannot disagree.</summary>
    [Fact]
    public void The_two_files_a_pass_publishes_are_decided_together()
    {
        var seed = Measured("25180");
        var adopted = new MainWindowViewModel.SharingBase(seed, FromSeed: true);   // the always-write case
        using var cts = new CancellationTokenSource();

        var live = MainWindowViewModel.SharingPublishes(seed, adopted, cts.Token);
        Assert.True(live.Cache);
        Assert.True(live.Memo);

        cts.Cancel();
        var cancelled = MainWindowViewModel.SharingPublishes(seed, adopted, cts.Token);
        Assert.False(cancelled.Cache);
        Assert.False(cancelled.Memo);
        // and the decision, once taken, is a value: cancelling afterwards cannot half-apply it
        Assert.True(live.Cache && live.Memo);
    }

    [Fact]
    public void A_pass_that_wrote_no_new_rows_still_publishes_its_memo()
    {
        // The two are not the same question. The index is not rewritten when its rows are what the file
        // already says — but the memo may have learned bundle content the index's unchanged rows never
        // needed, and withholding it would make the next pass re-read exactly that.
        var cached = Measured("25180");
        var publish = MainWindowViewModel.SharingPublishes(
            cached, new MainWindowViewModel.SharingBase(cached, FromSeed: false), default);

        Assert.False(publish.Cache);
        Assert.True(publish.Memo);
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
        Assert.Equal("No match for 'zzz'.", MainWindowViewModel.NoMatchLine("zzz", shown: 0));
        Assert.Equal("No match for 'zzz'.", MainWindowViewModel.NoMatchLine("  zzz  ", shown: 0));
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
