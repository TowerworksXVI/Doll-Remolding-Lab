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

    [Fact]
    public void Snapshot_serves_byte_equivalent_lookups_and_invalidates_on_length_or_mtime_change()
    {
        using var t = new TempGame();
        string source = Build(t,
            (NameA, FakeGff.Stub(PhysA, 0, 0, 1)),
            (NameB, FakeGff.Stub(PhysB, 4096, 777, 2)));
        string snapshot = t.At("gff.snapshot");
        DateTime sourceStamp = File.GetLastWriteTimeUtc(source);

        var parsed = GffManifest.LoadCached(source, snapshot, out bool firstHit);
        var cached = GffManifest.LoadCached(source, snapshot, out bool secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        AssertEquivalent(parsed, cached);
        Assert.Equal(new[] { PhysA, PhysB }, cached.PhysicalHashes);

        // Length is one third of the key. A valid trailing byte leaves the source structurally readable,
        // but the old snapshot must not answer for it.
        using (var append = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.Read))
            append.WriteByte(0);
        File.SetLastWriteTimeUtc(source, sourceStamp);   // isolate the length half of the invalidation
        var longer = GffManifest.LoadCached(source, snapshot, out bool lengthHit);
        Assert.False(lengthHit);
        AssertEquivalent(parsed, longer);

        // With the new length now snapshotted, mtime alone is the remaining invalidation half.
        File.SetLastWriteTimeUtc(source, sourceStamp.AddMinutes(2));
        var restamped = GffManifest.LoadCached(source, snapshot, out bool mtimeHit);
        Assert.False(mtimeHit);
        AssertEquivalent(parsed, restamped);

        static void AssertEquivalent(GffManifest expected, GffManifest actual)
        {
            Assert.Equal(expected.EntryCount, actual.EntryCount);
            Assert.Equal(expected.Names, actual.Names);
            Assert.Equal(expected.PhysicalHashes, actual.PhysicalHashes);
            foreach (string name in expected.Names)
            {
                var left = expected.Locate(name);
                var right = actual.Locate(name);
                Assert.Equal(left.Position, right.Position);
                Assert.Equal(left.Stub.PhysHash, right.Stub.PhysHash);
                Assert.Equal(left.Stub.Offset, right.Stub.Offset);
                Assert.Equal(left.Stub.Size, right.Stub.Size);
                Assert.Equal(left.Stub.SubHash, right.Stub.SubHash);
                Assert.Equal(right.Stub, actual.ReadStubAt(right.Position));
            }
        }
    }
}
