using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="SharingIndex"/> query semantics, witness eligibility, and cache persistence, plus the
/// measurement pass's per-outfit staged commit over a synthetic corpus. The full-roster crawl itself is
/// what needs a live install; every decision it makes is testable here.
/// </summary>
public class SharingIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-si-" + Guid.NewGuid().ToString("N"));

    public SharingIndexTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly SharingIndex.Wearer A = new("Vesna", "Vesna", "VesnaSSR01", "Silver Line");
    private static readonly SharingIndex.Wearer B = new("Karst", null, "KarstDorm", null);

    /// <summary>The roster the persisted rows re-join to: the same two subjects <see cref="A"/> and
    /// <see cref="B"/> name, with their display names where the wearers carry them.</summary>
    private static SharingPopulation TwoWearerRoster() => SharingPopulation.Of(new[]
    {
        new Character(1, "Vesna", "SSR", 10, 1099, new List<Remold.Core.Model.Outfit>
            { new(10, "VesnaSSR01", OutfitKind.Base) { DisplayName = "Silver Line" } }) { DisplayName = "Vesna" },
        new Character(2, "Karst", "SSR", 20, 2099, new List<Remold.Core.Model.Outfit>
            { new(20, "KarstDorm", OutfitKind.Base) }),
    });

    private static SharingIndex TwoWearers() => SharingIndex.FromMeasurements("25180",
        new[] { A, B },
        new Dictionary<string, int[]> { ["11111111"] = new[] { 0, 1 }, ["22222222"] = new[] { 0 } },
        new Dictionary<string, int[]> { ["aaaaaaaa"] = new[] { 0, 1 }, ["bbbbbbbb"] = new[] { 1 } },
        new Dictionary<int, string[]> { [0] = new[] { "cccccccc" } });

    // ---- queries ----------------------------------------------------------------------------------

    [Fact]
    public void Other_wearers_exclude_the_asking_outfit()
    {
        var idx = TwoWearers();
        var others = idx.TexOtherWearers("11111111", "Vesna", "VesnaSSR01");
        Assert.Single(others);
        Assert.Equal("Karst", others[0].Character);
        Assert.Empty(idx.TexOtherWearers("22222222", "Vesna", "VesnaSSR01"));
        Assert.Empty(idx.TexOtherWearers("99999999", "Vesna", "VesnaSSR01"));   // unknown hash = unworn
    }

    [Fact]
    public void Character_label_falls_back_to_the_internal_name()
    {
        var idx = TwoWearers();
        var others = idx.MeshOtherWearers("aaaaaaaa", "Vesna", "VesnaSSR01");
        Assert.Equal("Karst", Assert.Single(others).CharacterLabel);
        Assert.Equal("Vesna", idx.MeshOtherWearers("aaaaaaaa", "Karst", "KarstDorm")[0].CharacterLabel);
    }

    [Fact]
    public void Coverage_is_per_outfit_and_case_insensitive()
    {
        var idx = TwoWearers();
        Assert.True(idx.Covers("vesna", "VESNASSR01"));
        Assert.False(idx.Covers("Vesna", "VesnaSSR02"));
    }

    [Fact]
    public void Witnesses_answer_for_the_owning_outfit_only()
    {
        var idx = TwoWearers();
        Assert.Equal(new[] { "cccccccc" }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        Assert.Empty(idx.WitnessIbs("Karst", "KarstDorm"));
        Assert.Empty(idx.WitnessIbs("Nobody", "NobodySSR01"));
    }

    // ---- witness eligibility ----------------------------------------------------------------------

    [Theory]
    [InlineData("body", true)]
    [InlineData("cloth2", true)]
    [InlineData("c_vesna01_body_lod0", true)]
    [InlineData("P1_body", false)]                     // modular: any combination can co-draw
    [InlineData("c_vesna01_P2_cloth_lod0", false)]
    [InlineData("body_Dorm", false)]                   // context-locked: draws in one scene class
    [InlineData("cloth_fight", false)]
    [InlineData("c_vesna01_body_lod0_Dorm", false)]
    [InlineData("c_vesna01_cloth_lod0_Fight", false)]
    public void Witness_eligibility_rejects_modular_and_context_locked_names(string name, bool eligible) =>
        Assert.Equal(eligible, SharingIndex.EligibleWitnessName(name));

    // ---- persistence ------------------------------------------------------------------------------

    [Fact]
    public void Round_trips_through_the_cache_file()
    {
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_25180.json");
        idx.Save(path);
        var back = SharingIndex.TryLoad(path, TwoWearerRoster());
        Assert.NotNull(back);
        Assert.Equal("25180", back!.CatalogVersion);
        Assert.Single(back.TexOtherWearers("11111111", "Vesna", "VesnaSSR01"));
        Assert.Equal(new[] { "cccccccc" }, back.WitnessIbs("Vesna", "VesnaSSR01"));
        // the display names came back from the ROSTER, not the file
        Assert.Equal("Silver Line", back.TexOtherWearers("11111111", "Karst", "KarstDorm")[0].StemDisplay);
    }

    [Fact]
    public void The_persisted_file_holds_no_roster_name_in_the_clear()
    {
        // The invariant that makes one machine's measurement shippable to every other install.
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_25180.json");
        idx.Save(path);
        string text = File.ReadAllText(path);
        foreach (var name in new[] { "Vesna", "VesnaSSR01", "Silver Line", "Karst", "KarstDorm" })
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unreadable_or_foreign_schema_files_load_as_null()
    {
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_25180.json");
        idx.Save(path);
        Assert.Null(SharingIndex.TryLoad(Path.Combine(_root, "absent.json"), TwoWearerRoster()));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["SchemaVersion"] = 3;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Null(SharingIndex.TryLoad(path, TwoWearerRoster()));
        File.WriteAllText(path, "{not json");
        Assert.Null(SharingIndex.TryLoad(path, TwoWearerRoster()));
    }

    [Fact]
    public void A_row_the_roster_no_longer_names_is_dropped_at_the_join()
    {
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_25180.json");
        idx.Save(path);
        var narrowed = SharingPopulation.Of(new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099, new List<Remold.Core.Model.Outfit>
                { new(10, "VesnaSSR01", OutfitKind.Base) }),
        });
        var back = SharingIndex.TryLoad(path, narrowed);
        Assert.NotNull(back);
        Assert.True(back!.Covers("Vesna", "VesnaSSR01"));
        Assert.False(back.Covers("Karst", "KarstDorm"));
        // and the departed outfit is nobody's co-wearer any more
        Assert.Empty(back.TexOtherWearers("11111111", "Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void The_load_carries_the_catalog_version_the_file_was_measured_under()
    {
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_25180.json");
        idx.Save(path);
        // A file from an older catalog still loads: it is the base a delta pass repairs, and only the
        // caller knows whether that version is the running one.
        Assert.Equal("25180", SharingIndex.TryLoad(path, TwoWearerRoster())!.CatalogVersion);
    }

    [Fact]
    public void Failed_outfits_round_trip_and_stay_uncovered()
    {
        var idx = SharingIndex.FromMeasurements("25180", new[] { A },
            new Dictionary<string, int[]>(), new Dictionary<string, int[]>(),
            new Dictionary<int, string[]>(), failedOutfits: new[] { "karst|karstdorm" });
        string path = Path.Combine(_root, "sharing_failed.json");
        idx.Save(path);
        var back = SharingIndex.TryLoad(path, TwoWearerRoster());
        Assert.NotNull(back);
        Assert.Equal(new[] { "karst|karstdorm" }, back!.FailedOutfits);
        Assert.False(back.Covers("Karst", "KarstDorm"));
        Assert.True(back.Covers("Vesna", "VesnaSSR01"));
    }

    // ---- the measurement pass itself --------------------------------------------------------------

    /// <summary>An outfit's prefab, its material bundle's texture, and its mesh bundle, wired through the
    /// catalog exactly as the game addresses them. <paramref name="meshBundle"/> names the logical bundle
    /// the mesh address resolves to: pointing it at one no crawl produced is how a real outfit fails to
    /// measure (a mid-update install, a bundle the deobfuscation can't open).</summary>
    private void Outfit(string abw, char fill, string stem, string meshBundle, bool buildMesh,
        List<(string Address, string OwnerBundle)> rows, List<(string Address, string[] Deps)> deps)
    {
        string slot = $"c_{stem}_slg_body_lod0";
        WorkbenchPrefab.Build(Path.Combine(abw, new string(fill, 32) + ".bundle"),
            bundleName: $"prefab{stem}.bundle", rootName: stem,
            slots: new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (1, 21L) }) },
            recipe: new[] { (slot, $"Assets/X/{slot}.mesh") },
            externalCabs: new[] { "CAB-mat" },
            bones: new[] { ("Bip001", -1), ("Bip001 Pelvis", 0) });
        if (buildMesh)
            SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string(fill, 31) + "m.bundle"), slot,
                new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 }, new uint[] { 7 },
                bundleName: meshBundle);

        string prefabAddress = GameVfs.PrefabAddress("Character/Player", stem);
        rows.Add((prefabAddress, $"prefab{stem}.bundle"));
        rows.Add(($"Assets/X/{slot}.mesh", meshBundle));
        deps.Add((prefabAddress, new[] { $"prefab{stem}.bundle", "mat.bundle" }));
    }

    [Fact]
    public void An_outfit_that_cannot_be_read_whole_stays_uncovered_while_the_clean_one_commits()
    {
        // The staged commit: measuring is all-or-nothing per outfit, because a half-read one would
        // report its neighbours' shared assets as private. Both outfits here wear one stock texture.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");

        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        Outfit(abw, '1', "VesnaSSR01", "vmesh.bundle", buildMesh: true, rows, deps);
        Outfit(abw, '2', "KarstSSR01", "ghost.bundle", buildMesh: false, rows, deps);
        var roster = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
            new Character(2, "Karst", "SSR", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "KarstSSR01", OutfitKind.Base) }),
        };

        var idx = SharingIndex.Build(SharingPopulation.Of(roster), CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        // its private mesh committed with it, and reads as this outfit's alone
        string vib = Assert.Single(idx.WitnessIbs("Vesna", "VesnaSSR01"));
        Assert.Empty(idx.MeshOtherWearers(vib, "Vesna", "VesnaSSR01"));
        Assert.False(idx.Covers("Karst", "KarstSSR01"));
        Assert.Equal(1, idx.MeasuredOutfitCount);
        Assert.Equal(new[] { "karst|karstssr01" }, idx.FailedOutfits);
        Assert.Contains(idx.Problems, p => p.StartsWith("KarstSSR01:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_skeleton_only_degradation_does_not_cost_an_outfit_its_coverage()
    {
        // A bone carrying the container root's name costs the SKELETON — display and scene-rig
        // niceties — while the parts read whole. This measurement reads parts and textures only, so
        // the outfit commits: a stationary summon with a doubled Transform name stays covered.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");

        string slot = "c_TestySSR01_slg_body_lod0";
        WorkbenchPrefab.Build(Path.Combine(abw, new string('3', 32) + ".bundle"),
            bundleName: "prefabTestySSR01.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (1, 21L) }) },
            recipe: new[] { (slot, $"Assets/X/{slot}.mesh") },
            externalCabs: new[] { "CAB-mat" },
            // the second bone carries the container root's name — the rig read refuses, parts stand
            bones: new[] { ("Bip001", -1), ("TestySSR01", 0) });
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('3', 31) + "m.bundle"), slot,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 }, new uint[] { 7 },
            bundleName: "tmesh.bundle");

        string prefabAddress = GameVfs.PrefabAddress("Character/Player", "TestySSR01");
        var rows = new List<(string, string)>
        {
            (prefabAddress, "prefabTestySSR01.bundle"),
            ($"Assets/X/{slot}.mesh", "tmesh.bundle"),
        };
        var deps = new List<(string, string[])>
        {
            (prefabAddress, new[] { "prefabTestySSR01.bundle", "mat.bundle" }),
        };
        var roster = new[]
        {
            new Character(1, "Testy", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "TestySSR01", OutfitKind.Base) }),
        };

        var idx = SharingIndex.Build(SharingPopulation.Of(roster), CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        Assert.True(idx.Covers("Testy", "TestySSR01"));
        Assert.Empty(idx.FailedOutfits);
        Assert.Equal(1, idx.MeasuredOutfitCount);
    }

    // ---- delta repair -----------------------------------------------------------------------------

    /// <summary>Two clean outfits over one shared texture, plus the catalog rows behind them.</summary>
    private (SharingPopulation Population, List<(string, string)> Rows, List<(string, string[])> Deps)
        TwoCleanOutfits(string abw)
    {
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        Outfit(abw, '1', "VesnaSSR01", "vmesh.bundle", buildMesh: true, rows, deps);
        Outfit(abw, '2', "KarstSSR01", "kmesh.bundle", buildMesh: true, rows, deps);
        var roster = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
            new Character(2, "Karst", "SSR", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "KarstSSR01", OutfitKind.Base) }),
        };
        return (SharingPopulation.Of(roster), rows, deps);
    }

    [Fact]
    public void A_delta_reads_the_moved_outfit_and_keeps_the_rest()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var first = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);

        // The catalog moves one outfit's dependency closure; the other's rows are untouched.
        string moved = GameVfs.PrefabAddress("Character/Player", "KarstSSR01");
        var deps2 = deps.Select(d => d.Item1 == moved
            ? (d.Item1, new[] { "prefabKarstSSR01.bundle", "mat.bundle", "extra.bundle" }) : d).ToList();

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps2),
            FixtureCrawl.DeobfuscateOver(abw), "25200", first,
            new InlineProgress(seen));

        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Equal("25200", second.CatalogVersion);
        // exactly one outfit had to be read, and the pass knows it is a delta
        Assert.All(seen, p => Assert.True(p.Delta));
        Assert.All(seen, p => Assert.Equal(1, p.Total));
    }

    [Fact]
    public void A_kept_outfit_is_never_read_again()
    {
        // The proof that the reuse is a reuse: the unchanged outfit's bundles are gone from disk, and it
        // still comes through the delta covered.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var first = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        File.Delete(Path.Combine(abw, new string('1', 32) + ".bundle"));
        File.Delete(Path.Combine(abw, new string('1', 31) + "m.bundle"));

        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first);

        Assert.True(second.Covers("Vesna", "VesnaSSR01"));
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void A_pass_over_unmoved_data_reads_nothing_and_reports_nothing()
    {
        // What every launch now runs. The plan pass is catalog-only, so an install whose data has not moved
        // pays a scan and no reads — and the cell stays blank, because it reports re-measures, not sweeps.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps);
        var first = SharingIndex.Build(population, catalog, FixtureCrawl.DeobfuscateOver(abw), "25180");

        // a deobfuscate that records every ask: nothing may reach it
        var asked = new List<string>();
        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog,
            id => { lock (asked) asked.Add(id); return null; }, "25180", first,
            new InlineProgress(seen));

        Assert.Empty(asked);
        Assert.Empty(seen);
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
        // and the result is the base row for row, so nothing has to be rewritten
        Assert.True(second.SameRowsAs(first));
    }

    [Fact]
    public void An_outfit_whose_mesh_owner_bundle_re_minted_is_measured_again()
    {
        // The mesh-owner blind spot. A part's mesh resolves catalog-WIDE, so its owner bundle can sit
        // outside the subject's dependency closure — the fingerprint cannot see it move. Here only that
        // bundle's manifest join changes: the closure, and so the fingerprint, is untouched.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        CatalogIndex With(string vmeshInternalId) => CatalogIndex.ForTest(rows, deps, new[]
        {
            ("vmesh.bundle", vmeshInternalId),
            ("kmesh.bundle", "kmesh-1"),
        });
        var before = With("vmesh-1");
        var first = SharingIndex.Build(population, before, FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);
        // the fingerprint really is blind to it — that is what the read record is for
        Assert.Equal(SubjectFingerprint.For(before, population.Roster[0].Outfits[0]),
            SubjectFingerprint.For(With("vmesh-2"), population.Roster[0].Outfits[0]));

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, With("vmesh-2"), FixtureCrawl.DeobfuscateOver(abw),
            "25180", first, new InlineProgress(seen));

        // exactly the outfit that read that bundle, and only it
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
    }

    [Fact]
    public void A_row_with_no_recorded_reads_is_kept_on_its_fingerprint_alone()
    {
        // The seed-bootstrap allowance: the shipped seed predates the read record, and its rows must not
        // all re-measure on the first launch that carries this code.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps, new[] { ("vmesh.bundle", "vmesh-1") });
        var measured = SharingIndex.Build(population, catalog, FixtureCrawl.DeobfuscateOver(abw), "25180");
        string path = Path.Combine(_root, "seedshape.json");
        measured.Save(path);

        // strip the read record, leaving exactly the shape the shipped seed has
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var row in json["Outfits"]!.AsArray()) row!.AsObject().Remove("R");
        File.WriteAllText(path, json.ToJsonString());
        var bootstrap = SharingIndex.TryLoad(path, population)!;

        // a bundle it read has re-minted, and it is kept anyway — the allowance, stated
        var seen = new List<SharingProgress>();
        var next = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps,
                new[] { ("vmesh.bundle", "vmesh-2") }), FixtureCrawl.DeobfuscateOver(abw), "25180",
            bootstrap, new InlineProgress(seen));
        Assert.Empty(seen);
        Assert.Equal(2, next.MeasuredOutfitCount);
    }

    [Fact]
    public void An_outfit_the_previous_pass_never_measured_is_read()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var onlyOne = SharingPopulation.Of(new[] { population.Roster[0] });
        var first = SharingIndex.Build(onlyOne, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first,
            new InlineProgress(seen));

        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.All(seen, p => Assert.Equal(1, p.Total));      // only the newcomer
    }

    [Fact]
    public void A_previous_failure_is_retried_rather_than_kept()
    {
        // A failure is a fact about the run — the game holding its bundles open — not about the catalog,
        // so a matching fingerprint is no reason to keep it.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        Outfit(abw, '1', "VesnaSSR01", "vmesh.bundle", buildMesh: false, rows, deps);
        var population = SharingPopulation.Of(new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
        });
        var catalog = CatalogIndex.ForTest(rows, deps);
        var first = SharingIndex.Build(population, catalog, FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Single(first.FailedOutfits);

        // the mesh bundle arrives, nothing else moves
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('1', 31) + "m.bundle"),
            "c_VesnaSSR01_slg_body_lod0", new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
            new[] { 0, 1, 2 }, new uint[] { 7 }, bundleName: "vmesh.bundle");

        var second = SharingIndex.Build(population, catalog, FixtureCrawl.DeobfuscateOver(abw), "25180", first);
        Assert.True(second.Covers("Vesna", "VesnaSSR01"));
        Assert.Empty(second.FailedOutfits);
    }

    // ---- the duplicate-door filter ----------------------------------------------------------------

    /// <summary>A playable outfit and two enemy doors: one with the playable's exact mesh set, one that
    /// carries a mesh of its own.</summary>
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
        new Dictionary<int, string[]> { [0] = new[] { "bbbbbbbb" } },
        enemyCharacters: new[] { "Door" });

    [Fact]
    public void An_enemy_door_with_a_playable_outfits_exact_mesh_set_is_filtered()
    {
        var idx = Doors();
        Assert.True(idx.IsDuplicateDoor("Door", "DoorTwin"));
        Assert.False(idx.Covers("Door", "DoorTwin"));
        Assert.Equal(2, idx.MeasuredOutfitCount);
        // and it is no wearer of anything it carried
        Assert.Empty(idx.MeshOtherWearers("bbbbbbbb", "Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void A_door_carrying_any_mesh_of_its_own_stays()
    {
        var idx = Doors();
        Assert.False(idx.IsDuplicateDoor("Door", "DoorOwn"));
        Assert.True(idx.Covers("Door", "DoorOwn"));
        // and it is still a co-wearer of what it shares
        Assert.Single(idx.MeshOtherWearers("aaaaaaaa", "Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void A_witness_returns_when_its_only_other_wearer_was_a_filtered_twin()
    {
        // Unfiltered, the twin's copy of the mesh makes it public and the subject has no witness.
        var unfiltered = SharingIndex.FromMeasurements("25180",
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
            new Dictionary<int, string[]> { [0] = new[] { "bbbbbbbb" } });
        Assert.Empty(unfiltered.WitnessIbs("Vesna", "VesnaSSR01"));

        Assert.Equal(new[] { "bbbbbbbb" }, Doors().WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void A_witness_stolen_only_by_a_filtered_door_comes_back_through_the_file()
    {
        // The derivation that a LOADED index has to run for itself. The file states observations, never
        // relations: the witness candidate and the door's copy of the same mesh are both in it, and privacy
        // is decided at load — so the enemy population the caller supplies is what returns the witness.
        // The in-memory tests state candidates by hand and cannot see the persisted-candidate route.
        var idx = SharingIndex.FromMeasurements("25180",
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
            new Dictionary<int, string[]> { [0] = new[] { "bbbbbbbb" } });
        string path = Path.Combine(_root, "sharing_doors.json");
        idx.Save(path);

        var playable = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099, new List<Remold.Core.Model.Outfit>
                { new(10, "VesnaSSR01", OutfitKind.Base) }),
        };
        var enemies = new[]
        {
            new Character(2, "Door", "", 20, 2099, new List<Remold.Core.Model.Outfit>
                { new(20, "DoorTwin", OutfitKind.Base), new(21, "DoorOwn", OutfitKind.Base) }),
        };

        // with no enemy side, the twin's copy makes the mesh public and the subject has no witness
        Assert.Empty(SharingIndex.TryLoad(path, SharingPopulation.Of(playable.Concat(enemies).ToList()))!
            .WitnessIbs("Vesna", "VesnaSSR01"));

        var back = SharingIndex.TryLoad(path, SharingPopulation.Of(playable, enemies))!;
        Assert.True(back.IsDuplicateDoor("Door", "DoorTwin"));
        Assert.Equal(new[] { "bbbbbbbb" }, back.WitnessIbs("Vesna", "VesnaSSR01"));
        // the door that carries a mesh of its own is untouched by the filter
        Assert.True(back.Covers("Door", "DoorOwn"));
    }

    [Fact]
    public void A_mesh_less_row_is_never_a_door()
    {
        // Two rows sharing nothing but their emptiness are not the same content.
        var idx = SharingIndex.FromMeasurements("25180",
            new[]
            {
                new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null),
                new SharingIndex.Wearer("Door", null, "DoorEmpty", null),
            },
            new Dictionary<string, int[]>(), new Dictionary<string, int[]>(),
            new Dictionary<int, string[]>(), enemyCharacters: new[] { "Door" });
        Assert.False(idx.IsDuplicateDoor("Door", "DoorEmpty"));
        Assert.True(idx.Covers("Door", "DoorEmpty"));
    }

    [Fact]
    public void A_re_measured_twin_is_still_filtered()
    {
        // The whole population is measured for real, then the door's fingerprint moves and it is read
        // again — its mesh set is still the playable outfit's, so it is still a duplicate door.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        // identical geometry under two stems: the ib is the topology's, so both wear the same mesh
        Outfit(abw, '1', "VesnaSSR01", "vmesh.bundle", buildMesh: true, rows, deps);
        Outfit(abw, '2', "ElidDoor", "dmesh.bundle", buildMesh: true, rows, deps);
        var playable = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
        };
        var enemies = new[]
        {
            new Character(2, "ElidDoor", "", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "ElidDoor", OutfitKind.Base) }),
        };
        var population = SharingPopulation.Of(playable, enemies);

        var first = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.True(first.IsDuplicateDoor("ElidDoor", "ElidDoor"));

        string moved = GameVfs.PrefabAddress("Character/Player", "ElidDoor");
        var deps2 = deps.Select(d => d.Item1 == moved
            ? (d.Item1, new[] { "prefabElidDoor.bundle", "mat.bundle", "extra.bundle" }) : d).ToList();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps2),
            FixtureCrawl.DeobfuscateOver(abw), "25200", first);
        Assert.True(second.IsDuplicateDoor("ElidDoor", "ElidDoor"));
    }

    /// <summary>Reports on the caller's own thread. System.Progress posts to the thread pool, so a test
    /// asserting right after Build returns would race its own reports.</summary>
    private sealed class InlineProgress : System.IProgress<SharingProgress>
    {
        private readonly System.Collections.Generic.List<SharingProgress> _into;
        public InlineProgress(System.Collections.Generic.List<SharingProgress> into) => _into = into;
        public void Report(SharingProgress value) { lock (_into) _into.Add(value); }
    }
}
