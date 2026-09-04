using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Mesh;

/// <summary>Which slot of a preview material an image fills.</summary>
public enum MapKind { BaseColor, Normal, Rmo, Blend, Texture }

/// <summary>One open's transformed preview-image memo, BOUNDED. Source bytes are never retained — only a
/// path→content-hash note (so an unchanged path skips the read entirely when its transform is already
/// held) and the transformed blobs, under a retained-byte ceiling with least-recently-served eviction.
/// Different workspace paths carrying identical PNG bytes share the decode/transform/encode result,
/// including across concurrent part exports; an evicted entry falls back to what every caller did before
/// the memo existed — read and transform again — so the ceiling costs time, never correctness.</summary>
public sealed class PreviewBlobMemo
{
    private const long DefaultMaxRetainedBytes = 256L * 1024 * 1024;

    private readonly long _maxRetainedBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _hashByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string ContentHash, MapKind Kind), LinkedListNode<Entry>> _blobs = new();
    private readonly LinkedList<Entry> _recency = new();   // most recently served at the head
    private long _retainedBytes;

    public PreviewBlobMemo(long maxRetainedBytes = DefaultMaxRetainedBytes) =>
        _maxRetainedBytes = Math.Max(1, maxRetainedBytes);

    internal readonly record struct Blob(byte[] Bytes, string Hash, PreviewMaps.AlphaCoverage Alpha);
    private readonly record struct Entry((string ContentHash, MapKind Kind) Key, Blob Blob);

    /// <summary>Bytes currently retained by held blobs — the number the ceiling bounds. Test seam.</summary>
    internal long RetainedBytes { get { lock (_gate) return _retainedBytes; } }

    internal Blob Get(string pngPath, MapKind kind)
    {
        string full = Path.GetFullPath(pngPath);
        lock (_gate)
        {
            if (_hashByPath.TryGetValue(full, out string? known)
                && TryServeLocked((known, kind), out var held))
                return held;
        }
        // The read, hash and transform run OUTSIDE the lock: they are the expensive part, and two workers
        // minting the same content concurrently just produce byte-identical blobs (one of them is kept).
        byte[] source = File.ReadAllBytes(full);
        string contentHash = PreviewMaps.Hash(source);
        lock (_gate)
        {
            _hashByPath[full] = contentHash;
            if (TryServeLocked((contentHash, kind), out var held)) return held;
        }
        byte[] transformed = PreviewMaps.ToPreviewWithAlphaCoverage(source, kind, out var alpha);
        var blob = new Blob(transformed, PreviewMaps.Hash(transformed), alpha);
        lock (_gate)
        {
            var key = (contentHash, kind);
            if (TryServeLocked(key, out var raced)) return raced;   // a concurrent mint won; serve it
            var node = new LinkedListNode<Entry>(new Entry(key, blob));
            _blobs[key] = node;
            _recency.AddFirst(node);
            _retainedBytes += blob.Bytes.Length;
            while (_retainedBytes > _maxRetainedBytes && _recency.Last is { } oldest
                   && !ReferenceEquals(oldest, node))
            {
                _recency.RemoveLast();
                _blobs.Remove(oldest.Value.Key);
                _retainedBytes -= oldest.Value.Blob.Bytes.Length;
            }
        }
        return blob;
    }

    private bool TryServeLocked((string ContentHash, MapKind Kind) key, out Blob blob)
    {
        if (_blobs.TryGetValue(key, out var node))
        {
            _recency.Remove(node);
            _recency.AddFirst(node);
            blob = node.Value.Blob;
            return true;
        }
        blob = default;
        return false;
    }
}

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
/// in Blender's slot list), empty when it has none. <paramref name="RmoStockSource"/> is the picture the
/// session sent this primitive's RMO slot — the stock map, or the modder's own — as the alpha an authored
/// RMO is rebuilt over where the record's per-submesh RMO rows stop short (a replacement's submesh past
/// the ones the record was written for).</summary>
public readonly record struct IncomingMaps(ResolvedMap BaseColor, ResolvedMap Normal, ResolvedMap Rmo = default,
    string MaterialName = "", IReadOnlyList<IncomingTexture>? Textures = null,
    string? BaseColorName = null, string? NormalName = null, string? RmoName = null,
    string? RmoStockSource = null);

