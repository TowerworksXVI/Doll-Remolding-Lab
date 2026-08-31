using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using Remold.App.Textures;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels.EditPage;

/// <summary>Blocking render and decode services for the session-native Edit page. It shares the app's
/// install-keyed thumbnail cache but owns no authored state.</summary>
internal sealed class EditPreviewService
{
    internal readonly record struct MeshRender(byte[] Png, int VertexCount);

    private readonly Func<GameVfs?> _vfs;
    private readonly Func<CatalogIndex?> _catalog;
    private readonly Func<string, byte[]?> _deobfuscate;
    private readonly ThumbnailCache _thumbs;

    internal EditPreviewService(Func<GameVfs?> vfs, Func<CatalogIndex?> catalog,
        Func<string, byte[]?> deobfuscate, ThumbnailCache thumbs)
    {
        _vfs = vfs;
        _catalog = catalog;
        _deobfuscate = deobfuscate;
        _thumbs = thumbs;
    }

    internal MeshRender? RenderGameMesh(RecipePart recipe,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? samplers, bool ownMaps,
        bool cacheable = true)
    {
        if (MeshBundle(recipe) is not { } bundle) return null;
        byte[]? bytes;
        try { bytes = _deobfuscate(bundle); }
        catch { bytes = null; }
        if (bytes is null) return null;

        long pathId = recipe.IsRecipeBacked ? 0 : recipe.MeshPathId;
        string version = _vfs()?.CatalogVersion ?? "unknown";
        // Not cacheable = a game-map sampler the plan expected came back null. That miss may be transient
        // or permanent; the non-caching route is safe for both because storing this render under the
        // game-identity key would serve the degraded picture to every later textured ask.
        if (ownMaps || !cacheable)
            return _thumbs.RenderMeshThumb(bytes, recipe.SlotName, samplers, pathId) is { } fresh
                ? new MeshRender(fresh.Png, fresh.VertexCount) : null;
        if (_thumbs.EnsureMeshThumb(bytes, bundle, recipe.SlotName, version, samplers, pathId)
            is not { } thumb) return null;
        try { return new MeshRender(File.ReadAllBytes(thumb.Path), thumb.VertexCount); }
        catch
        {
            try { File.Delete(thumb.Path); } catch { }
            return null;
        }
    }

    /// <summary>The game mesh's original vertex count, and nothing else: the cached count where a mesh
    /// thumb already exists, else a decode that neither renders nor writes. The edit preview's
    /// original-count read takes this — routing it through the rendering path would cache an untextured
    /// picture under the key the bare part's preview is served from.</summary>
    internal int? GameMeshVertexCount(RecipePart recipe)
    {
        if (MeshBundle(recipe) is not { } bundle) return null;
        long pathId = recipe.IsRecipeBacked ? 0 : recipe.MeshPathId;
        string version = _vfs()?.CatalogVersion ?? "unknown";
        if (_thumbs.TryGetCachedMesh(bundle, recipe.SlotName, version, pathId) is { } hit)
            return hit.VertexCount;
        byte[]? bytes;
        try { bytes = _deobfuscate(bundle); }
        catch { bytes = null; }
        return bytes is null ? null : ThumbnailCache.MeshVertexCount(bytes, recipe.SlotName, pathId);
    }

    internal static MeshRender? RenderProjectMesh(string glbPath,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? samplers)
    {
        var mesh = MeshGltf.ImportGlb(glbPath);
        var png = MeshPreviewRenderer.RenderWorkspacePng(mesh, ThumbnailCache.MaxDim, samplers);
        return new MeshRender(png, mesh.VertexCount);
    }

    internal static MeshPreviewRenderer.PreviewTexture? Sampler(string file)
    {
        try { return MeshPreviewRenderer.PreviewTexture.TryFromPng(File.ReadAllBytes(file)); }
        catch { return null; }
    }

