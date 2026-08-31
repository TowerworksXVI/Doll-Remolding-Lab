using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Remold.Core.Bundles;

/// <summary>
/// READ-ONLY decoder for the game's GFF VFS manifest (<c>08dfe7d8….bundle</c>): the bundle-name →
/// (physical file, byte range) map every bundle load resolves through. Stored names are Addressables
/// <b>bundleInternalIds</b>, NOT the embedded <c>AssetBundle.m_Name</c> the corpus identity read keys
/// on; <see cref="CatalogIndex"/> joins the two. The entry count is the corpus completeness oracle.
///
/// <para>Layout: 20-byte header <c>"GFF"|version|encBytes(4)|MaxFileCount|MaxBlockCount|BlockCount</c>;
/// 12-byte blocks (StringIndex==i, ClusterIndex, Length=40); 256-byte name slots
/// (<c>[len][name XOR encBytes cyclic]</c> — the seed rotates every update, always read from the
/// header); one 4096-byte cluster per stub at <c>ClusterIndex*4096</c>, first 40 bytes =
/// <c>physHash(16)|offset(u32)|size(u32, 0 = whole file)|subHash(16)</c>. Any structural violation
/// refuses the whole manifest at <see cref="Read"/>.</para>
/// </summary>
public sealed class GffManifest
{
    /// <summary>The manifest's physical filename (sans <c>.bundle</c>).</summary>
    public const string ManifestHash = "08dfe7d89b6fe56375d6dfec87ffcc8a";

    /// <summary>One 40-byte stub: which physical file holds a logical bundle and where inside it.
    /// <see cref="Size"/> == 0 means the bundle IS the whole physical file.</summary>
    public readonly record struct Stub(string PhysHash, uint Offset, uint Size, byte[] SubHash);

    /// <summary>A stub plus its byte position in the image.</summary>
    public readonly record struct Located(long Position, Stub Stub);

    private const int HeaderSize = 20;
    private const int BlockSize = 12;
    private const int StringSlotSize = 256;
    private const int ClusterSize = 4096;
    private const int StubSize = 40;

    private readonly Dictionary<string, Located> _byName;
    private readonly Dictionary<long, Stub> _stubsByPosition;
    private readonly List<string> _namesInBlockOrder;
    private readonly List<string> _physicalHashes;

    private GffManifest(Dictionary<string, Located> byName, Dictionary<long, Stub> stubsByPosition,
        List<string> namesInBlockOrder, List<string> physicalHashes)
    {
        _byName = byName;
        _stubsByPosition = stubsByPosition;
        _namesInBlockOrder = namesInBlockOrder;
        _physicalHashes = physicalHashes;
    }

    /// <summary>Header-declared entry count: the logical bundles this install's VFS addresses (the
    /// completeness denominator).</summary>
    public int EntryCount { get; private init; }

    /// <summary>Every stored bundle name (internalId, <c>&lt;hash&gt;.bundle</c>) in block order.</summary>
    public IReadOnlyList<string> Names => _namesInBlockOrder;

    /// <summary>Every physical bundle hash addressed by at least one stub, first-seen order. Kept directly
    /// so install completeness checks walk physical files rather than all logical entries.</summary>
    public IReadOnlyList<string> PhysicalHashes => _physicalHashes;

