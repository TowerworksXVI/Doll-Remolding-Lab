using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// An edited texture reaching everything that shows it, not just its own map card: the part's mesh preview
/// samples the workspace PNG, the persisted vanilla cache is left out of it, the render on screen is dropped
/// when the file changes, and the card's size line follows the file it now stands for.
/// </summary>
[Collection("Dispatcher")]
public class TexturePropagationVmTests
{
    // ---- sampler source: the modder's file wins over the game's thumb ----

    [Fact]
    public void PreviewSamplers_EditedSubmesh_ReadTheWorkspacePng()
    {
        using var temp = new TempGame();
        var vm = Vm(new ModProject());
        var red = TestImages.WritePng(temp.At("face.bundle1.png"), 4, 4, r: 255, g: 0, b: 0);
        var part = PartNode();
        part.EditedBaseMaps = new string?[] { red };

        var samplers = vm.BuildPreviewSamplers(part, "cat-1", Memo(), out bool usedOwnMaps);

        var tex = Assert.Single(samplers!);
        Assert.NotNull(tex);
        Assert.Equal(4, tex!.Value.Width);
        Assert.Equal(255, tex.Value.Pixels[0].R);   // the edited pixels, not the vanilla thumb
        Assert.Equal(0, tex.Value.Pixels[0].B);
        Assert.True(usedOwnMaps);                   // → the render this feeds may not be persisted
    }

    [Fact]
    public void PreviewSamplers_UntouchedSubmesh_TakesTheVanillaRoute()
    {
        // No workspace or authored file on the row, so the only source left is the thumb cache — which this
        // VM cannot reach (its deobfuscate yields nothing), leaving the submesh untextured rather than
        // sampling some other part's file.
        var vm = Vm(new ModProject());
        var part = PartNode();

        var samplers = vm.BuildPreviewSamplers(part, "cat-1", Memo(), out bool usedOwnMaps);

        Assert.Null(samplers);
        Assert.False(usedOwnMaps);          // → this render IS the game's, and may be cached as such
        Assert.False(part.HasOwnBaseMaps);
    }

    [Fact]
    public void PreviewSamplers_AuthoredMapOutranksAnEditedGameTexture()
    {
        using var temp = new TempGame();
        var vm = Vm(new ModProject());
        var part = PartNode();
        part.AuthoredBaseMaps = new string?[] { TestImages.WritePng(temp.At("authored.png"), 4, 4, r: 0, g: 255, b: 0) };
        part.EditedBaseMaps = new string?[] { TestImages.WritePng(temp.At("edited.png"), 4, 4, r: 255, g: 0, b: 0) };

        var tex = Assert.Single(vm.BuildPreviewSamplers(part, "cat-1", Memo(), out _)!);

        Assert.Equal(255, tex!.Value.Pixels[0].G);   // the authored file replaced the slot outright
    }

    // ---- the cache rule lives on the node, so both the read and the write side read the same predicate ----

