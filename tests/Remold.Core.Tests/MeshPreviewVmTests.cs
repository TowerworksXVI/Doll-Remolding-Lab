using System;
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
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public class MeshPreviewVmTests
{
    [Fact]
    public void ImportedEditedMesh_WithoutOriginalVertexCount_RendersPreview()
    {
        using var temp = new TempDir();
        var project = new ModProject { RootDir = temp.Root };
        var vm = Vm(project);
        var (part, target, fullPath) = AddPart(project, vm, "cloth1");
        target.Edited = true;
        target.OriginalVerts = null;
        target.OriginalFile = null;

        vm.NotifyMeshEdited(fullPath);

        Assert.True(PumpUntil(() => part.HasMeshPreview || part.IsMeshPreviewFailed));
        Assert.True(part.HasMeshPreview);
        Assert.False(part.IsMeshPreviewFailed);
        Assert.Equal("3 vertices", Assert.Single(part.InspectorLines));
    }

    [Fact]
    public void NotifyMeshEdited_TwoEditedParts_AdvancesOnlyAffectedPartRequest()
    {
        using var temp = new TempDir();
        var project = new ModProject { RootDir = temp.Root };
        var vm = Vm(project);
        var (first, _, firstPath) = AddPart(project, vm, "cloth1");
        var (second, _, _) = AddPart(project, vm, "face");
        int firstRequest = first.BeginMeshPreviewRequest(edited: true);
        int secondRequest = second.BeginMeshPreviewRequest(edited: true);

        vm.NotifyMeshEdited(firstPath);

        Assert.False(first.IsCurrentMeshPreviewRequest(firstRequest));
        Assert.True(second.IsCurrentMeshPreviewRequest(secondRequest));
        Assert.True(PumpUntil(() => first.HasMeshPreview || first.IsMeshPreviewFailed));
    }

    [Fact]
    public void NotifyMeshEdited_UsesByteComparisonWhenPersistedFlagIsStale()
    {
        using var temp = new TempDir();
        var project = new ModProject { RootDir = temp.Root };
        var vm = Vm(project);
        var (part, target, fullPath) = AddPart(project, vm, "cloth1");
        string originalRelative = Path.Combine(Materializer.SubjectFolder(Character, Stem), "original.glb");
        string originalPath = Path.Combine(temp.Root, originalRelative);
        MeshGltf.ExportGlb(Triangle("different", scale: 2f), originalPath);
        target.OriginalFile = originalRelative;
        target.Edited = false;
        int prior = part.BeginMeshPreviewRequest();

        vm.NotifyMeshEdited(fullPath);

        Assert.False(part.IsCurrentMeshPreviewRequest(prior));
        Assert.True(PumpUntil(() => part.HasMeshPreview || part.IsMeshPreviewFailed));
        Assert.True(part.IsPreviewingEditedMesh);
    }

    [Fact]
    public void VanillaPreview_CatalogUnavailable_SurfacesEnvironmentCause()
    {
        var project = new ModProject();
        var vm = Vm(project);
        var part = Part("cloth1", recipe: true);
        vm.Nodes.Add(part);
        part.MarkMeshPreviewFailed();

        vm.SelectedNode = part;

        Assert.True(PumpUntil(() => part.HasMeshPreviewFailureCause));
        Assert.True(part.IsMeshPreviewFailed);
    }

    private const string Character = "Testy";
    private const string Stem = "TestySSR01";
    private const string Prefix = "c_TestySSR01_slg_";

    private static WorkbenchVm Vm(ModProject project) => new(
        project: () => project,
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null,
        decodeMeshPreview: _ => (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap)));

    private static (WorkbenchNodeVm Part, ProjectTarget Target, string FullPath) AddPart(
        ModProject project, WorkbenchVm vm, string token)
    {
        string relative = Path.Combine(Materializer.SubjectFolder(Character, Stem), token + ".glb");
        string fullPath = Path.Combine(project.RootDir!, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        MeshGltf.ExportGlb(Triangle(Prefix + token), fullPath);
        var target = new ProjectTarget
        {
            AssetType = "Mesh",
            Bundle = "bundle",
            ObjectName = Prefix + token,
            ReplaceFile = relative,
            Edited = true,
        };
        project.Targets.Add(target);
        var part = Part(token);
        vm.Nodes.Add(part);
        return (part, target, fullPath);
    }

    private static WorkbenchNodeVm Part(string token, bool recipe = false) => new()
    {
        Kind = WorkbenchNodeKind.Part,
        Title = token,
        PartToken = token,
        Subject = new WorkbenchSubjectRef(Character, Stem, Prefix,
            new Outfit(0, Stem, OutfitKind.Base)),
        Recipe = recipe
            ? new RecipePart(token, Prefix + token, "address", Array.Empty<RecipeTierSlot>())
            : null,
    };

    private static UnityMesh Triangle(string name, float scale = 1f) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new[] { 0f, 0f, 0f, scale, 0f, 0f, 0f, scale, 0f },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
        Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
    };

    private static bool PumpUntil(Func<bool> condition) => SpinWait.SpinUntil(() =>
    {
        Dispatcher.UIThread.RunJobs();
        return condition();
    }, TimeSpan.FromSeconds(10));

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gf2-mesh-vm-" + Guid.NewGuid());
        public TempDir() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
