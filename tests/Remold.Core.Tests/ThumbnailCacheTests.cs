using System;
using System.IO;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="ThumbnailCache"/>: the downscale math (aspect preserved, ≤256, no upscale), cache keying by
/// catalog version + bundle + name, the failure-is-never-cached rule, and the full decode→downscale→write
/// route over a synthetic bundle.
/// </summary>
public class ThumbnailCacheTests
{
    // ---- the RMO thumbnail composite (TextureExport.OpaquePng) ----

    [Fact]
    public void OpaquePng_ClearsAlpha_AndLeavesTheColoursAlone()
    {
        // An RMO packs the emissive mask in alpha, so most of it is transparent as a PICTURE. A viewer
        // sampling that as coverage renders the tile ghostly; the thumbnail composites over it instead.
        using var src = new Image<Rgba32>(2, 2);
        src[0, 0] = new Rgba32(10, 20, 30, 0);
        src[1, 0] = new Rgba32(40, 50, 60, 128);
        src[0, 1] = new Rgba32(70, 80, 90, 255);
        src[1, 1] = new Rgba32(1, 2, 3, 7);
        using var ms = new MemoryStream();
        src.SaveAsPng(ms);

        using var flat = Image.Load<Rgba32>(TextureExport.OpaquePng(ms.ToArray()));

        Assert.Equal(new Rgba32(10, 20, 30, 255), flat[0, 0]);
        Assert.Equal(new Rgba32(40, 50, 60, 255), flat[1, 0]);
        Assert.Equal(new Rgba32(70, 80, 90, 255), flat[0, 1]);   // already opaque, unchanged
        Assert.Equal(new Rgba32(1, 2, 3, 255), flat[1, 1]);
    }

    [Fact]
    public void OpaquePng_FitsWithinTheBound_WithTheCompositeTakenFirst()
    {
        // The card wants a 256-class tile, so a 2048 workspace map must not be re-encoded at 2048 to show
        // at 256. ORDER is the whole thing: resampling runs on premultiplied alpha, so a downscale taken
        // before the composite washes the colour out of every texel the emissive mask left transparent —
        // and clearing alpha afterwards would only make that loss opaque.
        using var src = new Image<Rgba32>(512, 512, new Rgba32(200, 100, 50, 0));
        using var ms = new MemoryStream();
        src.SaveAsPng(ms);

        using var thumb = Image.Load<Rgba32>(TextureExport.OpaquePng(ms.ToArray(), maxDim: 64));

        Assert.Equal(64, thumb.Width);
        Assert.Equal(64, thumb.Height);
        Assert.Equal(new Rgba32(200, 100, 50, 255), thumb[32, 32]);
    }

    [Fact]
    public void OpaquePng_NeverUpscales()
    {
        using var src = new Image<Rgba32>(8, 4, new Rgba32(1, 2, 3, 0));
        using var ms = new MemoryStream();
        src.SaveAsPng(ms);

        using var same = Image.Load<Rgba32>(TextureExport.OpaquePng(ms.ToArray(), maxDim: 256));

        Assert.Equal(8, same.Width);
        Assert.Equal(4, same.Height);
    }

    [Fact]
    public void OpaquePng_LeavesTheSourceBytesAlone()
    {
        // the composite is for the thumbnail alone; the file the build reads must come back untouched
        using var src = new Image<Rgba32>(1, 1, new Rgba32(9, 9, 9, 0));
        using var ms = new MemoryStream();
        src.SaveAsPng(ms);
        var before = ms.ToArray();

        TextureExport.OpaquePng(before);

        using var again = Image.Load<Rgba32>(before);
        Assert.Equal(0, again[0, 0].A);
    }

    // ---- downscale math (ThumbnailCache.FitWithin) ----

    [Theory]
    [InlineData(2048, 2048, 256, 256, 256)]   // square, scaled to the box
    [InlineData(2048, 1024, 256, 256, 128)]   // 2:1 landscape — aspect preserved
    [InlineData(1024, 2048, 256, 128, 256)]   // 1:2 portrait — aspect preserved
    [InlineData(300, 100, 256, 256, 85)]      // 3:1, non-integer scale — longest side hits the box, rounds
    [InlineData(256, 256, 256, 256, 256)]     // exactly the box — unchanged, not upscaled
    [InlineData(200, 100, 200, 200, 100)]     // smaller than the box in BOTH dims — NO upscale
    [InlineData(1, 1, 256, 1, 1)]             // degenerate small — never grows
    public void FitWithin_PreservesAspect_CapsAtMax_AndNeverUpscales(int w, int h, int max, int expW, int expH)
    {
        // route: ThumbnailCache.FitWithin (pure)
        var (rw, rh) = ThumbnailCache.FitWithin(w, h, max);

        Assert.Equal((expW, expH), (rw, rh));
        Assert.True(rw <= max && rh <= max, $"result {rw}x{rh} exceeds max {max}");
        Assert.True(rw <= w && rh <= h, "thumb was upscaled beyond the source");
    }

