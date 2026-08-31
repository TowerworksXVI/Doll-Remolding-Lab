using System;
using System.Collections.Generic;
using System.Linq;
using AssetsTools.NET;

namespace Remold.Core.Bundles;

/// <summary>One fragment shader variant of a serialized Shader asset: which pass it belongs to, the
/// keyword set it was compiled for, where it binds <c>UnityPerMaterial</c> and at what byte width, the
/// buffer's vector fields (name → byte offset), and the 3DMigoto hash of its shipped DXBC. Everything
/// here is Unity's own serialized reflection — no disassembly, no frame dump.</summary>
public sealed record ShaderVariant(
    string ShaderName,
    int Pass,
    string PassName,
    IReadOnlySet<string> Keywords,
    int? MaterialBufferSlot,
    int MaterialBufferWidth,
    IReadOnlyDictionary<string, int> VectorOffsets,
    string DxbcHash);

/// <summary>
/// Reads a serialized Shader asset's fragment subprograms into <see cref="ShaderVariant"/> rows. Unity
/// ships the binding tables itself (<c>m_ConstantBufferBindings</c>, <c>m_ConstantBuffers</c>, keyword
/// indices) and the raw DXBC containers in an LZ4-block-compressed blob, and 3DMigoto's shader hash is
/// FNV-1 64-bit with offset basis 0 over exactly those DXBC bytes — measured: the offline hashes
/// reproduce the frame-dump-observed ones, and the register maps validate against live dump evidence.
/// </summary>
public static class ShaderReflection
{
    /// <summary>The d3d11 platform id in a Shader asset's <c>platforms</c> list.</summary>
    private const int PlatformD3D11 = 4;

