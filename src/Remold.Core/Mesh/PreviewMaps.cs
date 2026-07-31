using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Mesh;

/// <summary>Which slot of a preview material an image fills.</summary>
public enum MapKind { BaseColor, Normal, Rmo }

/// <summary>Where a submesh's map came from once an edited glb is read back.</summary>
public enum MapOrigin
{
    /// <summary>No image in that slot; the submesh inherits the anchor's maps.</summary>
    None,
    /// <summary>Byte-identical to what was embedded; the submesh keeps its stock map.</summary>
    Vanilla,
    /// <summary>Swapped or painted; ships as an authored map.</summary>
    Authored,
    /// <summary>The shipped neutral normal, plugged in deliberately. Nothing ships: the build binds its
    /// own neutral resource on that slot.</summary>
    Neutral,
}

/// <summary><see cref="Origin"/> plus its payload (both null for <see cref="MapOrigin.None"/> and
/// <see cref="MapOrigin.Neutral"/>). <see cref="AuthoredPng"/> comes back in STOCK-PNG space — top-down
/// rows, packed channels — so it encodes by the same rule as an exported map.</summary>
public readonly record struct ResolvedMap(MapOrigin Origin, string? StockPng = null, byte[]? AuthoredPng = null);

/// <summary>One submesh's resolved map slots, in the order the glb's primitives appear.
/// <paramref name="MaterialName"/> is the returned primitive's own material name (what the modder sees
/// in Blender's slot list), empty when it has none.</summary>
public readonly record struct IncomingMaps(ResolvedMap BaseColor, ResolvedMap Normal, ResolvedMap Rmo = default,
    string MaterialName = "");

/// <summary>
/// The preview maps embedded in a Blender-facing glb, and their identity on the way back.
///
/// <para>An embedded image is a DERIVATIVE of the stock PNG: packed normals are unpacked to glTF's RGB
/// tangent normal, an RMO's channels are permuted into glTF's ORM order, and every image is re-encoded.
/// Row order is NOT one of the differences — both spaces are top-down. <see cref="ToPreview"/> and
/// <see cref="FromPreview"/> MUST stay exact inverses.</para>
///
/// <para>Identity is the image's CONTENT, recorded in a sidecar written by the same call that writes the
/// glb (so the two cannot drift). Blender re-packs without re-encoding, so an untouched map returns
/// byte-identical and hashes to its sidecar entry; anything else is authored. Material and image names,
/// duplication and slot order do not enter the decision.</para>
///
/// <para>The sidecar also records, per (mesh, submesh), the stock RMO that submesh's material was built
/// over. glTF has no channel for the emissive mask an RMO carries in alpha, so an authored RMO's mask is
/// read back off that map at intake — the export's own answer, not a re-derivation that could disagree
/// with it.</para>
///
/// <para>The sidecar also names the workspace's shipped neutral normal (<see cref="NeutralN"/>), so a normal
/// slot the modder filled with it classifies as <see cref="MapOrigin.Neutral"/> by the same content match.
/// That is the whole "neutralize this slot" gesture: plug the file, no separate control. The normal slot is
/// the only one it covers — <see cref="NeutralRmo"/> is ordinary content, and an RMO carrying it comes back
/// authored like any painted map.</para>
/// </summary>
public static class PreviewMaps
{
    /// <summary>The workspace's flat neutral normal — plugging it into a normal slot in Blender asks the
    /// build for its own neutral bind.</summary>
    public const string NeutralN = "neutral_n.png";

    /// <summary>The workspace's flat RMO, in glTF's ORM order — a ready starting map for an RMO painted from
    /// scratch. Ordinary content, not a sentinel: a slot filled with it ships as an authored map.</summary>
    public const string NeutralRmo = "neutral_rmo.png";