    // ---- cache keying + hit short-circuit ----

    [Fact]
    public void EnsureThumb_CacheHit_ReturnsPath_WithoutDeobfuscating()
    {
        // route: EnsureThumb(tryDeobfuscate,…) with the keyed PNG already on disk
        using var t = new TempGame();
        var cache = new ThumbnailCache(t.Root);
        var path = cache.PathFor("bundleX", "T_body_d", "24535");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });   // any file — the hit is existence, not content

        int deobfuscates = 0;
        var result = cache.EnsureThumb(_ => { deobfuscates++; return null; }, "bundleX", "T_body_d", "24535");

        Assert.Equal(path, result);
        Assert.Equal(0, deobfuscates);   // a hit never deobfuscates
    }

    [Fact]
    public void EnsureThumb_KeyedByCatalogVersion_DifferentVersionMisses()
    {
        // route: TryGetCachedPath keying — a thumb under version A is not a hit for version B
        using var t = new TempGame();
        var cache = new ThumbnailCache(t.Root);
        var pathA = cache.PathFor("bundleX", "T_body_d", "AAA");
        Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
        File.WriteAllBytes(pathA, new byte[] { 1 });

        Assert.Equal(pathA, cache.TryGetCachedPath("bundleX", "T_body_d", "AAA"));
        Assert.Null(cache.TryGetCachedPath("bundleX", "T_body_d", "BBB"));

        int deobfuscates = 0;
        Assert.Null(cache.EnsureThumb(_ => { deobfuscates++; return null; }, "bundleX", "T_body_d", "BBB"));
        Assert.Equal(1, deobfuscates);   // the other-version miss DID try to deobfuscate
    }

    // ---- failure is never cached ----

    [Fact]
    public void EnsureThumb_DeobfuscateMiss_ReturnsNull_WritesNothing_AndRetries()
    {
        // route: EnsureThumb(tryDeobfuscate,…) where deobfuscate returns null — e.g. a sharing lock
        using var t = new TempGame();
        var cache = new ThumbnailCache(t.Root);

        int deobfuscates = 0;
        var r1 = cache.EnsureThumb(_ => { deobfuscates++; return null; }, "bundleX", "T_missing", "24535");
        var r2 = cache.EnsureThumb(_ => { deobfuscates++; return null; }, "bundleX", "T_missing", "24535");

        Assert.Null(r1);
        Assert.Null(r2);
        Assert.False(File.Exists(cache.PathFor("bundleX", "T_missing", "24535")));   // failure not cached
        Assert.Equal(2, deobfuscates);   // both calls retried the deobfuscate (no poisoned cache entry)
    }

    [Fact]
    public void EnsureThumb_DecodeFault_ReturnsNull_WritesNothing()
    {
        // route: EnsureThumb(bytes,…) where the bytes aren't a valid bundle — decode throws, caught, not cached
        using var t = new TempGame();
        var cache = new ThumbnailCache(t.Root);

        var result = cache.EnsureThumb(new byte[] { 1, 2, 3, 4 }, "bundleX", "T_garbage", "24535");

        Assert.Null(result);
        Assert.False(File.Exists(cache.PathFor("bundleX", "T_garbage", "24535")));
    }

    // ---- full decode → downscale → write, over a synthetic bundle ----

    [Fact]
    public void EnsureThumb_FromSyntheticBundle_WritesDownscaledPng()
    {
        // route: EnsureThumb(bytes,…) → BundleReader.GetTexture → FitWithin → PNG on disk
        using var t = new TempGame();
        string bundlePath = t.At("subject.bundle");
        // 512×256 is larger than the 256 box, so it downscales to 256×128, aspect preserved
        SyntheticBundle.Build(bundlePath,
            new SyntheticBundle.TextureSpec("T_big", 512, 256, SyntheticBundle.SolidRgba32(512, 256, 40, 80, 120, 255)));
        byte[] plain = File.ReadAllBytes(bundlePath);

        var cache = new ThumbnailCache(Path.Combine(t.Root, "thumbs"));
        var path = cache.EnsureThumb(plain, "subject.bundle", "T_big", "24535");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        using var img = Image.Load(path!);
        Assert.Equal(256, img.Width);
        Assert.Equal(128, img.Height);

        // a second call is a cache hit → same path, no re-write needed
        var again = cache.EnsureThumb(_ => { Assert.Fail("hit must not deobfuscate"); return null; },
            "subject.bundle", "T_big", "24535");
        Assert.Equal(path, again);
    }

    // ---- (bundle, name) identity: a same-named texture from another bundle is a DISTINCT thumb ----

    [Fact]
    public void SameNamedTextures_InDifferentBundles_YieldDistinctWarmResults()
    {
        // route: PathFor/EnsureThumb keyed by bundle — bundle B must NOT warm-hit bundle A's pixels.
        using var t = new TempGame();
        const string texName = "c_body_d";                 // the SAME asset name in both bundles
        string bundleA = t.At("a.bundle"), bundleB = t.At("b.bundle");
        SyntheticBundle.Build(bundleA,
            new SyntheticBundle.TextureSpec(texName, 64, 64, SyntheticBundle.SolidRgba32(64, 64, 200, 30, 30, 255)));
        SyntheticBundle.Build(bundleB,
            new SyntheticBundle.TextureSpec(texName, 64, 64, SyntheticBundle.SolidRgba32(64, 64, 30, 30, 200, 255)));

        var cache = new ThumbnailCache(Path.Combine(t.Root, "thumbs"));
        // distinct cache paths first (the identity is the path, not just the returned decode)
        Assert.NotEqual(cache.PathFor("bundle-a", texName, "24535"), cache.PathFor("bundle-b", texName, "24535"));

        var pathA = cache.EnsureThumb(File.ReadAllBytes(bundleA), "bundle-a", texName, "24535");
        var pathB = cache.EnsureThumb(File.ReadAllBytes(bundleB), "bundle-b", texName, "24535");

        Assert.NotNull(pathA);
        Assert.NotNull(pathB);
        Assert.NotEqual(pathA, pathB);                     // no collision on one shared file
        // and the warm pixels differ — B never served A's red thumb
        using var imgA = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pathA!);
        using var imgB = Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pathB!);
        Assert.NotEqual(imgA[0, 0], imgB[0, 0]);

        // B is a real hit on its OWN path, not a miss that fell through to A
        Assert.Equal(pathB, cache.TryGetCachedPath("bundle-b", texName, "24535"));
        Assert.Equal(pathA, cache.TryGetCachedPath("bundle-a", texName, "24535"));
    }

    // ---- reserved Windows device names: a texture literally named "NUL"/"CON"/… ----

    [Fact]
    public void ReservedDeviceName_Texture_WritesWithUnderscorePrefix()
    {
        // "NUL.png" is still the reserved NUL device on Windows and would fail to create; the "_" prefix
        // makes it an ordinary writable file.
        using var t = new TempGame();
        string bundlePath = t.At("subject.bundle");
        SyntheticBundle.Build(bundlePath,
            new SyntheticBundle.TextureSpec("NUL", 64, 64, SyntheticBundle.SolidRgba32(64, 64, 10, 20, 30, 255)));
        byte[] plain = File.ReadAllBytes(bundlePath);

        var cache = new ThumbnailCache(Path.Combine(t.Root, "thumbs"));
        var path = cache.EnsureThumb(plain, "subject.bundle", "NUL", "24535");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal("_NUL.png", Path.GetFileName(path));                                  // reserved stem → "_" prefix
        Assert.Equal(path, cache.TryGetCachedPath("subject.bundle", "NUL", "24535"));      // the hit path agrees with the write
    }

    // ---- tmp-orphan sweep: first write clears a killed process's residue ----

    [Fact]
    public void FirstWrite_SweepsOldOrphanTemps_ButSparesFreshOnes()
    {
        // route: WriteThumb → SweepOrphanTemps (once per version dir per instance).
        using var t = new TempGame();
        var cache = new ThumbnailCache(Path.Combine(t.Root, "thumbs"));
        var versionDir = Path.GetDirectoryName(cache.PathFor("subject.bundle", "T_any", "24535"))!;
        Directory.CreateDirectory(versionDir);

        // an orphan a killed process left between encode and move: an OLD *.tmp
        var orphan = Path.Combine(versionDir, "T_dead.png.deadbeef.tmp");
        File.WriteAllBytes(orphan, new byte[] { 9 });
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow - TimeSpan.FromMinutes(5));
        // a fresh temp a concurrent worker is mid-writing: the age guard must SPARE it
        var live = Path.Combine(versionDir, "T_live.png.cafef00d.tmp");
        File.WriteAllBytes(live, new byte[] { 9 });

        // the first real write into this dir triggers the sweep
        string bundlePath = t.At("subject.bundle");
        SyntheticBundle.Build(bundlePath,
            new SyntheticBundle.TextureSpec("T_ok", 64, 64, SyntheticBundle.SolidRgba32(64, 64, 1, 2, 3, 255)));
        // re-stamp right before the sweep: the bundle build above can take real time on a loaded box,
        // and the assertion is about the age GUARD, not about this test finishing inside it
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);
        var path = cache.EnsureThumb(File.ReadAllBytes(bundlePath), "subject.bundle", "T_ok", "24535");

        Assert.NotNull(path);
        Assert.False(File.Exists(orphan), "an old orphaned temp should be swept on first write");
        Assert.True(File.Exists(live), "a fresh in-flight temp must be spared (age-guarded sweep)");
    }
}
