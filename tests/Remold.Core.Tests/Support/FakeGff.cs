using System;
using System.Buffers.Binary;
using System.IO;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A synthetic GFF VFS manifest in the REAL on-disk layout <see cref="Core.Bundles.GffManifest"/> parses:
/// 20-byte header, identity block directory, XOR-scrambled 256-byte name slots, one 4096-byte cluster per
/// 40-byte stub at the file-absolute <c>ClusterIndex*4096</c>.
/// </summary>
internal static class FakeGff
{
    public static readonly byte[] EncBytes = { 0x24, 0x6D, 0x17, 0x56 };

    /// <summary>One 40-byte stub: physHash(16) + offset(4) + size(4) + subHash(16 × <paramref name="subSeed"/>).</summary>
    public static byte[] Stub(string physHash, uint offset, uint size, byte subSeed = 0x5A)
    {
        var b = new byte[40];
        Convert.FromHexString(physHash).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16, 4), offset);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20, 4), size);
        for (int i = 24; i < 40; i++) b[i] = subSeed;
        return b;
    }

    /// <summary>Write a full real-layout manifest image holding exactly these named entries.</summary>
    public static void Write(string path, params (string Name, byte[] Stub)[] entries)
    {
        int n = entries.Length;
        long stringsAt = 20 + 12L * n;
        long tablesEnd = stringsAt + 256L * n;
        int firstCluster = (int)((tablesEnd + 4095) / 4096);

        var image = new byte[(firstCluster + n) * 4096L];
        image[0] = (byte)'G'; image[1] = (byte)'F'; image[2] = (byte)'F'; image[3] = 0;
        EncBytes.CopyTo(image, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8, 4), (uint)n);    // MaxFileCount
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12, 4), (uint)n);   // MaxBlockCount
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16, 4), (uint)n);   // BlockCount

        for (int i = 0; i < n; i++)
        {
            long blockAt = 20 + 12L * i;
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan((int)blockAt, 4), i);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan((int)blockAt + 4, 4), firstCluster + i);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan((int)blockAt + 8, 4), 40);

            var (name, stub) = entries[i];
            long slotAt = stringsAt + 256L * i;
            image[slotAt] = (byte)name.Length;
            for (int j = 0; j < name.Length; j++)
                image[slotAt + 1 + j] = (byte)((byte)name[j] ^ EncBytes[j % 4]);

            stub.CopyTo(image, (firstCluster + i) * 4096L);
        }
        File.WriteAllBytes(path, image);
    }

    /// <summary>One whole-file entry per seeded single-bundle fixture — the live singles identity rule.</summary>
    public static void WriteWholeFileStubs(string manifestPath, params string[] bundleHashes)
    {
        var entries = new (string, byte[])[bundleHashes.Length];
        for (int i = 0; i < bundleHashes.Length; i++)
            entries[i] = (bundleHashes[i] + ".bundle", Stub(bundleHashes[i], 0, 0, (byte)(i + 1)));
        Write(manifestPath, entries);
    }
}
