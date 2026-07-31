using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The VFS chain-walk over synthetic packed files: segment enumeration, per-segment deobfuscation (each
/// segment's key comes from its OWN header, not the file's), honest reporting of a foreign or truncated
/// tail, and that an extracted segment is a self-contained readable bundle.
/// </summary>
public class BundleSegmentsTests
{
    private static readonly byte[] TestKey =
        { 0xA5, 0x3C, 0x77, 0x01, 0xEE, 0x42, 0x19, 0xB0, 0x08, 0xD3, 0x5F, 0x66, 0x2A, 0x91, 0xC4, 0x7D };

    /// <summary>Three single-texture bundles with the MIDDLE one obfuscated (the game's per-segment
    /// scheme), concatenated. Returns the packed bytes plus each segment's plain length.</summary>
    private static (byte[] Raw, long[] Lengths) BuildPackedChain(TempGame t)
    {
        var lens = new long[3];
        using var ms = new MemoryStream();
        for (int i = 0; i < 3; i++)
        {
            string p = t.At($"seg{i}.bundle");
            SyntheticBundle.BuildOneTexture(p, $"seg{i}_texture", 4, 4, r: (byte)(0x10 * (i + 1)), g: 0x22, b: 0x33, a: 0xFF);
            byte[] bytes = File.ReadAllBytes(p);
            lens[i] = bytes.Length;
            if (i == 1) BundleObfuscation.XorPrefix(bytes, TestKey);   // per-segment obfuscation, mid-chain
            ms.Write(bytes);
        }
        return (ms.ToArray(), lens);
    }

    [Fact]
    public void Walk_SingleBundleFile_OneSegmentCoveringTheWholeFile()
    {
        using var t = new TempGame();
        string p = t.At("single.bundle");
        SyntheticBundle.BuildOneTexture(p, "poc_texture", 4, 4);
        byte[] raw = File.ReadAllBytes(p);

        var walk = BundleSegments.Walk(raw);

        var seg = Assert.Single(walk.Segments);
        Assert.Equal((0L, (long)raw.Length), (seg.Offset, seg.Size));
        Assert.Equal(0, walk.UnconsumedBytes);
    }

    [Fact]
    public void Walk_PackedChain_FindsEverySegmentAtItsOffset_IncludingTheObfuscatedOne()
    {
        using var t = new TempGame();
        var (raw, lens) = BuildPackedChain(t);

        var walk = BundleSegments.Walk(raw);

        Assert.Equal(3, walk.Segments.Count);
        Assert.Equal(0, walk.UnconsumedBytes);
        Assert.Equal(new[] { 0L, lens[0], lens[0] + lens[1] }, walk.Segments.Select(s => s.Offset).ToArray());
        Assert.Equal(lens, walk.Segments.Select(s => s.Size).ToArray());
    }

    [Fact]
    public void ExtractPlain_EverySegment_IsAStandaloneReadableBundle_WithItsOwnContent()
    {
        using var t = new TempGame();
        var (raw, _) = BuildPackedChain(t);
        var walk = BundleSegments.Walk(raw);

        for (int i = 0; i < walk.Segments.Count; i++)
        {
            byte[] plain = BundleSegments.ExtractPlain(raw, walk.Segments[i]);
            Assert.True(BundleObfuscation.IsPlain(plain));                   // deobfuscated (segment 1 was XOR'd)
            Assert.Equal(walk.Segments[i].Size, BundleSegments.DeclaredSize(plain));
            var assets = new BundleReader().ListAssets(plain, SyntheticBundle.ClassTexture2D);
            var a = Assert.Single(assets);                                   // self-contained: parses on its own
            Assert.Equal($"seg{i}_texture", a.Name);
        }
    }

    [Fact]
    public void Walk_ForeignTail_ReportsTheUnconsumedBytes_InsteadOfSwallowingThem()
    {
        using var t = new TempGame();
        var (raw, _) = BuildPackedChain(t);
        byte[] withTail = raw.Concat(new byte[137]).ToArray();   // junk past the last segment

        var walk = BundleSegments.Walk(withTail);

        Assert.Equal(3, walk.Segments.Count);
        Assert.Equal(137, walk.UnconsumedBytes);
    }

    [Fact]
    public void Walk_TruncatedLastSegment_StopsBeforeIt_AndReportsTheRemainder()
    {
        using var t = new TempGame();
        var (raw, lens) = BuildPackedChain(t);
        long cut = lens[2] / 2;                                   // cut mid-way through the third segment
        byte[] truncated = raw[..(int)(raw.Length - cut)];

        var walk = BundleSegments.Walk(truncated);

        Assert.Equal(2, walk.Segments.Count);                     // the overrunning declaration is refused
        Assert.Equal(lens[2] - cut, walk.UnconsumedBytes);
    }

    [Fact]
    public void DeclaredSize_NonUnityFsBytes_ReturnsMinusOne()
    {
        Assert.Equal(-1, BundleSegments.DeclaredSize(new byte[64]));
        Assert.Equal(-1, BundleSegments.DeclaredSize("UnityFS\0"u8));   // too short to carry a size
    }

    [Fact]
    public void ReadSegmentPlain_ByRange_MatchesExtractPlain_IncludingTheObfuscatedSegment()
    {
        using var t = new TempGame();
        var (raw, _) = BuildPackedChain(t);
        string packed = t.At("packed.bundle");
        File.WriteAllBytes(packed, raw);
        var walk = BundleSegments.Walk(raw);

        for (int i = 0; i < walk.Segments.Count; i++)
        {
            var seg = walk.Segments[i];
            byte[] streamed = BundleSegments.ReadSegmentPlain(packed, seg.Offset, seg.Size);
            Assert.Equal(BundleSegments.ExtractPlain(raw, seg), streamed);
        }
    }

    [Fact]
    public void ReadSegmentPlain_RangePastEof_ThrowsInsteadOfReturningWrongBytes()
    {
        using var t = new TempGame();
        var (raw, lens) = BuildPackedChain(t);
        string packed = t.At("packed.bundle");
        File.WriteAllBytes(packed, raw);

        // a stale map pointing past the (now shorter) file must refuse, never mis-slice
        Assert.Throws<InvalidDataException>(
            () => BundleSegments.ReadSegmentPlain(packed, raw.Length - lens[2] + 1, lens[2]));
    }
}
