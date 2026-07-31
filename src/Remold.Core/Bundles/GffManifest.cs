using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

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
    /// <summary>The manifest's physical filename (sans <c>.bundle</c>): <c>md5("AssetBundleFileSystem")</c>.</summary>
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

    private readonly byte[] _bytes;
    private readonly Dictionary<string, long> _nameToPosition;
    private readonly List<string> _namesInBlockOrder;

    private GffManifest(byte[] bytes, Dictionary<string, long> nameToPosition, List<string> namesInBlockOrder)
    {
        _bytes = bytes;
        _nameToPosition = nameToPosition;
        _namesInBlockOrder = namesInBlockOrder;
    }

    /// <summary>Header-declared entry count: the logical bundles this install's VFS addresses (the
    /// completeness denominator).</summary>
    public int EntryCount { get; private init; }

    /// <summary>Every stored bundle name (internalId, <c>&lt;hash&gt;.bundle</c>) in block order.</summary>
    public IReadOnlyList<string> Names => _namesInBlockOrder;

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

        var nameToPosition = new Dictionary<string, long>((int)blockCount, StringComparer.Ordinal);
        var namesInOrder = new List<string>((int)blockCount);
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
            if (!nameToPosition.TryAdd(entryName, stubPos))
                throw new InvalidDataException($"{name}: bundle name '{entryName}' appears twice in the name table");
            namesInOrder.Add(entryName);
        }

        return new GffManifest(bytes, nameToPosition, namesInOrder) { EntryCount = (int)blockCount };
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
        if (_nameToPosition.TryGetValue(internalId, out var pos))
        {
            located = new Located(pos, ReadStubAt(pos));
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
        var s = _bytes.AsSpan((int)position, StubSize);
        string phys = Convert.ToHexString(s.Slice(0, 16)).ToLowerInvariant();
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(20, 4));
        return new Stub(phys, off, size, s.Slice(24, 16).ToArray());
    }
}
