using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Remold.App.ViewModels.Workbench;
using Xunit;

namespace Remold.Core.Tests;

public class MeshPreviewStateTests
{
    [Fact]
    public void PartPreview_LoadingToLoaded_SetsBitmapAndVertexDelta()
    {
        var node = Part();
        Assert.True(node.IsMeshPreviewLoading);
        Assert.False(node.HasMeshPreview);
        Assert.False(node.IsMeshPreviewFailed);

        // State-only VM test: an uninitialized Bitmap avoids starting Avalonia's platform renderer.
        var bitmap = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
        node.SetMeshPreview(bitmap, 1_125, 1_000, edited: true);

        Assert.False(node.IsMeshPreviewLoading);
        Assert.True(node.HasMeshPreview);
        Assert.False(node.IsMeshPreviewFailed);
        Assert.Equal("1,125 vertices (+125 vs original)", Assert.Single(node.InspectorLines));
    }

    [Fact]
    public void PartPreview_LoadingToFailure_SettlesToQuietNoPreviewState()
    {
        var node = Part();

        node.MarkMeshPreviewFailed();

        Assert.False(node.IsMeshPreviewLoading);
        Assert.False(node.HasMeshPreview);
        Assert.True(node.IsMeshPreviewFailed);
        Assert.Empty(node.InspectorLines);
    }

    [Fact]
    public void PartPreview_NewerRequestInvalidatesOlderProducer()
    {
        var node = Part();

        int vanilla = node.BeginMeshPreviewRequest();
        int edited = node.BeginMeshPreviewRequest(edited: true);

        Assert.False(node.IsCurrentMeshPreviewRequest(vanilla));
        Assert.True(node.IsCurrentMeshPreviewRequest(edited));
        Assert.True(node.IsPreviewingEditedMesh);
    }

    [Fact]
    public void PartPreview_EnvironmentFailure_SurfacesCauseWhileGeometryFailureStaysQuiet()
    {
        var environment = Part();
        var geometry = Part();

        environment.MarkMeshPreviewFailed(environmentFailure: true);
        geometry.MarkMeshPreviewFailed();

        Assert.True(environment.HasMeshPreviewFailureCause);
        Assert.False(geometry.HasMeshPreviewFailureCause);
    }

    private static WorkbenchNodeVm Part() => new() { Kind = WorkbenchNodeKind.Part, Title = "cloth1" };
}