    /// <summary>One image the sidecar can identify by content: the hash, the workspace PNG behind it, the
    /// slot kind it was recorded for, and what a match MEANS. <see cref="MapOrigin.Vanilla"/> entries are
    /// images embedded from a stock map; a <see cref="MapOrigin.Neutral"/> entry is the shipped neutral
    /// normal, which is never embedded and is matched only when the modder plugs it in; an
    /// <see cref="MapOrigin.Authored"/> entry records a map of the MODDER's own that an export embedded, and
    /// classifies nothing (see <see cref="ReadSidecar"/>).
    /// Byte-identical stock maps share a hash, so the reported source is a best match, not an identity.
    ///
    /// <para><see cref="Owner"/> is the mesh whose preview material embedded the image. A combined glb holds
    /// several parts over one image cache, and which part a stock map belongs to is the whole difference
    /// between "bound to its own map" and a deliberate sibling link, so an image two parts share is recorded
    /// once per owner. Empty on the shipped neutral, which belongs to no part.</para></summary>
    public readonly record struct Entry(
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("kind")] MapKind Kind,
        [property: JsonPropertyName("origin")] MapOrigin Origin = MapOrigin.Vanilla,
        [property: JsonPropertyName("owner")] string Owner = "");

    /// <summary>The stock RMO one submesh's preview material was built over: the mesh it belongs to, its
    /// primitive index within that mesh, and the workspace PNG. Alpha carries the emissive mask and has no
    /// glTF channel to travel in, so an authored RMO is rebuilt over the alpha of the map recorded here.
    /// A combined glb holds several meshes, so the mesh name is part of the identity.</summary>
    public readonly record struct SubmeshSource(
        [property: JsonPropertyName("mesh")] string Mesh,
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("rmo")] string Rmo);

    private sealed class Sidecar
    {
        [JsonPropertyName("images")] public List<Entry> Images { get; set; } = new();
        [JsonPropertyName("submeshes")] public List<SubmeshSource> Submeshes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SidecarPath(string glbPath) => Path.ChangeExtension(glbPath, ".maps.json");

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A readable name for an embedded image so Blender's UI doesn't show <c>Image_0</c>.
    /// Diagnostic only.</summary>
    public static string ImageName(string pngPath, MapKind kind) =>
        Path.GetFileNameWithoutExtension(pngPath).Split('.')[0]
        + kind switch { MapKind.Normal => "_nrm", MapKind.Rmo => "_rmo", _ => "_base" };

    // ---------------------------------------------------------------- the paired transforms

    /// <summary>Stock PNG to the form embedded in the glb. Rows are untouched — both spaces are top-down.
    /// Inverse of <see cref="FromPreview"/>.
    ///
    /// <para><see cref="MapKind.Normal"/> is unpacked and <see cref="MapKind.Rmo"/> is permuted into glTF's
    /// ORM order; <see cref="MapKind.BaseColor"/> is re-encoded and otherwise passes through, since glTF
    /// reads its channels exactly as the game packs them.</para></summary>
    public static byte[] ToPreview(string pngPath, MapKind kind)
    {
        using var img = PreviewImage(pngPath, kind);
        return SavePng(img);
    }

    /// <summary>The same transform, stopping at the pixels — for a caller that compares rather than embeds
    /// and has no use for the encode.</summary>
    private static Image<Rgba32> PreviewImage(string pngPath, MapKind kind)
    {
        var img = Image.Load<Rgba32>(pngPath);
        if (kind == MapKind.Normal) UnpackNormal(img);
        else if (kind == MapKind.Rmo) RmoToOrm(img);
        return img;
    }

    /// <summary>An image out of a returned glb, back to stock-PNG space. Inverse of
    /// <see cref="ToPreview"/>: a normal is re-packed, an ORM is permuted back, a base colour passes
    /// through.</summary>
    public static byte[] FromPreview(byte[] previewPng, MapKind kind)
    {
        using var img = Image.Load<Rgba32>(previewPng);
        if (kind == MapKind.Normal) PackNormal(img);
        else if (kind == MapKind.Rmo) OrmToRmo(img);
        return SavePng(img);
    }

    // ---------------------------------------------------------------- the shipped neutrals

    /// <summary>The flat content of each map, in the space the slot that reads it works in: a flat tangent
    /// normal, and an RMO in glTF's ORM order — an ORM node in Blender reads the game's own order as a
    /// polished chrome. The normal is what the build's flat-map writer binds, in the GAME's order, so the two
    /// match pixel for pixel and may not drift.</summary>
    private static (byte R, byte G, byte B, byte A) NeutralPixel(MapKind kind) =>
        kind == MapKind.Rmo ? ((byte)255, (byte)128, (byte)0, (byte)0)
                            : ((byte)128, (byte)128, (byte)255, (byte)255);

    /// <summary>Put the two flat maps in a workspace <c>textures/</c> folder: plugging <see cref="NeutralN"/>
    /// into a normal slot in Blender is how that slot gets blanked, and <see cref="NeutralRmo"/> is a flat RMO
    /// to paint over. Written only where absent: an existing file is what the sidecar recorded and what any
    /// open session already carries.</summary>
    public static void WriteNeutrals(string texturesDir)
    {
        Directory.CreateDirectory(texturesDir);
        Write(NeutralN, MapKind.Normal);
        Write(NeutralRmo, MapKind.Rmo);

        void Write(string name, MapKind kind)
        {
            var path = Path.Combine(texturesDir, name);
            if (File.Exists(path)) return;
            var (r, g, b, a) = NeutralPixel(kind);
            using var img = new Image<Rgba32>(NeutralSize, NeutralSize, new Rgba32(r, g, b, a));
            img.SaveAsPng(path);
        }
    }

    /// <summary>Flat content needs no resolution; this is the size the build's own neutral uses.</summary>
    private const int NeutralSize = 8;

    /// <summary>Sidecar entries for the neutral normal, wherever one sits beside the stock PNGs an export
    /// embedded. It lives in the same <c>textures/</c> folder, so the embedded sources locate it without the
    /// codec being told where the workspace is. <see cref="NeutralRmo"/> gets no entry: only the normal slot
    /// reads its flat map as an ask to blank.</summary>
    private static IEnumerable<Entry> NeutralEntries(IEnumerable<Entry> embedded)
    {
        var dirs = embedded
            .Select(e => Path.GetDirectoryName(Path.GetFullPath(e.Source)))
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir!, NeutralN);
            if (File.Exists(path))
                yield return new Entry(Hash(File.ReadAllBytes(path)), path, MapKind.Normal, MapOrigin.Neutral);
        }
    }

    /// <summary>Packed tangent normal (X in alpha, Y in green) to the RGB normal glTF expects,
    /// reconstructing Z.
    /// X and Y must move as BYTES, never through the float encoding: negating Y is exactly
    /// <c>255 - g</c>, which makes this and <see cref="PackNormal"/> inverses by construction. Only Z
    /// needs arithmetic, and Z is display-only — the shader recomputes it, the inverse drops it.</summary>
    private static void UnpackNormal(Image<Rgba32> img) => img.ProcessPixelRows(rows =>
    {
        for (int y = 0; y < rows.Height; y++)
        {
            var row = rows.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x];
                float nx = p.A / 255f * 2f - 1f;
                float ny = -(p.G / 255f * 2f - 1f);
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny));
                row[x] = new Rgba32(p.A, (byte)(255 - p.G), Enc(nz), 255);
            }
        }
    });

    /// <summary>RGB tangent normal back to the packed layout: X to alpha, Y to green un-negated, Z
    /// dropped. R (filler) and B (mirror of G) are rebuilt, not recovered — safe only because this runs
    /// on AUTHORED images; a stock map resolves by content and comes from disk.</summary>
    private static void PackNormal(Image<Rgba32> img)
    {
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                byte g = (byte)(255 - p.G);
                img[x, y] = new Rgba32(255, g, g, p.R);
            }
    }

    /// <summary>The game's packed RMO (R roughness, G metallic, B occlusion, A emissive mask) to the glTF
    /// ORM layout (R occlusion, G roughness, B metallic). A pure byte permutation, so this and
    /// <see cref="OrmToRmo"/> are inverses by construction; alpha rides unchanged, carrying a channel glTF
    /// has no slot for.</summary>
    private static void RmoToOrm(Image<Rgba32> img) => img.ProcessPixelRows(rows =>
    {
        for (int y = 0; y < rows.Height; y++)
        {
            var row = rows.GetRowSpan(y);
            for (int x = 0; x < row.Length; x++)
            {
                var p = row[x];
                row[x] = new Rgba32(p.B, p.R, p.G, p.A);
            }
        }
    });

    /// <summary>The exact inverse of <see cref="RmoToOrm"/>.</summary>
    private static void OrmToRmo(Image<Rgba32> img)
    {
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                img[x, y] = new Rgba32(p.G, p.B, p.R, p.A);
            }
    }

    /// <summary>Signed component to a byte, for the reconstructed Z only. Rounds, not truncates —
    /// truncation biases every value down by up to a step.</summary>
    private static byte Enc(float v) => (byte)Math.Clamp(MathF.Round((v + 1f) * 0.5f * 255f), 0f, 255f);

    private static byte[] SavePng(Image<Rgba32> img)
    {
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- sidecar

    /// <summary>Record what was embedded beside the glb: the images by content, and the stock RMO behind each
    /// submesh that got one. Sources are stored relative to the glb so a mod folder stays movable.</summary>
    public static void WriteSidecar(string glbPath, IEnumerable<Entry> entries, IEnumerable<SubmeshSource> submeshes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        var embedded = entries as IReadOnlyCollection<Entry> ?? entries.ToList();
        var rel = embedded.Concat(NeutralEntries(embedded))
            .Select(e => e with { Source = Rel(dir, e.Source) })
            .DistinctBy(e => (e.Hash, e.Source, e.Kind, e.Owner))
            .ToList();
        if (rel.Count == 0)
        {
            // no maps: clear the sidecar — a stale one would resolve an authored image as stock
            var stale = SidecarPath(glbPath);
            if (File.Exists(stale)) File.Delete(stale);
            return;
        }
        var subs = submeshes.Select(s => s with { Rmo = Rel(dir, s.Rmo) }).ToList();
        File.WriteAllText(SidecarPath(glbPath),
            JsonSerializer.Serialize(new Sidecar { Images = rel, Submeshes = subs }, JsonOpts));
    }

    /// <summary>The sidecar's CLASSIFYING entries for a glb, keyed by content hash AND slot kind, or empty when
    /// there is none — every image then reads as authored, shipping a redundant stock copy rather than losing an
    /// authored map. The kind belongs in the key: the RMO permutation is the identity on any pixel with
    /// R==G==B, so a grayscale map embedded in two slots hashes the same in both, and a hash-only key would keep
    /// one of them and misread the other as authored.
    ///
    /// <para><see cref="MapOrigin.Authored"/> entries are dropped here rather than filtered downstream. They
    /// record what an export embedded of the modder's OWN maps, which nothing classifies against: an image
    /// reproducing one is authored work either way. Left in the dictionary such an entry would also take a
    /// (hash, kind) key a byte-identical STOCK entry needs, and the stock map behind it would stop resolving.
    /// </para></summary>
    public static IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> ReadSidecar(string glbPath)
    {
        var map = new Dictionary<(string, MapKind), Entry>();
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return map;
        var doc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts);
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        foreach (var e in doc?.Images ?? new List<Entry>())
        {
            if (e.Origin == MapOrigin.Authored) continue;
            map.TryAdd((e.Hash.ToLowerInvariant(), e.Kind),
                       e with { Source = Path.GetFullPath(Path.Combine(dir, e.Source)) });
        }
        return map;
    }

    /// <summary>The stock RMO behind each submesh of one mesh in a glb, by primitive index — the alpha source
    /// an authored RMO ships over. <paramref name="meshName"/> null reads the glb's FIRST mesh, the same one
    /// <see cref="MeshGltf.ReadSubmeshMaps"/> reads with no name — the two must key the same part, or a
    /// no-name caller pairs one part's slots with another's alpha. Empty where the glb embedded no RMO, and
    /// a submesh past the recorded ones is simply absent: the intake then ships a zero mask rather than
    /// another submesh's.</summary>
    public static IReadOnlyDictionary<int, string> ReadSubmeshRmoSources(string glbPath, string? meshName = null)
    {
        var sources = new Dictionary<int, string>();
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return sources;
        var rows = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts)?.Submeshes;
        if (rows is not { Count: > 0 }) return sources;
        var mesh = meshName ?? FirstMeshName(glbPath);
        if (mesh is null) return sources;
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        foreach (var r in rows)
            if (string.Equals(r.Mesh, mesh, StringComparison.Ordinal))
                sources.TryAdd(r.Index, Path.GetFullPath(Path.Combine(dir, r.Rmo)));
        return sources;
    }

    /// <summary>The stock PNGs one mesh's own preview materials were embedded from, absolute — what tells a
    /// slot bound to the part's own vanilla map from one deliberately linked to a SIBLING part's. Null when
    /// ownership is unknowable, which leaves the caller with its unknowable case rather than an empty answer
    /// that would read every match as a sibling link: no sidecar, no entry recording an owner at all, or a
    /// sidecar that never mentions the requested mesh — under an owner or a submesh row. A sidecar that DOES
    /// hold the mesh and owns nothing for it answers with the empty set.
    /// <paramref name="meshName"/> resolves as in <see cref="ReadSubmeshRmoSources"/>.</summary>
    public static IReadOnlySet<string>? ReadOwnedStock(string glbPath, string? meshName = null)
    {
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return null;
        var doc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts);
        var images = doc?.Images;
        if (images is not { Count: > 0 } || !images.Any(e => !string.IsNullOrEmpty(e.Owner))) return null;
        var mesh = meshName ?? FirstMeshName(glbPath);
        if (mesh is null) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in images)
            // STOCK entries only: an authored entry records the modder's own map, which is not one of the
            // part's stock images and would widen this set past what it answers about.
            if (e.Origin == MapOrigin.Vanilla && string.Equals(e.Owner, mesh, StringComparison.Ordinal))
                owned.Add(Path.GetFullPath(Path.Combine(dir, e.Source)));
        if (owned.Count == 0
            && !(doc?.Submeshes ?? new List<SubmeshSource>())
                .Any(r => string.Equals(r.Mesh, mesh, StringComparison.Ordinal)))
            return null;
        return owned;
    }

    /// <summary>The glb's first mesh name — the part a no-name read keys on. Null when the glb won't open,
    /// which leaves every no-name read with nothing rather than a guess at which part it holds.</summary>
    private static string? FirstMeshName(string glbPath)
    {
        try { return MeshGltf.MeshNames(glbPath).FirstOrDefault(); }
        catch { return null; }
    }

    private static string Rel(string fromDir, string path) =>
        Path.IsPathRooted(path) ? Path.GetRelativePath(fromDir, path) : path;

    // ---------------------------------------------------------------- resolve

    /// <summary>Classify one slot of a returned material. <paramref name="imageBytes"/> is null when the
    /// slot carries no image. A content match against a shipped neutral is the explicit "blank this slot"
    /// intent; the file's NAME is advisory only, exactly as for stock maps.
    ///
    /// <para>A byte miss falls through to a PIXEL comparison against the images recorded for the slot's kind,
    /// so a re-encode of an untouched map still classifies as whatever it reproduces — a stock map, or the
    /// neutral normal (see <see cref="SamePixelsAsRecorded"/>).</para>
    ///
    /// <para><paramref name="owner"/> is the mesh whose material carries the slot, which decides ties in that
    /// fallback; <paramref name="stock"/> carries what earlier slots already measured about the recorded
    /// images, and one is made per call when the caller keeps none.</para></summary>
    public static ResolvedMap Resolve(byte[]? imageBytes, MapKind kind,
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, string? owner = null,
        StockPixels? stock = null)
    {
        if (imageBytes is null || imageBytes.Length == 0) return new ResolvedMap(MapOrigin.None);
        if (sidecar.TryGetValue((Hash(imageBytes), kind), out var hit))
            return hit.Origin == MapOrigin.Neutral
                ? new ResolvedMap(MapOrigin.Neutral)
                : new ResolvedMap(MapOrigin.Vanilla, StockPng: hit.Source);
        if (SamePixelsAsRecorded(imageBytes, kind, sidecar, owner, stock ?? new StockPixels()) is { } same)
            return same;
        return new ResolvedMap(MapOrigin.Authored, AuthoredPng: FromPreview(imageBytes, kind));
    }

    /// <summary>What this returned image reproduces PIXEL for pixel, alpha included — as the classification a
    /// match earns — or null when nothing recorded does. An encoder that re-compresses an image it never
    /// edited changes its bytes and nothing else, and the hash cannot tell that from a repaint, so what the
    /// image SHOWS decides where the bytes could not. Identical pixels mean the recorded map and the returned
    /// one put the same picture in the slot, so this only ever widens the recorded answer; a single changed
    /// pixel keeps the authored one.
    ///
    /// <para>Reached only on a byte miss, and a candidate is measured once per <paramref name="stock"/>
    /// however many slots ask about it. A candidate whose file is gone or won't decode is skipped — it cannot
    /// settle the slot, and the authored answer stands if nothing else does.</para></summary>
    private static ResolvedMap? SamePixelsAsRecorded(byte[] imageBytes, MapKind kind,
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, string? owner, StockPixels stock)
    {
        Image<Rgba32> returned;
        try { returned = Image.Load<Rgba32>(imageBytes); }
        catch { return null; }
        using (returned)
        {
            var want = Fingerprint(returned);
            foreach (var e in Candidates(sidecar, kind, owner))
            {
                if (stock.Measure(e) != want) continue;
                using var recorded = RecordedPixels(e);
                if (recorded is null || !SamePixels(returned, recorded)) continue;
                return e.Origin == MapOrigin.Neutral
                    ? new ResolvedMap(MapOrigin.Neutral)
                    : new ResolvedMap(MapOrigin.Vanilla, StockPng: e.Source);
            }
        }
        return null;
    }

    /// <summary>The recorded images a slot of this kind could be reproducing, best answer first. The owning
    /// mesh's own images come first: a part's map and a sibling part's can hold the same picture under
    /// different encodings, and taking the sibling's would record a link the modder never made. The rest are
    /// ordered by source path, so two recorded images with identical pixels — one picture under two names —
    /// answer the same on every read.</summary>
    private static IEnumerable<Entry> Candidates(
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, MapKind kind, string? owner) =>
        sidecar.Values
            .Where(e => e.Kind == kind && e.Origin is MapOrigin.Vanilla or MapOrigin.Neutral)
            .OrderBy(e => !string.IsNullOrEmpty(owner)
                          && string.Equals(e.Owner, owner, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(e => e.Source, StringComparer.Ordinal);

    /// <summary>A recorded image's pixels in the space the returned image is compared in. A
    /// <see cref="MapOrigin.Vanilla"/> entry records the stock PNG an export EMBEDDED, so its file goes
    /// through the preview transform first; a <see cref="MapOrigin.Neutral"/> entry records the flat file the
    /// modder plugs in unchanged, which is already in that space. Null when the file is gone or won't
    /// decode.</summary>
    private static Image<Rgba32>? RecordedPixels(Entry e)
    {
        try
        {
            return e.Origin == MapOrigin.Neutral ? Image.Load<Rgba32>(e.Source) : PreviewImage(e.Source, e.Kind);
        }
        catch { return null; }
    }

    /// <summary>An image's size and the hash of its raw rows: enough to rule a candidate out without holding
    /// it decoded, which a session's worth of full-size maps could not afford. What it rules IN is confirmed
    /// pixel by pixel before it classifies anything.</summary>
    private static (int Width, int Height, string Pixels) Fingerprint(Image<Rgba32> img)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
                sha.AppendData(MemoryMarshal.AsBytes(rows.GetRowSpan(y)));
        });
        return (img.Width, img.Height, Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>What a run of slot classifications has already measured about the recorded images. Decoding a
    /// stock map costs far more than comparing one, and a session whose maps all returned re-encoded asks the
    /// same candidates again for every slot. A candidate that could not be read is remembered as such, so it
    /// is not reopened either. Belongs to one read; not thread-safe.</summary>
    public sealed class StockPixels
    {
        private readonly Dictionary<(string Source, MapKind Kind), (int Width, int Height, string Pixels)?> _measured
            = new();

        internal (int Width, int Height, string Pixels)? Measure(Entry e)
        {
            if (_measured.TryGetValue((e.Source, e.Kind), out var known)) return known;
            (int, int, string)? measured = null;
            using (var img = RecordedPixels(e))
                if (img is not null) measured = Fingerprint(img);
            _measured[(e.Source, e.Kind)] = measured;
            return measured;
        }
    }

    private static bool SamePixels(Image<Rgba32> a, Image<Rgba32> b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        bool same = true;
        a.ProcessPixelRows(b, (ra, rb) =>
        {
            for (int y = 0; y < ra.Height && same; y++)
                if (!ra.GetRowSpan(y).SequenceEqual(rb.GetRowSpan(y))) same = false;
        });
        return same;
    }
}
