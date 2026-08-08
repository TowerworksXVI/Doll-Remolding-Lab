using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <c>PrefabReader</c> against the synthetic assembly-prefab fixture: the recipe, the SMR slots with
/// ordered CAB-resolved material refs, and the not-a-prefab null contract.
/// </summary>
public class PrefabReaderTests
{
    private static byte[] BuildPrefabBytes(string dir)
    {
        var path = Path.Combine(dir, "prefab.bundle");
        SyntheticBundle.BuildPrefab(path, "7777777777777777777777777777777a.bundle",
            rootName: "TestySSR01", slotName: "c_TestySSR01_slg_cloth_lod0",
            recipe: new[]
            {
                ("c_TestySSR01_slg_cloth_lod0", "Assets/X/Models/c_TestySSR01_slg_cloth_lod0.mesh"),
                ("c_TestySSR01_slg_cloth_lod1", "Assets/X/Models/c_TestySSR01_slg_cloth_lod1.mesh"),
            },
            slotMaterials: new[] { (0, 11L), (1, 42L) },   // one local, one external
            externalCabs: new[] { "CAB-ext1" });
        var raw = File.ReadAllBytes(path);
        return BundleSegments.ExtractPlain(raw, BundleSegments.Walk(raw).Segments[0]);
    }

    [Fact]
    public void Read_ParsesRecipeSlotsAndMaterialRefs()
    {
        using var g = new TempGame();
        var prefab = PrefabReader.Read(BuildPrefabBytes(g.Root));

        Assert.NotNull(prefab);
        Assert.Equal("TestySSR01", prefab!.RootName);
        Assert.True(prefab.HasReplaceableModel == false);   // fixture carries no ReplaceableModel

        Assert.Equal(2, prefab.Recipe.Count);
        Assert.Equal("c_TestySSR01_slg_cloth_lod0", prefab.Recipe[0].SlotPath);
        Assert.Equal("Assets/X/Models/c_TestySSR01_slg_cloth_lod0.mesh", prefab.Recipe[0].MeshAddress);

        var slot = Assert.Single(prefab.Slots);
        Assert.Equal("c_TestySSR01_slg_cloth_lod0", slot.Name);
        Assert.False(slot.HasMesh);   // character prefabs ship empty renderer slots
        // this fixture's renderer carries no m_CastShadows at all: an unread flag reads as casting
        Assert.True(slot.CastsShadows);
        Assert.Equal(2, slot.Materials.Count);
        Assert.Null(slot.Materials[0].Cab);                 // local ref: resolve in this bundle
        Assert.Equal(11L, slot.Materials[0].PathId);
        Assert.Equal("CAB-ext1", slot.Materials[1].Cab);    // external ref: resolve via CabToBundle
        Assert.Equal(42L, slot.Materials[1].PathId);

        Assert.Equal(new[] { "CAB-ext1" }, prefab.ExternalCabs);
    }

