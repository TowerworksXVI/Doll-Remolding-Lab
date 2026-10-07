using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The recipe-versus-serialized precedence rule and the geometry-backed confirm gate, over a synthetic
/// prefab in the support-team shape: a curated addressable route, a RoleMeshRes recipe whose addresses may
/// or may not resolve, and renderer slots that may or may not carry a serialized mesh. Each test names the
/// slot-geometry case it runs.
/// </summary>
public class SupportTeamGeometryTests
{
    private const string Root = "TEAM01_MemberA";
    private const string PrefabAddress = "Assets/ConfigPrefab/Character/SupportTeam/TEAM01/TEAM01_MemberA.prefab";
    private const string Prefix = "c_TEAM01_MemberA_slg_";
    private const string Bundle = "team.bundle";

    private static string Address(string slot) => $"Assets/ArtsResource/SupportTeam/TEAM01/Models/{slot}.mesh";

    private static Outfit MemberOutfit() =>
        new(-9_000_010_010L, Root, OutfitKind.Other)
        {
            MeshPrefixOverride = Prefix,
            Route = SubjectRoute.Addressable(PrefabAddress, Root),
            DisplayName = "A",
        };

    /// <summary>A catalog that resolves the member prefab through its route, plus whichever mesh addresses
    /// the test wants to resolve (to a bundle nothing reads — the builder resolves, it does not open).</summary>
    private static CatalogIndex Catalog(params string[] resolvingMeshAddresses) =>
        CatalogIndex.ForTest(
            new[] { (PrefabAddress, Bundle) }.Concat(resolvingMeshAddresses.Select(a => (a, "mesh.bundle"))),
            new[] { (PrefabAddress, new[] { Bundle }) });