/// <summary>One property-keyed image returned for a material/primitive owner. <see cref="ShaderProperty"/>
/// is authoritative; <see cref="Kind"/> describes the transform only and never selects a slot.</summary>
public readonly record struct IncomingTexture(int MaterialIndex, int? PrimitiveIndex, string ShaderProperty,
    MapKind Kind, ResolvedMap Map, string? ImageName = null);

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
/// byte-identical and hashes to its sidecar entry; anything else is authored. Material and image names and
/// duplication do not enter the decision.</para>
///
/// <para>Content alone is not the whole of it: the sidecar also records, per primitive and kind, WHICH stock
/// image that slot was exported over (<see cref="SlotSource"/>). A stock map returning on a slot it was never
/// exported on is a link the modder made by hand — inside one part exactly as across two — and it publishes
/// like a painted map. A record with no slot rows keeps the older, content-only answer.</para>
///
/// <para>The sidecar also records, per (mesh, submesh), the stock RMO that submesh's material was built
/// over. The ORM image physically carries alpha, but glTF assigns it no material semantic, so the default
/// intake rebuild reads the mask from that recorded map rather than trusting a Blender workflow to preserve
/// it. An explicit authored-alpha answer can override that default.</para>
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
    /// primitive index within that mesh, and the workspace PNG. Alpha carries the emissive mask but has no
    /// glTF material semantic, so the default authored-RMO intake rebuilds from the map recorded here.
    /// A combined glb holds several meshes, so the mesh name is part of the identity.</summary>
    public readonly record struct SubmeshSource(
        [property: JsonPropertyName("mesh")] string Mesh,
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("rmo")] string Rmo);

    /// <summary>The STOCK image one primitive's preview material was built over in one slot: the mesh, the
    /// primitive index within it, the kind, and the content hash of what the export embedded there.
    ///
    /// <para>This is what tells a slot still bound to ITS OWN stock map from one the modder deliberately
    /// re-pointed at another slot's. The <see cref="Entry"/> list answers "is this image one of the maps this
    /// export embedded", which is the same answer for every slot of the part and so cannot see an intra-part
    /// link at all: plugging material 2's map into material 1 read as material 1 untouched, and the ask
    /// vanished.</para>
    ///
    /// <para>A slot the export gave no stock map — an authored file of the modder's own, or none — records
    /// nothing. Absence is meaningful and is not the same as the mesh being unrecorded: see
    /// <see cref="ReadSlotStock"/>.</para></summary>
    public readonly record struct SlotSource(
        [property: JsonPropertyName("mesh")] string Mesh,
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("kind")] MapKind Kind,
        [property: JsonPropertyName("hash")] string Hash);

    /// <summary>Reserved per-material shader inputs for one property binding. Empty in this stage; the
    /// stable container means later detail-layer floats and keywords extend the record rather than replace
    /// its identity shape.</summary>
    public sealed class TransportParameters
    {
        [JsonPropertyName("floats")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, float>? Floats { get; set; }
        [JsonPropertyName("keywords")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Keywords { get; set; }
    }

    /// <summary>The stock Texture2D behind a transport binding. Property identity stays outside this record:
    /// two properties may name this same resource and remain independent bindings.</summary>
    public readonly record struct TransportStock(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("bundle")] string Bundle,
        [property: JsonPropertyName("path_id")] long PathId);

    /// <summary>One exact property-keyed image written beside and inside a Blender-facing glb. The owner is
    /// both the installed material position and its projected primitive (null for a surplus material).
    /// <see cref="OutboundHash"/> identifies the preview bytes Blender received; <see cref="Stock"/> keeps
    /// resource identity separate from that derivative content identity.</summary>
    public readonly record struct TransportBinding(
        [property: JsonPropertyName("mesh")] string Mesh,
        [property: JsonPropertyName("material")] int MaterialIndex,
        [property: JsonPropertyName("primitive")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PrimitiveIndex,
        [property: JsonPropertyName("property")] string ShaderProperty,
        [property: JsonPropertyName("semantic")] MapKind Kind,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("outbound_hash")] string OutboundHash,
        [property: JsonPropertyName("stock")] TransportStock Stock,
        [property: JsonPropertyName("srgb")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Srgb = null,
        [property: JsonPropertyName("origin")] MapOrigin Origin = MapOrigin.Vanilla,
        [property: JsonPropertyName("parameters")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TransportParameters? Parameters = null,
        [property: JsonPropertyName("texCoord")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TexCoord = null,
        [property: JsonPropertyName("drawable")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Drawable = null);

    private sealed class Sidecar
    {
        [JsonPropertyName("images")] public List<Entry> Images { get; set; } = new();
        [JsonPropertyName("submeshes")] public List<SubmeshSource> Submeshes { get; set; } = new();
        [JsonPropertyName("slots")] public List<SlotSource> Slots { get; set; } = new();
        [JsonPropertyName("bindings")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<TransportBinding>? Bindings { get; set; }
        /// <summary>The scene-rest uprighting the glb's geometry is baked by (<see cref="RestBake"/>,
        /// 16 floats); absent on a file in bind space.</summary>
        [JsonPropertyName("baked_rest")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<float>? BakedRest { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SidecarPath(string glbPath) => Path.ChangeExtension(glbPath, ".maps.json");

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Path-independent content identity of a workspace GLB, its sidecar semantics and every
    /// picture that sidecar names. Missing dependencies return null, so a final-composition key can never
    /// certify a workspace whose comparison inputs have disappeared.</summary>
    public static string? WorkspaceContentIdentity(string glbPath)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Bytes(byte[] bytes)
            {
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            void FileBytes(string path)
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Span<byte> length = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(length, stream.Length);
                hash.AppendData(length);
                hash.AppendData(SHA256.HashData(stream));
            }
            string FileHash(string path)
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            string glb = Path.GetFullPath(glbPath);
            FileBytes(glb);
            string sidecar = SidecarPath(glb);
            if (!File.Exists(sidecar))
            {
                Bytes("sidecar-absent"u8.ToArray());
                return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            var doc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sidecar), JsonOpts)
                ?? new Sidecar();
            string directory = Path.GetDirectoryName(glb)!;
            string Content(string relative) => FileHash(Path.GetFullPath(Path.Combine(directory, relative)));
            var contentAddressed = new Sidecar
            {
                Images = doc.Images.Select(entry => entry with { Source = Content(entry.Source) }).ToList(),
                Submeshes = doc.Submeshes.Select(row => row with { Rmo = Content(row.Rmo) }).ToList(),
                Slots = doc.Slots,
                Bindings = doc.Bindings?.Select(binding => binding with
                    { Source = Content(binding.Source) }).ToList(),
                BakedRest = doc.BakedRest,
            };
            Bytes(JsonSerializer.SerializeToUtf8Bytes(contentAddressed, JsonOpts));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Copy one workspace glb and make its comparison record self-contained at the destination.
    /// Every external picture named by the record is copied under the destination and the relative source
    /// is rewritten there. The result can therefore move as one immutable directory after intake accepts
    /// it, without its authored-map classifier continuing to point into disposable preparation staging.</summary>
    public static void CopyPortableWorkspace(string sourceGlb, string destinationGlb,
        string picturesRelativeDirectory = "maps", bool requireSelfContained = false)
    {
        string source = Path.GetFullPath(sourceGlb);
        string destination = Path.GetFullPath(destinationGlb);
        string destinationDirectory = Path.GetDirectoryName(destination)!;
        string pictures = Path.GetFullPath(Path.Combine(destinationDirectory, picturesRelativeDirectory));
        string destinationPrefix = destinationDirectory.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!pictures.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The workspace picture directory must be under the destination.",
                nameof(picturesRelativeDirectory));
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(source, destination, overwrite: false);

        string sourceRecord = SidecarPath(source);
        if (!File.Exists(sourceRecord)) return;
        var doc = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(sourceRecord), JsonOpts)
            ?? new Sidecar();
        string sourceDirectory = Path.GetDirectoryName(source)!;
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int next = 0;

        string Portable(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return relative;
            string full = Path.GetFullPath(Path.Combine(sourceDirectory, relative));
            if (!File.Exists(full))
            {
                if (requireSelfContained)
                    throw new FileNotFoundException("A workspace picture could not be copied.", full);
                return Path.GetRelativePath(destinationDirectory, full);
            }
            if (!copied.TryGetValue(full, out string? held))
            {
                Directory.CreateDirectory(pictures);
                string extension = Path.GetExtension(full);
                held = Path.Combine(pictures, $"{next++:D4}{extension}");
                File.Copy(full, held, overwrite: false);
                copied.Add(full, held);
            }
            return Path.GetRelativePath(destinationDirectory, held);
        }

        var portable = new Sidecar
        {
            Images = doc.Images.Select(entry => entry with { Source = Portable(entry.Source) }).ToList(),
            Submeshes = doc.Submeshes.Select(row => row with { Rmo = Portable(row.Rmo) }).ToList(),
            Slots = doc.Slots,
            Bindings = doc.Bindings?.Select(binding => binding with
                { Source = Portable(binding.Source) }).ToList(),
            BakedRest = doc.BakedRest,
        };
        File.WriteAllText(SidecarPath(destination), JsonSerializer.Serialize(portable, JsonOpts));
    }

    /// <summary>A readable name for an embedded image so Blender's UI doesn't show <c>Image_0</c>.
    /// Diagnostic only.</summary>
    public static string ImageName(string pngPath, MapKind kind) =>
        Path.GetFileNameWithoutExtension(pngPath).Split('.')[0]
        + kind switch
        {
            MapKind.Normal => "_nrm",
            MapKind.Rmo => "_rmo",
            MapKind.Blend => "_effect",
            MapKind.Texture => "_texture",
            _ => "_base",
        };

    // ---------------------------------------------------------------- the paired transforms

    /// <summary>Stock PNG to the form embedded in the glb. Rows are untouched — both spaces are top-down.
    /// Inverse of <see cref="FromPreview"/>.
    ///
    /// <para><see cref="MapKind.Normal"/> is unpacked and <see cref="MapKind.Rmo"/> is permuted into glTF's
    /// ORM order; <see cref="MapKind.BaseColor"/> is re-encoded and otherwise passes through, since glTF
    /// reads its channels exactly as the game packs them.</para></summary>
    public static byte[] ToPreview(string pngPath, MapKind kind) =>
        ToPreviewWithAlphaCoverage(pngPath, kind, out _);

    /// <inheritdoc cref="ToPreview(string, MapKind)"/>
    /// <param name="fractionBelowHalfAlpha">The share of the embedded image's pixels whose alpha is under
    /// half — measured here because the image is already decoded, and reading the file a second time to ask
    /// costs as much as the encode. Only a BASE COLOUR answer means coverage: an RMO's alpha is the emissive
    /// mask and a normal's is the packed X, neither of which is transparency. See
    /// <see cref="CutoutAlphaByte"/> for why HALF and not "not fully opaque".</param>
    public static byte[] ToPreview(string pngPath, MapKind kind, out double fractionBelowHalfAlpha)
    {
        var png = ToPreviewWithAlphaCoverage(pngPath, kind, out var coverage);
        fractionBelowHalfAlpha = coverage.FractionBelowHalf;
        return png;
    }

    /// <summary>The two base-colour coverage measurements made while <see cref="ToPreview(string, MapKind)"/>
    /// already holds the decoded pixels. <see cref="MidCore5Fraction"/> is the share of the whole image that
    /// remains after the alpha-16..239 mask is eroded by two pixels (a 5x5 all-mid neighbourhood).</summary>
    internal readonly record struct AlphaCoverage(double FractionBelowHalf, double MidCore5Fraction);

    /// <summary>The embedding transform and its full alpha measurement in one decode. Non-base maps retain
    /// the legacy below-half answer for callers that inspect it, but have no blend-coverage answer because
    /// their alpha channel is data rather than transparency.</summary>
    internal static byte[] ToPreviewWithAlphaCoverage(
        string pngPath, MapKind kind, out AlphaCoverage alphaCoverage)
    {
        using var img = PreviewImage(pngPath, kind);
        return PreviewBytes(img, kind, out alphaCoverage);
    }

    internal static byte[] ToPreviewWithAlphaCoverage(
        byte[] png, MapKind kind, out AlphaCoverage alphaCoverage)
    {
        using var img = PreviewImage(png, kind);
        return PreviewBytes(img, kind, out alphaCoverage);
    }

    private static byte[] PreviewBytes(Image<Rgba32> img, MapKind kind,
        out AlphaCoverage alphaCoverage)
    {
        alphaCoverage = kind == MapKind.BaseColor
            ? MeasureBaseColorAlpha(img)
            : new AlphaCoverage(FractionBelowHalfAlpha(img), 0);
        return SavePng(img);
    }

    /// <summary>The alpha at or above which a pixel survives, in glTF's own 0..1 units — glTF's default
    /// cutoff, stated rather than assumed, and THE authoritative half of this pair. It is what
    /// <see cref="MeshGltf"/> writes on a MASK material, so what the mode is chosen for is exactly what the
    /// viewer then discards.</summary>
    internal const float GltfAlphaCutoff = 0.5f;

    /// <summary>The same cutoff as a BYTE: the lowest alpha <see cref="GltfAlphaCutoff"/> keeps, so a pixel
    /// under this is exactly a pixel that cutoff discards. DERIVED rather than spelled a second time — a byte
    /// <c>b</c> rides as <c>b/255</c>, so the first alpha the cutoff keeps is <c>ceil(cutoff × 255)</c> — and
    /// the two units therefore cannot drift apart: the share measured here and the cut the viewer performs
    /// are the same rule in two spellings.
    ///
    /// <para>Half, rather than "anything short of 255", because the game's own textures are BC-compressed and
    /// decode with whole uniform 4x4 blocks of alpha 254 — encoder quantization, present in the shipped data.
    /// Measured against that noise, "not fully opaque" is true of essentially every diffuse map in the game,
    /// while the share of pixels under half is 0.0%.</para></summary>
    private static readonly byte CutoutAlphaByte = (byte)MathF.Ceiling(GltfAlphaCutoff * 255f);

    /// <summary>The share of an image's pixels whose alpha is under <see cref="CutoutAlphaByte"/> — what says
    /// whether a base colour carries genuine cutout content rather than compression noise. 0 for an image with
    /// no pixels at all.</summary>
    private static double FractionBelowHalfAlpha(Image<Rgba32> img)
    {
        long below = 0;
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x].A < CutoutAlphaByte) below++;
            }
        });
        long total = (long)img.Width * img.Height;
        return total == 0 ? 0 : (double)below / total;
    }

    /// <summary>Measure cut coverage and area-forming graded coverage in one walk over the decoded base
    /// colour. The horizontal/vertical five-pixel windows implement a square erosion without a 25-read inner
    /// loop; the two byte masks cost two bytes per pixel only while this already-decoded image is embedded.</summary>
    private static AlphaCoverage MeasureBaseColorAlpha(Image<Rgba32> img)
    {
        int width = img.Width;
        int height = img.Height;
        long total = (long)width * height;
        if (total == 0) return default;

        // A map narrower or shorter than five pixels has no legal core centre and therefore cannot declare
        // BLEND; its below-half result still lets the material fall through honestly to MASK or OPAQUE.
        byte[]? mid = width >= 5 && height >= 5 ? new byte[checked(width * height)] : null;
        long below = 0;
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                int offset = y * width;
                for (int x = 0; x < row.Length; x++)
                {
                    byte alpha = row[x].A;
                    if (alpha < CutoutAlphaByte) below++;
                    if (mid is not null
                        && alpha is >= MeshGltf.BlendMidAlphaMin and <= MeshGltf.BlendMidAlphaMax)
                        mid[offset + x] = 1;
                }
            }
        });

        long core = mid is null ? 0 : MidAlphaCore5(mid, width, height);
        return new AlphaCoverage((double)below / total, (double)core / total);
    }

    /// <summary>Count pixels whose entire 5x5 neighbourhood belongs to the supplied mid-alpha mask.</summary>
    private static long MidAlphaCore5(byte[] mid, int width, int height)
    {
        var horizontal = new byte[mid.Length];
        for (int y = 0; y < height; y++)
        {
            int offset = y * width;
            int window = mid[offset] + mid[offset + 1] + mid[offset + 2] + mid[offset + 3] + mid[offset + 4];
            for (int x = 2; x <= width - 3; x++)
            {
                if (window == 5) horizontal[offset + x] = 1;
                if (x < width - 3)
                    window += mid[offset + x + 3] - mid[offset + x - 2];
            }
        }

        long core = 0;
        for (int x = 2; x <= width - 3; x++)
        {
            int window = horizontal[x] + horizontal[width + x] + horizontal[2 * width + x]
                + horizontal[3 * width + x] + horizontal[4 * width + x];
            for (int y = 2; y <= height - 3; y++)
            {
                if (window == 5) core++;
                if (y < height - 3)
                    window += horizontal[(y + 3) * width + x] - horizontal[(y - 2) * width + x];
            }
        }
        return core;
    }

    /// <summary>The same transform, stopping at the pixels — for a caller that compares rather than embeds
    /// and has no use for the encode.</summary>
    private static Image<Rgba32> PreviewImage(string pngPath, MapKind kind)
    {
        var img = Image.Load<Rgba32>(pngPath);
        TransformPreview(img, kind);
        return img;
    }

    private static Image<Rgba32> PreviewImage(byte[] png, MapKind kind)
    {
        var img = Image.Load<Rgba32>(png);
        TransformPreview(img, kind);
        return img;
    }

    private static void TransformPreview(Image<Rgba32> img, MapKind kind)
    {
        if (kind == MapKind.Normal) UnpackNormal(img);
        else if (kind == MapKind.Rmo) RmoToOrm(img);
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
    /// <summary>The neutral normal found beside any picture the export embedded or carried as an exact
    /// binding. The bindings count too: an open whose three fixed maps are all the modder's own embeds
    /// nothing from the workspace folder, and the stock bindings are then the only rows that still name
    /// it.</summary>
    private static IEnumerable<Entry> NeutralEntries(IEnumerable<Entry> embedded,
        IEnumerable<TransportBinding>? bindings = null)
    {
        var dirs = embedded.Select(e => e.Source)
            .Concat(bindings?.Select(b => b.Source) ?? Array.Empty<string>())
            .Select(source => Path.GetDirectoryName(Path.GetFullPath(source)))
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

    /// <summary>Record what was embedded beside the glb: the images by content, the stock RMO behind each
    /// submesh that got one, and the stock image behind each primitive's every slot
    /// (<paramref name="slots"/>). Sources are stored relative to the glb so a mod folder stays movable;
    /// slot rows carry content hashes, which no move touches.</summary>
    public static void WriteSidecar(string glbPath, IEnumerable<Entry> entries,
        IEnumerable<SubmeshSource> submeshes, IEnumerable<SlotSource>? slots = null,
        IEnumerable<TransportBinding>? bindings = null, IReadOnlyList<float>? bakedRest = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        var embedded = entries as IReadOnlyCollection<Entry> ?? entries.ToList();
        var rel = embedded.Concat(NeutralEntries(embedded, bindings))
            .Select(e => e with { Source = Rel(dir, e.Source) })
            .DistinctBy(e => (e.Hash, e.Source, e.Kind, e.Owner))
            .ToList();
        var transport = (bindings ?? Array.Empty<TransportBinding>())
            .Select(binding => binding with { Source = Rel(dir, binding.Source) })
            .ToList();
        if (rel.Count == 0 && transport.Count == 0 && bakedRest is null)
        {
            // nothing to record: clear the sidecar — a stale one would resolve an authored image as stock
            var stale = SidecarPath(glbPath);
            if (File.Exists(stale)) File.Delete(stale);
            return;
        }
        var subs = submeshes.Select(s => s with { Rmo = Rel(dir, s.Rmo) }).ToList();
        File.WriteAllText(SidecarPath(glbPath),
            JsonSerializer.Serialize(
                new Sidecar
                {
                    Images = rel,
                    Submeshes = subs,
                    Slots = slots?.ToList() ?? new List<SlotSource>(),
                    Bindings = transport.Count == 0 ? null : transport,
                    BakedRest = bakedRest?.ToList(),
                },
                JsonOpts));
    }

    /// <summary>The scene-rest uprighting a glb's geometry is baked by, as its record states it (see
    /// <see cref="RestBake"/>); null where the record says nothing, which is a file in bind space.</summary>
    public static IReadOnlyList<float>? ReadBakedRest(string glbPath)
    {
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts)?.BakedRest;
    }

    /// <summary>The exact property bindings recorded for an outbound glb. Source paths are made absolute;
    /// absence is the legacy three-channel record shape.</summary>
    public static IReadOnlyList<TransportBinding> ReadTransportBindings(string glbPath)
    {
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return Array.Empty<TransportBinding>();
        var rows = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts)?.Bindings;
        if (rows is not { Count: > 0 }) return Array.Empty<TransportBinding>();
        var dir = Path.GetDirectoryName(Path.GetFullPath(glbPath))!;
        return rows.Select(binding => binding with
        {
            Source = Path.GetFullPath(Path.Combine(dir, binding.Source)),
        }).ToList();
    }

    /// <summary>The sidecar's CLASSIFYING entries for a glb, keyed by content hash AND slot kind, or empty when
    /// there is none — every image then reads as authored, shipping a redundant stock copy rather than losing an
    /// authored map. It answers what the whole export embedded; WHICH slot each image sat on is the separate
    /// answer <see cref="ReadSlotStock"/> gives, and <see cref="Resolve"/> wants both.
    /// The kind belongs in the key: the RMO permutation is the identity on any pixel with
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

    /// <summary>What each primitive of one mesh was exported over, per slot kind, as the content hash of the
    /// stock image embedded there — what tells a slot still bound to its OWN stock map from one deliberately
    /// re-pointed at another slot's (see <see cref="SlotSource"/>). A slot the export gave no stock map is
    /// ABSENT from the answer, and absent means "no stock map belongs here", so a stock image arriving on it
    /// is an ask.
    ///
    /// <para>Null where the record cannot say, which is a different answer from an empty one and leaves the
    /// caller on the whole-record classification rather than reading every one of the part's own maps as a
    /// link: no sidecar, a record written before slots were recorded at all (a workspace from an earlier
    /// release), or a record that holds slots but names this mesh in none of them — which is what a mesh
    /// renamed in Blender comes back as.</para>
    ///
    /// <para>Keyed by PRIMITIVE INDEX, as every other per-submesh answer in this pipeline is
    /// (<see cref="ReadSubmeshRmoSources"/>, <c>SubmeshTextures.Submesh</c>): a primitive past the recorded
    /// ones is simply absent.</para>
    ///
    /// <para>THE INVARIANT the writing side owes: a mesh the record names at all carries a row for EVERY
    /// (primitive, kind) its export bound a stock map to. The fallback above is per MESH while the answer
    /// here is scoped per (index, kind), so a record naming the mesh with rows for only some of the kinds
    /// would not fall back — it would answer "no stock map belongs here" for every slot of the missing kinds
    /// and read each untouched map of those kinds as the modder's own work, shipping a redundant copy of the
    /// game's picture. Nothing writes such a record: every export records all three kinds of a primitive
    /// under one guard.</para></summary>
    public static IReadOnlyDictionary<(int Index, MapKind Kind), string>? ReadSlotStock(string glbPath,
        string meshName)
    {
        var path = SidecarPath(glbPath);
        if (!File.Exists(path)) return null;
        var rows = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(path), JsonOpts)?.Slots;
        if (rows is not { Count: > 0 }) return null;
        var owned = new Dictionary<(int, MapKind), string>();
        foreach (var r in rows)
            if (string.Equals(r.Mesh, meshName, StringComparison.Ordinal))
                owned.TryAdd((r.Index, r.Kind), r.Hash.ToLowerInvariant());
        return owned.Count == 0 ? null : owned;
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
    /// images, and one is made per call when the caller keeps none.</para>
    ///
    /// <para><paramref name="slot"/> is what THIS slot was exported over (see <see cref="SlotStock"/>). Given
    /// one, a stock map settles the slot only when it is the slot's OWN: another slot's stock map, of this
    /// very mesh, is a link the modder made by hand and publishes exactly like a painted map. Omitted, the
    /// record could not say which map was this slot's, and any stock image the record holds settles it — the
    /// answer every workspace written before slots were recorded still gets.</para></summary>
    public static ResolvedMap Resolve(byte[]? imageBytes, MapKind kind,
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, string? owner = null,
        StockPixels? stock = null, SlotStock? slot = null)
    {
        if (imageBytes is null || imageBytes.Length == 0) return new ResolvedMap(MapOrigin.None);
        var hash = Hash(imageBytes);
        if (sidecar.TryGetValue((hash, kind), out var hit)
            && (hit.Origin == MapOrigin.Neutral || Owns(slot, hash)))
            return hit.Origin == MapOrigin.Neutral
                ? new ResolvedMap(MapOrigin.Neutral)
                : new ResolvedMap(MapOrigin.Vanilla, StockPng: hit.Source);
        if (SamePixelsAsRecorded(imageBytes, kind, sidecar, owner, stock ?? new StockPixels(), slot) is { } same)
            return same;
        return new ResolvedMap(MapOrigin.Authored, AuthoredPng: FromPreview(imageBytes, kind));
    }

    /// <summary>Classify a returned carrier image against its exact outbound property row. The outbound
    /// bytes are the comparison baseline even when the project authored them: returning that picture
    /// untouched is not a new ask, while changing it still is. The classifier is scoped to this one
    /// binding's outbound hash so another property with identical content cannot claim the slot.</summary>
    /// <para><paramref name="neutrals"/> are the record's own neutral entries (see <see cref="ReadSidecar"/>),
    /// the candidates that make "plug the neutral" answer on every normal slot: a slot whose outbound picture
    /// is the modder's own authored normal has no <see cref="NeutralN"/> beside that picture, and the record
    /// is where the session's neutral is named.</para></summary>
    public static ResolvedMap ResolveTransport(byte[]? imageBytes, TransportBinding binding,
        IEnumerable<Entry>? neutrals = null)
    {
        if (imageBytes is null || imageBytes.Length == 0) return new ResolvedMap(MapOrigin.None);

        var recorded = new Dictionary<(string Hash, MapKind Kind), Entry>
        {
            [(binding.OutboundHash.ToLowerInvariant(), binding.Kind)] = new Entry(binding.OutboundHash,
                binding.Source, binding.Kind, MapOrigin.Vanilla, binding.Mesh),
        };
        if (binding.Kind == MapKind.Normal)
        {
            string? directory = Path.GetDirectoryName(binding.Source);
            string neutral = Path.Combine(directory ?? "", NeutralN);
            if (File.Exists(neutral))
                recorded.TryAdd((Hash(File.ReadAllBytes(neutral)), MapKind.Normal),
                    new Entry(Hash(File.ReadAllBytes(neutral)), neutral, MapKind.Normal, MapOrigin.Neutral));
            foreach (var entry in neutrals ?? Array.Empty<Entry>())
                if (entry.Origin == MapOrigin.Neutral && entry.Kind == MapKind.Normal)
                    recorded.TryAdd((entry.Hash.ToLowerInvariant(), MapKind.Normal), entry);
        }
        return Resolve(imageBytes, binding.Kind, recorded, binding.Mesh, new StockPixels(),
            new SlotStock(binding.OutboundHash));
    }

    /// <summary>What ONE slot of one primitive was exported over: the content hash of the stock image the
    /// export embedded there, or null where it embedded none of the part's own stock — an authored map of the
    /// modder's, or no map at all. Passing NO <c>SlotStock</c> to <see cref="Resolve"/> is the third answer:
    /// the record cannot say, and the whole record settles the slot as it always did.</summary>
    public readonly record struct SlotStock(string? Hash);

    /// <summary>Whether a recorded stock image is the one this slot was exported over. True when the caller
    /// could not say which map that was — the unscoped answer — and false for a slot the export gave no stock
    /// map, which nothing the record holds belongs to.</summary>
    private static bool Owns(SlotStock? slot, string hash) =>
        slot is not { } s || string.Equals(s.Hash, hash, StringComparison.OrdinalIgnoreCase);

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
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, string? owner, StockPixels stock,
        SlotStock? slot)
    {
        Image<Rgba32> returned;
        try { returned = Image.Load<Rgba32>(imageBytes); }
        catch { return null; }
        using (returned)
        {
            var want = Fingerprint(returned);
            foreach (var e in Candidates(sidecar, kind, owner, slot))
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

    /// <summary>The recorded images a slot of this kind could be reproducing, best answer first. Where the
    /// record says what this slot was exported over, only THAT image can settle it — a re-encode of another
    /// slot's map is the modder's ask, and reading its pixels as untouched is the same loss the byte path
    /// refuses. The shipped neutral is never any slot's own and always stays a candidate.
    ///
    /// <para>Unscoped, the owning mesh's own images come first: a part's map and a sibling part's can hold the
    /// same picture under different encodings, and taking the sibling's would record a link the modder never
    /// made. The rest are ordered by source path, so two recorded images with identical pixels — one picture
    /// under two names — answer the same on every read.</para></summary>
    private static IEnumerable<Entry> Candidates(
        IReadOnlyDictionary<(string Hash, MapKind Kind), Entry> sidecar, MapKind kind, string? owner,
        SlotStock? slot) =>
        sidecar.Values
            .Where(e => e.Kind == kind && e.Origin is MapOrigin.Vanilla or MapOrigin.Neutral)
            .Where(e => e.Origin == MapOrigin.Neutral || Owns(slot, e.Hash))
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
