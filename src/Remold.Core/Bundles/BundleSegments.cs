using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core.Bundles;

/// <summary>
/// The VFS chain-walk over a physical <c>.bundle</c> file. A physical file is a CHAIN of one or more
/// embedded UnityFS bundles ("segments"), each obfuscated separately (first 0x8000 bytes of the segment, key
/// recoverable from the segment's own header) and each carrying an <c>AssetBundle</c> object whose
/// <c>m_Name</c> is its logical bundle name.
/// </summary>
public static class BundleSegments
{
    /// <summary>One embedded bundle's byte range inside the physical file.</summary>
    public readonly record struct Segment(long Offset, long Size);

    /// <summary>The chain-walk outcome. <see cref="UnconsumedBytes"/> is what remains past the last
    /// recognisable segment — 0 for an intact game file; non-zero means a foreign/corrupt tail the caller
    /// must surface rather than ignore.</summary>
    public readonly record struct WalkResult(IReadOnlyList<Segment> Segments, long UnconsumedBytes);

    /// <summary>Walk the raw ON-DISK bytes of a physical bundle file into its embedded segments. Each
    /// header is validated by the same known-plaintext rule the obfuscation codec uses — a PLAIN segment
    /// passes with a zero key, so plain chains walk the same way — and each length is the self-declared
    /// UnityFS total size. The walk stops at the first unrecognisable header; the remainder is
    /// reported.</summary>
    public static WalkResult Walk(byte[] raw)
    {
        var segments = new List<Segment>();
        int off = 0;
        while (raw.Length - off >= 30)
        {
            byte[] key;
            try { key = BundleObfuscation.RecoverKey(raw.AsSpan(off, 30)); }
            catch (ArgumentException) { break; }
            catch (FormatException) { break; }   // not a segment header — foreign tail

            // decode just enough header to read the declared size (magic + version strings + int64)
            int headLen = Math.Min(0x40, raw.Length - off);
            var head = new byte[headLen];
            Array.Copy(raw, off, head, 0, headLen);
            for (int i = 0; i < headLen; i++) head[i] ^= key[i % 16];

            long size = DeclaredSize(head);
            if (size <= 0 || off + size > raw.Length) break;   // corrupt/overrunning declaration
            segments.Add(new Segment(off, size));
            off += (int)size;
        }
        return new WalkResult(segments, raw.Length - off);
    }

    /// <summary>Slice one segment out of the raw file bytes and deobfuscate it (per-segment key from its
    /// own header; a no-op for a plain segment). The result is a standalone plain UnityFS bundle.</summary>
    public static byte[] ExtractPlain(byte[] raw, Segment segment)
    {
        if (segment.Offset < 0 || segment.Size <= 0 || segment.Offset + segment.Size > raw.Length)
            throw new ArgumentOutOfRangeException(nameof(segment),
                $"segment [{segment.Offset}, +{segment.Size}) is outside the file ({raw.Length} bytes)");
        var slice = new byte[segment.Size];
        Array.Copy(raw, segment.Offset, slice, 0, segment.Size);
        BundleObfuscation.Deobfuscate(slice);
        return slice;
    }

    /// <summary>Read ONE segment from a physical bundle file by its known byte range — a seek plus one slice
    /// read, no chain-walk — and deobfuscate it. A range that no longer fits the file (a game update under a
    /// stale index) throws rather than returning bytes from the wrong segment.</summary>
    public static byte[] ReadSegmentPlain(string path, long offset, long size)
    {
        if (offset < 0 || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(offset), $"segment [{offset}, +{size}) is not a valid range");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset + size > fs.Length)
            throw new InvalidDataException(
                $"segment [{offset}, +{size}) is outside {Path.GetFileName(path)} ({fs.Length} bytes). Stale segment map?");
        var slice = new byte[size];
        fs.Position = offset;
        fs.ReadExactly(slice);
        BundleObfuscation.Deobfuscate(slice);
        return slice;
    }

    /// <summary>A UnityFS bundle's self-declared total size, parsed from its PLAIN header bytes:
    /// "UnityFS\0" + u32 format version + two null-terminated version strings + big-endian int64. Returns -1
    /// when the bytes aren't a readable UnityFS header.</summary>
    public static long DeclaredSize(ReadOnlySpan<byte> plainHead)
    {
        ReadOnlySpan<byte> magic = "UnityFS\0"u8;
        if (plainHead.Length < 30 || !plainHead[..8].SequenceEqual(magic)) return -1;
        int p = 8 + 4;                                       // magic + u32 format version
        int z = plainHead[p..].IndexOf((byte)0);             // unity version cstring
        if (z < 0) return -1;
        p += z + 1;
        if (p >= plainHead.Length) return -1;
        z = plainHead[p..].IndexOf((byte)0);                 // unity revision cstring
        if (z < 0) return -1;
        p += z + 1;
        if (p + 8 > plainHead.Length) return -1;
        long size = BinaryPrimitives.ReadInt64BigEndian(plainHead.Slice(p, 8));
        return size > 0 ? size : -1;
    }
}
