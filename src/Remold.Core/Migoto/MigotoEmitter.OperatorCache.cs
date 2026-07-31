using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Remold.Core.Migoto;

/// <summary>
/// The persistent side of operator conditioning: solving one part's recovery operator is the build's
/// dominant cost and depends on nothing but the mesh and the algorithm, so a solved operator is kept on
/// disk and re-read on the next build of the same mesh.
///
/// <para>A cache hit skips the SOLVE only — the conditioning residual, the slim anchor search and the
/// rigid ties. The part's stream dump is still written and still read: the union reconciliation, the
/// per-part bone ownership and the identity concat all consume it.</para>
/// </summary>
public sealed partial class MigotoEmitter
{
    /// <summary>Bumped BY HAND for the half of the algorithm no constant can show. Exactly these rules are
    /// its scope, and a change to any of them is a bump:
    /// <list type="bullet">
    /// <item>the column-keeping shape of <see cref="PoolMath.LocalPInvRows"/> — the <c>maxKept</c> formula
    /// and the mass order it keeps columns by</item>
    /// <item>the construction of the synthetic probe palette in <see cref="BuildOperator"/> (its rotation
    /// and translation terms)</item>
    /// <item>the slim-vs-dense size formula, and the ragged assembly layout it ships</item>
    /// <item>the anchor and discriminator SELECTION rules, how K escalates between them, and the tie
    /// choice</item>
    /// </list>
    /// Everything else the solve is parameterised by is a constant, and <see cref="AlgoVersion"/> stamps
    /// those directly — they are not this revision's job. An entry written by another revision is not this
    /// revision's answer, so it is never read.</summary>
    const int OperatorAlgoRevision = 2;

    /// <summary>The identity a cached operator is valid under: the hand-bumped revision plus the VALUE of
    /// every constant the solve is parameterised by, so editing one of them retires the entries it would
    /// have changed without anyone remembering to bump. Round-trip ("r") formatting, so two gates that
    /// differ below print precision still key apart.</summary>
    static string AlgoVersion() => string.Create(CultureInfo.InvariantCulture,
        $"op{OperatorAlgoRevision}|err{OperatorErrGate:r}|def{SlimDefectGate:r}|cap{string.Join('.', CapDivisors)}" +
        $"|k{KStart}.{KCap}|rcond{OperatorRcond:r}|tie{TieCoWeightFloor:r}" +
        $"|disc{PoolMath.DiscriminatorTargetFloor:r}");

    /// <summary>The full identity a solved operator is filed under, or null when this build is not caching
    /// (no cache root wired) or the caller could not name the mesh (no key). The operator NAME joins it: the
    /// name is embedded in the diagnostics the payload carries. So does the pool's bind-space CONVERSION:
    /// the solve runs on the part's geometry AFTER it, so a solve from an unconverted pool is not a
    /// converted pool's answer.</summary>
    string? OperatorCacheKey(string? opKey, string name, Matrix4x4? conversion) =>
        OperatorCacheDir is null || opKey is null
            ? null : $"{AlgoVersion()}|{name}|{opKey}{ConversionTag(conversion)}";

    /// <summary>The conversion's nine rotation entries, or nothing at all when there is none — a part in
    /// its pool's reference space keys exactly as it does in a pool that converts nothing. A snapped
    /// conversion's entries are exactly 0 or ±1, so the tag is stable build to build.</summary>
    static string ConversionTag(Matrix4x4? conversion) => conversion is { } d
        ? $"|conv{(int)d.M11}.{(int)d.M12}.{(int)d.M13}"
          + $".{(int)d.M21}.{(int)d.M22}.{(int)d.M23}"
          + $".{(int)d.M31}.{(int)d.M32}.{(int)d.M33}"
        : "";

    /// <summary>Where the operator with this identity lives. The name is a hash of the identity, which the
    /// payload repeats so a hash collision reads as a miss rather than as another mesh's operator.</summary>
    string OperatorCachePath(string key) =>
        Path.Combine(OperatorCacheDir!,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".op");