    /// <summary>Every fragment variant of the Shader asset behind <paramref name="shaderField"/>.
    /// Variants whose blob holds no DXBC (stripped or empty entries) are skipped; a shader with no
    /// d3d11 platform yields an empty list.</summary>
    public static IReadOnlyList<ShaderVariant> FragmentVariants(AssetTypeValueField shaderField)
    {
        var parsed = shaderField["m_ParsedForm"];
        string shaderName = parsed["m_Name"].AsString;
        var platforms = shaderField["platforms"]["Array"].Children.Select(c => c.AsInt).ToList();
        int platform = platforms.IndexOf(PlatformD3D11);
        if (platform < 0) return Array.Empty<ShaderVariant>();

        var segments = BlobSegments(shaderField, platform);
        var entries = BlobEntries(segments);

        var variants = new List<ShaderVariant>();
        int passIndex = -1;
        foreach (var sub in parsed["m_SubShaders"]["Array"].Children)
        foreach (var pass in sub["m_Passes"]["Array"].Children)
        {
            passIndex++;
            var names = new Dictionary<int, string>();
            foreach (var pair in pass["m_NameIndices"]["Array"].Children)
                names[pair["second"].AsInt] = pair["first"].AsString;
            string passName = pass["m_State"]["m_Name"].AsString;
            foreach (var sp in pass["progFragment"]["m_SubPrograms"]["Array"].Children)
            {
                uint blobIndex = sp["m_BlobIndex"].AsUInt;
                if (blobIndex >= entries.Length) continue;
                var dxbc = DxbcOf(segments, entries[blobIndex]);
                if (dxbc.IsEmpty) continue;

                var keywords = sp["m_GlobalKeywordIndices"]["Array"].Children
                    .Concat(sp["m_LocalKeywordIndices"]["Array"].Children)
                    .Select(k => Name(names, k.AsInt))
                    .ToHashSet(StringComparer.Ordinal);

                int? upmSlot = null;
                foreach (var binding in sp["m_ConstantBufferBindings"]["Array"].Children)
                    if (Name(names, binding["m_NameIndex"].AsInt) == "UnityPerMaterial")
                        upmSlot = binding["m_Index"].AsInt;
                int upmWidth = 0;
                var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var cb in sp["m_ConstantBuffers"]["Array"].Children)
                {
                    if (Name(names, cb["m_NameIndex"].AsInt) != "UnityPerMaterial") continue;
                    upmWidth = cb["m_Size"].AsInt;
                    foreach (var vector in cb["m_VectorParams"]["Array"].Children)
                        offsets[Name(names, vector["m_NameIndex"].AsInt)] = vector["m_Index"].AsInt;
                }

                variants.Add(new ShaderVariant(shaderName, passIndex, passName, keywords,
                    upmSlot, upmWidth, offsets, Fnv64(dxbc).ToString("x16")));
            }
        }
        return variants;
    }

    private static string Name(Dictionary<int, string> names, int index) =>
        names.TryGetValue(index, out var name) ? name : $"?{index}";

    /// <summary>The blob's decompressed segments for one platform: LZ4-block streams whose sizes the
    /// asset states beside them.</summary>
    private static List<byte[]> BlobSegments(AssetTypeValueField shaderField, int platform)
    {
        var offsets = shaderField["offsets"]["Array"].Children[platform]["Array"].Children
            .Select(c => c.AsUInt).ToList();
        var compressed = shaderField["compressedLengths"]["Array"].Children[platform]["Array"].Children
            .Select(c => c.AsUInt).ToList();
        var decompressed = shaderField["decompressedLengths"]["Array"].Children[platform]["Array"].Children
            .Select(c => c.AsUInt).ToList();
        byte[] blob = shaderField["compressedBlob"]["Array"].AsByteArray;
        var segments = new List<byte[]>(offsets.Count);
        for (int i = 0; i < offsets.Count; i++)
        {
            var segment = new byte[decompressed[i]];
            int produced = Lz4DecodeBlock(blob.AsSpan((int)offsets[i], (int)compressed[i]), segment);
            if (produced != segment.Length)
                throw new InvalidOperationException(
                    $"shader blob segment {i} decompressed to {produced} of {segment.Length} bytes");
            segments.Add(segment);
        }
        return segments;
    }

    /// <summary>Segment 0 opens with the entry table: <c>uint32 count</c> then per entry
    /// <c>(offset, length, segment)</c>; entry <c>i</c> serves <c>m_BlobIndex == i</c>.</summary>
    private static (uint Offset, uint Length, uint Segment)[] BlobEntries(List<byte[]> segments)
    {
        var head = segments[0];
        uint count = BitConverter.ToUInt32(head, 0);
        var entries = new (uint, uint, uint)[count];
        for (int i = 0; i < count; i++)
            entries[i] = (BitConverter.ToUInt32(head, 4 + 12 * i),
                BitConverter.ToUInt32(head, 8 + 12 * i),
                BitConverter.ToUInt32(head, 12 + 12 * i));
        return entries;
    }

    /// <summary>An entry's raw DXBC container — the bytes Unity hands to <c>CreatePixelShader</c>, found
    /// past a short Unity header and sized by the container's own header field. Empty when the entry
    /// holds none.</summary>
    private static ReadOnlySpan<byte> DxbcOf(List<byte[]> segments,
        (uint Offset, uint Length, uint Segment) entry)
    {
        if (entry.Length == 0) return default;
        var data = segments[(int)entry.Segment].AsSpan((int)entry.Offset, (int)entry.Length);
        int at = data.IndexOf("DXBC"u8);
        if (at < 0) return default;
        uint size = BitConverter.ToUInt32(data.Slice(at + 24, 4));
        if (size == 0 || at + size > data.Length) return default;
        return data.Slice(at, (int)size);
    }

    /// <summary>3DMigoto's shader hash (its <c>shader_hash = 3dmigoto</c> default): FNV-1 64-bit with
    /// offset basis 0 over the raw DXBC container.</summary>
    public static ulong Fnv64(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 0;
        foreach (byte b in bytes)
        {
            hash = unchecked(hash * 0x100000001b3UL);
            hash ^= b;
        }
        return hash;
    }

    /// <summary>Raw LZ4 block decode (token/literals/match), the format the shader blob segments use.
    /// Returns the produced byte count.</summary>
    private static int Lz4DecodeBlock(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length)
        {
            byte token = src[s++];
            int literals = token >> 4;
            if (literals == 15) { byte b; do { b = src[s++]; literals += b; } while (b == 255); }
            src.Slice(s, literals).CopyTo(dst.Slice(d));
            s += literals;
            d += literals;
            if (s >= src.Length) break;
            int offset = src[s] | (src[s + 1] << 8);
            s += 2;
            int match = token & 0xF;
            if (match == 15) { byte b; do { b = src[s++]; match += b; } while (b == 255); }
            match += 4;
            int from = d - offset;
            for (int i = 0; i < match; i++) dst[d + i] = dst[from + i];
            d += match;
        }
        return d;
    }
}
