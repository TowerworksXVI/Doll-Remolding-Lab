using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Remold.Core.Mesh;
using Remold.Core.Migoto;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Mesh-dump and donor-stream fixtures in the exact on-disk shape the emitter's loaders consume —
/// deterministic, no game bytes. Shared by the golden-emission pin and the build-orchestration tests.
/// </summary>
public static class SyntheticPool
{
    /// <summary>Deterministic small floats: k spread over ±2 with three decimals, never NaN/huge.</summary>
    private static float F(int k) => ((k * 37 % 400) - 200) / 100f;

    /// <summary>One pool part's mesh-dump dir. Bindposes are identity and identical across parts — union
    /// reconciliation requires consistent bindposes for shared hashes. Each vertex sits fully on bone
    /// <c>v % bones</c>. <paramref name="weightedBones"/> &gt; 0 confines the skin to the FIRST that many,
    /// leaving the rest rigged but unweighted — bones the operator can only recover as min-norm noise, and
    /// that no co-riding bone can stand in for.</summary>
    public static void WritePartDump(string dir, int seed, int verts, uint[] boneHashes, int weightedBones = 0)
    {
        Directory.CreateDirectory(dir);
        int nb = boneHashes.Length;
        int wb = weightedBones > 0 ? Math.Min(weightedBones, nb) : nb;

        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
            for (int c = 0; c < 3; c++)
                BitConverter.GetBytes(F(seed + v * 3 + c)).CopyTo(s0, v * 40 + c * 4);
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s1 = new byte[verts * 20];
        for (int i = 0; i < s1.Length; i++) s1[i] = (byte)(seed + i);
        File.WriteAllBytes(Path.Combine(dir, "stream1.buf"), s1);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes(1f).CopyTo(s2, v * 32);                       // w0 = 1, rest 0
            BitConverter.GetBytes((uint)(v % wb)).CopyTo(s2, v * 32 + 16);      // bi0
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);

