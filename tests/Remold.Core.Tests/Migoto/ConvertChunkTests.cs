using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// A pooled Replace whose pool holds more parts than one convert pass can bind constants for converts in
/// chunks, and a pool that fits one pass emits exactly what it always did.
/// </summary>
public class ConvertChunkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-chunk-" + Guid.NewGuid().ToString("N"));

    public ConvertChunkTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "golden");

    /// <summary>One pipeline of <paramref name="partCount"/> parts, each rigging two bones no other part
    /// rigs, and a donor weighted to every bone of the union. The last part anchors; with
    /// <paramref name="anchorTier"/> it also renders at a lower-detail tier rigging the same bones.</summary>
    private string RunPool(int partCount, bool anchorTier = false)
    {
        string dumps = Path.Combine(_root, "dumps");
        var parts = new List<PoolPart>();
        var hashes = new Dictionary<string, string>();
        for (int i = 0; i < partCount; i++)
        {
            string name = $"p{i}";
            uint first = (uint)(100 * (i + 1) + 1);
            SyntheticPool.WritePartDump(Path.Combine(dumps, name), seed: 10 + 7 * i, verts: 64,
                boneHashes: new[] { first, first + 1 });
            parts.Add(new PoolPart(name, Path.Combine(dumps, name)));
            hashes[name] = $"{0xa0000000u + (uint)i:x8}";
        }
        PoolTier[]? tiers = null;
        if (anchorTier)
        {
            string anchor = $"p{partCount - 1}", tier = anchor + "_lod1";
            uint first = (uint)(100 * partCount + 1);
            SyntheticPool.WritePartDump(Path.Combine(dumps, tier), seed: 5, verts: 32,
                boneHashes: new[] { first, first + 1 });
            tiers = new[] { new PoolTier(anchor, tier, "lod1", Path.Combine(dumps, tier), "b0000001") };
        }
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 4 * partCount, unionBones: 2 * partCount, submeshes: 1);

        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = parts,
                    DonorDir = donor,
                    CaptureHashes = hashes,
                    Tiers = tiers,
                },
            },
        });
        ModBuilderTests.AssertNoDuplicateSections(File.ReadAllText(Path.Combine(outDir, "mod.ini")));
        return outDir;
    }

    /// <summary>The whole output of a build as one comparable text: every file by name, the text files
    /// in full, the palette seed, owner table, scatter maps and replacement streams by content hash. The
    /// solved operators (cpinv, sel, off) are listed by name only: they come from a numeric solve the
    /// convert pass never touches.</summary>
    private static string Snapshot(string outDir)
    {
        var sb = new StringBuilder();
        foreach (string path in Directory.GetFiles(outDir).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            string ext = Path.GetExtension(name);
            if (ext is ".ini" or ".hlsl" or ".json")
                sb.Append("=== ").Append(name).Append('\n').Append(File.ReadAllText(path)).Append('\n');
            else if (name.EndsWith("_cpinv.buf", StringComparison.Ordinal)
                || name.EndsWith("_sel.buf", StringComparison.Ordinal)
                || name.EndsWith("_off.buf", StringComparison.Ordinal))
                sb.Append("=== ").Append(name).Append('\n');
            else
                sb.Append("=== ").Append(name).Append(" sha256 ")
                    .Append(System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void An_eight_part_pool_emits_the_pinned_bytes()
    {
        // I1: a pool that fits one convert pass emits the same ini, shaders, file names and palette bytes
        // as before chunking existed
        string outDir = RunPool(8);
        HlslCheck.EveryShaderCompilesClean(File.ReadAllText(Path.Combine(outDir, "mod.ini")), outDir);
        string emitted = Snapshot(outDir);
        string goldenPath = Path.Combine(GoldenDir(), "pool8_emission.txt");
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        if (regold)
            File.WriteAllText(goldenPath, emitted);
        else
        {
            Assert.True(File.Exists(goldenPath), $"golden asset missing: {goldenPath} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(goldenPath), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    /// <summary>One file's text out of the eight-part golden snapshot.</summary>
    private static string GoldenFile(string name)
    {
        string golden = File.ReadAllText(Path.Combine(GoldenDir(), "pool8_emission.txt"));
        string head = $"=== {name}\n";
        int start = golden.IndexOf(head, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} is not in the golden snapshot");
        start += head.Length;
        int end = golden.IndexOf("\n=== ", start, StringComparison.Ordinal);
        return golden[start..end];
    }

    /// <summary>What a stamped convert shader does with a union row whose owner index is
    /// <paramref name="owner"/>, read off its guard and selection lines in order: null = returns without
    /// writing, -1 = writes the zero fallback, otherwise the part whose constants convert the row.</summary>
    private static int? Converts(string shader, uint owner)
    {
        foreach (string raw in shader.Split('\n'))
        {
            string line = raw.Trim();
            var m = Regex.Match(line, @"^if\(pi>=(\d+) && pi<(\d+)\) return;");
            if (m.Success && owner >= uint.Parse(m.Groups[1].Value) && owner < uint.Parse(m.Groups[2].Value))
                return null;
            m = Regex.Match(line, @"^if\(pi<(\d+) \|\| pi>=(\d+)\) return;");
            if (m.Success && (owner < uint.Parse(m.Groups[1].Value) || owner >= uint.Parse(m.Groups[2].Value)))
                return null;
            m = Regex.Match(line, @"^(?:else )?if\(pi==(\d+)\) WP=");
            if (m.Success && owner == uint.Parse(m.Groups[1].Value)) return (int)owner;
            if (line.StartsWith("else WP=float4x4(0,", StringComparison.Ordinal)) return -1;
        }
        throw new InvalidOperationException($"the shader selects nothing for owner {owner}");
    }

    /// <summary>The register a stamped convert shader declares part <paramref name="part"/>'s constants
    /// at, or null when it declares none for that part.</summary>
    private static int? RegisterOf(string shader, int part)
    {
        var m = Regex.Match(shader, $@"cbuffer PartCB{part} : register\(b(\d+)\) {{ float4 W{part}\[4\]; }}");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [Fact]
    public void A_pool_that_fits_one_convert_stamps_the_shader_it_always_did()
    {
        // I1: eight parts bind at b5..b12 with no chunk guard, in the text the single convert always had,
        // and an owner index outside the pool still takes the zero fallback
        string convert = ComputeTemplates.EmitConvert(partCount: 8, unionBones: 16);
        for (int p = 0; p < 8; p++)
            Assert.Equal(5 + p, RegisterOf(convert, p));
        Assert.Contains("cbuffer AnchorCB : register(b13)", convert);
        Assert.Single(Regex.Matches(convert, @"return;"));   // the row-count bound, and nothing else
        Assert.Equal(GoldenFile("convert_cs_swap.hlsl"), convert);
        Assert.Equal(1, ComputeTemplates.ConvertChunks(8));
        Assert.Equal(-1, Converts(convert, 8));
        Assert.Equal(-1, Converts(convert, uint.MaxValue));
    }

    [Fact]
    public void A_ten_part_pool_converts_each_row_in_exactly_one_chunk()
    {
        // I2: the second chunk binds parts 8 and 9 at b5 and b6 beside the anchor's b13, and writes only
        // their rows; the first binds parts 0..7 and leaves parts 8 and 9's rows alone. An owner index
        // outside the pool is written by the first chunk's zero fallback, as it was before chunking.
        Assert.Equal(2, ComputeTemplates.ConvertChunks(10));
        string first = ComputeTemplates.EmitConvert(partCount: 10, unionBones: 20, chunk: 0);
        string second = ComputeTemplates.EmitConvert(partCount: 10, unionBones: 20, chunk: 1);

        for (int p = 0; p < 8; p++)
        {
            Assert.Equal(5 + p, RegisterOf(first, p));
            Assert.Null(RegisterOf(second, p));
        }
        Assert.Equal(5, RegisterOf(second, 8));
        Assert.Equal(6, RegisterOf(second, 9));
        Assert.Null(RegisterOf(first, 8));
        Assert.Null(RegisterOf(first, 9));
        foreach (string shader in new[] { first, second })
        {
            Assert.Contains("cbuffer AnchorCB : register(b13)", shader);
            Assert.Contains("static const uint ROWS=80;", shader);
        }

        for (uint owner = 0; owner < 10; owner++)
        {
            var writers = new[] { Converts(first, owner), Converts(second, owner) };
            Assert.Single(writers, w => w is not null);
            Assert.Contains((int)owner, writers);
            Assert.Equal(owner < 8 ? (int)owner : null, writers[0]);
        }
        foreach (uint outside in new[] { 10u, uint.MaxValue })
        {
            Assert.Equal(-1, Converts(first, outside));
            Assert.Null(Converts(second, outside));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeTemplates.EmitConvert(10, 20, chunk: 2));
    }

    [Fact]
    public void A_ten_part_pool_builds_and_runs_its_convert_chunks_in_order_before_the_palette_is_read()
    {
        // I2, I3, I4: a pool past one convert's width builds; it ships one convert shader and section per
        // chunk, each binding the anchor at cb13, and the anchor's lod0 chain runs them first to last where
        // the single convert ran, after every recover and before the ties and skin that read the palette.
        // The tier chain keeps the witness convert.
        string outDir = RunPool(10, anchorTier: true);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "convert_cs_swap.hlsl")));
        Assert.True(File.Exists(Path.Combine(outDir, "convert_c1_cs_swap.hlsl")));
        Assert.Equal(2, Directory.GetFiles(outDir, "convert_c*_cs_*.hlsl").Length
            + Directory.GetFiles(outDir, "convert_cs_*.hlsl").Length);
        Assert.Equal(ComputeTemplates.EmitConvert(10, 20, 0),
            File.ReadAllText(Path.Combine(outDir, "convert_cs_swap.hlsl")));
        Assert.Equal(ComputeTemplates.EmitConvert(10, 20, 1),
            File.ReadAllText(Path.Combine(outDir, "convert_c1_cs_swap.hlsl")));

        string Section(string name)
        {
            int start = ini.IndexOf($"[{name}]\n", StringComparison.Ordinal);
            Assert.True(start >= 0, $"no [{name}]");
            int end = ini.IndexOf("\n\n", start, StringComparison.Ordinal);
            return ini[start..end];
        }
        string c0 = Section("CustomShaderConvert_swap"), c1 = Section("CustomShaderConvertC1_swap");
        Assert.Contains("cs = convert_cs_swap.hlsl\n", c0);
        Assert.Contains("cs = convert_c1_cs_swap.hlsl\n", c1);
        for (int p = 0; p < 8; p++)
            Assert.Contains($"cs-cb{5 + p} = Resource_p{p}_CB\n", c0);
        Assert.Contains("cs-cb5 = Resource_p8_CB\n", c1);
        Assert.Contains("cs-cb6 = Resource_p9_CB\n", c1);
        Assert.DoesNotContain("Resource_p0_CB", c1);
        Assert.DoesNotContain("Resource_p8_CB", c0);
        foreach (string s in new[] { c0, c1 })
        {
            Assert.Contains("cs-cb13 = Resource_p9_CB\n", s);
            Assert.Contains("cs-u1 = copy Resource_PaletteConv_swap\n", s);
            Assert.EndsWith("Resource_PaletteConv_swap = copy cs-u1\npost cs-u1 = null", s);
        }

        Assert.Contains("run = CustomShaderRecover_p9_swap\n"
            + "run = CustomShaderConvert_swap\n"
            + "run = CustomShaderConvertC1_swap\n"
            + "if $zz_gate_src_p0 == 0\nrun = CustomShaderTie_p0_swap\n", ini);
        Assert.Contains("run = CustomShaderTie_p8_swap\nendif\nrun = CustomShaderSkin_swap\n", ini);
        Assert.Single(Regex.Matches(ini, @"^run = CustomShaderConvert_swap$", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(ini, @"^run = CustomShaderConvertC1_swap$", RegexOptions.Multiline));

        // the tier chain converts through the witness route, which binds no part constants
        Assert.Contains("run = CustomShaderConvertW_swap\n", ini);
        Assert.DoesNotContain("cs-cb", Section("CustomShaderConvertW_swap"));
    }
}
