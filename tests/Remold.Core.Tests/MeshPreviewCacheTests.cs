using System.IO;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Export;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

public class MeshPreviewCacheTests
{
    [Fact]
    public void TryGetCachedMesh_RequiresImageAndVertexSidecar_AndKeysCatalogVersion()
    {
        using var temp = new TempDir();
        var cache = new ThumbnailCache(temp.Root);
        var path = cache.MeshPathFor("bundle-a", "c_body_lod0", "24535");
        Assert.Equal("meshes6", new FileInfo(path).Directory!.Parent!.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1 });
        Assert.Null(cache.TryGetCachedMesh("bundle-a", "c_body_lod0", "24535"));

        File.WriteAllText(Path.ChangeExtension(path, ".vertices"), "12480");
        var hit = cache.TryGetCachedMesh("bundle-a", "c_body_lod0", "24535");

        Assert.NotNull(hit);
        Assert.Equal(path, hit.Value.Path);
        Assert.Equal(12_480, hit.Value.VertexCount);
        Assert.Null(cache.TryGetCachedMesh("bundle-a", "c_body_lod0", "24536"));
    }

    [Fact]
    public void EnsureMeshThumb_DecodeFailure_WritesNoCacheEntry()
    {
        using var temp = new TempDir();
        var cache = new ThumbnailCache(temp.Root);

        var result = cache.EnsureMeshThumb(new byte[] { 1, 2, 3 }, "bundle-a", "bad_mesh", "24535");

        Assert.Null(result);
        Assert.Null(cache.TryGetCachedMesh("bundle-a", "bad_mesh", "24535"));
        Assert.False(File.Exists(cache.MeshPathFor("bundle-a", "bad_mesh", "24535")));
    }

    [Fact]
    public void SameNamedMeshes_InDifferentBundles_ProduceDistinctCacheEntries()
    {
        using var temp = new TempDir();
        var cache = new ThumbnailCache(Path.Combine(temp.Root, "cache"));
        const string meshName = "shared_mesh";
        Directory.CreateDirectory(temp.Root);
        var bundleA = Path.Combine(temp.Root, "a.bundle");
        var bundleB = Path.Combine(temp.Root, "b.bundle");
        SyntheticBundle.BuildOneMesh(bundleA, meshName,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        SyntheticBundle.BuildOneMesh(bundleB, meshName,
            new[] { 0f, 0f, 0f, 2f, 0f, 0f, 2f, 1f, 0f, 0f, 1f, 0f },
            new[] { 0, 1, 2, 0, 2, 3 });

        var first = cache.EnsureMeshThumb(File.ReadAllBytes(bundleA), "bundle-a", meshName, "24535");
        var second = cache.EnsureMeshThumb(File.ReadAllBytes(bundleB), "bundle-b", meshName, "24535");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Value.Path, second.Value.Path);
        Assert.Equal(3, first.Value.VertexCount);
        Assert.Equal(4, second.Value.VertexCount);
        Assert.True(File.Exists(first.Value.Path));
        Assert.True(File.Exists(second.Value.Path));
    }

    /// <summary>The persistence rule: the mesh key is catalog + bundle + mesh + path id and carries NO
    /// texture identity, so a render that sampled the modder's own map may not be stored under it — the next
    /// project asking for that game mesh would be served those pixels. Such a render comes through
    /// <see cref="ThumbnailCache.RenderMeshThumb"/>, which leaves the cache untouched in both directions.</summary>
    [Fact]
    public void RenderMeshThumb_ProducesTheRender_ButWritesNoCacheEntry()
    {
        using var temp = new TempDir();
        var cache = new ThumbnailCache(Path.Combine(temp.Root, "cache"));
        Directory.CreateDirectory(temp.Root);
        var bundle = Path.Combine(temp.Root, "a.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "edited_mesh",
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });

        var render = cache.RenderMeshThumb(File.ReadAllBytes(bundle), "edited_mesh");

        Assert.NotNull(render);
        Assert.Equal(3, render.Value.VertexCount);
        Assert.NotEmpty(render.Value.Png);
        // nothing persisted, under any key this cache would serve
        Assert.Null(cache.TryGetCachedMesh("bundle-a", "edited_mesh", "24535"));
        Assert.False(File.Exists(cache.MeshPathFor("bundle-a", "edited_mesh", "24535")));
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "cache")));
    }

    /// <summary>The count-only read exists so the edit preview's original-count line never renders: a
    /// render without samplers cached under the game-identity key was served to the bare part's preview
    /// as its textured picture. Counting must leave the cache untouched in both directions.</summary>
    [Fact]
    public void MeshVertexCount_ReturnsTheCount_AndWritesNoCacheEntry()
    {
        using var temp = new TempDir();
        var cacheRoot = Path.Combine(temp.Root, "cache");
        var cache = new ThumbnailCache(cacheRoot);
        Directory.CreateDirectory(temp.Root);
        var bundle = Path.Combine(temp.Root, "a.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "counted_mesh",
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });

        Assert.Equal(3, ThumbnailCache.MeshVertexCount(File.ReadAllBytes(bundle), "counted_mesh"));
        Assert.Null(ThumbnailCache.MeshVertexCount(File.ReadAllBytes(bundle), "absent_mesh"));
        Assert.Null(ThumbnailCache.MeshVertexCount(new byte[] { 1, 2, 3 }, "counted_mesh"));
        Assert.Null(cache.TryGetCachedMesh("bundle-a", "counted_mesh", "24535"));
        Assert.False(Directory.Exists(cacheRoot));
    }

    [Fact]
    public void GameMeshVertexCount_OnACacheMiss_WritesNothingUnderAFakeRoot()
    {
        using var temp = new TempDir();
        string cacheRoot = Path.Combine(temp.Root, "fake-cache-root");
        string bundle = Path.Combine(temp.Root, "game.bundle");
        Directory.CreateDirectory(temp.Root);
        long meshPathId = SyntheticBundle.BuildOneMesh(bundle, "counted_mesh",
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        byte[] bytes = File.ReadAllBytes(bundle);
        var service = new EditPreviewService(() => null, () => null, _ => bytes,
            new ThumbnailCache(cacheRoot));
        var recipe = new RecipePart("body", "counted_mesh", "",
            System.Array.Empty<RecipeTierSlot>(), "bundle-a", meshPathId);

        int? count = service.GameMeshVertexCount(recipe);

        Assert.Equal(3, count);
        Assert.False(Directory.Exists(cacheRoot));
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gf2-mesh-thumb-" + System.Guid.NewGuid());
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
