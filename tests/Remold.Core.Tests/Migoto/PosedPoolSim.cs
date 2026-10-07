using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Remold.Core.Mesh;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Plays the game's half of a pooled swap against an emission's SHIPPED files: pose a dumped mesh the way
/// the game would — every vertex skinned with that mesh's OWN bind poses — then run the shipped recovery
/// operator over the posed positions exactly as the recover shader does, and hand back the palette rows it
/// scatters. What a test then asks is the only thing the donor ever sees: is the row for bone b the skin
/// matrix <c>Bind_reference · World_b</c>?
/// </summary>
internal static class PosedPoolSim
{
    /// <summary>A deterministic rigid pose per bone, different for every hash and nowhere near identity, so
    /// a bind carried on the wrong side of the world matrix cannot pass by commuting with it.</summary>
    public static Matrix4x4 WorldOf(uint hash)
    {
        float a = (hash % 997) / 997f * 5.1f + 0.3f, b = (hash % 613) / 613f * 4.3f + 0.2f;
        return Matrix4x4.CreateFromYawPitchRoll(a, b, a * 0.37f)
             * Matrix4x4.CreateTranslation(MathF.Sin(a) * 1.5f, MathF.Cos(b) * 1.1f, MathF.Sin(a + b));
    }

    public static (uint[] Hashes, Matrix4x4[] Binds) ReadBinds(string dumpDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dumpDir, "bindpose.json")));
        var hashes = new List<uint>();
        var binds = new List<Matrix4x4>();
        foreach (var bone in doc.RootElement.GetProperty("bones").EnumerateArray())
        {
            hashes.Add((uint)bone.GetProperty("hash").GetInt64());
            binds.Add(BindSpace.FromRowMajor(bone.GetProperty("bindpose").EnumerateArray().Select(e => e.GetDouble()).ToArray()));
        }
        return (hashes.ToArray(), binds.ToArray());
    }

    /// <summary>The bones a dump POSES: nonzero summed weight in its skin stream.</summary>
    public static HashSet<uint> Posed(string dumpDir, IReadOnlyList<uint> hashes)
    {
        var s2 = File.ReadAllBytes(Path.Combine(dumpDir, "stream2.buf"));
        var posed = new HashSet<uint>();
        for (int o = 0; o + 32 <= s2.Length; o += 32)
            for (int k = 0; k < 4; k++)
                if (BitConverter.ToSingle(s2, o + k * 4) > 0) posed.Add(hashes[(int)BitConverter.ToUInt32(s2, o + 16 + k * 4)]);
        return posed;
    }

    /// <summary>The palette rows the shipped operator of <paramref name="name"/> writes for pipeline
    /// <paramref name="sfx"/>, by palette slot, from the mesh in <paramref name="dumpDir"/> posed under
    /// <see cref="WorldOf"/>. The pipeline's converted operator copy is read where one shipped, else the
    /// shared solved one — the same choice the ini makes.</summary>
    public static Dictionary<uint, Matrix4x4> Recover(string outDir, string sfx, string name, string dumpDir)
    {
        var (hashes, binds) = ReadBinds(dumpDir);
        var s0 = File.ReadAllBytes(Path.Combine(dumpDir, "stream0.buf"));
        var s2 = File.ReadAllBytes(Path.Combine(dumpDir, "stream2.buf"));
        int n = s0.Length / 40;
        var posed = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            var p = new Vector3(BitConverter.ToSingle(s0, v * 40), BitConverter.ToSingle(s0, v * 40 + 4),
                BitConverter.ToSingle(s0, v * 40 + 8));
            var acc = Vector3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = BitConverter.ToSingle(s2, v * 32 + k * 4);
                if (w <= 0) continue;
                int bi = (int)BitConverter.ToUInt32(s2, v * 32 + 16 + k * 4);
                acc += w * Vector3.Transform(p, binds[bi] * WorldOf(hashes[bi]));
            }
            posed[v] = acc;
        }

        string converted = Path.Combine(outDir, $"{name}_cpinv_{sfx}.buf");
        var cp = Floats(File.Exists(converted) ? converted : Path.Combine(outDir, $"{name}_cpinv.buf"));
        var map = UInts(Path.Combine(outDir, $"{name}_map_{sfx}.buf"));
        var sel = SyntheticPool.ShippedSel(outDir, name);
        var off = sel is null ? null : UInts(Path.Combine(outDir, $"{name}_off.buf"));

        var rows = new Dictionary<uint, Matrix4x4>();
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] == 0xFFFFFFFF) continue;
            var m = new float[4, 3];
            for (int r = 0; r < 4; r++)
            {
                var a = Vector3.Zero;
                if (sel is not null)
                {
                    int bas = (int)off![2 * i], width = (int)off[2 * i + 1];
                    for (int t = 0; t < width; t++) a += cp[4 * bas + r * width + t] * posed[sel[bas + t]];
                }
                else
                    for (int v = 0; v < n; v++) a += cp[(4 * i + r) * n + v] * posed[v];
                m[r, 0] = a.X; m[r, 1] = a.Y; m[r, 2] = a.Z;
            }
            rows[map[i]] = new Matrix4x4(
                m[0, 0], m[0, 1], m[0, 2], 0, m[1, 0], m[1, 1], m[1, 2], 0,
                m[2, 0], m[2, 1], m[2, 2], 0, m[3, 0], m[3, 1], m[3, 2], 1);
        }
        return rows;
    }

    /// <summary>Largest element of <c>row − expected</c>.</summary>
    public static float Error(Matrix4x4 row, Matrix4x4 expected)
    {
        var d = row - expected;
        float m = 0;
        foreach (float e in new[]
                 {
                     d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33, d.M41, d.M42, d.M43,
                 })
            m = MathF.Max(m, MathF.Abs(e));
        return m;
    }

    static float[] Floats(string path)
    {
        var b = File.ReadAllBytes(path);
        var f = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, f, 0, f.Length * 4);
        return f;
    }

    static uint[] UInts(string path)
    {
        var b = File.ReadAllBytes(path);
        var u = new uint[b.Length / 4];
        Buffer.BlockCopy(b, 0, u, 0, u.Length * 4);
        return u;
    }
}
