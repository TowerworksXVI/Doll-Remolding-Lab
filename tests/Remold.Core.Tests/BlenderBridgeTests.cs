using System.Collections.Generic;
using System.IO;
using Remold.Core.Blender;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The testable parts of the Blender bridge: discovery, the sidecar contract, and reading a completed
/// "Send to the Lab" export back into a Unity-space mesh. The launch and the watcher are I/O glue.
/// </summary>
public class BlenderBridgeTests
{
    [Fact]
    public void InstallDirCandidates_PrefersNewestVersion_AndIncludesSteam()
    {
        using var g = new TempGame();
        // synthetic "Program Files" with two versioned installs + a Steam install
        foreach (var (dir, _) in new[] { ("Blender Foundation/Blender 4.2", ""), ("Blender Foundation/Blender 4.3", "") })
            Touch(Path.Combine(g.Root, dir, "blender.exe"));
        Touch(Path.Combine(g.Root, "Steam/steamapps/common/Blender/blender.exe"));

        var hits = BlenderLocator.InstallDirCandidates(g.Root);

        Assert.Equal(3, hits.Count);
        Assert.Contains("Blender 4.3", hits[0]);    // newest first
        Assert.Contains("Blender 4.2", hits[1]);
        Assert.EndsWith(Path.Combine("common", "Blender", "blender.exe"), hits[2]);  // Steam last
    }

    [Fact]
    public void SidecarPath_And_GlbForSidecar_RoundTrip()
    {
        var glb = Path.Combine("C:", "mods", "x", "body_lod0.glb");
        var sc = BlenderBridge.SidecarPath(glb);
        Assert.EndsWith("body_lod0.gf2send.json", sc);
        Assert.Equal(glb, BlenderBridge.GlbForSidecar(sc));
        Assert.Null(BlenderBridge.GlbForSidecar(glb));   // a .glb is not a sidecar
    }

    [Theory]
    // a complete sidecar object — Blender finished json.dump
    [InlineData("{\"source\":\"blender-send\"}", true)]
    [InlineData("{}", true)]
    [InlineData("  {\n  \"source\": \"blender-send\"\n}  ", true)]
    // half-written / malformed / empty — the writer is still going or the file is corrupt: NOT ready
    [InlineData("{\"source\":\"blen", false)]     // truncated mid-dump
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not json", false)]
    [InlineData("[1,2,3]", false)]                // valid JSON but not the sentinel object shape
    [InlineData("\"blender-send\"", false)]       // a bare string, not an object
    public void IsCompleteSidecar_AcceptsOnlyACompleteJsonObject(string text, bool expected)
    {
        // The sidecar is the write-complete SENTINEL: a partial write must read as incomplete so the
        // watcher keeps waiting rather than treat a half-written file as done.
        Assert.Equal(expected, BlenderBridge.IsCompleteSidecar(text));
    }

    [Fact]
    public void ReadSend_ImportsMeshToUnitySpace()
    {
        // A Blender-space glb plus the write-complete sidecar: ReadSend imports it back to Unity space.
        using var g = new TempGame();
        var glb = g.At("cloth1_lod0.glb");
        var src = new UnityMesh
        {
            Name = "cloth1_lod0",
            VertexCount = 3,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };
        MeshGltf.ExportGlb(src, glb);                                    // Blender-space on disk
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Equal("cloth1_lod0", edit.Name);
        Assert.NotNull(edit.Mesh);
        // came back in Unity space: first vertex X round-tripped to 0
        Assert.Equal(0f, edit.Mesh.Channels["Vertex"][0], 5);
        Assert.False(edit.NodeTransformIgnored);                        // clean export: identity node
    }

    [Fact]
    public void ReadSend_FlagsNonIdentityNodeTransform()
    {
        // An Object-mode move/rotate leaves a non-identity node transform that the geometry import IGNORES,
        // so ReadSend must FLAG it rather than apply it.
        using var g = new TempGame();
        var glb = g.At("moved_lod0.glb");
        BuildGlbWithNodeTransform(glb, "moved_lod0",
            System.Numerics.Matrix4x4.CreateTranslation(5, 0, 0));

        var edit = BlenderBridge.ReadSend(glb);
        Assert.True(edit.NodeTransformIgnored);
        Assert.True(MeshGltf.HasNonIdentityNodeTransform(glb));
    }

    private static void BuildGlbWithNodeTransform(string path, string meshName,
        System.Numerics.Matrix4x4 nodeMatrix)
    {
        // a plain geometry glb re-opened and stamped with a non-identity node transform
        var src = new UnityMesh
        {
            Name = meshName, VertexCount = 3,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };
        MeshGltf.ExportGlb(src, path);
        var model = SharpGLTF.Schema2.ModelRoot.Load(path);
        var mesh = model.LogicalMeshes[0];
        foreach (var node in model.LogicalNodes)
            if (node.Mesh == mesh) node.LocalMatrix = nodeMatrix;
        model.SaveGLB(path);
    }

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }
}