        var ib = new byte[verts * 2 * 3];
        for (int i = 0; i < verts * 3; i++)
            BitConverter.GetBytes((ushort)(i % verts)).CopyTo(ib, i * 2);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), ib);

        File.WriteAllText(Path.Combine(dir, "meta.json"),
            $"{{ \"mesh\": \"synthetic\", \"verts\": {verts}, \"boneCount\": {nb}, " +
            "\"indexFormat\": \"R16_UINT\", " +
            $"\"indexBufferBytes\": {ib.Length}, \"streams\": [] }}");

        var bones = string.Join(",", boneHashes.Select(h =>
            $"{{ \"hash\": {h}, \"bindpose\": [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1] }}"));
        File.WriteAllText(Path.Combine(dir, "bindpose.json"),
            $"{{ \"boneCount\": {nb}, \"bones\": [{bones}] }}");
    }

    /// <summary>A part dump with BLENDED weights: four influences from a window of
    /// <paramref name="span"/> bones around the primary, normalized to 1. <see cref="WritePartDump"/>
    /// reduces every slim local system to one bone; THIS exercises the local solve.
    /// <paramref name="traceWeight"/> &gt; 0 pins the fourth influence tiny — the near-null-column shape
    /// whose rcond truncation is the min-norm hazard.</summary>
    public static void WriteBlendedPartDump(string dir, int seed, int verts, uint[] boneHashes, int span,
        float traceWeight = 0)
    {
        WritePartDump(dir, seed, verts, boneHashes);
        int nb = boneHashes.Length;

        // A hash-spread cloud: F() cycles every ~133 vertices, which would collapse a large fixture onto
        // duplicate positions and fake degenerate bone support.
        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
            for (int c = 0; c < 3; c++)
            {
                uint h = (uint)(seed * 97) + (uint)v * 2654435761u + (uint)c * 40503u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                BitConverter.GetBytes(h % 1000 / 250f - 2f).CopyTo(s0, v * 40 + c * 4);
            }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < verts; v++)
        {
            int b0 = v % nb;
            double sum = 0;
            var wt = new double[4];
            for (int j = 0; j < 4; j++) { wt[j] = 1.0 + (v * 31 + j * 17) % 13; sum += wt[j]; }
            for (int j = 0; j < 4; j++) wt[j] /= sum;
            if (traceWeight > 0)
            {
                // The trace slot ROTATES per vertex, so every bone still gets substantial weight
                // somewhere: the hazard under test is trace columns in the local solve, not a bone
                // that is trace-supported everywhere (that one is legitimately weak).
                int tj = v % 4;
                double head = 0;
                for (int j = 0; j < 4; j++) if (j != tj) head += wt[j];
                for (int j = 0; j < 4; j++) if (j != tj) wt[j] *= (1.0 - traceWeight) / head;
                wt[tj] = traceWeight;
            }
            for (int j = 0; j < 4; j++)
            {
                BitConverter.GetBytes((float)wt[j]).CopyTo(s2, v * 32 + j * 4);
                BitConverter.GetBytes((uint)((b0 + (v * 7 + j * 3) % span) % nb)).CopyTo(s2, v * 32 + 16 + j * 4);
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);
    }

    /// <summary>Two bones whose columns are PROPORTIONAL over every vertex either one can anchor on: the
    /// first <paramref name="mixed"/> vertices are 50/50, the rest 90/10, and no vertex carries one bone
    /// alone. B's candidates are ranked by weight, so its selection never reaches the 90/10 region within
    /// the escalation cap, and there is no A-weighted vertex free of B to discriminate with — B holds at no
    /// width but the whole mesh. A escapes because its own ranking puts the 90/10 region first and its
    /// selection spills into the 50/50 one. The shape that forces the per-bone dense-width fallback.
    /// <paramref name="tiedHash"/> adds a third bone on two vertices of its own, co-weighted onto B — too
    /// little support to recover, so it rides B and inherits B's width. Its weight on B (0.3) ranks below
    /// the mixed region B selects from, so B's own verdict is unchanged.</summary>
    public static void WriteProportionalPairDump(string dir, int seed, int mixed, int skew, uint hashA, uint hashB,
        uint? tiedHash = null)
    {
        int verts = mixed + skew + (tiedHash is null ? 0 : 2);
        WritePartDump(dir, seed, verts,
            tiedHash is { } th ? new[] { hashA, hashB, th } : new[] { hashA, hashB });

        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
            for (int c = 0; c < 3; c++)
            {
                uint h = (uint)(seed * 131) + (uint)v * 2654435761u + (uint)c * 40503u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                BitConverter.GetBytes(h % 1000 / 250f - 2f).CopyTo(s0, v * 40 + c * 4);
            }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < mixed + skew; v++)
        {
            float wa = v < mixed ? 0.5f : 0.9f;
            BitConverter.GetBytes(wa).CopyTo(s2, v * 32);
            BitConverter.GetBytes(1f - wa).CopyTo(s2, v * 32 + 4);
            BitConverter.GetBytes(0u).CopyTo(s2, v * 32 + 16);
            BitConverter.GetBytes(1u).CopyTo(s2, v * 32 + 20);
        }
        for (int v = mixed + skew; v < verts; v++)
        {
            BitConverter.GetBytes(0.7f).CopyTo(s2, v * 32);
            BitConverter.GetBytes(0.3f).CopyTo(s2, v * 32 + 4);
            BitConverter.GetBytes(2u).CopyTo(s2, v * 32 + 16);
            BitConverter.GetBytes(1u).CopyTo(s2, v * 32 + 20);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);
    }

    /// <summary>Two bones only the WHOLE mesh separates: the first <paramref name="aOnly"/> vertices are
    /// A-only, the rest exactly 50/50. B's anchors all land in the mixed region where its column is
    /// COLLINEAR with A's, so no anchor-local solve over B's own vertices can hold — but the A-only region
    /// is exactly what a discriminator selection reaches for.</summary>
    public static void WriteCollinearPairDump(string dir, int seed, int aOnly, int mixed, uint hashA, uint hashB)
    {
        int verts = aOnly + mixed;
        WritePartDump(dir, seed, verts, new[] { hashA, hashB });

        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
            for (int c = 0; c < 3; c++)
            {
                uint h = (uint)(seed * 131) + (uint)v * 2654435761u + (uint)c * 40503u;
                h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
                BitConverter.GetBytes(h % 1000 / 250f - 2f).CopyTo(s0, v * 40 + c * 4);
            }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < verts; v++)
        {
            if (v < aOnly)
            {
                BitConverter.GetBytes(1f).CopyTo(s2, v * 32);
                BitConverter.GetBytes(0u).CopyTo(s2, v * 32 + 16);
            }
            else
            {
                BitConverter.GetBytes(0.5f).CopyTo(s2, v * 32);
                BitConverter.GetBytes(0.5f).CopyTo(s2, v * 32 + 4);
                BitConverter.GetBytes(0u).CopyTo(s2, v * 32 + 16);
                BitConverter.GetBytes(1u).CopyTo(s2, v * 32 + 20);
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);
    }

    // ---- bind-space fixtures -----------------------------------------------------------------------

    /// <summary>Replace a dump's positions with a cloud whose every component is STRICTLY NON-ZERO. A zero
    /// component leaves the sign of a rotated copy up to which terms of the row-vector sum are negative
    /// zeros, and a fixture that byte-compares two spellings of one space cannot afford that.</summary>
    public static void NonZeroPositions(string dir)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        for (int v = 0, n = s0.Length / 40; v < n; v++)
        {
            BitConverter.GetBytes((v * 13 % 17) / 4f + 0.75f).CopyTo(s0, v * 40);
            BitConverter.GetBytes((v * 7 % 23) / 5f + 1.25f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes((v * 11 % 29) / 6f + 2.5f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);
    }

    /// <summary>Re-author a part dump in the bind space <paramref name="delta"/> away from the one it was
    /// written in: every bindpose becomes <c>delta·bind</c> and every directional triple rotates by the
    /// inverse. The part still poses identically — but it can no longer ride the original space's shared
    /// palette until something converts it back. <paramref name="delta"/> must be an exact axis-aligned
    /// rotation, or the rewrite is not the bit-exact inverse of the conversion under test.</summary>
    public static void AuthorInSpace(string dir, Matrix4x4 delta)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"),
            PoolMath.RotateVertexStream(s0, Matrix4x4.Transpose(delta)));
        MapBindPoses(dir, b => delta * b);
    }

    /// <summary>Rewrite every bindpose in a dump's <c>bindpose.json</c> through <paramref name="f"/>; bone
    /// hashes and bone order are untouched.</summary>
    public static void MapBindPoses(string dir, Func<Matrix4x4, Matrix4x4> f)
    {
        string path = Path.Combine(dir, "bindpose.json");
        var rows = new List<string>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
            foreach (var b in doc.RootElement.GetProperty("bones").EnumerateArray())
            {
                var v = b.GetProperty("bindpose").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var mapped = BindSpace.ToRowMajor(f(BindSpace.FromRowMajor(v)));
                rows.Add($"{{ \"hash\": {b.GetProperty("hash").GetInt64()}, \"bindpose\": ["
                    + string.Join(",", mapped.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "] }");
            }
        File.WriteAllText(path, $"{{ \"boneCount\": {rows.Count}, \"bones\": [{string.Join(",", rows)}] }}");
    }

    /// <summary>Rewrite ONE bone's bindpose in a dump, by hash; other bones keep theirs.</summary>
    public static void SetBindPose(string dir, uint hash, Matrix4x4 bind)
    {
        var hashes = new List<uint>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "bindpose.json"))))
            foreach (var b in doc.RootElement.GetProperty("bones").EnumerateArray())
                hashes.Add((uint)b.GetProperty("hash").GetInt64());
        int i = 0;
        MapBindPoses(dir, b => hashes[i++] == hash ? bind : b);
    }

    /// <summary>A donor streams dir: vertices weighted round-robin to union bones, its triangles split
    /// evenly into <paramref name="submeshes"/> index ranges (the remainder rides the last).</summary>
    public static void WriteDonor(string dir, int verts, int unionBones, int submeshes = 2)
    {
        Directory.CreateDirectory(dir);

        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
            for (int c = 0; c < 3; c++)
                BitConverter.GetBytes(F(900 + v * 3 + c)).CopyTo(s0, v * 40 + c * 4);
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s1 = new byte[verts * 20];
        for (int i = 0; i < s1.Length; i++) s1[i] = (byte)(3 + i);
        File.WriteAllBytes(Path.Combine(dir, "stream1.buf"), s1);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes(1f).CopyTo(s2, v * 32);
            BitConverter.GetBytes((uint)(v % unionBones)).CopyTo(s2, v * 32 + 16);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);

        int tris = verts;   // a few triangles, indices wrapping the vertex range
        var ib = new byte[tris * 3 * 2];
        for (int i = 0; i < tris * 3; i++)
            BitConverter.GetBytes((ushort)(i % verts)).CopyTo(ib, i * 2);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), ib);

        var meta = new StringBuilder();
        meta.Append("{ \"mesh\": \"donor.swap\", ");
        meta.Append($"\"verts\": {verts}, \"indexFormat\": \"R16_UINT\", ");
        meta.Append("\"submeshes\": [");
        for (int s = 0, done = 0; s < submeshes; s++)
        {
            int take = s + 1 == submeshes ? tris - done : tris / submeshes;
            if (s > 0) meta.Append(", ");
            meta.Append($"{{ \"firstByte\": {done * 3 * 2}, \"indexCount\": {take * 3}, \"baseVertex\": 0 }}");
            done += take;
        }
        meta.Append("] }");
        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToString());
    }

    /// <summary>Bone 0 well-conditioned, bone 1 degenerate (2 vertices at 0.7, co-weighted 0.3 onto
    /// bone 0) — the rigid-tie rescue shape.</summary>
    public static void WriteCoWeightedDump(string dir, uint strongHash, uint weakHash, int strongVerts = 24)
    {
        Directory.CreateDirectory(dir);
        int verts = strongVerts + 2;

        var s0 = new byte[verts * 40];
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes((v * 13 % 17) / 4f).CopyTo(s0, v * 40);
            BitConverter.GetBytes((v * 7 % 23) / 5f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes((v * 11 % 29) / 6f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);

        var s1 = new byte[verts * 20];
        File.WriteAllBytes(Path.Combine(dir, "stream1.buf"), s1);

        var s2 = new byte[verts * 32];
        for (int v = 0; v < verts; v++)
        {
            if (v < strongVerts)
            {
                BitConverter.GetBytes(1f).CopyTo(s2, v * 32);
                BitConverter.GetBytes(0u).CopyTo(s2, v * 32 + 16);
            }
            else
            {
                BitConverter.GetBytes(0.7f).CopyTo(s2, v * 32);
                BitConverter.GetBytes(0.3f).CopyTo(s2, v * 32 + 4);
                BitConverter.GetBytes(1u).CopyTo(s2, v * 32 + 16);
                BitConverter.GetBytes(0u).CopyTo(s2, v * 32 + 20);
            }
        }
        File.WriteAllBytes(Path.Combine(dir, "stream2.buf"), s2);

        var ib = new byte[verts * 2 * 3];
        for (int i = 0; i < verts * 3; i++)
            BitConverter.GetBytes((ushort)(i % verts)).CopyTo(ib, i * 2);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), ib);

        File.WriteAllText(Path.Combine(dir, "meta.json"),
            $"{{ \"mesh\": \"synthetic\", \"verts\": {verts}, \"boneCount\": 2, " +
            "\"indexFormat\": \"R16_UINT\", " +
            $"\"indexBufferBytes\": {ib.Length}, \"streams\": [] }}");
        File.WriteAllText(Path.Combine(dir, "bindpose.json"),
            $"{{ \"boneCount\": 2, \"bones\": [" +
            $"{{ \"hash\": {strongHash}, \"bindpose\": [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1] }}," +
            $"{{ \"hash\": {weakHash}, \"bindpose\": [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1] }}] }}");
    }

}
