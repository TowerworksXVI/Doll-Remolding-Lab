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
/// The workbench builder over a synthetic corpus: real bundles on disk → the live PrefabReader/material
/// chain, through a hand-authored catalog + <see cref="SubjectScope"/>. Each test names the route it runs.
/// </summary>
public class SubjectModelBuilderTests
{
    private static string Bundle(string abw, char fill, string logical, Action<string> build)
    {
        var path = Path.Combine(abw, new string(fill, 32) + ".bundle");
        build(path);
        return logical;
    }

    /// <summary>A catalog whose formula row resolves the stem's prefab and whose dep closure carries the
    /// subject's other bundles.</summary>
    private static CatalogIndex Catalog(string stem, string prefabLogical,
        string[]? closure = null, string contextRoot = "Character/Player")
    {
        var address = GameVfs.PrefabAddress(contextRoot, stem);
        return CatalogIndex.ForTest(
            new[] { (address, prefabLogical) },
            new[] { (address, new[] { prefabLogical }.Concat(closure ?? Array.Empty<string>()).ToArray()) });
    }

    // ---- route 1: prefab-sourced model — grouping, lod0 representative, ordered materials + placeholder ----
    [Fact]
    public void PrefabModel_GroupsByToken_PicksLod0_PreservesMaterialOrderAndPlaceholder()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                // TWO LOD tiers must collapse to ONE part whose representative is lod0. The lod0 material
                // order (external, EMPTY placeholder, external) is binding.
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (1, 21L), (0, 0L), (2, 22L) }),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod1", new[] { (1, 21L) }),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (1, 21L) }),
            },
            recipe: new[]
            {
                ("c_TestySSR01_slg_cloth1_lod0", "Assets/X/c_TestySSR01_slg_cloth1_lod0.mesh"),
                ("c_TestySSR01_slg_body_lod0", "Assets/X/c_TestySSR01_slg_body_lod0.mesh"),
            },
            externalCabs: new[] { "CAB-matA", "CAB-matB" },
            bones: new[] { ("Bip001", -1), ("Bip001 Pelvis", 0), ("Bip001 Spine", 1) });

        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "matA.bundle", materialName: "M_cloth1", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-matA");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "matB.bundle", materialName: "M_cloth1b", materialPathId: 22,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1b_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x22, 0xAA, 0x22, 0xFF)), cabName: "CAB-matB");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "matA.bundle", "matB.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        Assert.Empty(model.Problems);
        Assert.Equal(new[] { "body", "cloth1" }, model.Parts.Select(p => p.Token).ToArray());   // ordinal order

        var cloth1 = model.Parts.Single(p => p.Token == "cloth1");
        Assert.Equal("c_TestySSR01_slg_cloth1_lod0", cloth1.SlotName);              // lod0 representative
        Assert.Equal("Assets/X/c_TestySSR01_slg_cloth1_lod0.mesh", cloth1.MeshAddress);
        Assert.Equal(3, cloth1.Materials.Count);                                    // order preserved
        Assert.Equal("M_cloth1", cloth1.Materials[0].Name);
        Assert.True(cloth1.Materials[1].IsPlaceholder);                             // empty slot kept in place
        Assert.Equal("", cloth1.Materials[1].Name);
        Assert.Equal("M_cloth1b", cloth1.Materials[2].Name);

        var baseMap = Assert.Single(cloth1.Materials[0].Maps);
        Assert.Equal("_BaseMap", baseMap.Slot);
        Assert.Equal("c_TestySSR01_slg_cloth1_d", baseMap.TextureName);

        // The fixture's root and 3 slot GameObjects ALSO carry Transforms, as real prefabs do — the read
        // must exclude those, so the count is 3 rig bones, not 3 + root + 3 slots.
        Assert.NotNull(model.Skeleton);
        Assert.Equal(3, model.Skeleton!.BoneCount);
        Assert.Contains(model.Skeleton.Bones, b => b.Name == "Bip001 Spine");
        Assert.DoesNotContain(model.Skeleton.Bones, b => b.Name.Contains("_slg_"));   // no slot carrier
        Assert.DoesNotContain(model.Skeleton.Bones, b => b.Name == "TestySSR01");     // no container root

        // route: every part records the candidate root it came from — here the stem's own prefab
        Assert.Equal("TestySSR01", model.PrimaryRoot);
        Assert.All(model.Parts, p => Assert.Equal("TestySSR01", p.SourceRoot));
    }

    // named entry point for the live rig read, which the route above already covers
    [Fact]
    public void PrefabModel_ReadsTheRigSkeleton() => PrefabModel_GroupsByToken_PicksLod0_PreservesMaterialOrderAndPlaceholder();

    // ---- route 1b: a rig name collision degrades the SKELETON ONLY ----
    // The by-name exclusion can't survive a bone carrying the container root's name. The skeleton is one
    // display node, so the subject keeps every part, material and texture affordance; the collision is a
    // loud Problems sentence, not a lost model.
    [Fact]
    public void PrefabModel_RigNameCollision_LosesTheSkeletonOnly()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>(),
            // the second bone carries the container root's name, so the root exclusion matches TWO Transforms
            bones: new[] { ("Bip001", -1), ("TestySSR01", 0) });

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");   // must not throw

        // the parts survive whole — this is the regression that matters
        Assert.Equal(new[] { "body", "cloth1" }, model.Parts.Select(p => p.Token).ToArray());
        Assert.Equal("c_TestySSR01_slg_body_lod0", model.Parts.Single(p => p.Token == "body").SlotName);
        Assert.All(model.Parts, p => Assert.Null(p.Problem));

        Assert.Null(model.Skeleton);   // only the display-only rig is lost...
        Assert.Empty(model.Problems);  // ...and the loss must not read as a curtailed part list
        var problem = model.SkeletonProblem;
        Assert.NotNull(problem);
        Assert.Contains("'TestySSR01'", problem);
        Assert.Contains("two Transforms", problem);
        Assert.Matches(@"path ids \d+ and \d+", problem);   // both colliding Transforms are named
    }

    // ---- route 2: Fight/Dorm variant slot naming yields a DISTINCT part token ----
    [Fact]
    public void PrefabModel_VariantSlot_IsADistinctPart()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        Assert.Contains(model.Parts, p => p.Token == "cloth1");
        Assert.Contains(model.Parts, p => p.Token == "cloth1_Fight");   // the infixed-LOD variant stays distinct
    }

    // ---- route 2b: a part whose lod0 tier ships ONLY under a variant name. MeshName.Part derives TWO
    // tokens from those slots, but there is one garment: the bare token is not a part, its slot is a LOD tier
    // of the variant. Pick roster and workbench must agree. ----
    [Fact]
    public void PrefabModel_VariantOnlyLod0_BareTokenFoldsIntoTheVariantPart()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        // the phantom is gone: 'cloth2' owns no mesh of its own, so it is not a part
        Assert.DoesNotContain(model.Parts, p => p.Token == "cloth2");
        var fight = Assert.Single(model.Parts, p => p.Token == "cloth2_Fight");
        Assert.Equal("c_TestySSR01_slg_cloth2_lod0_Fight", fight.SlotName);   // the real lod0 slot, not the tier
        Assert.Equal(902L, fight.SiblingTiers!.Single().MeshPathId);          // …_lod1 rides along as its tier
        Assert.Equal("c_TestySSR01_slg_cloth2_lod1", fight.SiblingTiers!.Single().SlotName);

        // the Pick roster reads the SAME rule — no phantom token there either
        var scope = SubjectScope.Build(cat, deobfuscate, outfit);
        Assert.Equal(new[] { "cloth2_Fight" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit).ToArray());
    }

    // ---- the healthy shape: BOTH a plain lod0 and a variant lod0 = two REAL garments, each keeping its
    // own tiers. Nothing folds. ----
    [Fact]
    public void PrefabModel_BothLod0Tiers_StayTwoPartsWithTheirOwnTiers()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 902L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        var plain = Assert.Single(model.Parts, p => p.Token == "cloth1");
        Assert.Equal("c_TestySSR01_slg_cloth1_lod0", plain.SlotName);
        Assert.Equal("c_TestySSR01_slg_cloth1_lod1", plain.SiblingTiers!.Single().SlotName);   // its OWN tier

        var fight = Assert.Single(model.Parts, p => p.Token == "cloth1_Fight");
        Assert.Equal("c_TestySSR01_slg_cloth1_lod0_Fight", fight.SlotName);
        Assert.Empty(fight.SiblingTiers!);                                   // the lod1 belongs to the base part

        var scope = SubjectScope.Build(cat, deobfuscate, outfit);
        Assert.Equal(new[] { "cloth1", "cloth1_Fight" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit).ToArray());
    }

    // ---- the fold's ambiguity clause: TWO variant siblings own a lod0, so which garment the bare tier
    // belongs to is unknowable from the names. It stays a part of its own rather than being attached to
    // whichever variant the enumeration reached first. ----
    [Fact]
    public void PrefabModel_TwoVariantsOwnLod0_BareTokenDoesNotFold()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod0_Dorm", new[] { (0, 0L) }, Mesh: (0, 902L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        // the bare tier is its own part, carrying its own mesh — not folded into either variant
        var bare = Assert.Single(model.Parts, p => p.Token == "cloth2");
        Assert.Equal("c_TestySSR01_slg_cloth2_lod1", bare.SlotName);
        Assert.Equal(903L, bare.MeshPathId);
        // and neither variant claimed it as a tier
        foreach (var v in new[] { "cloth2_Fight", "cloth2_Dorm" })
            Assert.Empty(Assert.Single(model.Parts, p => p.Token == v).SiblingTiers!);

        var scope = SubjectScope.Build(cat, deobfuscate, outfit);
        Assert.Equal(new[] { "cloth2", "cloth2_Dorm", "cloth2_Fight" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    // ---- the fold's lod0 condition: a bare token folds ONLY into a variant sibling that owns a lod0. With
    // no such sibling there is no part for its slots to be tiers OF, and both tokens stay parts. ----
    [Fact]
    public void PrefabModel_NoVariantSiblingOwnsLod0_NothingFolds()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1_Fight", new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        var bare = Assert.Single(model.Parts, p => p.Token == "cloth2");
        Assert.Equal(901L, bare.MeshPathId);
        Assert.Empty(bare.SiblingTiers!);
        var fight = Assert.Single(model.Parts, p => p.Token == "cloth2_Fight");
        Assert.Equal(902L, fight.MeshPathId);
        Assert.Empty(fight.SiblingTiers!);
    }

    // ---- the owner-pick order: the fold pools ALL candidates, so a variant's lod0 can sit in a LATER
    // candidate than the bare tier it absorbs. The part is owned by whoever holds the lod0 (candidate
    // priority only breaks ties between lod0 owners), and the folded tier is gathered, not dropped. ----
    [Fact]
    public void PrefabModel_VariantLod0InALaterCandidate_KeepsItsMeshAndItsFoldedTier()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // candidate 0 (the stem's own prefab) carries ONLY the bare LOD tier …
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 900L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });
        // … while the sibling root holds the garment's only lod0, under the variant name
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR0101_slg_skin_model",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 901L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "sibling.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.DoesNotContain(model.Parts, p => p.Token == "cloth2");           // still no phantom
        var fight = Assert.Single(model.Parts, p => p.Token == "cloth2_Fight");
        Assert.Equal("c_TestySSR01_slg_cloth2_lod0_Fight", fight.SlotName);     // the lod0, from the LATER candidate
        Assert.Equal(901L, fight.MeshPathId);                                   // its own mesh, not the tier's
        Assert.Equal("c_TestySSR0101_slg_skin_model", fight.SourceRoot);
        var tier = Assert.Single(fight.SiblingTiers!);                          // the tier from the OTHER candidate
        Assert.Equal("c_TestySSR01_slg_cloth2_lod1", tier.SlotName);
        Assert.Equal(903L, tier.MeshPathId);
    }

    // ---- the tier gather's precedence: a tier SLOT NAME can repeat across candidates, and the copy that
    // belongs to the part is the OWNING candidate's, not the highest-priority one's. Candidate order first
    // would hand the part a tier mesh from a prefab it isn't made of, and ModBuilder ships that mesh. ----
    [Fact]
    public void PrefabModel_TierNameInTwoCandidates_TakesTheOwningCandidatesMesh()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // candidate 0 (the stem's own prefab) carries a lod1 under the same name …
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 900L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });
        // … while the sibling root holds the garment's only lod0 AND its own copy of that lod1
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR0101_slg_skin_model",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod0_Fight", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth2_lod1", new[] { (0, 0L) }, Mesh: (0, 904L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "sibling.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        var fight = Assert.Single(model.Parts, p => p.Token == "cloth2_Fight");
        Assert.Equal(901L, fight.MeshPathId);
        var tier = Assert.Single(fight.SiblingTiers!);              // the repeated name lands once
        Assert.Equal("c_TestySSR01_slg_cloth2_lod1", tier.SlotName);
        Assert.Equal(904L, tier.MeshPathId);                        // the OWNER's copy, not candidate 0's
        Assert.Equal("sibling.bundle", tier.MeshBundle);
    }

    // ---- the tier gather's mesh-identity gate: a filler slot (no recipe row, no serialized mesh) parses to
    // the SAME bare token on a part-less subject, so a token-only gate hands the part a tier with no mesh.
    // Every tier must carry one identity or the other, or the mod build fails on it. ----
    [Fact]
    public void PrefabModel_FillerSlotSharingThePartlessToken_IsNotATier()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // the part-less summon shape: the token parses EMPTY and falls back to the part-less token
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "Blob",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_Blob_slg_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());
        // a sibling root carrying an EMPTY placeholder tier: same token, no recipe row, no serialized mesh
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_Blob_slg_skin_model",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_Blob_slg_lod1", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("Blob", "prefab.bundle", new[] { "sibling.bundle" });
        var outfit = new Outfit(0, "Blob", OutfitKind.Other) { MeshPrefixOverride = "c_Blob_slg" };
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Blob");

        var only = Assert.Single(model.Parts);
        Assert.Equal(SubjectModelBuilder.PartlessToken, only.Token);
        Assert.DoesNotContain(only.SiblingTiers!, t => t.SlotName == "c_Blob_slg_lod1");
        // what the build requires of every tier it fans out: one identity or the other, never neither
        Assert.All(only.SiblingTiers!, t =>
            Assert.True(t.MeshAddress.Length > 0 || (t.MeshBundle is not null && t.MeshPathId != 0)));
    }

    // ---- route 3: external-CAB material resolves; a missing CAB / unreadable bundle → Problems, not a throw ----
    [Fact]
    public void PrefabModel_ExternalMaterial_ResolvesOrSurfacesAProblem()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('b', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_a_lod0", new[] { (1, 21L) }, Mesh: (0, 901L)),   // CAB present → resolves
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_b_lod0", new[] { (2, 21L) }, Mesh: (0, 902L)),   // CAB absent → problem
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_c_lod0", new[] { (3, 21L) }, Mesh: (0, 903L)),   // CAB present but deobfuscate nulled
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-A", "CAB-missing", "CAB-C" });

        const string matALogical = "aaaa1111aaaa1111aaaa1111aaaa1111.bundle";
        const string matCLogical = "cccc3333cccc3333cccc3333cccc3333.bundle";
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('c', 32) + ".bundle"),
            matALogical, materialName: "M_a", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_a_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 0xFF)), cabName: "CAB-A");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('d', 32) + ".bundle"),
            matCLogical, materialName: "M_c", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_c_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 4, 5, 6, 0xFF)), cabName: "CAB-C");
        // CAB-missing is never built — BundleForCab returns null for it.

        var fixtureDeobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { matALogical, matCLogical });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        // pretend matC's bundle is unreadable (the matDec-null branch); everything else is real
        Func<string, byte[]?> deobfuscate = l => l == matCLogical ? null : fixtureDeobfuscate(l);

        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");   // must not throw

        var a = model.Parts.Single(p => p.Token == "a").Materials.Single();
        Assert.Equal("M_a", a.Name);
        Assert.Null(a.Problem);
        Assert.Single(a.Maps);

        var b = model.Parts.Single(p => p.Token == "b").Materials.Single();
        Assert.NotNull(b.Problem);
        Assert.Contains("CAB-missing", b.Problem);

        var c = model.Parts.Single(p => p.Token == "c").Materials.Single();
        Assert.NotNull(c.Problem);

        Assert.True(model.Problems.Count >= 2);   // both failures rolled up to the subject
    }

    [Fact]
    public void PrefabModel_MixedSlot_FailedMaterialCarriesProblem_PlaceholderDoesNot_ResolvedMapsIntact()
    {
        // ONE renderer slot binding three materials in order: resolved, an empty 0:0 placeholder, and a
        // FAILED reference. The failed slot must carry a Problem (never blank like the placeholder), the
        // placeholder stays a clean IsPlaceholder, and the resolved slot's maps survive. Order is preserved.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('9', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth_lod0", new[] { (1, 21L), (0, 0L), (2, 99L) }, Mesh: (0, 901L)),
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-ok", "CAB-absent" });   // CAB-absent referenced but no bundle provides it
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('8', 32) + ".bundle"),
            "okmat11111okmat11111okmat11111a.bundle", materialName: "M_cloth", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)), cabName: "CAB-ok");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "okmat11111okmat11111okmat11111a.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        var cloth = model.Parts.Single(p => p.Token == "cloth");
        Assert.Equal(3, cloth.Materials.Count);                  // slot order preserved
        Assert.Equal("M_cloth", cloth.Materials[0].Name);        // resolved
        Assert.Null(cloth.Materials[0].Problem);
        Assert.Single(cloth.Materials[0].Maps);                  // its maps intact
        Assert.True(cloth.Materials[1].IsPlaceholder);           // empty 0:0
        Assert.Null(cloth.Materials[1].Problem);                 // a placeholder is NOT a failure
        Assert.False(cloth.Materials[2].IsPlaceholder);          // the failed reference is not a placeholder
        Assert.NotNull(cloth.Materials[2].Problem);              // it carries a loud Problem
        Assert.NotEmpty(model.Problems);                         // rolled up to the subject
    }

    // ---- route 4: no assembly prefab → a part-less model + a loud Problems note; never a throw, never a
    // name-guessed part list ----
    [Fact]
    public void NoPrefab_YieldsPartlessModel_WithALoudProblem()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('e', 32) + ".bundle"),
            "c_Lonely_slg_cloth_lod0", new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 },
            "mesh.bundle");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = CatalogIndex.ForTest(Array.Empty<(string, string)>());   // no formula hit for the stem
        var outfit = new Outfit(1, "Lonely", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Lonely");

        Assert.Empty(model.Parts);                             // no prefab ⇒ no parts — never name-derived
        Assert.Null(model.Skeleton);
        Assert.Equal("", model.PrimaryRoot);
        Assert.Contains(model.Problems, m => m.Contains("No assembly prefab"));
    }

    // ---- route 5: the smr-body prefab (EMPTY recipe, slots reference meshes directly) — parts carry the
    // resolved bundle+path-id identity, tiers collapse with per-tier ids, a mesh CAB nothing in scope
    // provides is a LOUD per-part problem, and an identity-less slot is correctly absent ----
    [Fact]
    public void EnemyPrefab_EmptyRecipe_BuildsPartsFromSlots()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('f', 32) + ".bundle"),
            "prefab.bundle", rootName: "EnemyX",
            slots: new[]
            {
                // one part, two LOD tiers — local serialized mesh refs (the ELID/CF corpus shape)
                new WorkbenchPrefab.SlotSpec("c_EnemyX_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_EnemyX_slg_body_lod1", new[] { (0, 0L) }, Mesh: (0, 902L)),
                // a mesh reference into a CAB nothing in scope provides → a loud per-part problem
                new WorkbenchPrefab.SlotSpec("c_EnemyX_slg_arm_lod0", new[] { (0, 0L) }, Mesh: (1, 777L)),
                // the combat HP-bar placeholder: no recipe row, no serialized mesh → correctly absent
                new WorkbenchPrefab.SlotSpec("HPMesh", new[] { (0, 0L) }),
            },
            recipe: Array.Empty<(string, string)>(),   // smr-body: no RoleMeshRes recipe entries
            externalCabs: new[] { "CAB-gone" },
            bones: new[] { ("Bip001", -1) });

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("EnemyX", "prefab.bundle", contextRoot: "Character/Enemy");
        // the enemy roster's SHORT prefix rule (GameDatabase.ReadEnemyRoster)
        var outfit = new Outfit(0, "EnemyX", OutfitKind.Other) { MeshPrefixOverride = "c_EnemyX_" };
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "EnemyX");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        Assert.Equal(new[] { "slg_arm", "slg_body" }, model.Parts.Select(p => p.Token).ToArray());

        var body = model.Parts.Single(p => p.Token == "slg_body");
        Assert.Equal("", body.MeshAddress);                    // no recipe — the identity is the mesh ref
        Assert.Equal("prefab.bundle", body.MeshBundle);        // local ref → the prefab's own bundle
        Assert.Equal(901L, body.MeshPathId);
        Assert.Null(body.Problem);
        var tier = Assert.Single(body.SiblingTiers!);          // lod1 collapsed into the part
        Assert.Equal("c_EnemyX_slg_body_lod1", tier.SlotName);
        Assert.Equal("prefab.bundle", tier.MeshBundle);
        Assert.Equal(902L, tier.MeshPathId);
        var rp = body.ToRecipePart();
        Assert.False(rp.IsRecipeBacked);
        Assert.True(rp.IsSmrBacked);                           // the exporter's smr route engages

        var arm = model.Parts.Single(p => p.Token == "slg_arm");
        Assert.NotNull(arm.Problem);                           // the missing mesh CAB is loud, not blank
        Assert.Contains("CAB-gone", arm.Problem);
        Assert.Contains(model.Problems, m => m.Contains("CAB-gone"));   // and rolled up to the subject

        Assert.DoesNotContain(model.Parts, p => p.Token.Contains("HPMesh"));   // filler never a part
    }

    // ---- parts SPLIT across the stem prefab + a stem-sibling root — merged, sibling recorded ----
    [Fact]
    public void PrefabModel_MergesPartsAcrossCandidatePrefabs()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // the stem's own prefab: the base cloth1 part (external material CAB-base)
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (1, 21L) }, Mesh: (0, 901L)) },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-base" });
        // a stem-SIBLING root (c_<stem>…_slg_skin_model) carrying the Fight coat the stem prefab doesn't
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR01x_slg_skin_model",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0_Fight", new[] { (1, 22L) }, Mesh: (0, 902L)) },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-fight" });

        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "matBase.bundle", materialName: "M_base", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 0xFF)), cabName: "CAB-base");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('4', 32) + ".bundle"),
            "matFight.bundle", materialName: "M_fight", materialPathId: 22,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_Fight_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 4, 5, 6, 0xFF)), cabName: "CAB-fight");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle",
            new[] { "sibling.bundle", "matBase.bundle", "matFight.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        Assert.Equal("TestySSR01", model.PrimaryRoot);

        var baseCloth = model.Parts.Single(p => p.Token == "cloth1");
        Assert.Equal("TestySSR01", baseCloth.SourceRoot);           // came from the stem's own prefab

        var fight = model.Parts.Single(p => p.Token == "cloth1_Fight");
        Assert.Equal("c_TestySSR01x_slg_skin_model", fight.SourceRoot);   // came from the sibling root
        var fightMat = fight.Materials.Single();
        Assert.Equal("M_fight", fightMat.Name);                    // its material resolved (sibling's slot)
        Assert.Equal("c_TestySSR01_slg_cloth1_Fight_d", fightMat.Maps.Single().TextureName);
    }

    // ---- a token in BOTH candidates → the priority candidate's slot wins, exactly once ----
    [Fact]
    public void PrefabModel_SharedToken_PriorityCandidateWinsOnce()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (1, 21L) }, Mesh: (0, 901L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: new[] { "CAB-primary" });
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR01x_slg_skin_model",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth1_lod0", new[] { (1, 31L) }, Mesh: (0, 902L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: new[] { "CAB-sibling" });

        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "matP.bundle", materialName: "M_primary", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 1, 1, 1, 0xFF)), cabName: "CAB-primary");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('4', 32) + ".bundle"),
            "matS.bundle", materialName: "M_sibling", materialPathId: 31,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 2, 2, 2, 0xFF)), cabName: "CAB-sibling");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle",
            new[] { "sibling.bundle", "matP.bundle", "matS.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        var cloth1 = Assert.Single(model.Parts, p => p.Token == "cloth1");   // exactly once, not duplicated
        Assert.Equal("TestySSR01", cloth1.SourceRoot);                       // the priority candidate won
        Assert.Equal("M_primary", cloth1.Materials.Single().Name);           // and supplied the material
    }

    // ---- a recipe entry naming a slot that has NO renderer slot → a loud part row, not a throw ----
    [Fact]
    public void PrefabModel_RecipeOrphan_IsLoudNotDropped()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }) },
            recipe: new[]
            {
                ("c_TestySSR01_slg_body_lod0", "Assets/X/body.mesh"),
                ("c_TestySSR01_slg_ghost_lod0", "Assets/X/ghost.mesh"),   // no slot carries this name
            },
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");   // no throw

        var ghost = model.Parts.Single(p => p.Token == "ghost");
        Assert.Empty(ghost.Materials);
        Assert.NotNull(ghost.Problem);
        Assert.Contains(model.Problems, m => m.Contains("no renderer slot"));
    }

    // ---- a slot whose part-token parse is EMPTY falls back to the part-less token (summon), never vanishes ----
    [Fact]
    public void PrefabModel_PartlessSlot_FallsBackToThePartlessToken()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // MeshPrefixOverride "c_Blob_slg" (no trailing _) makes the slot name parse to an EMPTY token —
        // exactly the part-less summon shape the fallback must catch.
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "Blob",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_Blob_slg_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("Blob", "prefab.bundle");
        var outfit = new Outfit(0, "Blob", OutfitKind.Other) { MeshPrefixOverride = "c_Blob_slg" };
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Blob");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        var only = Assert.Single(model.Parts);
        Assert.Equal(SubjectModelBuilder.PartlessToken, only.Token);   // empty parse → the part-less token
        Assert.Equal("c_Blob_slg_lod0", only.SlotName);        // the originating slot is the representative
    }

    // ---- slot-level ownership: an alt-stem SIBLING root contributes only its subject-prefixed slots, and
    // its own foreign-prefixed ones are dropped SILENTLY (no Problems entry) — it rides in through the
    // dependency closure, but only the base-prefixed slot it carries is ours. ----
    [Fact]
    public void PrefabModel_SiblingRoot_KeepsSubjectPrefixedSlot_DropsForeignSilently()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // the exact-stem prefab, with a rig so the skeleton resolves and the only thing that can land in
        // Problems is a slot-ownership failure
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });
        // the ALT's sibling root: a base prop under the BASE prefix (ours) + the alt's own foreign-prefixed
        // cloth1 (a different subject's part)
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR0101_slg_skin_model",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_prop_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),      // base prefix → ours
                new WorkbenchPrefab.SlotSpec("c_TestySSR0101_slg_cloth1_lod0", new[] { (0, 0L) }, Mesh: (0, 903L)),  // alt prefix → foreign
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "sibling.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        var prop = model.Parts.Single(p => p.Token == "prop");
        Assert.Equal("c_TestySSR0101_slg_skin_model", prop.SourceRoot);   // owned by the sibling, kept by prefix
        Assert.DoesNotContain(model.Parts, p => p.Token == "cloth1");      // the foreign slot never became a part
        // dropping a foreign-prefixed sibling slot is CORRECT, not a failure — it is silent
        Assert.DoesNotContain(model.Problems, m => m.Contains("cloth1"));
        Assert.DoesNotContain(model.Problems, m => m.Contains("TestySSR0101_slg_cloth1"));
        Assert.Empty(model.Problems);
    }

    // ---- ownership BY ROOT: a foreign-looking slot in the EXACT-STEM root is still kept — that prefab
    // owns all its slots whatever they're named. ----
    [Fact]
    public void PrefabModel_ExactStemRoot_KeepsOddlyNamedSlot()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // a slot NOT under its own mesh prefix — a real pattern (a borrowed prop mesh). Ownership is by
        // root, so it must still appear.
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_NylaSSR01_slg_bomb_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),   // foreign-looking name
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle");
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);
        var borrowed = model.Parts.Single(p => p.SlotName == "c_NylaSSR01_slg_bomb_lod0");
        Assert.Equal("TestySSR01", borrowed.SourceRoot);   // kept by root ownership, not by prefix
        // …and it reads as the PART it is: the subject's prefix can't strip a name from another family,
        // so the token comes from the prefix the slot itself carries
        Assert.Equal("bomb", borrowed.Token);
        Assert.Empty(model.Problems);
    }

    // ---- a foreign-prefixed slot's short token, and the collision that declines it ----
    [Fact]
    public void PrefabModel_ForeignPrefixedSlot_ReadsAsItsOwnPartToken()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // the shipped shape: a skin whose prefab carries a shared mesh named for ANOTHER skin's family,
        // plus a weapon family with no context segment of its own
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "Testy02",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_Testy02_dorm_hair_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_Testy_dorm_face_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),
                new WorkbenchPrefab.SlotSpec("c_Testy_dorm_face_lod1", new[] { (0, 0L) }, Mesh: (0, 903L)),
                new WorkbenchPrefab.SlotSpec("cw_Testy_WL_lod0", new[] { (0, 0L) }, Mesh: (0, 904L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("Testy02", "prefab.bundle");
        var outfit = new Outfit(-1, "Testy02", OutfitKind.Other) { MeshPrefixOverride = "c_Testy02_dorm_" };
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(new[] { "face", "hair", "WL" }.OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
            model.Parts.Select(p => p.Token));
        Assert.Empty(model.Problems);

        // the foreign part's other tier rides along on it rather than becoming a part of its own
        var face = model.Parts.Single(p => p.Token == "face");
        Assert.Equal("c_Testy_dorm_face_lod0", face.SlotName);
        Assert.Equal("c_Testy_dorm_face_lod1", Assert.Single(face.SiblingTiers!).SlotName);

        // and the Pick roster reads the same tokens
        var scope = SubjectScope.Build(cat, deobfuscate, outfit);
        Assert.Equal(new[] { "face", "hair", "WL" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Fact]
    public void PrefabModel_ForeignShortTokenCollidingWithAnOwnPart_KeepsItsFullName()
    {
        // Two different meshes merging into one part would silently lose one of them. The colliding slot
        // lists under its full name instead - a complete answer, so it records no problem: real subjects
        // carry these collisions (shared cross-prefix parts), and a recorded problem would read them as
        // failed reads everywhere problems mean that.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "Testy02",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_Testy02_dorm_face_lod0", new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec("c_Testy_dorm_face_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>(),
            bones: new[] { ("Bip001", -1) });

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var outfit = new Outfit(-1, "Testy02", OutfitKind.Other) { MeshPrefixOverride = "c_Testy02_dorm_" };
        var model = SubjectModelBuilder.Build(Catalog("Testy02", "prefab.bundle"), deobfuscate, outfit, "Testy");

        Assert.Equal(new[] { "c_Testy_dorm_face", "face" }, model.Parts.Select(p => p.Token).ToArray());
        Assert.Empty(model.Problems);
    }

    // ---- the orphan-recipe sweep is scoped the same way: a sibling root's recipe row for its OWN foreign
    // slot produces NO orphan row, while a subject-prefixed row with no slot anywhere still does. ----
    [Fact]
    public void PrefabModel_OrphanRecipe_ScopedBySlotOwnership()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // exact-stem root: a real body slot + a subject-prefixed recipe row (`ghost`) that names NO slot → orphan
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }) },
            recipe: new[]
            {
                ("c_TestySSR01_slg_body_lod0", "Assets/X/body.mesh"),
                ("c_TestySSR01_slg_ghost_lod0", "Assets/X/ghost.mesh"),   // subject-prefixed, no slot → loud orphan
            },
            externalCabs: Array.Empty<string>());
        // sibling root: a recipe row for its OWN foreign slot that names no renderer slot either — NOT our
        // orphan, it describes another subject's part
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "sibling.bundle", rootName: "c_TestySSR0101_slg_skin_model",
            slots: Array.Empty<WorkbenchPrefab.SlotSpec>(),
            recipe: new[] { ("c_TestySSR0101_slg_phantom_lod0", "Assets/X/phantom.mesh") },
            externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "sibling.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");   // no throw

        var ghost = model.Parts.Single(p => p.Token == "ghost");
        Assert.NotNull(ghost.Problem);
        Assert.Contains(model.Problems, m => m.Contains("no renderer slot"));

        // the sibling's foreign phantom row is neither a part nor an orphan problem
        Assert.DoesNotContain(model.Parts, p => p.Token.Contains("phantom", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(model.Problems, m => m.Contains("phantom"));
    }

    // ---- a part whose materials resolve CLEANLY but bind ZERO texture maps gets a loud part-level Problem
    // (it drives the tree ⚠ badge); a part that binds a map does not. ----
    [Fact]
    public void PrefabModel_PartBindingNoTextureMaps_IsFlagged_MappedPartIsNot()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_mapped_lod0", new[] { (1, 21L) }, Mesh: (0, 901L)),   // material WITH a _BaseMap
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_bare_lod0",   new[] { (2, 31L) }, Mesh: (0, 902L)),   // material, NO tex envs
            },
            recipe: Array.Empty<(string, string)>(),
            externalCabs: new[] { "CAB-mapped", "CAB-bare" });

        // the mapped part's material binds a _BaseMap texture
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "matMapped.bundle", materialName: "M_mapped", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_mapped_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 0xFF)), cabName: "CAB-mapped");
        // the bare part's material resolves cleanly but binds ZERO texture maps (empty m_TexEnvs, no texture)
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "matBare.bundle", materialName: "M_bare", materialPathId: 31,
            texEnvs: Array.Empty<(string, int, long)>(), externalCabs: Array.Empty<string>(),
            cabName: "CAB-bare");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var cat = Catalog("TestySSR01", "prefab.bundle", new[] { "matMapped.bundle", "matBare.bundle" });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var model = SubjectModelBuilder.Build(cat, deobfuscate, outfit, "Testy");

        Assert.Equal(SubjectSource.Prefab, model.Source);

        var mapped = model.Parts.Single(p => p.Token == "mapped");
        Assert.Equal("M_mapped", mapped.Materials.Single().Name);
        Assert.Single(mapped.Materials.Single().Maps);
        Assert.Null(mapped.Problem);                        // binds a map → NOT flagged

        var bare = model.Parts.Single(p => p.Token == "bare");
        Assert.Equal("M_bare", bare.Materials.Single().Name);   // material resolved cleanly
        Assert.Empty(bare.Materials.Single().Maps);             // but binds no maps
        Assert.Null(bare.Materials.Single().Problem);           // not a resolution failure
        Assert.NotNull(bare.Problem);                            // the part-level "no textures" flag
        Assert.Contains("no texture maps", bare.Problem);
    }
}
