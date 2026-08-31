using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A texture is identified by its pathId, not by its name. The game ships bundles that hold many
/// same-named textures — every toon ramp in a ramp library is called <c>RampMap_Linear_RGBAHalf</c>, and
/// 130 of the 250 ramp bundles hold two or more — so a name-selected read returns whichever comes first
/// and is wrong for most of the corpus. These fixtures reproduce that bundle shape: two textures, one
/// name, different contents.
/// </summary>
public class TextureIdentityTests
{
    /// <summary>Two same-named textures with different colours, and the pathIds that tell them apart.</summary>
    private static (string Path, long First, long Second) TwoSameNamed(TempGame t, string name = "RampMap_Linear_RGBAHalf")
    {
        string bundle = t.At("library.bundle");
        var ids = SyntheticBundle.Build(bundle,
            new SyntheticBundle.TextureSpec(name, 4, 4, SyntheticBundle.SolidRgba32(4, 4, 0x11, 0x11, 0x11, 255)),
            new SyntheticBundle.TextureSpec(name, 4, 4, SyntheticBundle.SolidRgba32(4, 4, 0xEE, 0xEE, 0xEE, 255)));
        return (bundle, ids[0], ids[1]);
    }

    [Fact]
    public void GetTexture_ByPathId_ReadsThatTexture_NotTheFirstOfTheName()
    {
        using var t = new TempGame();
        var (path, first, second) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);
        var reader = new BundleReader();

        var one = reader.GetTexture(plain, BundleReader.TextureRef.ByPathId(first));
        var two = reader.GetTexture(plain, BundleReader.TextureRef.ByPathId(second));

        Assert.Equal(0x11, one!.Value.Bgra[0]);
        Assert.Equal(0xEE, two!.Value.Bgra[0]);
    }

    [Fact]
    public void GetTexture_ByName_TakesTheFirst_WhichIsWhyTheNameIsNotAnIdentity()
    {
        using var t = new TempGame();
        var (path, _, _) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);

        var byName = new BundleReader().GetTexture(plain, "RampMap_Linear_RGBAHalf");

        Assert.Equal(0x11, byName!.Value.Bgra[0]);   // the second texture is unreachable by name
    }

    [Fact]
    public void GetTextureHashSource_ByPathId_ReadsThatTexturesOwnBytes()
    {
        using var t = new TempGame();
        var (path, first, second) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);
        var reader = new BundleReader();

        var one = reader.GetTextureHashSource(plain, BundleReader.TextureRef.ByPathId(first));
        var two = reader.GetTextureHashSource(plain, BundleReader.TextureRef.ByPathId(second));

        Assert.Equal(0x11, one!.Value.PictureData[0]);
        Assert.Equal(0xEE, two!.Value.PictureData[0]);
    }

    [Fact]
    public void GetTextureMeta_ByPathId_ReadsThatTexturesOwnExtent()
    {
        using var t = new TempGame();
        string bundle = t.At("library.bundle");
        var ids = SyntheticBundle.Build(bundle,
            new SyntheticBundle.TextureSpec("same", 4, 4, SyntheticBundle.SolidRgba32(4, 4, 1, 1, 1, 255)),
            new SyntheticBundle.TextureSpec("same", 8, 8, SyntheticBundle.SolidRgba32(8, 8, 2, 2, 2, 255)));
        byte[] plain = File.ReadAllBytes(bundle);

        var meta = new BundleReader().GetTextureMeta(plain, BundleReader.TextureRef.ByPathId(ids[1]));

        Assert.Equal((8, 8), (meta!.Value.Width, meta.Value.Height));
    }

    [Fact]
    public void APathIdTheBundleDoesNotHold_ReadsAsAbsent_RatherThanFallingBackToTheName()
    {
        using var t = new TempGame();
        var (path, first, _) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);
        var reader = new BundleReader();

        // A stale reference must be a loud null the caller reports, never a different texture that
        // happens to share the name — silently shading with the wrong ramp is the failure being fixed.
        Assert.Null(reader.GetTexture(plain, BundleReader.TextureRef.ByPathId(first + 9999)));
        Assert.Null(reader.GetTextureHashSource(plain, BundleReader.TextureRef.ByPathId(first + 9999)));
        Assert.Null(reader.GetTextureMeta(plain, BundleReader.TextureRef.ByPathId(first + 9999)));
    }

    // ---- the resolver: two slots onto two same-named textures stay two maps ----------------------

    [Fact]
    public void ResolveTexSlots_KeepsSameNamedTexturesApart_ByPathId()
    {
        using var t = new TempGame();
        var (path, first, second) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);

        var maps = MaterialResolver.ResolveTexSlots(
            cabToBundle: _ => null, reader: new BundleReader(), tryDeobfuscate: _ => null,
            matBundle: "lib", matDec: plain, externalCabs: new[] { "cab" },
            slots: new[]
            {
                new BundleReader.TexSlot("_RampMap", 0, first),
                new BundleReader.TexSlot("_SecondRamp", 0, second),
            });

        Assert.Equal(2, maps.Count);   // a by-name dedupe would have collapsed these to one
        Assert.Equal(new[] { first, second }, maps.Select(m => m.PathId).ToArray());
        Assert.All(maps, m => Assert.Equal("RampMap_Linear_RGBAHalf", m.TextureName));
    }

    [Fact]
    public void ResolveTexSlots_KeepsTwoPropertiesBoundToTheSameTexture()
    {
        using var t = new TempGame();
        var (path, first, _) = TwoSameNamed(t);
        byte[] plain = File.ReadAllBytes(path);

        var maps = MaterialResolver.ResolveTexSlots(
            cabToBundle: _ => null, reader: new BundleReader(), tryDeobfuscate: _ => null,
            matBundle: "lib", matDec: plain, externalCabs: new[] { "cab" },
            slots: new[]
            {
                new BundleReader.TexSlot("_RampMap", 0, first),
                new BundleReader.TexSlot("_AlsoTheSameOne", 0, first),
            });

        Assert.Equal(2, maps.Count);
        Assert.Equal(new[] { "_RampMap", "_AlsoTheSameOne" }, maps.Select(map => map.Slot));
        Assert.All(maps, map => Assert.Equal(first, map.PathId));
    }

    [Fact]
    public void SubjectMap_ReadsByPathId_AndFallsBackToTheNameWithoutOne()
    {
        var withId = new Remold.Core.Workbench.SubjectMap("_RampMap", "RampMap_Linear_RGBAHalf", "lib", 42);
        var without = new Remold.Core.Workbench.SubjectMap("_BaseMap", "body_d", "lib");

        Assert.Equal(42, withId.Ref.PathId);
        Assert.Null(withId.Ref.Name);
        Assert.Equal(0, without.Ref.PathId);
        Assert.Equal("body_d", without.Ref.Name);
    }
}