    /// <summary>The cached operator at <paramref name="path"/>, or null on any miss — absent, unreadable,
    /// truncated, or written under a different key that hashed the same. A miss costs the solve, never a
    /// wrong answer.
    ///
    /// <para>Every length in the payload is checked against the bytes actually left in the file before
    /// anything is allocated for it, so a corrupt or crafted prefix is a cheap miss rather than an
    /// allocation the build dies on. The filter exempts nothing for the same reason: whatever a damaged
    /// entry manages to raise, the solve is still available.</para></summary>
    static OperatorArt? ReadCachedOperator(string path, string key)
    {
        try
        {
            using var f = File.OpenRead(path);
            using var r = new BinaryReader(f, Encoding.UTF8);
            if (ReadBoundedString(f, r) != CacheFormat || ReadBoundedString(f, r) != key) return null;
            int n = r.ReadInt32();
            var cpinv = ReadFloats(f, r);
            var weak = ReadBools(f, r);
            var tie = ReadInts(f, r);
            var hashes = ReadUInts(f, r);
            int dc = Count(f, r, bytesEach: 1);      // a string costs at least its own length prefix
            var diagnostics = new List<string>(dc);
            for (int i = 0; i < dc; i++) diagnostics.Add(ReadBoundedString(f, r));
            var sel = r.ReadBoolean() ? ReadUInts(f, r) : null;
            var off = r.ReadBoolean() ? ReadUInts(f, r) : null;
            return new OperatorArt(cpinv, weak, tie, hashes, diagnostics, sel, off, n);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>An element count read from the payload, refused when the elements it claims cannot fit in
    /// what is left of the file. The refusal is what keeps a garbage prefix from becoming an allocation:
    /// the entry is bounded by its own size, so no valid one can fail this.</summary>
    static int Count(Stream f, BinaryReader r, int bytesEach)
    {
        int count = r.ReadInt32();
        if (count < 0 || (long)count * bytesEach > f.Length - f.Position)
            throw new InvalidDataException($"cached operator claims {count} elements the file cannot hold");
        return count;
    }

    /// <summary>A length-prefixed UTF-8 string, with the same bound on its own prefix — the framing
    /// <see cref="BinaryWriter.Write(string)"/> produces, read without letting the prefix size an allocation
    /// on its own.</summary>
    static string ReadBoundedString(Stream f, BinaryReader r)
    {
        int len = r.Read7BitEncodedInt();
        if (len < 0 || len > f.Length - f.Position)
            throw new InvalidDataException($"cached operator claims a {len}-byte string the file cannot hold");
        var bytes = r.ReadBytes(len);
        if (bytes.Length != len) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Publish a solved operator atomically (unique temp + move), so a reader on another build
    /// never sees a partial payload. A write that fails leaves no entry and is not reported: the operator
    /// itself is in hand either way.</summary>
    static void WriteCachedOperator(string path, string key, OperatorArt art)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            CacheTemps.SweepOnce(dir);
            using (var f = File.Create(tmp))
            using (var w = new BinaryWriter(f, Encoding.UTF8))
            {
                w.Write(CacheFormat);
                w.Write(key);
                w.Write(art.N);
                WriteFloats(w, art.Cpinv);
                WriteBools(w, art.Weak);
                WriteInts(w, art.Tie);
                WriteUInts(w, art.Hashes);
                w.Write(art.Diagnostics.Count);
                foreach (var d in art.Diagnostics) w.Write(d);
                w.Write(art.Sel is not null);
                if (art.Sel is { } s) WriteUInts(w, s);
                w.Write(art.Off is not null);
                if (art.Off is { } o) WriteUInts(w, o);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Publishing is best-effort in every one of its failure modes — a full disk, a locked entry, a
            // cache root that cannot be created: the solved operator is in hand either way.
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Payload layout tag; a change to the field list changes it, so old entries read as a miss.</summary>
    const string CacheFormat = "remold-operator-1";

    static void WriteFloats(BinaryWriter w, float[] a) { w.Write(a.Length); foreach (var v in a) w.Write(v); }
    static void WriteInts(BinaryWriter w, int[] a) { w.Write(a.Length); foreach (var v in a) w.Write(v); }
    static void WriteUInts(BinaryWriter w, uint[] a) { w.Write(a.Length); foreach (var v in a) w.Write(v); }
    static void WriteBools(BinaryWriter w, bool[] a) { w.Write(a.Length); foreach (var v in a) w.Write(v); }

    static float[] ReadFloats(Stream f, BinaryReader r)
    {
        var a = new float[Count(f, r, sizeof(float))];
        for (int i = 0; i < a.Length; i++) a[i] = r.ReadSingle();
        return a;
    }

    static int[] ReadInts(Stream f, BinaryReader r)
    {
        var a = new int[Count(f, r, sizeof(int))];
        for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32();
        return a;
    }

    static uint[] ReadUInts(Stream f, BinaryReader r)
    {
        var a = new uint[Count(f, r, sizeof(uint))];
        for (int i = 0; i < a.Length; i++) a[i] = r.ReadUInt32();
        return a;
    }

    static bool[] ReadBools(Stream f, BinaryReader r)
    {
        var a = new bool[Count(f, r, sizeof(bool))];
        for (int i = 0; i < a.Length; i++) a[i] = r.ReadBoolean();
        return a;
    }
}
