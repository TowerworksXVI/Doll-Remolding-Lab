using System.IO;
using Remold.Core.Bundles;
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
        Assert.Equal(2, slot.Materials.Count);
        Assert.Null(slot.Materials[0].Cab);                 // local ref: resolve in this bundle
        Assert.Equal(11L, slot.Materials[0].PathId);
        Assert.Equal("CAB-ext1", slot.Materials[1].Cab);    // external ref: resolve via CabToBundle
        Assert.Equal(42L, slot.Materials[1].PathId);

        Assert.Equal(new[] { "CAB-ext1" }, prefab.ExternalCabs);
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
