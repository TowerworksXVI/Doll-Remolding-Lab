using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Blender;
using Remold.Core.Mesh;
using Remold.Core.Project;
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
    public void RegistrySubkeys_PreferNewestVersion_IndependentOfEnumerationOrder()
    {
        var hits = BlenderLocator.RegistrySubkeysNewestFirst(
            new[] { "4.1", "Blender beta", "4.3", "4.10" });

        Assert.Equal(new[] { "4.10", "4.3", "4.1", "Blender beta" }, hits);
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

    [Fact]
    public void SendSidecar_EditIdsReadTheExistingAndNewTargetUnion()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var targets = new Dictionary<string, BlenderPartTarget>(System.StringComparer.Ordinal)
        {
            ["body1_lod0"] = BlenderPartTarget.Existing("edit-long"),
            ["cloth1_lod0"] = BlenderPartTarget.New("New shape"),
            ["hair_lod0"] = BlenderPartTarget.New(""),
        };

        BlenderBridge.WriteSendSidecar(glb, System.Array.Empty<string>(), targets);

        var json = File.ReadAllText(BlenderBridge.SidecarPath(glb));
        Assert.Contains("\"body1_lod0\": \"edit-long\"", json);
        Assert.Contains("\"new\": \"New shape\"", json);
        var read = BlenderBridge.ReadEditIds(BlenderBridge.SidecarPath(glb));
        Assert.Equal("edit-long", read["body1_lod0"].ExistingEditId);
        Assert.Equal("New shape", read["cloth1_lod0"].NewEditName);
        Assert.Equal("", read["hair_lod0"].NewEditName); // intake supplies the default name later
    }

    [Fact]
    public void SendSidecar_AnExistingStringTargetRemainsBackwardReadable()
    {
        using var g = new TempGame();
        var sidecar = BlenderBridge.SidecarPath(g.At("_combined.glb"));
        File.WriteAllText(sidecar,
            "{\"source\":\"blender-send\",\"editIds\":{\"body1_lod0\":\"edit-long\"}}");

        var targets = BlenderBridge.ReadEditIds(sidecar);

        var target = Assert.Single(targets).Value;
        Assert.True(target.IsExisting);
        Assert.False(target.IsNew);
        Assert.Equal("edit-long", target.ExistingEditId);
    }

    /// <summary>The label the app writes for a part is what the Blender panel calls it — a multi-token
    /// name the addon could never recover from the asset name's structure — so it has to survive the
    /// session document verbatim.</summary>
    [Fact]
    public void SessionPart_labels_round_trip_the_session_document()
    {
        using var g = new TempGame();
        string glb = g.At("open.glb");
        File.WriteAllBytes(glb, new byte[0]);
        BlenderBridge.WriteSession(glb, null, new[]
        {
            new SessionPart("c_KarstSSR0101_slg_P3_body_fight_lod0", Edited: false,
                Label: "P3_body_fight"),
        });
        Assert.Equal("P3_body_fight",
            Assert.Single(BlenderBridge.ReadSessionDocument(glb)!.Parts).Label);
    }

    [Fact]
    public void SendSidecar_RefusesPartKeysThatCollideCaseInsensitively()
    {
        using var g = new TempGame();
        var sidecar = BlenderBridge.SidecarPath(g.At("_combined.glb"));
        File.WriteAllText(sidecar,
            "{\"editIds\":{\"Body1_lod0\":\"edit-long\",\"body1_LOD0\":{\"new\":\"Other\"}}}");

        var refusal = Assert.Throws<AuthoredRefusalException>(() => BlenderBridge.ReadEditIds(sidecar));

        Assert.Contains("body1_LOD0", refusal.Message);
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
        Assert.Equal(new[] { "moved_lod0" }, MeshGltf.MeshesWithNodeTransform(glb));
    }

    /// <summary>The whole point of naming the meshes rather than answering yes/no about the first one: a
    /// send carries every part of its session, and the modder moved ONE of them. Reporting on the send as a
    /// whole would name a part that arrived exactly as authored — and staying quiet because the first part
    /// was clean is the silence this detector exists to end. Both parts are UNSKINNED, which is the only
    /// state a node transform survives in: glTF forbids one on a skinned node, and Blender bakes it into
    /// the vertices there instead.</summary>
    [Fact]
    public void MeshesWithNodeTransform_NamesOnlyTheMovedPartOfAMultiPartSend()
    {
        using var g = new TempGame();
        var glb = TwoUnskinnedMeshGlb(g.At("_combined.glb"), moved: "cloth1_lod0");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Equal("body1_lod0", MeshGltf.MeshNames(glb)[0]);   // the clean one leads the file…
        Assert.True(edit.NodeTransformIgnored);                   // …and the send is still flagged
        Assert.Equal(new[] { "cloth1_lod0" }, MeshGltf.MeshesWithNodeTransform(glb));
        Assert.False(MeshGltf.HasNonIdentityNodeTransform(glb, "body1_lod0"));
    }

    /// <summary>The receive path's own wording and scoping: one line per part that lost its placement, and
    /// nothing at all for the parts that did not — nor for a return the read never flagged.</summary>
    [Fact]
    public void BlenderTransformNotes_ReportPerAffectedPart_AndOnlyWhenTheReadFlaggedIt()
    {
        using var g = new TempGame();
        var glb = TwoUnskinnedMeshGlb(g.At("_combined.glb"), moved: "cloth1_lod0");
        var parts = new[] { "body1_lod0", "cloth1_lod0" };

        var note = Assert.Single(Remold.App.ViewModels.MainWindowViewModel.BlenderTransformNotes(
            BlenderBridge.ReadSend(glb), parts));

        Assert.Equal("The Object-mode position or scale on cloth1_lod0 was not applied. "
            + "Apply the transform in Blender (Ctrl+A), then send it back.", note);
        // a return the read found clean re-opens nothing and says nothing
        Assert.Empty(Remold.App.ViewModels.MainWindowViewModel.BlenderTransformNotes(
            new IncomingEdit(null, glb), parts));
    }

    /// <summary>Two named, skinless meshes and the write-complete sidecar, with <paramref name="moved"/>
    /// placed by its node — the shape a send of static parts takes when one of them was dragged in Object
    /// mode. The triangles differ so the writer keeps them as two meshes.</summary>
    private static string TwoUnskinnedMeshGlb(string path, string moved)
    {
        var scene = new SharpGLTF.Scenes.SceneBuilder();
        float offset = 0;
        foreach (var name in new[] { "body1_lod0", "cloth1_lod0" })
        {
            var mesh = new SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPosition>(name);
            mesh.UsePrimitive(new SharpGLTF.Materials.MaterialBuilder(name + "_mat")).AddTriangle(
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(offset, 0, 0),
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(1, 0, 0),
                new SharpGLTF.Geometry.VertexTypes.VertexPosition(0, 1, 0));
            scene.AddRigidMesh(mesh, name == moved
                ? System.Numerics.Matrix4x4.CreateTranslation(5, 0, 0)
                : System.Numerics.Matrix4x4.Identity);
            offset += 0.25f;
        }
        scene.ToGltf2().SaveGLB(path);
        File.WriteAllText(BlenderBridge.SidecarPath(path), "{\"source\":\"blender-send\"}");
        return path;
    }

    /// <summary>The baseline a return is compared against has to survive the mod folder being RENAMED
    /// between the launch and the send. The app allows that rename — the hold on it only stands while a
    /// return is being applied, and Blender being open is not that — so an absolute path recorded at launch
    /// names a folder that has since ceased to exist, and a baseline that cannot be found reads as "cannot
    /// tell" and quietly turns every part of the outfit into an edit.</summary>
    [Fact]
    public void ReadReturnBaseline_StillNamesTheOpenedGlbAfterTheModFolderIsRenamed()
    {
        using var g = new TempGame();
        string before = g.At("Vesna casual"), after = g.At("Vesna casual v2");
        Directory.CreateDirectory(before);
        string opened = Path.Combine(before, "composition.glb");
        File.WriteAllText(opened, "");
        BlenderBridge.WriteSession(opened, null, new[] { new SessionPart("cloth1_lod0", false) },
            "return.glb",
            new[] { new BlenderSessionTarget("cloth1_lod0", "", opened, Subject: "Vesna", Outfit: "VesnaSSR01") });

        Directory.Move(before, after);

        Assert.Equal(Path.Combine(after, "composition.glb"),
            BlenderBridge.ReadReturnBaseline(Path.Combine(after, "return.glb")));
    }

    /// <summary>A session document that recorded the opened glb as an ABSOLUTE path — what a build between
    /// the field appearing and it being made relative wrote — is read exactly as it was written. The
    /// re-rooting is for paths that are relative, and nothing else.</summary>
    [Fact]
    public void ReadReturnBaseline_ReadsAnAbsolutelyRecordedPathAsGiven()
    {
        using var g = new TempGame();
        string returned = g.At("run/return.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(returned)!);
        string elsewhere = g.At("somewhere-else/composition.glb");
        File.WriteAllText(BlenderBridge.TargetPath(returned),
            "{\"sessionId\":\"abc\",\"openedGlb\":" + System.Text.Json.JsonSerializer.Serialize(elsewhere)
            + ",\"targets\":[]}");

        Assert.Equal(elsewhere, BlenderBridge.ReadReturnBaseline(returned));
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