    internal MeshPreviewRenderer.PreviewTexture? Sampler(string bundleId, string textureName)
    {
        if (GameTextureThumb(bundleId, textureName) is not { } path) return null;
        return Sampler(path);
    }

    internal string? GameTextureThumb(string bundleId, string textureName)
    {
        if (bundleId.Length == 0 || textureName.Length == 0) return null;
        string version = _vfs()?.CatalogVersion ?? "unknown";
        try
        {
            return _thumbs.TryGetCachedPath(bundleId, textureName, version)
                ?? _thumbs.EnsureThumb(_deobfuscate, bundleId, textureName, version);
        }
        catch { return null; }
    }

    /// <summary>Resolve a card batch's persistent thumbnails. Every path is checked before any bundle is
    /// opened; remaining requests share one deobfuscation per bundle.</summary>
    internal IReadOnlyList<ThumbnailCache.TextureThumb?> GameTextureThumbs(
        IReadOnlyList<(string Bundle, string Texture)> requests)
    {
        string version = _vfs()?.CatalogVersion ?? "unknown";
        var result = new ThumbnailCache.TextureThumb?[requests.Count];
        var missing = new List<int>();
        for (int i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            result[i] = _thumbs.TryGetCachedTexture(request.Bundle, request.Texture, version);
            if (result[i] is null) missing.Add(i);
        }
        foreach (var group in missing.GroupBy(index => requests[index].Bundle,
                     StringComparer.OrdinalIgnoreCase))
        {
            byte[]? bytes;
            try { bytes = _deobfuscate(group.Key); }
            catch { bytes = null; }
            if (bytes is null) continue;
            foreach (int index in group)
            {
                var request = requests[index];
                result[index] = _thumbs.EnsureTextureThumb(bytes, request.Bundle,
                    request.Texture, version);
            }
        }
        return result;
    }

    internal static Bitmap DecodeMap(Stream png, bool rmo)
    {
        if (!rmo) return Bitmap.DecodeToWidth(png, ThumbnailCache.MaxDim);
        using var raw = new MemoryStream();
        png.CopyTo(raw);
        try
        {
            using var opaque = new MemoryStream(TextureExport.OpaquePng(raw.ToArray(), ThumbnailCache.MaxDim),
                writable: false);
            return Bitmap.DecodeToWidth(opaque, ThumbnailCache.MaxDim);
        }
        catch
        {
            raw.Position = 0;
            return Bitmap.DecodeToWidth(raw, ThumbnailCache.MaxDim);
        }
    }

    /// <summary>Decode a project-owned map. Toon-ramp DDS files use the same strict ramp reader and
    /// display conversion as the ramp picker; ordinary project pictures keep the PNG path.</summary>
    internal static EditMapPreview DecodeProjectMap(string path, bool rmo)
    {
        try
        {
            if (string.Equals(Path.GetExtension(path), ".dds", StringComparison.OrdinalIgnoreCase))
            {
                var ramp = RampImage.ReadDds(path);
                return new EditMapPreview(RampImage.TryPreview(ramp.Width, ramp.Height, ramp.Fp16),
                    $"{ramp.Width}\u00d7{ramp.Height}");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            string dimensions = PngInfo.TrySize(path) is { } size
                ? $"{size.Width}\u00d7{size.Height}" : EditMapCardVm.NoDimensions;
            return new EditMapPreview(DecodeMap(stream, rmo), dimensions);
        }
        catch
        {
            return new EditMapPreview(null, EditMapCardVm.NoDimensions);
        }
    }

    internal static Bitmap DecodeMesh(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        return Bitmap.DecodeToWidth(stream, ThumbnailCache.MaxDim);
    }

    private string? MeshBundle(RecipePart recipe)
    {
        if (recipe.IsRecipeBacked)
        {
            try { return _catalog()?.ResolveAddress(recipe.MeshAddress); }
            catch { return null; }
        }
        return recipe.IsSmrBacked ? recipe.MeshBundle : null;
    }
}
