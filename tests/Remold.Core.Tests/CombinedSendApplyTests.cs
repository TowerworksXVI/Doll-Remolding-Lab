using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A whole outfit's send-back, through the receive the app runs. Two things are pinned here that no single
/// part can show: what a send-all does to the parts it merely CARRIED, and where the work runs. The apply
/// is seconds of parsing and re-splitting, so a live send does it off the UI thread — while the offline scan
/// needs the very same receive to finish before its caller's next line, which would otherwise rebuild the
/// workspace glbs it just wrote.
/// </summary>
[Collection("Dispatcher")]
public class CombinedSendApplyTests
{
    /// <summary>The inline mode's whole contract: a receive asked to run in place is finished by the time it
    /// hands a task back. The offline scan runs on its caller's thread precisely so a send that landed while
    /// the app was closed lands before anything regenerates the file it was written into.</summary>
    [Fact]
    public async Task TheInlineApplyIsFinishedBeforeItReturns()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        // body1 comes back moved: something has to be written, so "already finished" is a real claim
        var moved = Part("body1_lod0", 0f);
        moved.Channels["Vertex"][1] += 0.25f;
        var send = WriteSend(mod, moved, Part("cloth1_lod0", 3f));

        var apply = vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: false);

        Assert.True(apply.IsCompleted);
        await apply;
        Assert.Equal(0.25f, FirstY(mod.BodyGlb), 4);
    }

    /// <summary>The same receive run off the UI thread reaches the same place. The two modes differ in
    /// marshalling and in nothing else, so a live send and a replayed one cannot decide differently.</summary>
    [Fact]
    public async Task TheOffThreadApplyLandsTheSameEdit()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var moved = Part("body1_lod0", 0f);
        moved.Channels["Vertex"][1] += 0.25f;
        var send = WriteSend(mod, moved, Part("cloth1_lod0", 3f));

        await vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: true);

        Assert.Equal(0.25f, FirstY(mod.BodyGlb), 4);
        Assert.True(ModProject.Load(mod.Root).Targets.Find(t => t.ObjectName == "body1_lod0")!.Edited);
    }

    /// <summary>A send-all nobody edited. Every part comes back as a full re-export of itself — re-split at
    /// its seams, every float re-quantized — and not one of them may be written, flagged, or re-recorded.
    /// </summary>
    [Fact]
    public async Task ASendAllOfAnUntouchedOutfitWritesNothing()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var before = (Body: File.ReadAllBytes(mod.BodyGlb), Cloth: File.ReadAllBytes(mod.ClothGlb));
        var send = WriteSend(mod, ReSplit(Part("body1_lod0", 0f)), ReSplit(Part("cloth1_lod0", 3f)));

        await vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: true);

        Assert.Equal(before.Body, File.ReadAllBytes(mod.BodyGlb));
        Assert.Equal(before.Cloth, File.ReadAllBytes(mod.ClothGlb));
        foreach (var t in ModProject.Load(mod.Root).Targets) Assert.False(t.Edited);
        Assert.Equal("Nothing changed in the Blender send.", vm.Workbench.Status);
    }

    /// <summary>A part the send did not carry was context in that session, never an answer about itself. Its
    /// file, its flag and the donor record describing what is in it all stand exactly as they were.</summary>
    [Fact]
    public async Task APartTheSendDidNotCarryKeepsEverythingItHad()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g, clothRecord: "textures/kept.png");
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var clothBefore = File.ReadAllBytes(mod.ClothGlb);
        // only body1 comes back, and it comes back edited, so the receive really does write this run
        var moved = Part("body1_lod0", 0f);
        moved.Channels["Vertex"][1] += 0.25f;
        var send = WriteSend(mod, moved);

        await vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: true);

        var saved = ModProject.Load(mod.Root);
        Assert.True(saved.Targets.Find(t => t.ObjectName == "body1_lod0")!.Edited);
        var cloth = saved.Targets.Find(t => t.ObjectName == "cloth1_lod0")!;
        Assert.False(cloth.Edited);
        Assert.Equal("textures/kept.png", cloth.DonorTextures![0].Albedo);
        Assert.Equal(clothBefore, File.ReadAllBytes(mod.ClothGlb));
    }

    /// <summary>A second Send while the first is still applying. Refusing it would lose that work outright —
    /// Blender has already written the file and the sidecar that announced it is spent — so the newer send is
    /// what ends up on disk, whichever of the two applies got there first.</summary>
    [Fact]
    public async Task ASecondSendArrivingMidApplyIsNotDropped()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));

        var first = Part("body1_lod0", 0f);
        first.Channels["Vertex"][1] += 0.25f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, first, Part("cloth1_lod0", 3f))));
        var second = Part("body1_lod0", 0f);
        second.Channels["Vertex"][1] += 0.75f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, second, Part("cloth1_lod0", 3f))));
        await vm.SendApplyInFlight;

        Assert.Equal(0.75f, FirstY(mod.BodyGlb), 4);
    }

    /// <summary>Two outfits in one mod, so two <c>meshes/</c> folders and two send files. A queue holding one
    /// send drops whichever subject arrived first, and what it dropped is unrecoverable — the sidecar that
    /// announced the file is consumed as the watcher reads it.</summary>
    [Fact]
    public async Task SendsFromTwoOutfitsBothApply()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var second = AddSecondSubject(mod);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));

        var body = Part("body1_lod0", 0f);
        body.Channels["Vertex"][1] += 0.25f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, body, Part("cloth1_lod0", 3f))));
        var hair = Part("hair1_lod0", 7f);
        hair.Channels["Vertex"][1] += 0.5f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSendTo(second.Meshes, (hair, mod.ClothMap))));
        await vm.SendApplyInFlight;

        Assert.Equal(0.25f, FirstY(mod.BodyGlb), 4);
        Assert.Equal(7.5f, FirstY(second.Glb), 4);
    }

    /// <summary>A second send of the SAME file behind a first one, with another outfit's queued between them.
    /// The pair naming one file collapses — the file on disk holds the newer bytes and nothing else can be
    /// re-read from it — and that collapse may not take the other outfit's send with it.</summary>
    [Fact]
    public async Task AResentFileLandsAsItsLatestWithoutLosingTheOtherOutfitsSend()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var second = AddSecondSubject(mod);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));

        var first = Part("body1_lod0", 0f);
        first.Channels["Vertex"][1] += 0.25f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, first, Part("cloth1_lod0", 3f))));
        var hair = Part("hair1_lod0", 7f);
        hair.Channels["Vertex"][1] += 0.5f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSendTo(second.Meshes, (hair, mod.ClothMap))));
        var again = Part("body1_lod0", 0f);
        again.Channels["Vertex"][1] += 0.75f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, again, Part("cloth1_lod0", 3f))));
        var latest = Part("body1_lod0", 0f);
        latest.Channels["Vertex"][1] += 1.25f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, latest, Part("cloth1_lod0", 3f))));
        await vm.SendApplyInFlight;

        Assert.Equal(1.25f, FirstY(mod.BodyGlb), 4);
        Assert.Equal(7.5f, FirstY(second.Glb), 4);
    }

    /// <summary>A send queued for one mod while another is open. Its file is not in the mod whose ledger the
    /// apply would ask, so it is dropped and said — never applied against the wrong tree.</summary>
    [Fact]
    public async Task ASendQueuedForAnotherModIsDroppedRatherThanAppliedHere()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var other = Seed(g, name: "Other Mod");
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var otherBefore = File.ReadAllBytes(other.BodyGlb);

        var body = Part("body1_lod0", 0f);
        body.Channels["Vertex"][1] += 0.25f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, body, Part("cloth1_lod0", 3f))));
        var stray = Part("body1_lod0", 0f);
        stray.Channels["Vertex"][1] += 0.75f;
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(other, stray)));
        await vm.SendApplyInFlight;

        Assert.Equal(MainWindowViewModel.SendModNotOpen, vm.Workbench.Status);
        Assert.Equal(otherBefore, File.ReadAllBytes(other.BodyGlb));
        Assert.Equal(0.25f, FirstY(mod.BodyGlb), 4);   // the mod that IS open still got its own send
    }

    /// <summary>The mod is closed while its send is being applied. Everything past that point — the ledger
    /// flag, the save, the summary — would land on the project that replaced it, so the apply stops where it
    /// stands. The footer is already saying an apply is running, so stopping silently would leave it
    /// claiming work that ended.</summary>
    [Fact]
    public async Task AModClosedMidApplyStopsTheWritesAndSaysSo()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var moved = Part("body1_lod0", 0f);
        moved.Channels["Vertex"][1] += 0.25f;

        // the apply is parked on its first off-thread step when this returns
        vm.BeginCombinedApply(new IncomingEdit(null, WriteSend(mod, moved, Part("cloth1_lod0", 3f))));
        vm.NewMod();
        await vm.SendApplyInFlight;

        Assert.Equal(MainWindowViewModel.SendModNotOpen, vm.Workbench.Status);
        // the flag never reached a ledger — not the captured mod's, and not the one that replaced it
        foreach (var t in ModProject.Load(mod.Root).Targets) Assert.False(t.Edited);
    }

    /// <summary>A send whose folder holds no part of the mod. Nothing is written, and the footer says which
    /// of the two silences it is: matched nothing, as against came back carrying nothing.</summary>
    [Fact]
    public async Task ASendMatchingNoPartOfTheModSaysSo()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(mod.Root));
        var elsewhere = Path.Combine(mod.Root, "meshes-none");
        Directory.CreateDirectory(elsewhere);
        var send = WriteSendTo(elsewhere, (Part("body1_lod0", 0f), mod.BodyMap));

        await vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: true);

        Assert.Equal(MainWindowViewModel.SendMatchedNothing, vm.Workbench.Status);
    }

    /// <summary>A send reaching the app with no mod folder open at all.</summary>
    [Fact]
    public async Task ASendArrivingWithNoModOpenSaysSo()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var mod = Seed(g);
        var send = WriteSend(mod, Part("body1_lod0", 0f));
        var vm = new MainWindowViewModel(startLoad: false);   // never opened: the project has no folder

        await vm.ApplyCombinedSendAsync(new IncomingEdit(null, send), offThread: true);

        Assert.Equal(MainWindowViewModel.SendModNotOpen, vm.Workbench.Status);
    }

    // ---- fixtures ----

    private const uint HRoot = 0x1111_1111, HArm = 0x2222_2222;
    private static readonly Dictionary<uint, string> Paths = new() { [HRoot] = "root", [HArm] = "root/arm" };

    private sealed record Mod(string Root, string Meshes, string BodyGlb, string ClothGlb, string BodyMap,
        string ClothMap);

    /// <summary>A saved two-part mod as the app leaves it after an outfit open: each part's workspace glb with
    /// its <c>originals/</c> copy, the stock maps both embed, and the published combined session whose map
    /// record is what a send arriving under its own name is classified against. The folder is slug-matched so
    /// no autosave rename moves it out from under the watcher.</summary>
    private static Mod Seed(TempGame g, string? clothRecord = null, string name = "Outfit Mod")
    {
        var root = Path.Combine(g.Root, ModNaming.Slug(name));
        var meshes = Path.Combine(root, "meshes");
        Directory.CreateDirectory(meshes);
        Directory.CreateDirectory(Path.Combine(root, "originals"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        var bodyMap = WritePng(Path.Combine(root, "textures", "body_d.png"), 1);
        var clothMap = WritePng(Path.Combine(root, "textures", "cloth_d.png"), 90);

        var project = new ModProject { RootDir = root };
        project.Info.Name = name;
        string Workspace(string mesh, float yShift, string map)
        {
            var ws = Path.Combine(meshes, mesh + ".glb");
            MeshGltf.ExportRiggedGlb(Part(mesh, yShift), TwoBoneSkin(), h => Paths[h], ws, map);
            File.Copy(ws, Path.Combine(root, "originals", mesh + ".glb"));
            project.Targets.Add(new ProjectTarget
            {
                AssetType = "Mesh", Bundle = "b0", ObjectName = mesh,
                ReplaceFile = $"meshes/{mesh}.glb", OriginalFile = $"originals/{mesh}.glb",
            });
            return ws;
        }
        var body = Workspace("body1_lod0", 0f, bodyMap);
        var cloth = Workspace("cloth1_lod0", 3f, clothMap);
        if (clothRecord is not null)
            project.Targets[1].DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = clothRecord } };
        project.Save();

        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("body1_lod0", 0f), TwoBoneSkin(), bodyMap),
            new MeshGltf.RiggedPart(Part("cloth1_lod0", 3f), TwoBoneSkin(), clothMap),
        }, h => Paths[h], Path.Combine(meshes, AssetExporter.CombinedGlbName));
        return new Mod(root, meshes, body, cloth, bodyMap, clothMap);
    }

    /// <summary>The session as Blender hands it back: written under the send's own filename, carrying only the
    /// parts named, with no map record beside it — the bridge writes none, and the app's record was published
    /// beside the combined it built.</summary>
    private static string WriteSend(Mod mod, params UnityMesh[] parts)
    {
        var maps = new Dictionary<string, string>
        {
            ["body1_lod0"] = mod.BodyMap,
            ["cloth1_lod0"] = mod.ClothMap,
        };
        var withMaps = new (UnityMesh, string)[parts.Length];
        for (int i = 0; i < parts.Length; i++) withMaps[i] = (parts[i], maps[parts[i].Name]);
        return WriteSendTo(mod.Meshes, withMaps);
    }

    /// <inheritdoc cref="WriteSend"/>
    /// <remarks>A second subject's send lands in ITS folder, under the same filename — which is exactly what
    /// a queue keyed by anything but the path cannot tell apart.</remarks>
    private static string WriteSendTo(string meshesDir, params (UnityMesh Part, string Map)[] parts)
    {
        var rigged = new List<MeshGltf.RiggedPart>();
        foreach (var (part, map) in parts) rigged.Add(new MeshGltf.RiggedPart(part, TwoBoneSkin(), map));
        var send = Path.Combine(meshesDir, AssetExporter.CombinedSendGlbName);
        // A running apply snapshots this same file's bytes; a writer arriving inside that read window
        // retries, the way a real exporter's save does.
        for (int attempt = 0; ; attempt++)
        {
            try { MeshGltf.ExportCombinedRiggedGlb(rigged, h => Paths[h], send); break; }
            catch (IOException) when (attempt < 40) { System.Threading.Thread.Sleep(25); }
        }
        File.Delete(PreviewMaps.SidecarPath(send));
        return send;
    }

    /// <summary>A second subject in the same mod: its own meshes folder, workspace glb, originals copy and
    /// published combined. Runs before the mod is opened, so the VM loads a ledger that already names it.
    /// </summary>
    private static (string Meshes, string Glb) AddSecondSubject(Mod mod)
    {
        var meshes = Path.Combine(mod.Root, "meshes-hair");
        Directory.CreateDirectory(meshes);
        var ws = Path.Combine(meshes, "hair1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("hair1_lod0", 7f), TwoBoneSkin(), h => Paths[h], ws, mod.ClothMap);
        File.Copy(ws, Path.Combine(mod.Root, "originals", "hair1_lod0.glb"));
        var project = ModProject.Load(mod.Root);
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "hair1_lod0",
            ReplaceFile = "meshes-hair/hair1_lod0.glb", OriginalFile = "originals/hair1_lod0.glb",
        });
        project.Save();
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { new MeshGltf.RiggedPart(Part("hair1_lod0", 7f), TwoBoneSkin(), mod.ClothMap) },
            h => Paths[h], Path.Combine(meshes, AssetExporter.CombinedGlbName));
        return (meshes, ws);
    }

    /// <summary>The part as a glTF re-export hands it back: every triangle corner given its own vertex, and
    /// every geometry float shifted by what the transport's re-quantization can move it.</summary>
    private static UnityMesh ReSplit(UnityMesh m)
    {
        const float jitter = 4e-7f;
        var corners = new List<int>();
        foreach (var s in m.Submeshes) corners.AddRange(s);
        var channels = new Dictionary<string, float[]>();
        foreach (var (name, data) in m.Channels)
        {
            int d = m.Dims[name];
            bool shift = name is "Vertex" or "TexCoord0";
            var split = new float[corners.Count * d];
            for (int c = 0; c < corners.Count; c++)
                for (int k = 0; k < d; k++)
                    split[c * d + k] = data[corners[c] * d + k] + (shift ? jitter : 0f);
            channels[name] = split;
        }
        var submeshes = new List<int[]>();
        int next = 0;
        foreach (var s in m.Submeshes)
        {
            var indices = new int[s.Length];
            for (int i = 0; i < s.Length; i++) indices[i] = next++;
            submeshes.Add(indices);
        }
        return new UnityMesh
        {
            Name = m.Name, VertexCount = corners.Count, Channels = channels,
            Dims = new Dictionary<string, int>(m.Dims), Submeshes = submeshes,
        };
    }

    private static UnityMesh Part(string name, float yShift) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, yShift, 0, 0.5f, yShift + 1, 0, 1, yShift, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshSkin TwoBoneSkin() => new()
    {
        BoneHashes = new[] { HRoot, HArm },
        BindPoses = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0) },
    };

    private static float FirstY(string glb) => MeshGltf.ImportGlb(glb, lenient: true).Channels["Vertex"][1];

    /// <summary>A deterministic non-uniform image, so two fixtures' maps can never hash alike.</summary>
    private static string WritePng(string path, int seed)
    {
        using var img = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                img[x, y] = new Rgba32((byte)(x * 31 + seed), (byte)(y * 17 + seed), (byte)(x * y + seed), (byte)(200 + x));
        img.SaveAsPng(path);
        return path;
    }
}
