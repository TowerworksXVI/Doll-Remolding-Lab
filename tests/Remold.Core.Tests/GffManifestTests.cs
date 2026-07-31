using System;
using System.Buffers.Binary;
using System.IO;
using Remold.Core.Bundles;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The read-only GFF manifest decoder (<c>GffManifest</c>): full structural validation at Read,
/// name-keyed locate, and the loud refusals. Fixtures come from <see cref="FakeGff"/> (the real layout).
/// </summary>
public class GffManifestTests
{
    private const string PhysA = "aaaa000000000000000000000000000a";
    private const string PhysB = "bbbb000000000000000000000000000b";
    private const string NameA = PhysA + ".bundle";
    private const string NameB = PhysB + ".bundle";

    private static string Build(TempGame t, params (string Name, byte[] Stub)[] entries)
    {
        var path = t.At("gff.bundle");
        FakeGff.Write(path, entries);
        return path;
    }

    [Fact]
    public void Read_ParsesHeaderAndNameTable_AndLocatesByName()
    {
        using var t = new TempGame();
        var gff = GffManifest.Read(Build(t,
            (NameA, FakeGff.Stub(PhysA, 0, 0, 1)),                 // whole-file entry
            (NameB, FakeGff.Stub(PhysB, 4096, 777, 2))));          // packed-segment entry

        Assert.Equal(2, gff.EntryCount);
        Assert.Equal(new[] { NameA, NameB }, gff.Names);

        var hit = gff.Locate(NameB);
        Assert.Equal(PhysB, hit.Stub.PhysHash);
        Assert.Equal((4096u, 777u), (hit.Stub.Offset, hit.Stub.Size));
        Assert.All(hit.Stub.SubHash, b => Assert.Equal(2, b));

        Assert.True(gff.TryLocate(PhysA, out var whole));          // ".bundle" suffix optional
        Assert.Equal((0u, 0u), (whole.Stub.Offset, whole.Stub.Size));
        Assert.False(gff.TryLocate("cccc000000000000000000000000000c", out _));
        Assert.Throws<InvalidDataException>(() => gff.Locate("cccc000000000000000000000000000c"));
    }

    [Fact]
    public void Read_RefusesJunk_WrongSeed_AndDuplicateNames()
    {
        using var t = new TempGame();

        var junk = t.At("junk.bundle");
        File.WriteAllBytes(junk, new byte[64]);
        Assert.Throws<InvalidDataException>(() => GffManifest.Read(junk));

        var badSeed = Build(t, (NameA, FakeGff.Stub(PhysA, 0, 0, 1)));
        var bytes = File.ReadAllBytes(badSeed);
        bytes[4] ^= 0xFF;                                          // corrupt encBytes → garbage names
        File.WriteAllBytes(badSeed, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => GffManifest.Read(badSeed));
        Assert.Contains("does not decode", ex.Message);

        var dup = Build(t, (NameA, FakeGff.Stub(PhysA, 0, 0, 1)), (NameA, FakeGff.Stub(PhysB, 0, 0, 2)));
        Assert.Throws<InvalidDataException>(() => GffManifest.Read(dup));
    }

    [Fact]
    public void Read_RefusesAStubPositionOutsideTheFile()
    {
        using var t = new TempGame();
        var path = Build(t, (NameA, FakeGff.Stub(PhysA, 0, 0, 1)));
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), 1_000_000);   // block 0 ClusterIndex
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<InvalidDataException>(() => GffManifest.Read(path));
        Assert.Contains("outside the file's stub region", ex.Message);
    }
}
