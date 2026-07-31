using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Textures;

namespace Remold.Core.Workbench;

/// <summary>
/// The persistent, async preview cache for the Outfit Workbench: a decoded Texture2D (base mip, BGRA)
/// downscaled to fit <see cref="MaxDim"/>×<see cref="MaxDim"/> and written as a PNG under
/// <c>%LOCALAPPDATA%\DollRemoldingLab\thumbs\&lt;catalogVersion&gt;\&lt;bundleId&gt;\&lt;textureName&gt;.png</c>.
/// Recipe mesh previews share that version directory under <c>meshes5\&lt;bundleId&gt;\&lt;meshName&gt;.png</c>,
/// with a vertex-count sidecar so a cache hit can populate the inspector without reopening the game bundle.
///
/// <para><b>Keying.</b> catalog version + source bundle + asset name — the same (bundle, name) identity the
/// texture targets use, so two same-named maps from DIFFERENT bundles are distinct thumbs. Bundle and name are
/// filesystem-safe in this game; any stray invalid char maps to <c>_</c>, whose unlikely collision would only
/// reuse one thumb for the other.</para>
///
/// <para><b>Failure is never cached.</b> A deobfuscation miss, an absent texture, or a decode fault returns
/// <c>null</c> and writes nothing — a transient sharing lock while the game runs must not poison the cache.
/// Callers show a placeholder and re-try on re-selection.</para>
///
/// <para>Thumbs are never upscaled: a texture already within <see cref="MaxDim"/> in both dimensions is copied
/// at native size, only larger maps scale down, aspect preserved.</para>
///
/// <para>Thread-safe: every write is pure over the filesystem, and atomic (temp + move), so a concurrent
/// reader never sees a half-written PNG. The only instance state records which version directories this
/// instance has swept for orphaned temps, so the sweep runs at most once per directory.</para>
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>The thumbnail fits within this many pixels on its longest side (~256px).</summary>
    public const int MaxDim = 256;

    // Windows reserves the DOS device names: a file OR directory whose stem case-insensitively equals one is
    // unopenable, so a texture literally named "CON"/"NUL"/… must be prefixed to become a writable thumb.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly string _root;

    // Version dirs this instance has swept for orphaned temps (first-write-wins → sweep once per dir).
    private readonly ConcurrentDictionary<string, byte> _sweptDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="rootOverride">Cache root; defaults to the regenerable-cache thumbs dir
    /// (<see cref="LabPaths.ThumbnailRoot"/>, <c>%LOCALAPPDATA%\DollRemoldingLab\thumbs</c>). Tests pass a temp dir.</param>
    public ThumbnailCache(string? rootOverride = null)
    {
        _root = rootOverride ?? LabPaths.ThumbnailRoot;
    }

    /// <summary>The on-disk path a thumb for this texture would live at, whether or not it exists yet. Bundle
    /// identity scopes it: the game carries same-named textures in different bundles as distinct
    /// assets.</summary>
    public string PathFor(string bundleId, string textureName, string catalogVersion) =>
        Path.Combine(_root, Sanitize(catalogVersion), Sanitize(bundleId), Sanitize(textureName) + ".png");

    /// <summary>A cache hit: the thumb path if the PNG already exists, else null. Fast — one <c>File.Exists</c>,
    /// no deobfuscate. The batch calls this first to skip deobfuscating a bundle whose thumbs are all cached.</summary>
    public string? TryGetCachedPath(string bundleId, string textureName, string catalogVersion)
    {
        var p = PathFor(bundleId, textureName, catalogVersion);
        return File.Exists(p) ? p : null;
    }

    /// <summary>The cached mesh-preview PNG and the original mesh's vertex count.</summary>
    public readonly record struct MeshThumb(string Path, int VertexCount);

    /// <summary>The on-disk PNG path for a recipe mesh. Bundle identity is part of the key because the game
    /// carries same-named mesh copies in different bundles. The directory name doubles as the render-format
    /// discriminator ("meshes5" = textured, Blender-handed, scene-rest-uprighted like the export): bumping it
    /// orphans older-format entries instead of serving them as this version's truth. A non-zero
    /// <paramref name="pathId"/> joins the key, since smr-body parts select by exact path id.</summary>
    public string MeshPathFor(string bundleId, string meshName, string catalogVersion, long pathId = 0) =>
        Path.Combine(_root, Sanitize(catalogVersion), "meshes5", Sanitize(bundleId),
            Sanitize(meshName) + (pathId != 0 ? "." + pathId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + ".png");

    private string MeshCountPathFor(string bundleId, string meshName, string catalogVersion, long pathId = 0) =>
        Path.ChangeExtension(MeshPathFor(bundleId, meshName, catalogVersion, pathId), ".vertices");

    /// <summary>A mesh cache hit requires both the rendered PNG and its parseable vertex-count sidecar. A
    /// partial/crashed write is a miss and will be regenerated from the game bundle.</summary>
    public MeshThumb? TryGetCachedMesh(string bundleId, string meshName, string catalogVersion, long pathId = 0)
    {
        var path = MeshPathFor(bundleId, meshName, catalogVersion, pathId);
        var countPath = MeshCountPathFor(bundleId, meshName, catalogVersion, pathId);
        if (!File.Exists(path) || !File.Exists(countPath)) return null;
        try
        {
            if (int.TryParse(File.ReadAllText(countPath), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int count) && count >= 0)
                return new MeshThumb(path, count);
        }
        catch { /* an unreadable sidecar is a cache miss */ }
        return null;
    }

    /// <summary>A mesh preview rendered but NOT persisted: the PNG bytes and the original vertex count.</summary>
    public readonly record struct MeshRender(byte[] Png, int VertexCount);

    /// <summary>Decode the recipe-named mesh from an already-deobfuscated bundle, render it, and atomically
    /// cache the PNG plus original vertex count. Any failure returns null and leaves no valid cache entry.
    /// <paramref name="submeshTextures"/> feeds the renderer's per-submesh base-color sampling and is baked
    /// into the cached PNG — vanilla thumbs sample vanilla maps, both pinned by the catalog version.
    ///
    /// <para><b>Vanilla renders only.</b> The mesh key is catalog version + bundle + mesh (+ path id) and
    /// carries NO texture identity, so an entry made from a modder-supplied map would be served to every
    /// later project asking for that game mesh. A render sampling ANY edited or authored map must therefore
    /// go through <see cref="RenderMeshThumb"/>, which neither reads nor writes this cache.</para></summary>
    public MeshThumb? EnsureMeshThumb(byte[] deobfuscatedBundle, string bundleId, string meshName, string catalogVersion,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? submeshTextures = null, long pathId = 0)
    {
        var hit = TryGetCachedMesh(bundleId, meshName, catalogVersion, pathId);
        if (hit is not null) return hit;

        try
        {
            var render = RenderMesh(deobfuscatedBundle, meshName, submeshTextures, pathId);
            if (render is not { } r) return null;
            using var image = r.Image;
            return WriteMeshThumb(image, bundleId, meshName, catalogVersion, r.VertexCount, pathId);
        }
        catch { return null; }
    }

    /// <summary>Render a mesh preview to PNG bytes WITHOUT touching the persisted cache — neither a hit nor
    /// a write. The route for a render whose maps are the modder's own (see <see cref="EnsureMeshThumb"/>):
    /// those pixels belong to one project, and this cache is keyed only by game identity. Null on any
    /// decode/render failure, exactly as the cached route.</summary>
    public MeshRender? RenderMeshThumb(byte[] deobfuscatedBundle, string meshName,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? submeshTextures = null, long pathId = 0)
    {
        try
        {
            var render = RenderMesh(deobfuscatedBundle, meshName, submeshTextures, pathId);
            if (render is not { } r) return null;
            using var image = r.Image;
            using var stream = new MemoryStream();
            image.SaveAsPng(stream, TextureExport.FastPng);
            return new MeshRender(stream.ToArray(), r.VertexCount);
        }
        catch { return null; }
    }

    /// <summary>The shared render core both mesh routes run: decode by name (or exact path id), upright, and
    /// rasterize. The caller owns the returned image. Null when the bundle carries no such mesh.</summary>
    private static (Image<Rgba32> Image, int VertexCount)? RenderMesh(byte[] deobfuscatedBundle, string meshName,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? submeshTextures, long pathId)
    {
        var field = new BundleReader().GetMeshField(deobfuscatedBundle, meshName, pathId);
        if (field is null) return null;
        var mesh = UnityMesh.Decode(field);
        // Prefab-shipped lying-down bodies render 90° wrong from raw bind space; the mesh's own bundle
        // carries the scene rig the export path uses, so the thumb applies the SAME snap. Character
        // parts have no SMR in their bundles, and a scene-less smr bundle degrades to the raw render.
        try
        {
            var skin = MeshSkin.Decode(field);
            if (skin.IsSkinned
                && Skeleton.SceneRig.TryRead(deobfuscatedBundle, meshName, skin, pathId)?.Uprighting is { } g)
                mesh = RestBake.Apply(mesh, g);
        }
        catch { /* preview-only nicety — a rig read failure keeps the raw render */ }
        return (MeshPreviewRenderer.Render(mesh, MaxDim, submeshTextures), mesh.VertexCount);
    }

    /// <summary>Ensure a thumb exists for a bundled texture, deobfuscating on demand. No-ops fast on a cache
    /// hit, never deobfuscating. Returns the PNG path, or null on any failure (not cached). The
    /// single-call convenience; the batch uses the byte[] overload to reuse one deobfuscated bundle across
    /// all its textures.</summary>
    public string? EnsureThumb(Func<string, byte[]?> tryDeobfuscate, string bundleId, string textureName, string catalogVersion)
    {
        var hit = TryGetCachedPath(bundleId, textureName, catalogVersion);
        if (hit is not null) return hit;

        byte[]? deobfuscated;
        try { deobfuscated = tryDeobfuscate(bundleId); }
        catch { deobfuscated = null; }
        if (deobfuscated is null) return null;   // deobfuscation failure — not cached, retried on re-selection

        return EnsureThumb(deobfuscated, bundleId, textureName, catalogVersion);
    }

    /// <summary>Ensure a thumb exists from an already-deobfuscated bundle. No-ops fast on a cache hit. The
    /// <paramref name="bundleId"/> scopes the cache path only; the decode uses the supplied bytes. Returns
    /// the PNG path, or null if the texture is absent or won't decode.</summary>
    public string? EnsureThumb(byte[] deobfuscatedBundle, string bundleId, string textureName, string catalogVersion)
    {
        var hit = TryGetCachedPath(bundleId, textureName, catalogVersion);
        if (hit is not null) return hit;

        try
        {
            var dec = new BundleReader().GetTexture(deobfuscatedBundle, textureName);
            if (dec is null) return null;   // texture not in this bundle — not cached
            // GetTexture returns Unity's native bottom-up rows; flip to top-down like TextureExport does.
            return WriteThumb(dec.Value.Bgra, dec.Value.Width, dec.Value.Height, flip: true, bundleId, textureName, catalogVersion);
        }
        catch { return null; }   // decode fault — never cached
    }

    // Edited maps are re-thumbed by decoding the workspace PNG directly in the workbench VM, NOT through
    // this cache: the vanilla-thumb cache path must never be overwritten by mod-specific pixels.

    // ---- pixels → downscaled PNG on disk ----

    private string WriteThumb(byte[] bgra, int width, int height, bool flip, string bundleId, string textureName, string catalogVersion)
    {
        using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
        if (flip) image.Mutate(x => x.Flip(FlipMode.Vertical));
        return WriteThumb(image, bundleId, textureName, catalogVersion);
    }

    private string WriteThumb(Image<Bgra32> image, string bundleId, string textureName, string catalogVersion)
    {
        var (w, h) = FitWithin(image.Width, image.Height, MaxDim);
        if (w != image.Width || h != image.Height)
            image.Mutate(x => x.Resize(w, h));

        var path = PathFor(bundleId, textureName, catalogVersion);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        // first write into this version dir: clear any orphaned temps a killed process left
        if (_sweptDirs.TryAdd(dir, 0)) CacheTemps.Sweep(dir);
        // Atomic publish: encode to a unique temp, then move over the target, so a parallel reader never
        // observes a half-written PNG.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            image.SaveAsPng(tmp, TextureExport.FastPng);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
        return path;
    }

    private MeshThumb WriteMeshThumb(Image<Rgba32> image, string bundleId, string meshName,
        string catalogVersion, int vertexCount, long pathId = 0)
    {
        var path = MeshPathFor(bundleId, meshName, catalogVersion, pathId);
        var countPath = MeshCountPathFor(bundleId, meshName, catalogVersion, pathId);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (_sweptDirs.TryAdd(dir, 0)) CacheTemps.Sweep(dir);

        var id = Guid.NewGuid().ToString("N");
        var imageTmp = path + "." + id + ".tmp";
        var countTmp = countPath + "." + id + ".tmp";
        try
        {
            image.SaveAsPng(imageTmp, TextureExport.FastPng);
            File.WriteAllText(countTmp, vertexCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The PNG is the completion marker: publish its sidecar first, then the image.
            File.Move(countTmp, countPath, overwrite: true);
            File.Move(imageTmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(imageTmp)) { try { File.Delete(imageTmp); } catch { /* best-effort temp cleanup */ } }
            if (File.Exists(countTmp)) { try { File.Delete(countTmp); } catch { /* best-effort temp cleanup */ } }
        }
        return new MeshThumb(path, vertexCount);
    }

    /// <summary>Fit (w,h) within a max box, preserving aspect and never upscaling. A larger source is scaled
    /// down by the tighter ratio. Result dimensions are ≥1.</summary>
    internal static (int W, int H) FitWithin(int w, int h, int max)
    {
        if (w <= 0 || h <= 0) return (1, 1);          // degenerate — guard, never upscale-to-fill
        if (w <= max && h <= max) return (w, h);      // no upscale
        double scale = Math.Min((double)max / w, (double)max / h);
        int nw = Math.Max(1, (int)Math.Round(w * scale, MidpointRounding.AwayFromZero));
        int nh = Math.Max(1, (int)Math.Round(h * scale, MidpointRounding.AwayFromZero));
        return (nw, nh);
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        Span<char> buf = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            buf[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        var cleaned = new string(buf);
        // A name whose stem is a reserved device name is unwritable on Windows even with an extension
        // ("CON.png" is still CON), so prefix "_".
        int dot = cleaned.IndexOf('.');
        var stem = dot >= 0 ? cleaned[..dot] : cleaned;
        return ReservedDeviceNames.Contains(stem) ? "_" + cleaned : cleaned;
    }
}