    [Fact]
    public void EditedTextureTarget_MakesThePartCarryItsOwnBaseMaps()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, part, map) = TreeWithOneMap(project, g, edited: true);

        vm.RefreshNodeStates();

        Assert.True(map.IsEdited);
        Assert.Equal(project.Resolve(TextureRel), Assert.Single(part.EditedBaseMaps));
        Assert.True(part.HasOwnBaseMaps);   // → the render neither reads nor writes the vanilla mesh cache
    }

    [Fact]
    public void UnEditedTextureTarget_LeavesThePartOnTheVanillaCacheRoute()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, part, _) = TreeWithOneMap(project, g, edited: false);

        vm.RefreshNodeStates();

        Assert.Empty(part.EditedBaseMaps);
        Assert.False(part.HasOwnBaseMaps);
    }

    /// <summary>The persistence decision reads the EVIDENCE the samplers left, not the row's map lists a
    /// second time. Those lists are UI-thread state and the render runs on a worker, so a revert can land
    /// between the two reads — after which a re-read says "vanilla" about a render that sampled the modder's
    /// file, and the machine-wide mesh-thumb cache (keyed by game identity alone) would serve those pixels to
    /// every other project.</summary>
    [Fact]
    public void MapsClearedMidRender_StillLeavesTheVanillaMeshCacheEmpty()
    {
        using var g = new TempGame();
        var cacheRoot = Path.Combine(g.Root, "thumbs");
        var bundlePath = Path.Combine(g.Root, "body.bundle");
        SyntheticBundle.BuildOneMesh(bundlePath, MeshName,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        var bundleBytes = File.ReadAllBytes(bundlePath);
        var vm = Vm(new ModProject(), cacheRoot, id => id == "bundle1" ? bundleBytes : null);
        var part = SmrPartNode();
        part.EditedBaseMaps = new string?[] { TestImages.WritePng(g.At("edited.png"), 4, 4, r: 255, g: 0, b: 0) };
        // the revert, landing in the window between the sampler build and the persistence decision
        vm.OnSamplersBuiltForTest = () => part.EditedBaseMaps = Array.Empty<string?>();

        vm.MeshPreviewBatch(new[] { new WorkbenchVm.MeshPreviewRequest(part, part.BeginMeshPreviewRequest()) },
            CatalogVersion, CancellationToken.None, maxDop: 1);
        Dispatcher.UIThread.RunJobs();

        Assert.True(part.HasMeshPreview);   // the render really happened — the assertion below isn't vacuous
        var cache = new ThumbnailCache(cacheRoot);
        Assert.Null(cache.TryGetCachedMesh("bundle1", MeshName, CatalogVersion, MeshPathId));
        Assert.False(File.Exists(cache.MeshPathFor("bundle1", MeshName, CatalogVersion, MeshPathId)));
    }

    /// <summary>The other side of the same rule: a part sampling nothing of the modder's IS a vanilla render,
    /// and does land in the cache — so the test above is measuring the evidence, not a dead render path.</summary>
    [Fact]
    public void PartWithNoOwnMaps_PersistsItsRenderAsTheVanillaThumb()
    {
        using var g = new TempGame();
        var cacheRoot = Path.Combine(g.Root, "thumbs");
        var bundlePath = Path.Combine(g.Root, "body.bundle");
        SyntheticBundle.BuildOneMesh(bundlePath, MeshName,
            new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        var bundleBytes = File.ReadAllBytes(bundlePath);
        var vm = Vm(new ModProject(), cacheRoot, id => id == "bundle1" ? bundleBytes : null);
        var part = SmrPartNode();

        vm.MeshPreviewBatch(new[] { new WorkbenchVm.MeshPreviewRequest(part, part.BeginMeshPreviewRequest()) },
            CatalogVersion, CancellationToken.None, maxDop: 1);
        Dispatcher.UIThread.RunJobs();

        var hit = new ThumbnailCache(cacheRoot).TryGetCachedMesh("bundle1", MeshName, CatalogVersion, MeshPathId);
        Assert.NotNull(hit);
        Assert.Equal(3, hit!.Value.VertexCount);
    }

    // ---- invalidation: the part on screen stops showing the pre-edit render ----

    [Fact]
    public void TextureFileChanged_DropsThePreviewOfEveryPartThatSamplesIt()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, part, _) = TreeWithOneMap(project, g, edited: true);
        vm.RefreshNodeStates();
        int showing = part.BeginMeshPreviewRequest();   // the render made from the pre-edit map
        Assert.False(part.NeedsMeshPreview);            // memoized: nothing would re-render it

        vm.NotifyTextureFileChanged(project.Resolve(TextureRel));

        Assert.False(part.IsCurrentMeshPreviewRequest(showing));   // that render is rejected if it lands…
        Assert.True(part.NeedsMeshPreview);                        // …and selecting the part renders again
    }

    [Fact]
    public void TextureFileChanged_LeavesAPartThatDoesNotSampleItAlone()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, part, _) = TreeWithOneMap(project, g, edited: true);
        vm.RefreshNodeStates();
        int showing = part.BeginMeshPreviewRequest();

        vm.NotifyTextureFileChanged(Path.Combine(g.Root, "textures", "someone-elses.png"));

        Assert.True(part.IsCurrentMeshPreviewRequest(showing));
        Assert.False(part.NeedsMeshPreview);
    }

    // ---- the card's size line follows the file, on the bundle-backed branch too ----

    [Fact]
    public void TextureFileChanged_BundleBackedCard_RereadsItsDimensionsFromTheNewFile()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, _, map) = TreeWithOneMap(project, g, edited: true);
        map.Dimensions = "1024×1024";   // the stock size the card was showing

        // the drop landed a 64×32 image in the workspace file
        TestImages.WritePng(project.Resolve(TextureRel), 64, 32);
        vm.NotifyTextureFileChanged(project.Resolve(TextureRel));

        Assert.True(PumpUntil(() => map.Dimensions == "64×32"));
    }

    /// <summary>…and it stays that way. The meta cache is keyed by GAME identity and still holds the stock
    /// size, so re-selecting the material must not serve that back over the file's own.</summary>
    [Fact]
    public void EditedCard_Reselected_KeepsTheFilesSizeNotTheCachedStockOne()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var (vm, _, map) = TreeWithOneMap(project, g, edited: true);
        vm.RefreshNodeStates();
        TestImages.WritePng(project.Resolve(TextureRel), 64, 32);
        var material = vm.Nodes[0].Children[0].Children[0];

        vm.SelectedNode = material;

        Assert.True(PumpUntil(() => map.Dimensions == "64×32"));
    }

    // ---- fixtures ----

    // the workspace name every producer writes: <name>.<bundle>.<subject>.png
    private const string TextureRel = "textures/c_stem_body1_d.bundle1.char_stem.png";
    private const string TextureOrigRel = "originals/c_stem_body1_d.bundle1.char_stem.png";

    private static readonly WorkbenchSubjectRef Subject =
        new("char", "stem", "c_stem_slg_", new Outfit(0, "stem", OutfitKind.Base));

    private static ConcurrentDictionary<string, MeshPreviewRenderer.PreviewTexture?> Memo() =>
        new(StringComparer.Ordinal);

    private static WorkbenchVm Vm(ModProject project, string? thumbnailRoot = null,
        Func<string, byte[]?>? tryDeobfuscate = null) => new(
        project: () => project,
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: tryDeobfuscate ?? (_ => null),
        catalog: null,
        decodeMeshPreview: _ => FakeBitmap(),
        thumbnailRoot: thumbnailRoot);

    /// <summary>Avalonia isn't initialized in these tests, so a preview bitmap only has to be a reference.</summary>
    private static Bitmap FakeBitmap() => (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));

    private static WorkbenchNodeVm PartNode() => new()
    {
        Kind = WorkbenchNodeKind.Part,
        Title = "body",
        PartToken = "body",
        Subject = Subject,
        SubmeshBaseMaps = new SubjectMap?[] { new("_BaseMap", "c_stem_body1_d", "bundle1") },
    };

    private const string CatalogVersion = "cat-1";
    private const string MeshName = "c_stem_body_lod0";
    private const long MeshPathId = 1;   // SyntheticBundle assigns the mesh path id 1

    /// <summary>A part backed the SMR way — a resolved bundle plus an exact mesh path id — so the preview
    /// batch reaches the bundle without a catalog to resolve an address through.</summary>
    private static WorkbenchNodeVm SmrPartNode() => new()
    {
        Kind = WorkbenchNodeKind.Part,
        Title = "body",
        PartToken = "body",
        Subject = Subject,
        Recipe = new RecipePart("body", MeshName, "", Array.Empty<RecipeTierSlot>(), "bundle1", MeshPathId),
        SubmeshBaseMaps = new SubjectMap?[] { new("_BaseMap", "c_stem_body1_d", "bundle1") },
    };

    /// <summary>A one-part, one-material, one-map tree over a project whose texture target is (or isn't)
    /// edited — the workspace PNG differs from its original by bytes, which is what decides it.</summary>
    private static (WorkbenchVm Vm, WorkbenchNodeVm Part, WorkbenchMapVm Map) TreeWithOneMap(
        ModProject project, TempGame g, bool edited)
    {
        TestImages.WritePng(project.Resolve(TextureRel), 8, 8, r: 255, g: 0, b: 0);
        TestImages.WritePng(project.Resolve(TextureOrigRel), 8, 8,
            r: edited ? (byte)0 : (byte)255, g: 0, b: 0);
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D",
            Bundle = "bundle1",
            ObjectName = "c_stem_body1_d",
            ReplaceFile = TextureRel,
            OriginalFile = TextureOrigRel,
            SubjectCharacter = "char",
            SubjectOutfit = "stem",
        });

        var vm = Vm(project, Path.Combine(g.Root, "thumbs"));
        var map = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_body1_d", "bundle1") { Subject = Subject };
        var material = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Material, Title = "mat0", Subject = Subject, PartToken = "body", MaterialIndex = 0 };
        material.Maps.Add(map);
        var part = PartNode();
        part.Children.Add(material);
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        subject.Children.Add(part);
        vm.Nodes.Add(subject);
        return (vm, part, map);
    }

    private static bool PumpUntil(Func<bool> condition) => SpinWait.SpinUntil(() =>
    {
        Dispatcher.UIThread.RunJobs();
        return condition();
    }, TimeSpan.FromSeconds(10));
}