    /// <summary>The manifest file inside <paramref name="bundleDir"/>, or null when absent.</summary>
    public static string? PathIn(string bundleDir)
    {
        var p = Path.Combine(bundleDir, ManifestHash + ".bundle");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Read the manifest into memory and validate header, block table, name table and every
    /// stub position. Any violation throws — a manifest we cannot fully account for must not serve as
    /// an oracle.</summary>
    public static GffManifest Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        if (bytes.Length < HeaderSize || bytes[0] != (byte)'G' || bytes[1] != (byte)'F' || bytes[2] != (byte)'F')
            throw new InvalidDataException($"{name} is not a GFF manifest (bad magic)");
        if (bytes[3] != 0)
            throw new InvalidDataException($"{name}: unsupported GFF version {bytes[3]} (this build reads version 0)");

        ReadOnlySpan<byte> encBytes = bytes.AsSpan(4, 4);
        long maxFileCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        long maxBlockCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        long blockCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));

        long stringsAt = HeaderSize + BlockSize * maxBlockCount;
        long tablesEnd = stringsAt + StringSlotSize * maxFileCount;
        if (blockCount <= 0 || blockCount > maxBlockCount || tablesEnd > bytes.Length)
            throw new InvalidDataException(
                $"{name}: implausible GFF entry count {blockCount} (max blocks {maxBlockCount}, max files {maxFileCount}) for a {bytes.Length}-byte file");

        var byName = new Dictionary<string, Located>((int)blockCount, StringComparer.Ordinal);
        var stubsByPosition = new Dictionary<long, Stub>((int)blockCount);
        var namesInOrder = new List<string>((int)blockCount);
        var physicalHashes = new List<string>();
        var seenPhysical = new HashSet<string>(StringComparer.Ordinal);
        var stubPositions = new HashSet<long>((int)blockCount);   // one stub per cluster
        for (long i = 0; i < blockCount; i++)
        {
            long blockAt = HeaderSize + BlockSize * i;
            int stringIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)blockAt, 4));
            int clusterIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)blockAt + 4, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)blockAt + 8, 4));
            if (length != StubSize)
                throw new InvalidDataException($"{name}: block {i} declares a {length}-byte entry (only {StubSize}-byte stubs exist)");
            if (stringIndex < 0 || stringIndex >= maxFileCount)
                throw new InvalidDataException($"{name}: block {i} points at string slot {stringIndex} of {maxFileCount}");

            long stubPos = (long)clusterIndex * ClusterSize;
            if (stubPos < tablesEnd || stubPos + StubSize > bytes.Length)
                throw new InvalidDataException(
                    $"{name}: block {i} puts its stub at {stubPos}, outside the file's stub region ({tablesEnd}…{bytes.Length})");
            // every block must own a DISTINCT cluster; two names on one cluster would alias one stub
            if (!stubPositions.Add(stubPos))
                throw new InvalidDataException(
                    $"{name}: block {i}'s stub cluster {clusterIndex} is already claimed by another entry. Two names cannot share one stub");

            string entryName = DecodeName(bytes, stringsAt + (long)StringSlotSize * stringIndex, encBytes, name, i);
            var stub = DecodeStub(bytes.AsSpan((int)stubPos, StubSize));
            var located = new Located(stubPos, stub);
            if (!byName.TryAdd(entryName, located))
                throw new InvalidDataException($"{name}: bundle name '{entryName}' appears twice in the name table");
            stubsByPosition.Add(stubPos, stub);
            namesInOrder.Add(entryName);
            if (seenPhysical.Add(stub.PhysHash)) physicalHashes.Add(stub.PhysHash);
        }

        return new GffManifest(byName, stubsByPosition, namesInOrder, physicalHashes)
            { EntryCount = (int)blockCount };
    }

    // ---- compact parsed-manifest snapshot ------------------------------------------------------------

    private const uint SnapshotMagic = 0x53464647;   // "GFFS"
    private const byte SnapshotSchema = 1;

    /// <summary>Load the compact parsed form when it names this exact source path, length and mtime;
    /// otherwise run <see cref="Read"/>'s full structural validation and replace the snapshot.</summary>
    public static GffManifest LoadCached(string path, string snapshotPath) =>
        LoadCached(path, snapshotPath, out _);

    /// <summary><see cref="LoadCached(string, string)"/> with a test/diagnostic seam naming a snapshot hit.</summary>
    internal static GffManifest LoadCached(string path, string snapshotPath, out bool snapshotHit)
    {
        var source = new FileInfo(path);
        string sourcePath = source.FullName;
        long sourceLength = source.Length;
        long sourceMtimeTicks = source.LastWriteTimeUtc.Ticks;
        if (TryLoadSnapshot(snapshotPath, sourcePath, sourceLength, sourceMtimeTicks) is { } cached)
        {
            snapshotHit = true;
            return cached;
        }

        snapshotHit = false;
        var parsed = Read(path);   // the only source path: validates the complete large image before caching
        try { parsed.SaveSnapshot(snapshotPath, sourcePath, sourceLength, sourceMtimeTicks); }
        catch { /* regenerable cache only; the fully validated parse stands */ }
        return parsed;
    }

    private void SaveSnapshot(string path, string sourcePath, long sourceLength, long sourceMtimeTicks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20), Encoding.UTF8))
            {
                w.Write(SnapshotMagic); w.Write(SnapshotSchema);
                w.Write(sourcePath); w.Write(sourceLength); w.Write(sourceMtimeTicks);
                w.Write(EntryCount);
                foreach (string name in _namesInBlockOrder)
                {
                    var located = _byName[name];
                    w.Write(name); w.Write(located.Position);
                    WriteHash(w, located.Stub.PhysHash);
                    w.Write(located.Stub.Offset); w.Write(located.Stub.Size);
                    if (located.Stub.SubHash.Length != 16)
                        throw new InvalidDataException("a GFF stub carries no 16-byte content hash");
                    w.Write(located.Stub.SubHash);
                }
                w.Write(_physicalHashes.Count);
                foreach (string hash in _physicalHashes) WriteHash(w, hash);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    private static GffManifest? TryLoadSnapshot(string path, string sourcePath, long sourceLength,
        long sourceMtimeTicks)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20), Encoding.UTF8);
            if (r.ReadUInt32() != SnapshotMagic || r.ReadByte() != SnapshotSchema) return null;
            if (!string.Equals(r.ReadString(), sourcePath, StringComparison.OrdinalIgnoreCase)
                || r.ReadInt64() != sourceLength || r.ReadInt64() != sourceMtimeTicks)
                return null;
            int count = r.ReadInt32();
            if (count <= 0 || count > 1_000_000) return null;
            var byName = new Dictionary<string, Located>(count, StringComparer.Ordinal);
            var stubsByPosition = new Dictionary<long, Stub>(count);
            var names = new List<string>(count);
            var derivedPhysical = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString();
                long position = r.ReadInt64();
                var stub = new Stub(ReadHash(r), r.ReadUInt32(), r.ReadUInt32(), ReadExactly(r, 16));
                var located = new Located(position, stub);
                if (name.Length == 0 || !byName.TryAdd(name, located)
                    || !stubsByPosition.TryAdd(position, stub)) return null;
                names.Add(name);
                derivedPhysical.Add(stub.PhysHash);
            }
            int physicalCount = r.ReadInt32();
            if (physicalCount <= 0 || physicalCount > count) return null;
            var physical = new List<string>(physicalCount);
            var uniquePhysical = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < physicalCount; i++)
            {
                string hash = ReadHash(r);
                if (!uniquePhysical.Add(hash)) return null;
                physical.Add(hash);
            }
            if (!derivedPhysical.SetEquals(uniquePhysical) || r.BaseStream.Position != r.BaseStream.Length)
                return null;
            return new GffManifest(byName, stubsByPosition, names, physical) { EntryCount = count };
        }
        catch { return null; }
    }

    private static void WriteHash(BinaryWriter writer, string hash)
    {
        byte[] bytes = Convert.FromHexString(hash);
        if (bytes.Length != 16) throw new InvalidDataException("a GFF stub carries no 16-byte physical hash");
        writer.Write(bytes);
    }

    private static string ReadHash(BinaryReader reader) =>
        Convert.ToHexString(ReadExactly(reader, 16)).ToLowerInvariant();

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }

    private static string DecodeName(byte[] bytes, long slotAt, ReadOnlySpan<byte> encBytes, string file, long block)
    {
        int len = bytes[slotAt];
        if (len == 0 || slotAt + 1 + len > bytes.Length)
            throw new InvalidDataException($"{file}: block {block} has an empty/overlong name slot (len {len})");
        Span<char> chars = stackalloc char[len];
        for (int j = 0; j < len; j++)
        {
            byte b = (byte)(bytes[slotAt + 1 + j] ^ encBytes[j % 4]);
            if (b < 0x20 || b > 0x7E)
                throw new InvalidDataException(
                    $"{file}: block {block}'s name does not decode to printable text. Wrong seed or changed format");
            chars[j] = (char)b;
        }
        return new string(chars);
    }

    /// <summary>The stub for a stored bundle name (internalId, with or without <c>.bundle</c>), or
    /// false when the manifest has no such entry.</summary>
    public bool TryLocate(string internalId, out Located located)
    {
        if (!internalId.EndsWith(".bundle", StringComparison.Ordinal)) internalId += ".bundle";
        if (_byName.TryGetValue(internalId, out located))
        {
            return true;
        }
        located = default;
        return false;
    }

    /// <summary>As <see cref="TryLocate"/>, throwing when the name is absent.</summary>
    public Located Locate(string internalId)
    {
        if (!TryLocate(internalId, out var located))
            throw new InvalidDataException($"the game's file manifest has no entry named '{internalId}'");
        return located;
    }

    /// <summary>The stub at a known byte position (its 40 bytes decoded).</summary>
    public Stub ReadStubAt(long position)
    {
        if (_stubsByPosition.TryGetValue(position, out var stub)) return stub;
        throw new ArgumentOutOfRangeException(nameof(position), "the position is not a manifest stub");
    }

    private static Stub DecodeStub(ReadOnlySpan<byte> s)
    {
        string phys = Convert.ToHexString(s.Slice(0, 16)).ToLowerInvariant();
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20, 4));
        return new Stub(phys, off, size, s.Slice(24, 16).ToArray());
    }
}
