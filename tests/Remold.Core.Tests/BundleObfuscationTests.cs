using System;
using System.IO;
using System.Linq;
using System.Text;
using Remold.Core.Bundles;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The XOR bundle obfuscation — pure byte math, no real bundles. The expected behaviour is known-plaintext
/// key recovery over the first 0x8000 bytes, derived INDEPENDENTLY of the implementation.
/// </summary>
public class BundleObfuscationTests
{
    // Known-plaintext template: "UnityFS\0\0\0\0\x07" + version "5.x.x" / "2019.4.29f1".
    // Written out as explicit bytes (a C# "\x07..." escape would greedily eat following hex digits).
    private static readonly byte[] UnityFsHeader =
    {
        0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00, 0x00, 0x00, 0x00, 0x07, 0x35, 0x2E, 0x78, 0x2E,
        0x78, 0x00, 0x32, 0x30, 0x31, 0x39, 0x2E, 0x34, 0x2E, 0x32, 0x39, 0x66, 0x31, 0x00,
    };

    // 16 distinct NON-ZERO key bytes, so the obfuscated magic never looks "plain".
    private static byte[] SampleKey()
    {
        var k = new byte[16];
        for (int i = 0; i < 16; i++) k[i] = (byte)(0x11 + i);
        return k;
    }

    private static byte[] PlainBundle(int length)
    {
        var buf = new byte[length];
        Array.Copy(UnityFsHeader, buf, UnityFsHeader.Length);
        for (int i = UnityFsHeader.Length; i < length; i++) buf[i] = 0xAB;
        return buf;
    }

    [Fact]
    public void RecoverKey_ReturnsExactKey_FromKnownPlaintextHeader()
    {
        var key = SampleKey();
        var head = PlainBundle(64);
        BundleObfuscation.XorPrefix(head, key);   // obfuscate

        var recovered = BundleObfuscation.RecoverKey(head.AsSpan(0, 32));

        Assert.Equal(key, recovered);
    }

    [Fact]
    public void XorPrefix_IsSymmetric_RoundTrips()
    {
        var key = SampleKey();
        var plain = PlainBundle(0x8000 + 64);
        var work = (byte[])plain.Clone();

        BundleObfuscation.XorPrefix(work, key);
        Assert.NotEqual(plain, work);        // actually changed
        BundleObfuscation.XorPrefix(work, key);   // same pass undoes it
        Assert.Equal(plain, work);
    }

    [Fact]
    public void XorPrefix_TouchesExactly_First0x8000_Bytes()
    {
        var key = SampleKey();
        var plain = PlainBundle(0x8000 + 16);
        var work = (byte[])plain.Clone();

        BundleObfuscation.XorPrefix(work, key);

        Assert.NotEqual(plain[0x7FFF], work[0x7FFF]);   // last obfuscated byte changed
        Assert.Equal(plain[0x8000], work[0x8000]);      // first plaintext byte untouched
        Assert.Equal(plain[0x8000 + 15], work[0x8000 + 15]);
    }

    [Fact]
    public void Deobfuscate_RecoversKey_AndRestoresPlaintext()
    {
        var key = SampleKey();
        var plain = PlainBundle(0x8000 + 100);
        var enc = (byte[])plain.Clone();
        BundleObfuscation.XorPrefix(enc, key);

        var recoveredKey = BundleObfuscation.Deobfuscate(enc);   // deobfuscates in place

        Assert.Equal(key, recoveredKey);
        Assert.Equal(plain, enc);
    }

    [Fact]
    public void Deobfuscate_IsNoOp_OnAlreadyPlainBundle()
    {
        var plain = PlainBundle(0x8000 + 8);
        var work = (byte[])plain.Clone();

        var key = BundleObfuscation.Deobfuscate(work);

        Assert.Equal(new byte[16], key);   // zero key signals "was already plain"
        Assert.Equal(plain, work);
    }

    [Fact]
    public void IsPlain_TrueForUnityFsMagic_FalseForObfuscated()
    {
        var plain = PlainBundle(64);
        Assert.True(BundleObfuscation.IsPlain(plain));

        var enc = (byte[])plain.Clone();
        BundleObfuscation.XorPrefix(enc, SampleKey());
        Assert.False(BundleObfuscation.IsPlain(enc));
    }

    [Fact]
    public void RecoverKey_Throws_OnTooSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() => BundleObfuscation.RecoverKey(new byte[29]));
    }

    [Fact]
    public void RecoverKey_Throws_OnHeaderMismatch()
    {
        var head = PlainBundle(64);
        BundleObfuscation.XorPrefix(head, SampleKey());
        head[20] ^= 0xFF;   // corrupt a byte inside the validation window (16..29)

        Assert.Throws<FormatException>(() => BundleObfuscation.RecoverKey(head.AsSpan(0, 32)));
    }

    [Fact]
    public void RecoverKey_AcceptsSpecHeader_PinningTheMagicConstant()
    {
        // The magic XOR'd with a zero key obfuscates to itself, so recovery must return that zero key —
        // pinning the code's private template to the known-plaintext magic.
        var recovered = BundleObfuscation.RecoverKey(UnityFsHeader);
        Assert.Equal(new byte[16], recovered);
    }

    [Fact]
    public void DeobfuscateFile_RoundTripsAcrossThe0x8000Boundary()
    {
        // A >32KB bundle with a marker INSIDE the obfuscated prefix and another PAST 0x8000: the live read
        // must recover both, XOR-undoing the prefix while leaving the plaintext tail untouched.
        var key = SampleKey();
        var plain = new byte[0x8000 + 0x2000];          // 40 KB
        Array.Copy(UnityFsHeader, plain, UnityFsHeader.Length);
        var markerA = Encoding.ASCII.GetBytes("INSIDE_PREFIX");
        var markerB = Encoding.ASCII.GetBytes("PAST_BOUNDARY");
        markerA.CopyTo(plain, 0x100);                   // within first 0x8000 → gets obfuscated
        markerB.CopyTo(plain, 0x9000);                  // past 0x8000 → stays plaintext

        var enc = (byte[])plain.Clone();
        BundleObfuscation.XorPrefix(enc, key);
        Assert.NotEqual(plain[0x100], enc[0x100]);      // marker A is scrambled on disk
        Assert.Equal(plain[0x9000], enc[0x9000]);       // marker B is not

        using var g = new TempGame();
        var path = g.At("live.bundle");
        File.WriteAllBytes(path, enc);

        var dec = BundleReader.DeobfuscateFile(path);

        Assert.Equal(markerA, dec.Skip(0x100).Take(markerA.Length).ToArray());
        Assert.Equal(markerB, dec.Skip(0x9000).Take(markerB.Length).ToArray());
    }
}
