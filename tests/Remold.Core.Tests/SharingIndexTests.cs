using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <para>A BODY lod1 sibling tier — a second renderer under one part, the only shape that can tell
    /// the per-part gates from the per-tier ones — ships when <paramref name="bodyTier"/> is set or
    /// <paramref name="bodyTierCastShadows"/> names its <c>m_CastShadows</c> (which is what
    /// <see cref="Export.RecipeTierSlot.CastsShadows"/> exists to carry; without either the outfit is the
    /// two-slot shape the older cases measure and <c>BodyTierIb</c> is null).</para></summary>
    private (SharingIndex Index, string BodyIb, string HairIb, string? BodyTierIb) TwoPartOutfit(
        string abw, int hairCastShadows, int? bodyTierCastShadows = null,
        Func<TwoPartNodes, WorkbenchPrefab.VisibilityLists>? visibility = null, bool bodyTier = false)
    {
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('m', 32) + ".bundle"),
            "mat.bundle", materialName: "M_body", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_shared_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-mat");

        const string stem = "VesnaSSR01";
        string body = $"c_{stem}_slg_body_lod0", hair = $"c_{stem}_slg_hair_lod0";
        string bodyTierName = $"c_{stem}_slg_body_lod1";
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
        var roster = new[]
        {
            new Character(1, "Vesna", "SSR", 10, 1099,
                new List<Remold.Core.Model.Outfit> { new(10, stem, OutfitKind.Base) }),
        };
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var idx = SharingIndex.Build(SharingPopulation.Of(roster),
            CatalogIndex.ForTest(rows, deps), NoContentHashes, deobfuscate, "25180");
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
    public void The_persisted_schema_is_six()
    {
        // Witness eligibility is applied at MEASURE time and rows are fingerprint-reused, so a change to
        // the rule has to invalidate every prior cache and the shipped seed. So does a change to what the
        // read record's keys MEAN: schema 5 wrote two incompatible third keys inside one arc, and both are
        // the same length, so nothing but the version tells them apart. The twin of this pin is the shipped
        // seed's own version, in SharingSeedTests.
        var idx = TwoWearers();
        string path = Path.Combine(_root, "sharing_schema.json");
        idx.Save(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(6, (int)json["SchemaVersion"]!);
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
        var first = SharingIndex.Build(population, before, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
        Assert.Equal(2, first.MeasuredOutfitCount);
        // the fingerprint really is blind to it — that is what the read record is for
        Assert.Equal(SubjectFingerprint.For(before, population.Roster[0].Outfits[0]),
            SubjectFingerprint.For(With("vmesh-2"), population.Roster[0].Outfits[0]));

        var seen = new List<SharingProgress>();
        var second = SharingIndex.Build(population, With("vmesh-2"), NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180", first, new InlineProgress(seen));

        // exactly the outfit that read that bundle, and only it
        Assert.NotEmpty(seen);
        Assert.All(seen, p => Assert.Equal(1, p.Total));
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
    public void A_row_with_no_recorded_reads_is_kept_on_its_fingerprint_alone()
    {
        // The seed-bootstrap allowance: the shipped seed predates the read record, and its rows must not
        // all re-measure on the first launch that carries this code.
        using var g = new TempGame();
        string abw = g.At("AssetBundles_Windows");
        var (population, rows, deps) = TwoCleanOutfits(abw);
        var catalog = CatalogIndex.ForTest(rows, deps, new[] { ("vmesh.bundle", "vmesh-1") });
        var measured = SharingIndex.Build(population, catalog, NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180");
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
                new[] { ("vmesh.bundle", "vmesh-2") }), NoContentHashes,
            FixtureCrawl.DeobfuscateOver(abw), "25180",
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