    [Fact]
    public void Read_MapsCastShadowsOffToANonCastingSlot()
    {
        using var g = new TempGame();
        var path = g.At("shadows.bundle");
        WorkbenchPrefab.Build(path, "prefabShadow.bundle", rootName: "TestySSR01",
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 11L) }, CastShadows: 2),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_hair_lod0", new[] { (0, 12L) }, CastShadows: 0),
                new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_cloth_lod0", new[] { (0, 13L) }),
            },
            recipe: new[]
            {
                ("c_TestySSR01_slg_body_lod0", "Assets/X/body.mesh"),
                ("c_TestySSR01_slg_hair_lod0", "Assets/X/hair.mesh"),
                ("c_TestySSR01_slg_cloth_lod0", "Assets/X/cloth.mesh"),
            },
            externalCabs: Array.Empty<string>());
        var raw = File.ReadAllBytes(path);
        var prefab = PrefabReader.Read(BundleSegments.ExtractPlain(raw, BundleSegments.Walk(raw).Segments[0]));

        Assert.NotNull(prefab);
        bool Casts(string name) =>
            prefab!.Slots.Single(s => s.Name == name).CastsShadows;
        Assert.False(Casts("c_TestySSR01_slg_hair_lod0"));    // 0 = Off, the ONE value that excludes
        Assert.True(Casts("c_TestySSR01_slg_body_lod0"));     // any other value casts
        Assert.True(Casts("c_TestySSR01_slg_cloth_lod0"));    // unset by this slot, written On
    }

    // ---- the dorm visibility components -----------------------------------------------------------

    /// <summary>Three slots and a recipe naming them, with whatever dorm lists the case needs. Every list
    /// names SLOTS, which is what the shipped components point their Transform references at.</summary>
    private static CharacterPrefab BuildWithVisibility(TempGame g, string file,
        WorkbenchPrefab.VisibilityLists visibility)
    {
        var path = g.At(file);
        string[] names =
        {
            "c_TestySSR01_slg_body_lod0", "c_TestySSR01_slg_coat_lod0", "c_TestySSR01_slg_cloth_lod0",
        };
        WorkbenchPrefab.Build(path, "prefabVis.bundle", rootName: "TestySSR01",
            slots: names.Select((n, i) => new WorkbenchPrefab.SlotSpec(n, new[] { (0, 11L + i) })).ToArray(),
            recipe: names.Select(n => (n, $"Assets/X/{n}.mesh")).ToArray(),
            externalCabs: Array.Empty<string>(),
            visibility: visibility);
        var raw = File.ReadAllBytes(path);
        return PrefabReader.Read(BundleSegments.ExtractPlain(raw, BundleSegments.Walk(raw).Segments[0]))
            ?? throw new InvalidOperationException("fixture prefab did not parse");
    }

    [Fact]
    public void Read_MarksOnlyTheNodeTheCoatListNames()
    {
        using var g = new TempGame();
        // the context lists carry the other two: those are already modelled by the nodes' name tails, so
        // naming a node there must NOT demote it
        var prefab = BuildWithVisibility(g, "coat.bundle", new WorkbenchPrefab.VisibilityLists(
            DormNodes: new[] { "c_TestySSR01_slg_body_lod0" },
            FightNodes: new[] { "c_TestySSR01_slg_cloth_lod0" },
            ControlVisibleNodes: new[] { "c_TestySSR01_slg_coat_lod0" }));

        Assert.Equal(VisibilityOverride.CoatList, prefab.VisibilityOf("c_TestySSR01_slg_coat_lod0"));
        Assert.Equal(VisibilityOverride.None, prefab.VisibilityOf("c_TestySSR01_slg_body_lod0"));
        Assert.Equal(VisibilityOverride.None, prefab.VisibilityOf("c_TestySSR01_slg_cloth_lod0"));
    }

    [Fact]
    public void Read_MarksTheDormAndLobbyHiddenNodesApart()
    {
        using var g = new TempGame();
        var prefab = BuildWithVisibility(g, "hide.bundle", new WorkbenchPrefab.VisibilityLists(
            DormHideNodes: new[] { "c_TestySSR01_slg_cloth_lod0" },
            LobbyHideNodes: new[] { "c_TestySSR01_slg_coat_lod0" }));

        // distinct reasons, so a refusal can say which of the game's lists withheld the part
        Assert.Equal(VisibilityOverride.DormHidden, prefab.VisibilityOf("c_TestySSR01_slg_cloth_lod0"));
        Assert.Equal(VisibilityOverride.LobbyHidden, prefab.VisibilityOf("c_TestySSR01_slg_coat_lod0"));
        Assert.Equal(VisibilityOverride.None, prefab.VisibilityOf("c_TestySSR01_slg_body_lod0"));
    }

    [Fact]
    public void Read_IgnoresTheLobbyShowListAndTheSerializedHideFlag()
    {
        using var g = new TempGame();
        // LobbyShowNodes only ever ADDS a draw, and the serialized flag is overwritten at every apply, so
        // a prefab naming every slot in the show list with the flag set demotes nothing at all
        var prefab = BuildWithVisibility(g, "show.bundle", new WorkbenchPrefab.VisibilityLists(
            LobbyShowNodes: new[]
            {
                "c_TestySSR01_slg_body_lod0", "c_TestySSR01_slg_coat_lod0", "c_TestySSR01_slg_cloth_lod0",
            },
            LobbyHideEnable: 1));

        foreach (var slot in prefab.Slots)
            Assert.Equal(VisibilityOverride.None, prefab.VisibilityOf(slot.Name));
    }

    [Fact]
    public void Read_LeavesEveryNodeUnmarkedWhenThePrefabShipsNoDormComponent()
    {
        using var g = new TempGame();
        var prefab = BuildWithVisibility(g, "plain.bundle", default);

        // absence is not evidence: a prefab carrying no lists demotes nothing
        Assert.Null(prefab.VisibilityOverrides);
        foreach (var slot in prefab.Slots)
            Assert.Equal(VisibilityOverride.None, prefab.VisibilityOf(slot.Name));
    }

    [Fact]
    public void Read_GivesANodeTwoListsNameOneStableReason()
    {
        using var g = new TempGame();
        var prefab = BuildWithVisibility(g, "both.bundle", new WorkbenchPrefab.VisibilityLists(
            ControlVisibleNodes: new[] { "c_TestySSR01_slg_coat_lod0" },
            DormHideNodes: new[] { "c_TestySSR01_slg_coat_lod0" },
            LobbyHideNodes: new[] { "c_TestySSR01_slg_coat_lod0" }));

        Assert.Equal(VisibilityOverride.CoatList, prefab.VisibilityOf("c_TestySSR01_slg_coat_lod0"));
    }

    [Fact]
    public void Read_ReturnsNullForANonPrefabBundle()
    {
        using var g = new TempGame();
        var path = g.At("plain.bundle");
        SyntheticBundle.BuildOneMesh(path, "c_plain_slg_body_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 },
            "8888888888888888888888888888888a.bundle");
        var raw = File.ReadAllBytes(path);
        var dec = BundleSegments.ExtractPlain(raw, BundleSegments.Walk(raw).Segments[0]);

        Assert.Null(PrefabReader.Read(dec));   // no recipe root = not an assembly prefab
    }

    [Fact]
    public void Read_WithRootName_SelectsOnlyThatRoot()
    {
        using var g = new TempGame();
        var dec = BuildPrefabBytes(g.Root);

        Assert.NotNull(PrefabReader.Read(dec, "TestySSR01"));
        Assert.Null(PrefabReader.Read(dec, "SomeOtherRoot"));
    }
}