    private static (Func<string, byte[]?> Deobfuscate, string Abw) Corpus(TempGame g, WorkbenchPrefab.SlotSpec[] slots,
        (string SlotPath, string MeshAddress)[] recipe)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('7', 32) + ".bundle"), Bundle, Root,
            slots, recipe, externalCabs: Array.Empty<string>());
        return (FixtureCrawl.DeobfuscateOver(abw), abw);
    }

    // ---- case 1: the recipe address RESOLVES — recipe-backed, the address wins, serialized mesh or not ----
    [Fact]
    public void ResolvingAddress_StaysRecipeBacked_EvenBesideASerializedMesh()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            new[] { (slot, Address(slot)) });

        var model = SubjectModelBuilder.Build(Catalog(Address(slot)), deobfuscate, MemberOutfit(), "TEAM01");

        var part = Assert.Single(model.Parts);
        Assert.Equal("upper", part.Token);
        Assert.Equal(Address(slot), part.MeshAddress);
        Assert.Null(part.MeshBundle);
        Assert.Equal(0L, part.MeshPathId);
        Assert.True(part.ToRecipePart().IsRecipeBacked);
        Assert.False(part.ToRecipePart().IsSmrBacked);
    }

    // ---- case 2: the recipe address is DEAD and the slot carries a serialized mesh — smr-backed ----
    [Fact]
    public void DeadAddress_WithSerializedMesh_BecomesSmrBacked()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            new[] { (slot, Address(slot)) });

        var model = SubjectModelBuilder.Build(Catalog(), deobfuscate, MemberOutfit(), "TEAM01");

        var part = Assert.Single(model.Parts);
        Assert.Equal("", part.MeshAddress);
        Assert.Equal(Bundle, part.MeshBundle);
        Assert.Equal(901L, part.MeshPathId);
        Assert.Null(part.Problem);
        Assert.False(part.ToRecipePart().IsRecipeBacked);
        Assert.True(part.ToRecipePart().IsSmrBacked);
        Assert.DoesNotContain(model.Problems, p => p.Contains("upper", StringComparison.OrdinalIgnoreCase));
    }

    // ---- case 3: the recipe address is DEAD and the slot is EMPTY — the recipe's answer stands as it is ----
    [Fact]
    public void DeadAddress_WithNoMesh_KeepsTheDeadAddress()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }) },
            new[] { (slot, Address(slot)) });

        var model = SubjectModelBuilder.Build(Catalog(), deobfuscate, MemberOutfit(), "TEAM01");

        var part = Assert.Single(model.Parts);
        Assert.Equal(Address(slot), part.MeshAddress);
        Assert.Null(part.MeshBundle);
    }

    // ---- tiers: the same three cases, and a tier backed neither way is not a tier ----
    [Fact]
    public void SiblingTiers_FollowTheSameRule_AndAnUnbackedTierIsDropped()
    {
        using var g = new TempGame();
        string lod0 = Prefix + "upper_lod0", lod1 = Prefix + "upper_lod1";
        string hair0 = Prefix + "hair_lod0", hair1 = Prefix + "hair_lod1";
        var (deobfuscate, _) = Corpus(g,
            new[]
            {
                new WorkbenchPrefab.SlotSpec(lod0, new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec(lod1, new[] { (0, 0L) }),                    // dead address, no mesh
                new WorkbenchPrefab.SlotSpec(hair0, new[] { (0, 0L) }, Mesh: (0, 903L)),
                new WorkbenchPrefab.SlotSpec(hair1, new[] { (0, 0L) }, Mesh: (0, 904L)), // dead address, serialized
            },
            new[] { (lod0, Address(lod0)), (lod1, Address(lod1)), (hair0, Address(hair0)), (hair1, Address(hair1)) });

        var model = SubjectModelBuilder.Build(Catalog(), deobfuscate, MemberOutfit(), "TEAM01");

        var upper = model.Parts.Single(p => p.Token == "upper");
        Assert.Empty(upper.SiblingTiers!);          // the install ships no lod1 for this part: no tier, no problem
        Assert.Null(upper.Problem);

        var hair = model.Parts.Single(p => p.Token == "hair");
        var tier = Assert.Single(hair.SiblingTiers!);
        Assert.Equal(hair1, tier.SlotName);
        Assert.Equal("", tier.MeshAddress);
        Assert.Equal(Bundle, tier.MeshBundle);
        Assert.Equal(904L, tier.MeshPathId);
    }

    [Fact]
    public void SiblingTier_WithResolvingAddress_StaysRecipeBacked()
    {
        using var g = new TempGame();
        string lod0 = Prefix + "upper_lod0", lod1 = Prefix + "upper_lod1";
        var (deobfuscate, _) = Corpus(g,
            new[]
            {
                new WorkbenchPrefab.SlotSpec(lod0, new[] { (0, 0L) }, Mesh: (0, 901L)),
                new WorkbenchPrefab.SlotSpec(lod1, new[] { (0, 0L) }, Mesh: (0, 902L)),
            },
            new[] { (lod0, Address(lod0)), (lod1, Address(lod1)) });

        var model = SubjectModelBuilder.Build(Catalog(Address(lod0), Address(lod1)), deobfuscate, MemberOutfit(), "TEAM01");

        var upper = Assert.Single(model.Parts);
        var tier = Assert.Single(upper.SiblingTiers!);
        Assert.Equal(Address(lod1), tier.MeshAddress);
        Assert.Null(tier.MeshBundle);
    }

    // ---- the confirm gate: a subject lists iff an owned slot is geometry-backed ----
    [Fact]
    public void ListingGate_RecipeRowsAlone_DoNotConfirm()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }) },   // the hollow shape: rows, no meshes
            new[] { (slot, Address(slot)) });
        var catalog = Catalog();
        var scope = SubjectScope.Build(catalog, deobfuscate, MemberOutfit());

        Assert.NotEmpty(scope.Candidates);   // the prefab parses — it is the geometry that is missing
        Assert.False(SubjectModelBuilder.HasGeometryBackedSlot(scope.Candidates, MemberOutfit(), catalog));
    }

    [Fact]
    public void ListingGate_ASerializedMesh_Confirms()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            new[] { (slot, Address(slot)) });
        var catalog = Catalog();
        var scope = SubjectScope.Build(catalog, deobfuscate, MemberOutfit());

        Assert.True(SubjectModelBuilder.HasGeometryBackedSlot(scope.Candidates, MemberOutfit(), catalog));
    }

    [Fact]
    public void ListingGate_AResolvingAddress_Confirms()
    {
        using var g = new TempGame();
        string slot = Prefix + "upper_lod0";
        var (deobfuscate, _) = Corpus(g,
            new[] { new WorkbenchPrefab.SlotSpec(slot, new[] { (0, 0L) }) },
            new[] { (slot, Address(slot)) });
        var catalog = Catalog(Address(slot));
        var scope = SubjectScope.Build(catalog, deobfuscate, MemberOutfit());

        Assert.True(SubjectModelBuilder.HasGeometryBackedSlot(scope.Candidates, MemberOutfit(), catalog));
    }

    // ---- ownership across container roots: a sibling root's renderers share the file, not the subject ----
    [Fact]
    public void SiblingRootSlots_AreNotThisMembersParts_UnlessTheyCarryItsPrefix()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        string ownFace = Prefix + "face_lod0";
        string siblingFace = "c_TEAM01_MemberB_slg_face_lod0";          // the sibling member's own part
        string ownPrefixedUnderSibling = Prefix + "hair_lod0";           // named for THIS member, hung under the sibling root
        WorkbenchPrefab.Build(Path.Combine(abw, new string('8', 32) + ".bundle"), Bundle, Root,
            slots: new[] { new WorkbenchPrefab.SlotSpec(ownFace, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: new[] { (ownFace, Address(ownFace)) },
            externalCabs: Array.Empty<string>(),
            siblingRoot: ("TEAM01_MemberB", new[]
            {
                new WorkbenchPrefab.SlotSpec(siblingFace, new[] { (0, 0L) }, Mesh: (0, 902L)),
                new WorkbenchPrefab.SlotSpec(ownPrefixedUnderSibling, new[] { (0, 0L) }, Mesh: (0, 903L)),
            }));
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var catalog = Catalog();

        var model = SubjectModelBuilder.Build(catalog, deobfuscate, MemberOutfit(), "TEAM01");
        var scope = SubjectScope.Build(catalog, deobfuscate, MemberOutfit());

        // the sibling's face is the sibling's; the own-prefixed slot is ours wherever it hangs
        Assert.Equal(new[] { "face", "hair" }, model.Parts.Select(p => p.Token).OrderBy(t => t).ToArray());
        Assert.Equal(new[] { "face", "hair" }, SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, MemberOutfit()));
        Assert.Equal(903L, model.Parts.Single(p => p.Token == "hair").MeshPathId);

        // and the reader recorded the placement: the sibling's renderers are in the file, marked outside
        var prefab = scope.Candidates.Single(c => c.Root == Root).Prefab;
        Assert.True(prefab.Slots.Single(s => s.Name == ownFace).InRoot);
        Assert.False(prefab.Slots.Single(s => s.Name == siblingFace).InRoot);
        Assert.False(prefab.Slots.Single(s => s.Name == ownPrefixedUnderSibling).InRoot);
    }

    // ---- a multi-root file whose pinned root has no Transform is refused loudly, never read as a root that
    // owns nothing ----
    [Fact]
    public void MultiRootFile_RootWithoutTransform_IsRefusedLoudly()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        string ownFace = Prefix + "face_lod0";
        WorkbenchPrefab.Build(Path.Combine(abw, new string('9', 32) + ".bundle"), Bundle, Root,
            slots: new[] { new WorkbenchPrefab.SlotSpec(ownFace, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: new[] { (ownFace, Address(ownFace)) },
            externalCabs: Array.Empty<string>(),
            siblingRoot: ("TEAM01_MemberB", new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TEAM01_MemberB_slg_face_lod0", new[] { (0, 0L) }, Mesh: (0, 902L)),
            }),
            rootTransform: false);
        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);

        var scope = SubjectScope.Build(Catalog(), deobfuscate, MemberOutfit());

        Assert.Empty(scope.Candidates);
        Assert.Contains(scope.Problems, p => p.Contains("couldn't be read", StringComparison.Ordinal)
                                             && p.Contains(Root, StringComparison.Ordinal));
        // the one-root shape is untouched by the walk: the same missing Transform is no refusal there
        var single = g.At("single");
        Directory.CreateDirectory(single);
        WorkbenchPrefab.Build(Path.Combine(single, new string('a', 32) + ".bundle"), Bundle, Root,
            slots: new[] { new WorkbenchPrefab.SlotSpec(ownFace, new[] { (0, 0L) }, Mesh: (0, 901L)) },
            recipe: new[] { (ownFace, Address(ownFace)) },
            externalCabs: Array.Empty<string>(),
            rootTransform: false);
        var singleScope = SubjectScope.Build(Catalog(), FixtureCrawl.DeobfuscateOver(single), MemberOutfit());
        Assert.Single(singleScope.Candidates);
    }
}
