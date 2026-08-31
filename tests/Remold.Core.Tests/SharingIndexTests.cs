using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
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

    /// <summary>The content-hash lookup for fixtures that are pinning something other than content
    /// identity: no internalId resolves to a hash, so every bundle records the absent marker — the same
    /// value on both sides of a currency check, which is what keeps those fixtures reusable.</summary>
    private static readonly Func<string, string?> NoContentHashes = _ => null;

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
    public void A_persisted_schema_seven_file_loads_as_null()
    {
        string path = Path.Combine(_root, "sharing_schema7.json");
        TwoWearers().Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["SchemaVersion"] = 7;
        File.WriteAllText(path, json.ToJsonString());

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

    /// <summary>The node names <see cref="TwoPartOutfit"/> builds, so a case can name whichever one the
    /// rule under test needs — the body part's representative slot, the body's sibling TIER, or the
    /// hair.</summary>
    private readonly record struct TwoPartNodes(string Body, string BodyTier, string Hair);

    /// <summary>One outfit with two parts, the hair's renderer written with the given
    /// <c>m_CastShadows</c> and the prefab carrying whatever dorm visibility lists the case needs.
    /// Answers the measured index and the two parts' ib hashes, which differ by index buffer.
    ///
    /// <para>A BODY sibling tier — a second renderer under one part, the only shape that can tell
    /// the per-part gates from the per-tier ones — ships when <paramref name="bodyTier"/> is set or
    /// <paramref name="bodyTierCastShadows"/> names its <c>m_CastShadows</c> (which is what
    /// <see cref="Export.RecipeTierSlot.CastsShadows"/> exists to carry; without either the outfit is the
    /// two-slot shape the older cases measure and <c>BodyTierIb</c> is null).</para></summary>
    private static SharingPopulation TwoPartPopulation() => SharingPopulation.Of(new[]
    {
        new Character(1, "Vesna", "SSR", 10, 1099,
            new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
    });

    private (SharingIndex Index, string BodyIb, string HairIb, string? BodyTierIb) TwoPartOutfit(
        string abw, int hairCastShadows, int? bodyTierCastShadows = null,
        Func<TwoPartNodes, WorkbenchPrefab.VisibilityLists>? visibility = null, bool bodyTier = false,
        string bodyTierLod = "lod1", SharingIndex? previous = null,
        IProgress<SharingProgress>? progress = null)
    {
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");

        const string stem = "VesnaSSR01";
        string body = $"c_{stem}_slg_body_lod0", hair = $"c_{stem}_slg_hair_lod0";
        string bodyTierName = $"c_{stem}_slg_body_{bodyTierLod}";
        bool hasTier = bodyTier || bodyTierCastShadows is not null;
        var slots = new List<WorkbenchPrefab.SlotSpec>
        {
            new(body, new[] { (1, 21L) }, CastShadows: 2),
            new(hair, new[] { (1, 21L) }, CastShadows: hairCastShadows),
        };
        var recipe = new List<(string, string)>
            { (body, $"Assets/X/{body}.mesh"), (hair, $"Assets/X/{hair}.mesh") };
        if (hasTier)
        {
            // the body's OWN second renderer: same token, so the builder folds it in as a sibling tier
            // rather than a part of its own, and its m_CastShadows and marker are read off THIS slot
            slots.Add(new WorkbenchPrefab.SlotSpec(bodyTierName, new[] { (1, 21L) },
                CastShadows: bodyTierCastShadows ?? 2));
            recipe.Add((bodyTierName, $"Assets/X/{bodyTierName}.mesh"));
        }
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: $"prefab{stem}.bundle", rootName: stem,
            slots: slots.ToArray(),
            recipe: recipe.ToArray(),
            externalCabs: new[] { "CAB-mat" },
            bones: new[] { ("Bip001", -1), ("Bip001 Pelvis", 0) },
            visibility: visibility?.Invoke(new TwoPartNodes(body, bodyTierName, hair)));
        // different winding = a different index buffer = a different ib hash, so the two parts are told apart
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('2', 32) + ".bundle"), body,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 }, new uint[] { 7 },
            bundleName: "vbody.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('3', 32) + ".bundle"), hair,
            new[] { 0f, 0f, 0f, 2f, 0f, 0f, 0f, 2f, 0f }, new[] { 0, 2, 1 }, new uint[] { 7 },
            bundleName: "vhair.bundle");
        if (hasTier)
            // an index buffer of its own, so the tier's ib is distinguishable from every other in the outfit
            SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('4', 32) + ".bundle"),
                bodyTierName, new[] { 0f, 0f, 0f, 3f, 0f, 0f, 0f, 3f, 0f, 3f, 3f, 0f },
                new[] { 0, 1, 2, 2, 1, 3 }, new uint[] { 7 }, bundleName: "vbodytier.bundle");

        string prefabAddress = GameVfs.PrefabAddress("Character/Player", stem);
        var rows = new List<(string, string)>
        {
            (prefabAddress, $"prefab{stem}.bundle"),
            ($"Assets/X/{body}.mesh", "vbody.bundle"),
            ($"Assets/X/{hair}.mesh", "vhair.bundle"),
        };
        if (hasTier) rows.Add(($"Assets/X/{bodyTierName}.mesh", "vbodytier.bundle"));
        var deps = new List<(string, string[])>
            { (prefabAddress, new[] { $"prefab{stem}.bundle", "mat.bundle" }) };
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var idx = SharingIndex.Build(TwoPartPopulation(), CatalogIndex.ForTest(rows, deps), NoContentHashes,
            deobfuscate, "25180", previous, progress);
        var reader = new BundleReader();
        return (idx,
            BufferHash.Compute(deobfuscate("vbody.bundle")!, body, 0, reader).Ib.ToString("x8"),
            BufferHash.Compute(deobfuscate("vhair.bundle")!, hair, 0, reader).Ib.ToString("x8"),
            hasTier
                ? BufferHash.Compute(deobfuscate("vbodytier.bundle")!, bodyTierName, 0, reader).Ib.ToString("x8")
                : null);
    }

    [Fact]
    public void A_shadow_off_part_is_measured_but_never_witnesses()
    {
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, _) = TwoPartOutfit(g.At("AssetBundles_Windows"), hairCastShadows: 0);

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        // the hair draws only while it is in frame, so it vouches for nothing
        Assert.Equal(new[] { bodyIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        // …but it is still measured, so it stays editable and its reach is still reported. The asker is
        // uncovered, so nothing is filtered out as its own.
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(hairIb, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void The_same_part_witnesses_once_its_renderer_casts()
    {
        // The control: only m_CastShadows differs, so the flag alone decided the exclusion above.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, _) = TwoPartOutfit(g.At("AssetBundles_Windows"), hairCastShadows: 1);
        Assert.Equal(new[] { bodyIb, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Theory]
    [InlineData("coat")]
    [InlineData("dorm")]
    [InlineData("lobby")]
    public void A_part_the_game_can_withhold_is_measured_but_never_witnesses(string list)
    {
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, _) = TwoPartOutfit(g.At("AssetBundles_Windows"), hairCastShadows: 1,
            visibility: n => list switch
            {
                "coat" => new WorkbenchPrefab.VisibilityLists(ControlVisibleNodes: new[] { n.Hair }),
                "dorm" => new WorkbenchPrefab.VisibilityLists(DormHideNodes: new[] { n.Hair }),
                _ => new WorkbenchPrefab.VisibilityLists(LobbyHideNodes: new[] { n.Hair }),
            });

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        // the game's own logic decides whether the hair draws, so sighting it settles nothing
        Assert.Equal(new[] { bodyIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        // …but it is still measured, so it stays editable and its reach is still reported
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(hairIb, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void A_withheld_sibling_tier_drops_out_while_its_unmarked_representative_still_witnesses()
    {
        // PER TIER, not per part: no list names the body's representative slot, so the PART is clean and
        // still witnesses by the draw that survives — while the lod1 tier a hide list DOES name is left
        // off the stand. Pins the per-tier `vis == VisibilityOverride.None` gate in the tier loop, which
        // the part-level gate cannot stand in for here, the part being unmarked.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, visibility: n => new WorkbenchPrefab.VisibilityLists(
                DormHideNodes: new[] { n.BodyTier }), bodyTier: true);

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        Assert.NotNull(tierIb);
        Assert.NotEqual(bodyIb, tierIb);                            // the tier really is its own ib
        Assert.Equal(new[] { bodyIb, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        // still measured, so the tier stays editable and its reach is still reported
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(tierIb!, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void A_withheld_representative_takes_its_whole_part_off_the_stand_including_clean_tiers()
    {
        // The PART-level gate, which the per-tier one cannot stand in for: the hide list names the body's
        // representative slot, and the lod1 tier beside it is named by nothing at all. That clean tier
        // still may not witness — a part whose representative the game can withhold is refused wholesale,
        // the same stricter-than-per-tier reading the shadow rule takes. Pins the `part.Visibility ==
        // VisibilityOverride.None` term of partEligible; without it the clean tier would vouch.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, visibility: n => new WorkbenchPrefab.VisibilityLists(
                DormHideNodes: new[] { n.Body }), bodyTier: true);

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        Assert.NotNull(tierIb);
        Assert.Equal(new[] { hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        // both are still measured, so both stay editable and their reach is still reported
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(tierIb!, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void The_same_sibling_tier_witnesses_when_no_list_names_anything()
    {
        // The control for BOTH tests above: the identical three-slot fixture with no hide list at all, so
        // it is the naming alone that decided each exclusion — and it shows the tier witnessing normally.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, bodyTier: true);

        Assert.Equal(new[] { bodyIb, tierIb!, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void A_lodm0_sibling_with_its_own_ib_is_measured_and_witnesses()
    {
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, bodyTier: true, bodyTierLod: "lodm0");

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        Assert.NotNull(tierIb);
        Assert.NotEqual(bodyIb, tierIb);
        Assert.Equal(new[] { bodyIb, tierIb!, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(tierIb!, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void A_schema_seven_row_without_a_lodm_sibling_is_not_reused_when_the_sibling_arrives()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (old, _, _, _) = TwoPartOutfit(abw, hairCastShadows: 1);
        string path = Path.Combine(_root, "sharing_before_lodm.json");
        old.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["SchemaVersion"] = 7;
        File.WriteAllText(path, json.ToJsonString());

        var previous = SharingIndex.TryLoad(path, TwoPartPopulation());
        Assert.Null(previous);
        var seen = new List<SharingProgress>();
        var (current, bodyIb, hairIb, tierIb) = TwoPartOutfit(abw, hairCastShadows: 1,
            bodyTier: true, bodyTierLod: "lodm0", previous: previous, progress: new InlineProgress(seen));

        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.NotNull(tierIb);
        Assert.Equal(new[] { bodyIb, tierIb!, hairIb }, current.WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void The_same_part_witnesses_when_no_list_names_it()
    {
        // The control: the prefab ships both components with the hair named in NEITHER hide list, and the
        // show list naming it does not count. So it is the hide lists alone that decided the exclusions.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, _) = TwoPartOutfit(g.At("AssetBundles_Windows"), hairCastShadows: 1,
            visibility: n => new WorkbenchPrefab.VisibilityLists(
                DormNodes: new[] { n.Hair }, LobbyShowNodes: new[] { n.Hair }));
        Assert.Equal(new[] { bodyIb, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void A_shadow_off_sibling_tier_drops_out_while_its_casting_lod0_still_witnesses()
    {
        // Per TIER, not per part: the body's lod0 casts and its lod1 does not, so the part still witnesses
        // by the draw that survives being culled while the tier that does not is left off the stand. Pins
        // SubjectModelBuilder populating RecipeTierSlot.CastsShadows from the tier's OWN renderer, and
        // SharingIndex keying witness candidacy on that per-tier flag rather than the part's.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, bodyTierCastShadows: 0);

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));
        Assert.NotNull(tierIb);
        Assert.NotEqual(bodyIb, tierIb);                                // the tier really is its own ib
        Assert.Equal(new[] { bodyIb, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        Assert.DoesNotContain(tierIb, idx.WitnessIbs("Vesna", "VesnaSSR01"));
        // still measured, so the tier stays editable and its reach is still reported
        Assert.Equal("Vesna",
            Assert.Single(idx.MeshOtherWearers(tierIb!, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void The_same_sibling_tier_witnesses_once_its_own_renderer_casts()
    {
        // The control for the test above: the identical fixture with the tier's m_CastShadows On, so the
        // per-tier flag alone decided the exclusion.
        using var g = new TempGame();
        var (idx, bodyIb, hairIb, tierIb) = TwoPartOutfit(g.At("AssetBundles_Windows"),
            hairCastShadows: 1, bodyTierCastShadows: 1);

        Assert.Equal(new[] { bodyIb, tierIb!, hairIb }, idx.WitnessIbs("Vesna", "VesnaSSR01"));
    }

    [Fact]
    public void The_persisted_schema_is_eight()
    {
        // Witness eligibility is applied at MEASURE time and rows are reused, so a change to the rule has
        // to invalidate every prior cache and the shipped seed. So does a change to what the read record's
        // keys MEAN: schema 5 wrote two incompatible content keys inside one arc, schema 7 dropped the
        // internalId key out of the record entirely, and schema 8 admits lodm tiers and records the bundle
        // each part address resolved to — changes the file's own fingerprint cannot see. Every row also
        // carries both reuse records now, which prior rows did not have to.
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_schema.json");
        idx.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(8, (int)json["SchemaVersion"]!);
        Assert.All(json["Outfits"]!.AsArray(), row => Assert.NotNull(row!["R"]));
        Assert.All(json["Outfits"]!.AsArray(), row => Assert.NotNull(row!["A"]));
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
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");

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
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");

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
    public void Part_address_resolutions_round_trip_and_keep_unmoved_rows_reusable()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps);
        var first = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        string path = Path.Combine(_root, "address-roundtrip.json");
        first.Save(path);

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.All(json["Outfits"]!.AsArray(), row =>
            Assert.False(string.IsNullOrEmpty((string?)row!["A"])));
        string persisted = File.ReadAllText(path);
        Assert.DoesNotContain("Assets/X/", persisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vmesh.bundle", persisted, StringComparison.OrdinalIgnoreCase);

        var loaded = SharingIndex.TryLoad(path, population);
        Assert.NotNull(loaded);
        var asked = new List<string>();
        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, NoContentHashes,
            id => { asked.Add(id); return null; }, "25180", loaded, new InlineProgress(seen));

        Assert.Empty(asked);
        Assert.Empty(seen);
        Assert.True(second.SameRowsAs(first));
    }

    [Fact]
    public void A_part_address_retarget_remeasures_its_row_while_old_bundle_files_stand_still()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var before = CatalogIndex.ForTest(rows, deps);
        var first = SharingIndex.Build(population, before, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        const string stem = "VesnaSSR01";
        string slot = $"c_{stem}_slg_body_lod0";
        string address = $"Assets/X/{slot}.mesh";
        string oldPath = Path.Combine(abw, new string('1', 31) + "m.bundle");
        byte[] oldBytes = File.ReadAllBytes(oldPath);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('n', 32) + ".bundle"), slot,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 2, 1 }, new uint[] { 7 },
            bundleName: "vmesh-next.bundle");
        var movedRows = rows.Select(r => r.Item1 == address
            ? (r.Item1, "vmesh-next.bundle") : r).ToList();
        var after = CatalogIndex.ForTest(movedRows, deps);
        Assert.Equal(SubjectFingerprint.For(before, population.Roster[0].Outfits[0]),
            SubjectFingerprint.For(after, population.Roster[0].Outfits[0]));

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, after, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25200", first, new InlineProgress(seen));

        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.Problems);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldPath));
        var reader = new BundleReader();
        string newIb = BufferHash.Compute(FixtureCrawl.DeobfuscateOver(abw)("vmesh-next.bundle")!,
            slot, 0, reader).Ib.ToString("x8");
        Assert.Contains(second.MeshOtherWearers(newIb, "Nobody", "Nobody"),
            wearer => wearer.Character == "Vesna");
    }

    [Fact]
    public void A_delta_reads_the_moved_outfit_and_keeps_the_rest()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var first = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);

        // The catalog moves one outfit's dependency closure; the other's rows are untouched.
        string moved = GameVfs.PrefabAddress("Character/Player", "KarstSSR01");
        var deps2 = deps.Select(d => d.Item1 == moved
            ? (d.Item1, new[] { "prefabKarstSSR01.bundle", "mat.bundle", "extra.bundle" }) : d).ToList();

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps2),
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25200", first,
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
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");

        File.Delete(Path.Combine(abw, new string('1', 32) + ".bundle"));
        File.Delete(Path.Combine(abw, new string('1', 31) + "m.bundle"));

        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180", first);

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
        var first = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");

        // a deobfuscate that records every ask: nothing may reach it
        var asked = new List<string>();
        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, NoContentHashes,
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
    public void An_outfit_whose_mesh_owner_bundle_only_re_minted_is_never_read_again()
    {
        // A mesh owner's manifest join moving, with its CONTENT standing still, is what a repack does — and
        // it is the case that must cost nothing. Neither half of the gate may fire: the closure is
        // untouched, so the fingerprint stands, and the read record no longer carries the internalId that
        // moved. (What the record still catches — the same bundle rewritten where it stands — is
        // An_outfit_whose_single_file_bundle_was_rewritten_in_place_is_measured_again, below.)
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        CatalogIndex With(string vmeshInternalId) => CatalogIndex.ForTest(rows, deps, new[]
        {
            ("vmesh.bundle", vmeshInternalId),
            ("kmesh.bundle", "kmesh-1"),
        });
        var before = With("vmesh-1");
        var first = SharingIndex.Build(population, before, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);
        // the internalId really did move, and the fingerprint is deliberately blind to it
        Assert.NotEqual("vmesh-1", With("vmesh-2").BundleNameToInternalId["vmesh.bundle"]);
        Assert.Equal(SubjectFingerprint.For(before, population.Roster[0].Outfits[0]),
            SubjectFingerprint.For(With("vmesh-2"), population.Roster[0].Outfits[0]));

        var seen = new List<SharingProgress>();
        var asked = new List<string>();
        var second = SharingIndex.Build(population, With("vmesh-2"), NoContentHashes,
            id => { lock (asked) asked.Add(id); return null; }, "25180", first, new InlineProgress(seen));

        Assert.Empty(seen);
        Assert.Empty(asked);
        Assert.Equal(2, second.MeasuredOutfitCount);
    }

    /// <summary>The 32-hex physical name of a fixture bundle, and — because every entry these fixtures
    /// write is a SINGLE-file bundle — its manifest entry name too. That equality is the live install's
    /// rule for all 7,258 singles, and it is what makes the physical filename useless as a content key: a
    /// single's entry name simply restates it.</summary>
    private static string Single(char fill) => new string(fill, 32);

    /// <summary>A manifest holding one whole-file stub per named single, each entry keyed by
    /// <c>physHash + ".bundle"</c>, with the given content hash (the stub's subHash) per entry.</summary>
    private string WriteSingles(string dir, params (string Phys, byte Content)[] singles)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, GffManifest.ManifestHash + ".bundle");
        FakeGff.Write(path, singles
            .Select(s => (s.Phys + ".bundle", FakeGff.Stub(s.Phys, 0, 0, s.Content)))
            .ToArray());
        return path;
    }

    [Fact]
    public void An_outfit_whose_single_file_bundle_was_rewritten_in_place_is_measured_again()
    {
        // The defect class in the shape it really takes. A single-file bundle's manifest entry name IS its
        // physical filename plus ".bundle", so a patch that rewrites one where it stands moves NEITHER
        // name: not the logical bundle id, not the internalId, and not the physical file the internalId
        // resolves to — the internalId could not move without the file moving with it. The only thing that
        // moves is the stub's content hash, which is what this record keys on.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        string vmesh = Single('a'), kmesh = Single('b'), vprefab = Single('c'),
               kprefab = Single('d'), mat = Single('e');
        var catalog = CatalogIndex.ForTest(rows, deps, new[]
        {
            ("vmesh.bundle", vmesh + ".bundle"),
            ("kmesh.bundle", kmesh + ".bundle"),
            ("prefabVesnaSSR01.bundle", vprefab + ".bundle"),
            ("prefabKarstSSR01.bundle", kprefab + ".bundle"),
            ("mat.bundle", mat + ".bundle"),
        });
        // The two manifests differ in ONE byte of one stub: vmesh's content hash. Real images, read by the
        // real decoder, so the singles rule the fixture claims is the one the code sees.
        string before = WriteSingles(Path.Combine(_root, "m1"),
            (vmesh, 1), (kmesh, 2), (vprefab, 3), (kprefab, 4), (mat, 5));
        string after = WriteSingles(Path.Combine(_root, "m2"),
            (vmesh, 9), (kmesh, 2), (vprefab, 3), (kprefab, 4), (mat, 5));
        var m1 = GffManifest.Read(before);
        var m2 = GffManifest.Read(after);

        // What the old key would have had to see, and could not: same entry name, same physical file.
        Assert.Contains(vmesh + ".bundle", m1.Names);
        Assert.Equal(m1.Names, m2.Names);
        Assert.Equal(m1.Locate(vmesh + ".bundle").Stub.PhysHash,
                     m2.Locate(vmesh + ".bundle").Stub.PhysHash);
        Assert.NotEqual(BundleReads.ContentHashLookup(m1)(vmesh + ".bundle"),
                        BundleReads.ContentHashLookup(m2)(vmesh + ".bundle"));

        const string slot = "c_VesnaSSR01_slg_body_lod0";
        var reader = new BundleReader();
        string VesnaIb() => BufferHash.Compute(
            FixtureCrawl.DeobfuscateOver(abw)("vmesh.bundle")!, slot, 0, reader).Ib.ToString("x8");

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        // the two outfits ship byte-identical bodies, so the ib they share is worn by both
        string staleIb = VesnaIb();
        Assert.Equal(2, first.MeshOtherWearers(staleIb, "Nobody", "NobodySSR01").Count);

        // the same logical bundle, rewritten where it stands: a different index buffer, and so a
        // different ib for anything measured out of it
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('1', 31) + "m.bundle"), slot,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 2, 1 }, new uint[] { 7 },
            bundleName: "vmesh.bundle");
        string currentIb = VesnaIb();
        Assert.NotEqual(staleIb, currentIb);

        // The control, and the shape of the defect: with the manifest ALSO held constant, nothing the
        // record keys on has moved, the row is served as it stands, and the index goes on reporting Vesna
        // as a wearer of content her bundle no longer holds.
        var blind = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first);
        Assert.Equal(2, blind.MeshOtherWearers(staleIb, "Nobody", "NobodySSR01").Count);
        Assert.Empty(blind.MeshOtherWearers(currentIb, "Nobody", "NobodySSR01"));

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m2),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));

        // exactly the outfit that read the rewritten bundle, and only it
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
        // and its row carries the content that is there NOW: the mesh it used to share with Karst is
        // Karst's alone, and Vesna wears the one she was rebuilt with
        Assert.Equal("Karst",
            Assert.Single(second.MeshOtherWearers(staleIb, "Nobody", "NobodySSR01")).Character);
        Assert.Equal("Vesna",
            Assert.Single(second.MeshOtherWearers(currentIb, "Nobody", "NobodySSR01")).Character);
    }

    [Fact]
    public void An_outfit_whose_assembly_prefab_was_rewritten_in_place_is_measured_again()
    {
        // The prefab is not hashed, so nothing of it reaches the row directly — and it still decides the
        // whole row: which slots become parts, which tiers each part has, and whether a renderer casts
        // (which is what makes a mesh eligible to witness the outfit's presence). A prefab rewritten under
        // a name and internalId that both survive has to re-measure the outfits parsed out of it, or the
        // row keeps a mesh set and a witness list the prefab no longer describes.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        string vmesh = Single('a'), kmesh = Single('b'), vprefab = Single('c'),
               kprefab = Single('d'), mat = Single('e');
        var catalog = CatalogIndex.ForTest(rows, deps, new[]
        {
            ("vmesh.bundle", vmesh + ".bundle"),
            ("kmesh.bundle", kmesh + ".bundle"),
            ("prefabVesnaSSR01.bundle", vprefab + ".bundle"),
            ("prefabKarstSSR01.bundle", kprefab + ".bundle"),
            ("mat.bundle", mat + ".bundle"),
        });
        // only Vesna's PREFAB bundle changes content; every mesh and texture bundle stands still
        var m1 = GffManifest.Read(WriteSingles(Path.Combine(_root, "p1"),
            (vmesh, 1), (kmesh, 2), (vprefab, 3), (kprefab, 4), (mat, 5)));
        var m2 = GffManifest.Read(WriteSingles(Path.Combine(_root, "p2"),
            (vmesh, 1), (kmesh, 2), (vprefab, 9), (kprefab, 4), (mat, 5)));

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m2),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));

        // exactly the outfit whose prefab was parsed out of that bundle, and only it
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void A_bundle_the_model_build_only_looked_into_is_not_recorded()
    {
        // The other half of the prefab rule: the scope opens every closure entry hunting for a prefab, and
        // a bundle that yielded none shaped nothing. Recording what was OPENED rather than what was PARSED
        // would re-measure both outfits every time a shared texture bundle was repacked. Here mat.bundle —
        // opened by both subjects' scopes, an assembly prefab in neither — carries the texture both wear,
        // so it IS in each row through its map; what must not happen is Karst re-measuring because VESNA's
        // prefab moved.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        string vmesh = Single('a'), kmesh = Single('b'), vprefab = Single('c'),
               kprefab = Single('d'), mat = Single('e');
        var catalog = CatalogIndex.ForTest(rows, deps, new[]
        {
            ("vmesh.bundle", vmesh + ".bundle"),
            ("kmesh.bundle", kmesh + ".bundle"),
            ("prefabVesnaSSR01.bundle", vprefab + ".bundle"),
            ("prefabKarstSSR01.bundle", kprefab + ".bundle"),
            ("mat.bundle", mat + ".bundle"),
        });
        var m1 = GffManifest.Read(WriteSingles(Path.Combine(_root, "k1"),
            (vmesh, 1), (kmesh, 2), (vprefab, 3), (kprefab, 4), (mat, 5)));
        var m2 = GffManifest.Read(WriteSingles(Path.Combine(_root, "k2"),
            (vmesh, 1), (kmesh, 2), (vprefab, 9), (kprefab, 4), (mat, 5)));

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        var keys = BundleReads.CurrentKeys(catalog, BundleReads.ContentHashLookup(m2));

        // Vesna's own row moved with her prefab; Karst's scope opened that bundle for nothing and stayed
        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw),
            population.Roster[0].Outfits[0], "Vesna");
        Assert.Equal(new[] { "prefabVesnaSSR01.bundle" }, model.PrefabBundles);
        Assert.False(BundleReads.StillCurrent(keys,
            BundleReads.Of(catalog, BundleReads.ContentHashLookup(m1), model.PrefabBundles!)));

        var seen = new List<SharingProgress>();
        SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m2),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));
        Assert.All(seen, p => Assert.Equal(1, p.Total));
    }

    /// <summary>The two clean outfits again, arranged so no other record can stand in for the material's
    /// own bundle: each subject's material sits ALONE in a bundle of its own and binds the shared texture
    /// through an EXTERNAL CAB, so the texture bundle the row records (<c>mat.bundle</c>, where the pixels
    /// are) is a different bundle from the one the material was read out of. <c>decoy.bundle</c> is placed
    /// ahead of the material bundle in the closure, so the scope opens it — for a prefab, then for its CAB
    /// name — and gets nothing from it: the speculative open the record must not carry.</summary>
    private (SharingPopulation Population, List<(string, string)> Rows, List<(string, string[])> Deps)
        TwoOutfitsOverAnExternalTexture(string abw)
    {
        Directory.CreateDirectory(abw);
        // the texture carrier. Its own material object is inert here — no renderer points at it — and it is
        // what BuildOneMaterial needs to put a texture in a bundle at all.
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_unused", materialPathId: 21,
            texEnvs: Array.Empty<(string, int, long)>(), externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");
        SyntheticBundle.BuildOneTexture(Path.Combine(abw, new string('d', 32) + ".bundle"),
            "decoy_d", 4, 4, bundleName: "decoy.bundle");
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        OutfitOverItsOwnMaterial(abw, '1', 'v', "VesnaSSR01", "vmesh.bundle", "vmat.bundle", "CAB-vmat", rows, deps);
        OutfitOverItsOwnMaterial(abw, '2', 'k', "KarstSSR01", "kmesh.bundle", "kmat.bundle", "CAB-kmat", rows, deps);
        var roster = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
            new Character(2, "Karst", "SSR", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "KarstSSR01", OutfitKind.Base) }),
        };
        return (SharingPopulation.Of(roster), rows, deps);
    }

    /// <summary>One subject of <see cref="TwoOutfitsOverAnExternalTexture"/>: prefab, mesh, and a material
    /// bundle of its own whose single material binds the shared texture across <c>CAB-mat</c>.</summary>
    private void OutfitOverItsOwnMaterial(string abw, char fill, char matFill, string stem,
        string meshBundle, string matBundle, string matCab,
        List<(string Address, string OwnerBundle)> rows, List<(string Address, string[] Deps)> deps)
    {
        string slot = $"c_{stem}_slg_body_lod0";
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string(matFill, 32) + ".bundle"),
            matBundle, materialName: $"M_{stem}", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 1, 2L) }, externalCabs: new[] { "CAB-mat" }, cabName: matCab);
        WorkbenchPrefab.Build(Path.Combine(abw, new string(fill, 32) + ".bundle"),
            bundleName: $"prefab{stem}.bundle", rootName: stem,
            slots: new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (1, 21L) }) },
            recipe: new[] { (slot, $"Assets/X/{slot}.mesh") },
            externalCabs: new[] { matCab },
            bones: new[] { ("Bip001", -1), ("Bip001 Pelvis", 0) });
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string(fill, 31) + "m.bundle"), slot,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 }, new uint[] { 7 },
            bundleName: meshBundle);

        string prefabAddress = GameVfs.PrefabAddress("Character/Player", stem);
        rows.Add((prefabAddress, $"prefab{stem}.bundle"));
        rows.Add(($"Assets/X/{slot}.mesh", meshBundle));
        // decoy BEFORE the material bundle: the CAB walk reaches it first and finds nothing there
        deps.Add((prefabAddress, new[] { $"prefab{stem}.bundle", "decoy.bundle", matBundle, "mat.bundle" }));
    }

    /// <summary>The manifest names for <see cref="TwoOutfitsOverAnExternalTexture"/>'s bundles, and the
    /// catalog join onto them. Each is a single-file bundle, so its entry name is its physical name.</summary>
    private static (string[] Singles, (string, string)[] BundleRows) ExternalTextureNames()
    {
        // hex fills only: these are physical bundle names, which a manifest stub carries as raw bytes
        string[] singles =
        {
            Single('1'), Single('2'), Single('3'), Single('4'),
            Single('5'), Single('6'), Single('7'), Single('8'),
        };
        string[] logical =
        {
            "vmesh.bundle", "kmesh.bundle", "prefabVesnaSSR01.bundle", "prefabKarstSSR01.bundle",
            "vmat.bundle", "kmat.bundle", "mat.bundle", "decoy.bundle",
        };
        return (singles, logical.Select((l, i) => (l, singles[i] + ".bundle")).ToArray());
    }

    /// <summary>A manifest over <see cref="ExternalTextureNames"/>'s singles, with one named bundle's
    /// content hash set apart from the baseline — the rewrite-in-place of exactly that bundle.</summary>
    private GffManifest ExternalTextureManifest(string dir, int movedIndex = -1)
    {
        var (singles, _) = ExternalTextureNames();
        return GffManifest.Read(WriteSingles(dir, singles
            .Select((s, i) => (s, (byte)(i == movedIndex ? 200 : i + 1)))
            .ToArray()));
    }

    [Fact]
    public void An_outfit_whose_material_bundle_was_rewritten_in_place_is_measured_again()
    {
        // Nothing of the material is hashed either, and it is what decides the row's TEXTURE list: which
        // shader slots bind a map at all, and which texture each one points at. Its bundle reaches the row
        // through no other record — the texture bundles say where the PIXELS live, which is a different
        // bundle whenever the texture is not local to the material — so a material rebound in place under a
        // surviving name and internalId left the row listing textures this outfit no longer wears.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoOutfitsOverAnExternalTexture(abw);
        var (_, bundleRows) = ExternalTextureNames();
        var catalog = CatalogIndex.ForTest(rows, deps, bundleRows);

        // only Vesna's MATERIAL bundle changes content; her prefab, her mesh and the shared texture bundle
        // all stand still, and so does every name in either namespace
        var m1 = ExternalTextureManifest(Path.Combine(_root, "x1"));
        var m2 = ExternalTextureManifest(Path.Combine(_root, "x2"), movedIndex: 4);
        Assert.Equal(m1.Names, m2.Names);

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);
        Assert.Empty(first.FailedOutfits);

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m2),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));

        // exactly the outfit whose material was read out of that bundle, and only it
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void A_material_bundle_standing_still_costs_its_outfit_nothing()
    {
        // The control for the pin above: the same world, the same manifest on both sides. Recording the
        // material bundles must not cost a delta anything when nothing moved — the row comes back as it
        // stands, for both outfits, with no read reported.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoOutfitsOverAnExternalTexture(abw);
        var (_, bundleRows) = ExternalTextureNames();
        var catalog = CatalogIndex.ForTest(rows, deps, bundleRows);
        var m1 = ExternalTextureManifest(Path.Combine(_root, "s1"));

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));

        Assert.Empty(seen);
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.True(second.SameRowsAs(first));
    }

    [Fact]
    public void A_bundle_the_material_walk_only_opened_is_not_recorded()
    {
        // The other half of the material rule, and the reason the record cannot simply take the scope: the
        // scope opens decoy.bundle twice over — once hunting a prefab, once reading its CAB name on the way
        // to the material — and neither read shaped anything here. mat.bundle is opened too, and belongs to
        // the row as a TEXTURE bundle, not as a material one. Recording either as read would re-measure
        // both outfits every time an unrelated bundle beside them was repacked.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoOutfitsOverAnExternalTexture(abw);
        var (_, bundleRows) = ExternalTextureNames();
        var catalog = CatalogIndex.ForTest(rows, deps, bundleRows);

        var model = SubjectModelBuilder.Build(catalog, FixtureCrawl.DeobfuscateOver(abw),
            population.Roster[0].Outfits[0], "Vesna");
        Assert.Empty(model.Problems);
        // the one bundle a material was actually read out of — not the decoy, not the texture carrier,
        // not the prefab whose slot merely pointed at it
        Assert.Equal(new[] { "vmat.bundle" }, model.MaterialBundles);
        // and the texture the material reached across the CAB is still recorded, by its own route
        Assert.Equal("mat.bundle", Assert.Single(model.Parts[0].Materials[0].Maps).BundleId);

        // so a decoy rewritten in place is a bundle NO row read: neither outfit re-measures
        var m1 = ExternalTextureManifest(Path.Combine(_root, "o1"));
        var m2 = ExternalTextureManifest(Path.Combine(_root, "o2"), movedIndex: 7);
        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m1),
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m2),
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));
        Assert.Empty(seen);
        Assert.True(second.SameRowsAs(first));
    }

    [Fact]
    public void A_row_with_no_read_record_is_dropped_at_load_and_measured_again()
    {
        // The bootstrap allowance is gone. A row carrying no record of what its bundles held cannot be
        // gated on content, and there is no grain at which it may be kept instead — so it does not come
        // back off the file at all, and its outfit is measured like any the previous pass never covered.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps, new[] { ("vmesh.bundle", "vmesh-1") });
        var measured = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        string path = Path.Combine(_root, "seedshape.json");
        measured.Save(path);

        // strip one row's read record, leaving the shape a pre-content-gate seed row has
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Outfits"]!.AsArray()[0]!.AsObject().Remove("R");
        File.WriteAllText(path, json.ToJsonString());

        var back = SharingIndex.TryLoad(path, population)!;
        Assert.Equal(1, back.MeasuredOutfitCount);          // the stripped row never joined

        var seen = new List<SharingProgress>();
        var next = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180", back, new InlineProgress(seen));
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));    // exactly the dropped outfit
        Assert.Equal(2, next.MeasuredOutfitCount);
    }

    [Fact]
    public void A_row_with_an_empty_read_record_is_never_reused()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps);
        var measured = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        string path = Path.Combine(_root, "empty-reads.json");
        measured.Save(path);

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Outfits"]!.AsArray()[0]!["R"] = "";
        File.WriteAllText(path, json.ToJsonString());
        var loaded = SharingIndex.TryLoad(path, population);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.MeasuredOutfitCount);

        var seen = new List<SharingProgress>();
        var next = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180", loaded, new InlineProgress(seen));

        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, next.MeasuredOutfitCount);
    }

    [Fact]
    public void A_schema_eight_row_without_address_resolutions_is_dropped_and_measured_again()
    {
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps);
        var measured = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        string path = Path.Combine(_root, "schema8-without-addresses.json");
        measured.Save(path);

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Outfits"]!.AsArray()[0]!.AsObject().Remove("A");
        File.WriteAllText(path, json.ToJsonString());
        var loaded = SharingIndex.TryLoad(path, population);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.MeasuredOutfitCount);

        var seen = new List<SharingProgress>();
        var next = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180", loaded, new InlineProgress(seen));

        Assert.All(seen, p => Assert.Equal(1, p.Total));
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
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps),
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180", first,
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
        var first = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Single(first.FailedOutfits);

        // the mesh bundle arrives, nothing else moves
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('1', 31) + "m.bundle"),
            "c_VesnaSSR01_slg_body_lod0", new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
            new[] { 0, 1, 2 }, new uint[] { 7 }, bundleName: "vmesh.bundle");

        var second = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180", first);
        Assert.True(second.Covers("Vesna", "VesnaSSR01"));
        Assert.Empty(second.FailedOutfits);
    }

    // ---- the acceptance criterion: reads in proportion to the content that moved -------------------

    /// <summary>The six logical bundles <see cref="TwoIndependentOutfits"/> builds, in the order every
    /// fixture here indexes them by — so a case can name the one whose content it moves.</summary>
    private static readonly string[] IndependentBundles =
    {
        "prefabVesnaSSR01.bundle", "vmesh.bundle", "vmat.bundle",
        "prefabKarstSSR01.bundle", "kmesh.bundle", "kmat.bundle",
    };

    private const int VPrefab = 0, VMesh = 1, VMat = 2, KMesh = 4;

    /// <summary>Two outfits that share NOTHING: each has its own prefab, its own mesh bundle, and its own
    /// material bundle carrying its own local texture. That is what a per-wearer invalidation needs — a
    /// bundle whose content can move for one subject without touching the other — and it leaves each mesh
    /// bundle OUTSIDE both dependency closures, which is where a mesh owner really sits (a mesh address
    /// resolves catalog-wide).</summary>
    private (SharingPopulation Population, List<(string, string)> Rows, List<(string, string[])> Deps)
        TwoIndependentOutfits(string abw)
    {
        Directory.CreateDirectory(abw);
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        IndependentOutfit(abw, '1', 'v', "VesnaSSR01", rows, deps);
        IndependentOutfit(abw, '2', 'k', "KarstSSR01", rows, deps);
        var roster = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
            new Character(2, "Karst", "SSR", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "KarstSSR01", OutfitKind.Base) }),
        };
        return (SharingPopulation.Of(roster), rows, deps);
    }

    /// <summary>One subject of <see cref="TwoIndependentOutfits"/>. <paramref name="texFormat"/> is the
    /// Unity texture format its map binds — 4 (RGBA32) hashes, 3 (RGB24) has no DXGI mapping and is the
    /// unhashable verdict. <paramref name="meshBundleOverride"/> points the mesh address at a bundle the
    /// catalog names and no file backs, which is how a real outfit fails to measure.</summary>
    private void IndependentOutfit(string abw, char fill, char tag, string stem,
        List<(string Address, string OwnerBundle)> rows, List<(string Address, string[] Deps)> deps,
        int texFormat = SyntheticBundle.Rgba32, string? meshBundleOverride = null)
    {
        string slot = $"c_{stem}_slg_body_lod0";
        string matBundle = $"{tag}mat.bundle", meshBundle = meshBundleOverride ?? $"{tag}mesh.bundle";
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string(tag, 32) + ".bundle"),
            matBundle, materialName: $"M_{stem}", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec($"c_{stem}_d", 4, 4,
                texFormat == SyntheticBundle.Rgba32
                    ? SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)
                    : new byte[4 * 4 * 3],
                Format: texFormat),
            cabName: $"CAB-{tag}mat");
        WorkbenchPrefab.Build(Path.Combine(abw, new string(fill, 32) + ".bundle"),
            bundleName: $"prefab{stem}.bundle", rootName: stem,
            slots: new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (1, 21L) }) },
            recipe: new[] { (slot, $"Assets/X/{slot}.mesh") },
            externalCabs: new[] { $"CAB-{tag}mat" },
            bones: new[] { ("Bip001", -1), ("Bip001 Pelvis", 0) });
        if (meshBundleOverride is null)
            SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string(fill, 31) + "m.bundle"), slot,
                new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 }, new uint[] { 7 },
                bundleName: meshBundle);

        string prefabAddress = GameVfs.PrefabAddress("Character/Player", stem);
        rows.Add((prefabAddress, $"prefab{stem}.bundle"));
        rows.Add(($"Assets/X/{slot}.mesh", meshBundle));
        deps.Add((prefabAddress, new[] { $"prefab{stem}.bundle", matBundle }));
    }

    /// <summary>The physical file one of <see cref="IndependentBundles"/> lives in under packing generation
    /// <paramref name="gen"/>. Hex, 32 characters, and different in every generation: repacking mints new
    /// physical names, and a single-file bundle's manifest entry name IS its physical name.</summary>
    private static string PhysName(int gen, int index) =>
        new string("0123456789abcdef"[gen], 31) + "0123456789abcdef"[index];

    /// <summary>The catalog for <see cref="TwoIndependentOutfits"/> under packing generation
    /// <paramref name="gen"/>: the same address→logical-bundle rows and the same closures, joined to that
    /// generation's internalIds.</summary>
    private static CatalogIndex IndependentCatalog(List<(string, string)> rows,
        List<(string, string[])> deps, int gen) =>
        CatalogIndex.ForTest(rows, deps,
            IndependentBundles.Select((b, i) => (b, PhysName(gen, i) + ".bundle")).ToArray());

    /// <summary>A manifest over generation <paramref name="gen"/>'s singles, each stub carrying the content
    /// byte its bundle is assigned (1…6 by position). <paramref name="movedIndex"/> names the one bundle
    /// whose CONTENT differs from that baseline — a rewrite in place, whatever the packing.</summary>
    private GffManifest IndependentManifest(string dir, int gen, int movedIndex = -1) =>
        GffManifest.Read(WriteSingles(dir, IndependentBundles
            .Select((_, i) => (PhysName(gen, i), (byte)(i == movedIndex ? 200 : i + 1)))
            .ToArray()));

    /// <summary>A deobfuscate over <paramref name="abw"/> that records every logical bundle asked for.
    /// Reads are the unit the whole design is measured in, so the tests count the asks the pass makes at
    /// the one delegate every read goes through.</summary>
    private static Func<string, byte[]?> Counting(string abw, List<string> asked)
    {
        var real = FixtureCrawl.DeobfuscateOver(abw);
        return id => { lock (asked) asked.Add(id); return real(id); };
    }

    [Fact]
    public void A_pure_repack_costs_the_whole_population_nothing()
    {
        // THE acceptance criterion, through SharingIndex.Build's own delta path. Between two packing
        // generations every internalId and every physical file name is new — which is all a repack changes
        // — while each logical bundle still holds exactly the content it held. Not one row may be read.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var before = IndependentCatalog(rows, deps, gen: 0);
        var after = IndependentCatalog(rows, deps, gen: 1);
        var m0 = IndependentManifest(Path.Combine(_root, "gen0"), gen: 0);
        var m1 = IndependentManifest(Path.Combine(_root, "gen1"), gen: 1);

        // the repack is real: nothing a bundle is packaged as survives it, and the manifests share no name
        Assert.NotEqual(before.BundleNameToInternalId["vmesh.bundle"],
                        after.BundleNameToInternalId["vmesh.bundle"]);
        Assert.Empty(m0.Names.Intersect(m1.Names, StringComparer.OrdinalIgnoreCase));

        var first = SharingIndex.Build(population, before, BundleReads.ContentHashLookup(m0),
            FixtureCrawl.DeobfuscateOver(abw), "26109");
        Assert.Equal(2, first.MeasuredOutfitCount);
        Assert.Empty(first.FailedOutfits);

        var asked = new List<string>();
        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, after, BundleReads.ContentHashLookup(m1),
            Counting(abw, asked), "26932", first, new InlineProgress(seen));

        Assert.Empty(asked);                       // zero bundle reads: the criterion, stated
        Assert.Empty(seen);                        // and nothing to report, so the cell stays blank
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void One_bundles_content_moving_re_measures_its_wearer_and_reads_only_that_bundle()
    {
        // The other half of the criterion, through Build with the cross-pass memo wired to a real file.
        // Vesna's material-and-texture bundle is rewritten where it stands; Karst's world is untouched. So
        // Vesna's row is measured again — and even her own MESH bundle is never opened, because the memo
        // already knows what that content hashes to. The control at the end is the same pass with no memo,
        // where exactly that read comes back.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var m0 = IndependentManifest(Path.Combine(_root, "b0"), gen: 0);
        var moved = IndependentManifest(Path.Combine(_root, "b1"), gen: 0, movedIndex: VMat);
        string memoFile = Path.Combine(_root, "memo", "asset_hashes.json");

        var warm = new AssetHashMemo(memoFile);
        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m0),
            FixtureCrawl.DeobfuscateOver(abw), "26109", hashes: warm);
        Assert.Equal(2, first.MeasuredOutfitCount);
        warm.Flush();

        var asked = new List<string>();
        var seen = new List<SharingProgress>();
        var memo = new AssetHashMemo(memoFile);
        var second = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(moved),
            Counting(abw, asked), "26932", first, new InlineProgress(seen), hashes: memo);

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));            // exactly the one wearer
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Contains(IndependentBundles[VMat], asked);           // the bundle that moved IS read
        Assert.DoesNotContain(IndependentBundles[VMesh], asked);    // its unmoved mesh is not
        foreach (var karst in new[] { IndependentBundles[3], IndependentBundles[KMesh], IndependentBundles[5] })
            Assert.DoesNotContain(karst, asked);                    // and nothing of Karst's at all
        Assert.True(memo.Hits > 0);

        // the control: the identical pass with no memo behind it opens the mesh bundle again
        var cold = new List<string>();
        SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(moved),
            Counting(abw, cold), "26932", first);
        Assert.Contains(IndependentBundles[VMesh], cold);
    }

    [Fact]
    public void An_outfit_that_was_re_addressed_is_measured_again()
    {
        // The SHAPE half's first term. Nothing in the game moved — same catalog, same manifest, same bytes
        // — but the subject now resolves through a curated route instead of the stem formula. How a
        // subject was reached is part of what its row means, so the row is measured again.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var manifest = IndependentManifest(Path.Combine(_root, "r0"), gen: 0);

        var first = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(manifest),
            FixtureCrawl.DeobfuscateOver(abw), "26109");
        Assert.Equal(2, first.MeasuredOutfitCount);

        // the same prefab, named outright instead of derived from the stem: the scope it resolves is the
        // same set of bundles, so the route line is the only thing that moved
        var routed = SharingPopulation.Of(new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099, new List<Remold.Core.Model.Outfit>
            {
                new(10, "VesnaSSR01", OutfitKind.Base)
                {
                    Route = SubjectRoute.Addressable(
                        GameVfs.PrefabAddress("Character/Player", "VesnaSSR01"), "VesnaSSR01"),
                },
            }),
            population.Roster[1],
        });

        // stated and checked: the route resolves the identical scope, so the route line is the only thing
        // the shape half can be reacting to
        Assert.Equal(
            SubjectScope.Build(catalog, _ => null, population.Roster[0].Outfits[0]).ScopeBundles,
            SubjectScope.Build(catalog, _ => null, routed.Roster[0].Outfits[0]).ScopeBundles);

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(routed, catalog, BundleReads.ContentHashLookup(manifest),
            FixtureCrawl.DeobfuscateOver(abw), "26109", first, new InlineProgress(seen));

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void An_outfit_whose_closure_gained_a_bundle_is_measured_again_though_nothing_it_read_moved()
    {
        // The SHAPE half's second term, and the reason it is a SET rather than nothing at all. Vesna's
        // dependency closure gains a bundle that already existed — Karst's material bundle, whose content
        // is the same object on both sides of this test — so no bundle Vesna's row recorded has moved by
        // any measure, and she can still now reach something she never measured. Membership is the fact;
        // the content record cannot see it, because the newcomer is not in the record.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var manifest = IndependentManifest(Path.Combine(_root, "c0"), gen: 0);
        var before = IndependentCatalog(rows, deps, gen: 0);

        var first = SharingIndex.Build(population, before, BundleReads.ContentHashLookup(manifest),
            FixtureCrawl.DeobfuscateOver(abw), "26109");
        Assert.Equal(2, first.MeasuredOutfitCount);

        string vesna = GameVfs.PrefabAddress("Character/Player", "VesnaSSR01");
        var widened = deps
            .Select(d => d.Item1 == vesna
                ? (d.Item1, new[] { "prefabVesnaSSR01.bundle", "vmat.bundle", "kmat.bundle" })
                : d)
            .ToList();

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, IndependentCatalog(rows, widened, gen: 0),
            BundleReads.ContentHashLookup(manifest), FixtureCrawl.DeobfuscateOver(abw), "26109",
            first, new InlineProgress(seen));

        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));       // exactly the outfit whose closure grew
        Assert.Equal(2, second.MeasuredOutfitCount);
        Assert.Empty(second.FailedOutfits);
    }

    [Fact]
    public void A_full_measures_two_artifacts_serve_a_cold_install_as_the_shipped_seed()
    {
        // The mint route end to end, through the app's own LoadSharingBase: one full measure's index file
        // and observation memo, copied to where a release ships them, are what a cold install starts from.
        // Under the same catalog it reads nothing at all; when one bundle's content has moved since, it
        // reads that bundle and nothing else — which is what makes an invalidated seed row cheap.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var m0 = IndependentManifest(Path.Combine(_root, "s0"), gen: 0);
        string seedIndex = Path.Combine(_root, "shipped", "sharing_seed.json");
        string seedMemo = Path.Combine(_root, "shipped", "asset_hashes_seed.json");
        string cachePath = Path.Combine(_root, "cold", "sharing_26109.json");
        string cacheMemo = Path.Combine(_root, "cold", "asset_hashes.json");

        var minting = new AssetHashMemo(seedMemo);
        SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m0),
            FixtureCrawl.DeobfuscateOver(abw), "26109", hashes: minting).Save(seedIndex);
        minting.Flush();

        // a cold install: no cache for this catalog, so the seed is the base
        var basis = MainWindowViewModel.LoadSharingBase(cachePath, seedIndex, "26109", population);
        Assert.True(basis.FromSeed);
        Assert.Equal(2, basis.Index!.MeasuredOutfitCount);

        var asked = new List<string>();
        var same = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(m0),
            Counting(abw, asked), "26109", basis.Index, hashes: new AssetHashMemo(cacheMemo, seedMemo));
        Assert.Empty(asked);
        Assert.Equal(2, same.MeasuredOutfitCount);

        // …and the same cold install one patch later, with Vesna's material bundle rewritten in place
        var moved = IndependentManifest(Path.Combine(_root, "s1"), gen: 1, movedIndex: VMat);
        var patched = IndependentCatalog(rows, deps, gen: 1);
        var afterPatch = new List<string>();
        var repaired = SharingIndex.Build(population, patched, BundleReads.ContentHashLookup(moved),
            Counting(abw, afterPatch), "26932", basis.Index,
            hashes: new AssetHashMemo(cacheMemo, seedMemo));

        Assert.Equal(2, repaired.MeasuredOutfitCount);
        Assert.Contains(IndependentBundles[VMat], afterPatch);
        Assert.DoesNotContain(IndependentBundles[VMesh], afterPatch);
        Assert.DoesNotContain(IndependentBundles[KMesh], afterPatch);
    }

    [Fact]
    public void A_schema_seven_file_is_no_base_at_all_and_the_pass_rewrites_it_at_eight()
    {
        // Both regenerable, so neither gets a migration: a cache or a seed from before lodm measurement is
        // refused whole, the population is measured once, and what lands on disk is schema 8.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var manifest = IndependentManifest(Path.Combine(_root, "o0"), gen: 0);
        string path = Path.Combine(_root, "old", "sharing_26109.json");

        SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(manifest),
            FixtureCrawl.DeobfuscateOver(abw), "26109").Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["SchemaVersion"] = 7;
        File.WriteAllText(path, json.ToJsonString());

        // neither door opens: the cache path and the seed path both point at it
        var basis = MainWindowViewModel.LoadSharingBase(path, path, "26109", population);
        Assert.Null(basis.Index);

        var seen = new List<SharingProgress>();
        var built = SharingIndex.Build(population, catalog, BundleReads.ContentHashLookup(manifest),
            FixtureCrawl.DeobfuscateOver(abw), "26109", basis.Index, new InlineProgress(seen));
        Assert.All(seen, p => Assert.Equal(2, p.Total));      // the whole population, measured once
        Assert.Equal(2, built.MeasuredOutfitCount);

        built.Save(path);
        Assert.Equal(8, (int)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["SchemaVersion"]!);
    }

    [Fact]
    public void The_memo_keeps_the_unhashable_verdict_and_never_a_failed_read()
    {
        // What may be persisted, through the pass that persists it. A format with no DXGI mapping has no
        // offline hash at all — a fact about the content, kept. A bundle that would not open is a fact
        // about the RUN (the game holding its files), and keeping it would serve that verdict to every
        // later pass; it is not written, so the next pass tries the read again.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var rows = new List<(string, string)>();
        var deps = new List<(string, string[])>();
        // Vesna's texture is RGB24, which no DXGI format maps; Karst's mesh address points at a bundle the
        // catalog names and no file backs, which fails her outfit outright.
        IndependentOutfit(abw, '1', 'v', "VesnaSSR01", rows, deps, texFormat: 3);
        IndependentOutfit(abw, '2', 'k', "KarstSSR01", rows, deps, meshBundleOverride: "kmesh.bundle");
        File.Delete(Path.Combine(abw, new string('2', 31) + "m.bundle"));
        var population = SharingPopulation.Of(new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, "VesnaSSR01", OutfitKind.Base) }),
            new Character(2, "Karst", "SSR", 20, 2099,
                new List<Remold.Core.Model.Outfit> { new(20, "KarstSSR01", OutfitKind.Base) }),
        });
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var manifest = IndependentManifest(Path.Combine(_root, "v0"), gen: 0);
        var content = BundleReads.ContentHashLookup(manifest);
        string memoFile = Path.Combine(_root, "verdict", "asset_hashes.json");

        var memo = new AssetHashMemo(memoFile);
        var idx = SharingIndex.Build(population, catalog, content,
            FixtureCrawl.DeobfuscateOver(abw), "26109", hashes: memo);
        memo.Flush();

        Assert.True(idx.Covers("Vesna", "VesnaSSR01"));                 // unhashable is not a problem
        Assert.Equal(new[] { "karst|karstssr01" }, idx.FailedOutfits);  // a missing bundle is

        // read back off the file, so it is what was PERSISTED that answers
        var back = new AssetHashMemo(memoFile);
        Assert.True(back.TryGet(AssetHashMemo.TextureKey(
            BundleReads.ContentOf(catalog, content, "vmat.bundle"), "c_VesnaSSR01_d", 2), out var verdict));
        Assert.Null(verdict);                                           // the verdict, not a hash
        Assert.False(back.TryGet(AssetHashMemo.MeshKey(
            BundleReads.ContentOf(catalog, content, "kmesh.bundle"),
            "c_KarstSSR01_slg_body_lod0", 0), out _));
        // …and the key it would have been written under is a real one, so the miss is the rule and not a
        // bundle the fixture forgot to name
        Assert.NotNull(BundleReads.ContentOf(catalog, content, "kmesh.bundle"));
    }

    // ---- what the memo is coupled to --------------------------------------------------------------

    /// <summary>A memo file in a folder of its own, so a sweep test can watch the whole folder.</summary>
    private string MemoAt(string folder) => Path.Combine(_root, folder, "asset_hashes.json");

    [Fact]
    public void A_sharing_schema_bump_misses_every_value_the_memo_already_held()
    {
        // The coupling, mechanically. A memo entry is keyed on GAME CONTENT alone, so it self-invalidates
        // when the content moves — but its VALUE is something this app computed, and what a computed value
        // means moves with the app. A bump to SharingIndex's schema is the app SAYING its measurement means
        // something else; if the memo went on answering, the pass would dutifully re-measure every row and
        // then serve every hash straight back out of the old file — and stale hashes in the index make the
        // build's by-value sharing join miss, which ships a shared texture as private.
        string memoFile = MemoAt("bump");
        string key = AssetHashMemo.MeshKey("content-a", "c_VesnaSSR01_slg_body_lod0", 7)!;

        var wrote = new AssetHashMemo(memoFile);
        wrote.Put(key, "deadbeef");
        wrote.Flush();
        Assert.True(new AssetHashMemo(memoFile).TryGet(key, out var warm));
        Assert.Equal("deadbeef", warm);

        // The bump, simulated where it lands: the file is exactly what this build writes except that it
        // was written under the PREVIOUS sharing schema. Nothing about the key or the entry moved — only
        // what the value would have meant.
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(memoFile))!;
        Assert.Equal(SharingIndex.SchemaVersion, doc["SharingSchemaVersion"]!.GetValue<int>());
        doc["SharingSchemaVersion"] = SharingIndex.SchemaVersion - 1;
        File.WriteAllText(memoFile, doc.ToJsonString());

        Assert.False(new AssetHashMemo(memoFile).TryGet(key, out _));
    }

    [Fact]
    public void A_memo_this_install_owns_and_cannot_read_is_taken_over_once_not_re_parsed_forever()
    {
        // A foreign-schema file that never dirties is re-read and discarded on every launch for the life
        // of the install. One pass takes it over instead. The SEED is the other case: it is not this
        // install's file to replace, and dirtying on it would rewrite the install's own memo every launch
        // over a shipped file that never changes.
        string ownFile = MemoAt("takeover");
        Directory.CreateDirectory(Path.GetDirectoryName(ownFile)!);
        File.WriteAllText(ownFile,
            "{\"SchemaVersion\":99,\"SharingSchemaVersion\":99,\"Entries\":{\"k\":\"v\"}}");

        var memo = new AssetHashMemo(ownFile);
        Assert.False(memo.TryGet("k", out _));      // the lookup that loads the file
        memo.Flush();                               // …and publishes over it, with nothing measured

        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ownFile))!;
        Assert.Equal(AssetHashMemo.SchemaVersion, doc["SchemaVersion"]!.GetValue<int>());
        Assert.Equal(SharingIndex.SchemaVersion, doc["SharingSchemaVersion"]!.GetValue<int>());
        Assert.Empty(doc["Entries"]!.AsObject());

        // the seed half: a foreign SHIPPED memo leaves this install's own file unwritten
        string seedFile = MemoAt("foreignseed");
        Directory.CreateDirectory(Path.GetDirectoryName(seedFile)!);
        File.WriteAllText(seedFile, "{\"SchemaVersion\":99,\"SharingSchemaVersion\":99,\"Entries\":{}}");
        string fresh = Path.Combine(Path.GetDirectoryName(seedFile)!, "own.json");

        var seeded = new AssetHashMemo(fresh, seedFile);
        Assert.False(seeded.TryGet("k", out _));
        seeded.Flush();
        Assert.False(File.Exists(fresh));
    }

    [Fact]
    public void A_published_memo_sweeps_the_temps_its_own_publishes_mint_and_nothing_else()
    {
        // A hard kill between a publish's write and its move strands a temp beside the memo forever. The
        // sweep is name-scoped because this file shares the index folder with three other caches, so a
        // neighbour's in-flight temp must never be a candidate.
        string memoFile = MemoAt("sweep");
        Directory.CreateDirectory(Path.GetDirectoryName(memoFile)!);
        string stranded = memoFile + "." + new string('a', 32) + ".tmp";
        string foreign = memoFile + ".notaguid.tmp";
        string neighbour = Path.Combine(Path.GetDirectoryName(memoFile)!, "candidacy.json.beef.tmp");
        File.WriteAllText(stranded, "half a memo");
        File.WriteAllText(foreign, "somebody else's");
        File.WriteAllText(neighbour, "another cache's");

        var memo = new AssetHashMemo(memoFile);
        memo.Put(AssetHashMemo.MeshKey("content-a", "c_VesnaSSR01_slg_body_lod0", 0), "deadbeef");
        memo.Flush();

        Assert.True(File.Exists(memoFile));
        Assert.False(File.Exists(stranded));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(neighbour));
    }

    [Fact]
    public void A_memo_with_no_file_behind_it_costs_the_pass_no_keys_at_all()
    {
        // A memo key is a SHA-256 per mesh tier and per texture map. With nothing to look them up in,
        // computing them is pure cost, so the pass hands the memo a null key — the value both TryGet and
        // Put already read as "measured, not kept". Writes staying at zero IS that branch: a pass that
        // computed the keys would memoize under them whatever the memo then did with its file.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoIndependentOutfits(abw);
        var catalog = IndependentCatalog(rows, deps, gen: 0);
        var content = BundleReads.ContentHashLookup(IndependentManifest(Path.Combine(_root, "nokey"), gen: 0));

        var nowhere = new AssetHashMemo(null);
        Assert.False(nowhere.Enabled);
        var without = SharingIndex.Build(population, catalog, content,
            FixtureCrawl.DeobfuscateOver(abw), "26109", hashes: nowhere);

        Assert.Equal(2, without.MeasuredOutfitCount);
        Assert.Equal(0, nowhere.Writes);
        Assert.Equal(0, nowhere.Hits);

        // the control: the identical pass with a file behind the memo does memoize, so the zero above is
        // the short-circuit and not a fixture that measures nothing
        var somewhere = new AssetHashMemo(MemoAt("keys"));
        Assert.True(somewhere.Enabled);
        var with = SharingIndex.Build(population, catalog, content,
            FixtureCrawl.DeobfuscateOver(abw), "26109", hashes: somewhere);

        Assert.True(somewhere.Writes > 0);
        Assert.True(with.SameRowsAs(without));   // and the measurement itself is the same either way
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
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.True(first.IsDuplicateDoor("ElidDoor", "ElidDoor"));

        string moved = GameVfs.PrefabAddress("Character/Player", "ElidDoor");
        var deps2 = deps.Select(d => d.Item1 == moved
            ? (d.Item1, new[] { "prefabElidDoor.bundle", "mat.bundle", "extra.bundle" }) : d).ToList();
        var second = SharingIndex.Build(population, CatalogIndex.ForTest(rows, deps2),
            NoContentHashes, FixtureCrawl.DeobfuscateOver(abw), "25200", first);
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
