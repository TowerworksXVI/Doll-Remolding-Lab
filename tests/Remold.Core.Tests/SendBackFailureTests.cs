using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a send-back that will NOT read becomes on the target it names. The mark is the same either way — the
/// part is flagged edited so Revert lights up — but the DONOR RECORD it carries is not: records describe the
/// bytes in the workspace glb, and only a route that actually read new bytes can have voided them.
///
/// <para>Driven through the offline-send scan, which is the route a stale sidecar arrives on: it runs
/// synchronously at mod open, so the whole failure lands before the open returns.</para>
/// </summary>
[Collection("Dispatcher")]
public class SendBackFailureTests
{
    [Fact]
    public async Task AGlbThatCouldNotBeOpenedAtAllKeepsTheDonorRecordsItAlreadyHad()
    {
        // A replayed sidecar over a glb something else holds open. Nothing rewrote that file, so the record
        // still describes what is on disk — dropping it would discard a valid record over unchanged bytes.
        using var temp = new TempGame();
        using var settings = new SettingsSnapshot();
        var (root, glb) = Seed(temp, "Held Glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        using (File.Open(glb, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var vm = new MainWindowViewModel(startLoad: false);
            Assert.True(await vm.OpenModAsync(root));      // arms the watcher, which scans and fails on this send
            await Until(() => ModProject.Load(root).Targets[0].Edited, "the failed send to reach the target");
        }

        var target = ModProject.Load(root).Targets[0];
        Assert.True(target.Edited);
        Assert.NotNull(target.DonorTextures);
        Assert.Equal("body_s0_base.png", target.DonorTextures![0].Albedo);
        Assert.Equal(new[] { "DonorMat" }, target.DonorMaterials!.ToArray());
    }

    [Fact]
    public async Task AGlbThatOpensButWontParseVoidsTheDonorRecordsOfTheMeshItReplaced()
    {
        // Blender really did write these bytes, whatever they are. The mesh the records described is gone,
        // so keeping them would bind maps to submeshes that no longer exist.
        using var temp = new TempGame();
        using var settings = new SettingsSnapshot();
        var (root, glb) = Seed(temp, "Bad Glb");
        File.WriteAllText(glb, "not a glb at all");
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        await Until(() => ModProject.Load(root).Targets[0].Edited, "the failed send to reach the target");

        var target = ModProject.Load(root).Targets[0];
        Assert.True(target.Edited);
        Assert.Null(target.DonorTextures);
        Assert.Null(target.DonorMaterials);
    }

    // ---- helpers ----

    /// <summary>A saved one-mesh mod whose single target already carries donor records. The folder is
    /// slug-matched so no autosave rename moves it out from under the watcher.</summary>
    private static (string Root, string Glb) Seed(TempGame temp, string name)
    {
        var root = Path.Combine(temp.Root, ModNaming.Slug(name));
        var glb = Path.Combine(root, "meshes", "body1_lod0.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(glb)!);
        var seed = new ModProject { RootDir = root };
        seed.Info.Name = name;
        seed.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "abc", ObjectName = "body1_lod0",
            ReplaceFile = "meshes/body1_lod0.glb",
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "body_s0_base.png" } },
            DonorMaterials = new List<string> { "DonorMat" },
        });
        seed.Save();
        return (root, glb);
    }

    private static UnityMesh Triangle(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    /// <summary>The watcher may hand its report to the dispatcher rather than run it inline; nothing pumps
    /// that queue in a test host, so the poll drains it.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }
}
