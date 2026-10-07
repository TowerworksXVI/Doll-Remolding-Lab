using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>A part the game starts shrunk opens in Blender centred at full size, its own joints moved with
/// it, while every other part stays where it is.</summary>
public class HiddenPartExportTests
{
    private const string PropLogical = "p1111111111111111111111111111111.bundle";
    private const string PropPhys = "55555555555555555555555555555555";
    private const string BodyLogical = "b1111111111111111111111111111111.bundle";
    private const string BodyPhys = "66666666666666666666666666666666";
    private const string PropSlot = "prop1_lod0";
    private const string BodySlot = "body1_lod0";
    private const uint PropBone = 0x5555_5555;
    private static readonly uint[] BodyBones = { 11u, 22u };
    // the prop sits a hand's reach from the origin, as it was modelled: its box centre is (11, 22, 30)
    private static readonly float[] PropTri = { 10, 20, 30, 12, 20, 30, 12, 24, 30 };
    private static readonly float[] BodyTri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] Idx = { 0, 1, 2 };
    private static readonly Vector3 Centre = new(11, 22, 30);

    private sealed record Run(string PropGlb, string BodyGlb, string Combined);

    /// <summary>One export of the two-part subject, lone files for both parts and the combined session,
    /// with the prop placed by <paramref name="propPlacement"/>.</summary>
    private static Run Export(TempGame g, string folder, Matrix4x4 propPlacement, string? propPrepared = null,
        string? bodyPrepared = null)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        string propFile = Path.Combine(abw, PropPhys + ".bundle");
        if (!File.Exists(propFile))
        {
            SyntheticBundle.BuildOneSkinnedMesh(propFile, PropSlot, PropTri, Idx, new[] { PropBone },
                bundleName: PropLogical);
            SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, BodyPhys + ".bundle"), BodySlot, BodyTri, Idx,
                BodyBones, bundleName: BodyLogical);
        }
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (PropLogical, PropPhys), (BodyLogical, BodyPhys));
        var meshes = g.At(Path.Combine(folder, "meshes"));
        Directory.CreateDirectory(meshes);
        var run = new Run(Path.Combine(meshes, PropSlot + ".glb"), Path.Combine(meshes, BodySlot + ".glb"),
            Path.Combine(meshes, "_combined.glb"));
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, Remold.Core.Bundles.MeshSelector, string?)>
        {
            ("prop1", PropLogical, PropSlot, propPrepared is null ? run.PropGlb : null, null, 0L, propPrepared),
            ("body1", BodyLogical, BodySlot, bodyPrepared is null ? run.BodyGlb : null, null, 0L, bodyPrepared),
        };
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(PropSlot, "prop1", PropLogical, 0, true, VisibilityOverride.None),
            new AssetExporter.RosterPart(BodySlot, "body1", BodyLogical, 0, true, VisibilityOverride.None),
        });
        AssetExporter.BuildRiggedGlbsCore(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            spec, g.At(Path.Combine(folder, "textures")), null, run.Combined, null, roster, null,
            new CandidacyCache(null), default,
            placementOf: (slot, _) => new RigPlacement.Placed(
                slot == PropSlot ? propPlacement : Matrix4x4.Identity, null, null));
        return run;
    }

    private static readonly Matrix4x4 Shrunk = Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateTranslation(0.5f, 1.25f, 0);

    private static Matrix4x4 JointWorld(string glb, uint hash) => SharpGLTF.Schema2.ModelRoot.Load(glb)
        .LogicalNodes.Single(n => n.Name == $"bone_{hash:x8}").WorldMatrix;

    private static Vector3 BoxCentre(float[] pos)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 3 <= pos.Length; i += 3)
        {
            var p = new Vector3(pos[i], pos[i + 1], pos[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min + max) * 0.5f;
    }

    /// <summary>The lone file of a hidden part opens with the middle of its geometry at the origin and its
    /// own joint moved by the same centre; its record says by how much, and says which of the subject's bones
    /// only hidden parts weight.</summary>
    [Fact]
    public void A_hidden_parts_lone_file_opens_centred_with_its_joints_moved_by_the_centre()
    {
        using var g = new TempGame();
        var shown = Export(g, "base", Matrix4x4.Identity);
        var hidden = Export(g, "hidden", Shrunk);

        var stock = MeshGltf.ImportGlb(shown.PropGlb).Channels["Vertex"];
        var centred = MeshGltf.ImportGlb(hidden.PropGlb).Channels["Vertex"];
        Assert.Equal(Vector3.Zero, BoxCentre(centred));
        Assert.Equal(stock.Select((v, i) => v - Centre[i % 3]).ToArray(), centred);

        var moved = JointWorld(shown.PropGlb, PropBone)
            * AxisConvention.Reflect(Matrix4x4.CreateTranslation(-Centre));
        Assert.True(BindReference.SameBind(moved, JointWorld(hidden.PropGlb, PropBone)));

        Assert.Equal(new[] { 11f, 22f, 30f }, PreviewMaps.ReadShift(hidden.PropGlb));
        Assert.Null(PreviewMaps.ReadShift(shown.PropGlb));
        Assert.Null(PreviewMaps.ReadShift(hidden.BodyGlb));
        var facts = PreviewMaps.ReadHiddenFacts(hidden.BodyGlb);
        Assert.Equal(new[] { PropBone.ToString("x8") }, facts!.Bones);
        Assert.False(facts.StockMixes);
        Assert.Null(PreviewMaps.ReadHiddenFacts(shown.BodyGlb));
    }

    /// <summary>An open-all session composes its file from every part's prepared file, each taken as an
    /// edit is. There the part the game starts shrunk sits centred with its joint moved by the centre, and the
    /// body's geometry and joints stay where the game's own composition has them.</summary>
    [Fact]
    public void An_open_all_composition_of_prepared_files_has_the_shrunk_part_centred_and_the_body_unmoved()
    {
        using var g = new TempGame();
        var shown = Export(g, "base", Matrix4x4.Identity);
        var hidden = Export(g, "hidden", Shrunk);
        var propPrepared = g.At(Path.Combine("hidden", "prop.session.glb"));
        var bodyPrepared = g.At(Path.Combine("hidden", "body.session.glb"));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb, null,
            PropSlot, propPrepared, null));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.BodyGlb, null,
            BodySlot, bodyPrepared, null));

        var composed = Export(g, "composed", Shrunk, propPrepared, bodyPrepared);

        Assert.False(File.Exists(composed.PropGlb));
        Assert.Equal(Vector3.Zero, BoxCentre(MeshGltf.ImportGlb(composed.Combined, PropSlot).Channels["Vertex"]));
        var moved = JointWorld(shown.Combined, PropBone)
            * AxisConvention.Reflect(Matrix4x4.CreateTranslation(-Centre));
        Assert.True(BindReference.SameBind(moved, JointWorld(composed.Combined, PropBone)),
            $"the prop's joint stands at {JointWorld(composed.Combined, PropBone)}, not {moved}");
        Assert.Equal(MeshGltf.ImportGlb(shown.Combined, BodySlot).Channels["Vertex"],
            MeshGltf.ImportGlb(composed.Combined, BodySlot).Channels["Vertex"]);
        foreach (var bone in BodyBones)
            Assert.True(BindReference.SameBind(JointWorld(shown.Combined, bone), JointWorld(composed.Combined, bone)),
                $"body bone {bone:x8} moved");
    }

    /// <summary>The edit's own record of the centre its file was moved by reaches the prepare step through
    /// the open's plan for the part: an edit recording its centre opens as it is, while the same file
    /// recording none is centred again on its way in.</summary>
    [Fact]
    public void An_edit_recording_its_centre_is_not_centred_again_on_open()
    {
        using var g = new TempGame();
        var hidden = Export(g, "hidden", Shrunk);
        var centred = MeshGltf.ImportGlb(hidden.PropGlb).Channels["Vertex"];
        static IReadOnlyList<Remold.Core.Project.EditSlotState> Slots(IReadOnlyList<float>? shift) => new[]
        {
            new Remold.Core.Project.EditSlotState(
                new Remold.Core.Project.TargetSlot
                {
                    Id = "slot-geometry", Input = Remold.Core.Project.TargetInputKind.Geometry, Tier = "lod0",
                },
                new Remold.Core.Project.Binding
                {
                    SlotId = "slot-geometry", Kind = Remold.Core.Project.BindingKind.ProjectAsset,
                    ProjectAssetId = "geometry",
                },
                new Remold.Core.Project.ProjectAsset
                {
                    Id = "geometry", Kind = Remold.Core.Project.ProjectAssetKind.Geometry, Label = "Prop",
                    File = "prop.glb", Shift = shift?.ToList(),
                }),
        };

        string recorded = g.At(Path.Combine("hidden", "recorded.glb"));
        var plan = Remold.App.ViewModels.MainWindowViewModel.SessionPartPlanFor("prop1", PropSlot, hidden.PropGlb,
            recorded, false, hidden.PropGlb, Slots(new[] { 11f, 22f, 30f }), g.Root, () => null);
        Assert.Equal(new[] { 11f, 22f, 30f }, plan.Shift);
        Assert.Empty(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionParts(new[] { plan }));
        Assert.Equal(centred, MeshGltf.ImportGlb(recorded).Channels["Vertex"]);

        string unrecorded = g.At(Path.Combine("hidden", "unrecorded.glb"));
        var older = Remold.App.ViewModels.MainWindowViewModel.SessionPartPlanFor("prop1", PropSlot, hidden.PropGlb,
            unrecorded, false, hidden.PropGlb, Slots(null), g.Root, () => null);
        Assert.Empty(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionParts(new[] { older }));
        Assert.Equal(centred.Select((v, i) => v - Centre[i % 3]).ToArray(),
            MeshGltf.ImportGlb(unrecorded).Channels["Vertex"]);
    }

    /// <summary>A part no session can send back is posed where the prefab's rest pose puts it, which for a
    /// part the game starts hidden is shrunk out of sight; such a part takes no pose and opens centred at
    /// full size like every other hidden part.</summary>
    [Fact]
    public void A_gated_hidden_part_takes_no_context_pose()
    {
        var rig = new Remold.Core.Skeleton.SceneRig
        {
            BonePaths = new[] { "prop" },
            BoneRestWorlds = new[] { Shrunk },
        };

        var (gatedPose, _) = AssetExporter.CombinedPose(rig, null, () => false);
        var (hiddenPose, _) = AssetExporter.CombinedPose(rig, null, () => false, hidden: true);

        Assert.NotNull(gatedPose);
        Assert.Null(hiddenPose);
    }

    /// <summary>An edit of a hidden part sent back before hidden parts opened centred records no centre. It
    /// reopens centred by the centre this run's build states, joints and all, and the prepared file records
    /// that centre so its next send-back marks the edit with it.</summary>
    [Fact]
    public void An_edit_without_a_centre_is_centred_on_its_way_into_the_session()
    {
        using var g = new TempGame();
        var shown = Export(g, "base", Matrix4x4.Identity);
        var hidden = Export(g, "hidden", Shrunk);
        var prepared = g.At(Path.Combine("hidden", "prepared.glb"));

        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb,
            shown.PropGlb, PropSlot, prepared, null, editedBakedRest: null, editedShift: null));

        Assert.Equal(MeshGltf.ImportGlb(hidden.PropGlb).Channels["Vertex"],
            MeshGltf.ImportGlb(prepared).Channels["Vertex"]);
        Assert.True(BindReference.SameBind(JointWorld(hidden.PropGlb, PropBone), JointWorld(prepared, PropBone)));
        Assert.Equal(new[] { 11f, 22f, 30f }, PreviewMaps.ReadShift(prepared));
    }

    /// <summary>An edit whose asset records its centre is already centred and is not moved again; a bare
    /// hidden part's prepared file records the centre its build moved it by.</summary>
    [Fact]
    public void An_edit_that_records_its_centre_and_a_bare_part_are_not_moved_again()
    {
        using var g = new TempGame();
        var hidden = Export(g, "hidden", Shrunk);
        var centred = MeshGltf.ImportGlb(hidden.PropGlb).Channels["Vertex"];

        var edited = g.At(Path.Combine("hidden", "edited.glb"));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb,
            hidden.PropGlb, PropSlot, edited, null, editedShift: new[] { 11f, 22f, 30f }));
        Assert.Equal(centred, MeshGltf.ImportGlb(edited).Channels["Vertex"]);
        Assert.Equal(new[] { 11f, 22f, 30f }, PreviewMaps.ReadShift(edited));

        var bare = g.At(Path.Combine("hidden", "bare.glb"));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb, null,
            PropSlot, bare, null));
        Assert.Equal(centred, MeshGltf.ImportGlb(bare).Channels["Vertex"]);
        Assert.Equal(new[] { 11f, 22f, 30f }, PreviewMaps.ReadShift(bare));

        // a part that is not hidden records no centre, bare or edited
        var body = g.At(Path.Combine("hidden", "body.glb"));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.BodyGlb,
            hidden.BodyGlb, BodySlot, body, null));
        Assert.Null(PreviewMaps.ReadShift(body));
        Assert.Equal(MeshGltf.ImportGlb(hidden.BodyGlb).Channels["Vertex"],
            MeshGltf.ImportGlb(body).Channels["Vertex"]);
    }

    /// <summary>An edit of a hidden part sent back before hidden parts opened centred, with one vertex riding
    /// a bone of the body. Opened now, its geometry and its own joint move by minus the centre, while the body's
    /// bone stays where this session stands it, beside the body, not dragged along with the part.</summary>
    [Fact]
    public void A_legacy_edit_riding_a_body_bone_is_centred_and_leaves_the_body_bone_where_the_session_stands_it()
    {
        using var g = new TempGame();
        var hidden = Export(g, "hidden", Shrunk);
        var legacy = g.At(Path.Combine("hidden", "legacy.glb"));
        var mesh = new UnityMesh
        {
            Name = PropSlot,
            VertexCount = 3,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = PropTri,
                ["BlendWeight"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
                // the first vertex rides the body's bone, the other two the prop's own
                ["BlendIndices"] = new float[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["BlendWeight"] = 4, ["BlendIndices"] = 4 },
            Submeshes = new List<int[]> { Idx },
        };
        var skin = new MeshSkin
        {
            BoneHashes = new[] { PropBone, BodyBones[0] },
            BindPoses = new[] { Matrix4x4.CreateTranslation(-1, -2, -3), Matrix4x4.Identity },
        };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, legacy);
        var prepared = g.At(Path.Combine("hidden", "legacy-prepared.glb"));

        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb, legacy,
            PropSlot, prepared, null));

        Assert.Equal(PropTri.Select((v, i) => v - Centre[i % 3]).ToArray(),
            MeshGltf.ImportGlb(prepared).Channels["Vertex"]);
        var ownMoved = JointWorld(legacy, PropBone) * AxisConvention.Reflect(Matrix4x4.CreateTranslation(-Centre));
        Assert.True(BindReference.SameBind(ownMoved, JointWorld(prepared, PropBone)),
            $"the prop's own joint stands at {JointWorld(prepared, PropBone)}, not {ownMoved}");
        Assert.True(BindReference.SameBind(JointWorld(hidden.PropGlb, BodyBones[0]),
                JointWorld(prepared, BodyBones[0])),
            $"the body's bone stands at {JointWorld(prepared, BodyBones[0])}, not where the session stands it, "
            + $"{JointWorld(hidden.PropGlb, BodyBones[0])}");
    }

    /// <summary>Every file the prepare step hands a Blender session says that hidden parts open centred in
    /// that session, bare or edited, hidden part or not, and a copy of it keeps saying so; the export's own
    /// file says nothing, since no session opens it directly.</summary>
    [Fact]
    public void Every_prepared_file_says_hidden_parts_open_centred_in_its_session()
    {
        using var g = new TempGame();
        var hidden = Export(g, "hidden", Shrunk);
        var prepared = new[]
        {
            (hidden.PropGlb, (string?)null, PropSlot, g.At(Path.Combine("hidden", "prop-bare.glb"))),
            (hidden.PropGlb, hidden.PropGlb, PropSlot, g.At(Path.Combine("hidden", "prop-edited.glb"))),
            (hidden.BodyGlb, (string?)null, BodySlot, g.At(Path.Combine("hidden", "body-bare.glb"))),
            (hidden.BodyGlb, hidden.BodyGlb, BodySlot, g.At(Path.Combine("hidden", "body-edited.glb"))),
        };
        foreach (var (rigged, edited, slot, output) in prepared)
        {
            Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(rigged, edited, slot,
                output, null));
            Assert.True(PreviewMaps.ReadHiddenCentred(output), $"{output} does not say so");
        }
        Assert.False(PreviewMaps.ReadHiddenCentred(hidden.PropGlb));
        Assert.False(PreviewMaps.ReadHiddenCentred(hidden.BodyGlb));

        var copy = g.At(Path.Combine("copy", "workspace.glb"));
        PreviewMaps.CopyPortableWorkspace(prepared[3].Item4, copy);
        Assert.True(PreviewMaps.ReadHiddenCentred(copy));
    }

    /// <summary>An edit returned by 0.4.1 records only its rest. The rest record says nothing about a
    /// centre, so the edit is centred like any other uncentred edit of a hidden part, and keeps its rest
    /// record.</summary>
    [Fact]
    public void An_edit_recording_only_its_rest_is_centred_too()
    {
        using var g = new TempGame();
        var shown = Export(g, "base", Matrix4x4.Identity);
        var hidden = Export(g, "hidden", Shrunk);
        var rest = RestBake.ToList(new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1));
        var prepared = g.At(Path.Combine("hidden", "released.glb"));

        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb,
            shown.PropGlb, PropSlot, prepared, null, editedBakedRest: rest));

        Assert.Equal(MeshGltf.ImportGlb(hidden.PropGlb).Channels["Vertex"],
            MeshGltf.ImportGlb(prepared).Channels["Vertex"]);
        Assert.Equal(rest, PreviewMaps.ReadBakedRest(prepared));
        Assert.Equal(new[] { 11f, 22f, 30f }, PreviewMaps.ReadShift(prepared));
    }

    /// <summary>The Blender session tells the bridge which part opened centred as one the game starts
    /// hidden, and hands every part the subject's hidden bones, so a send weighting both kinds can say they
    /// do not line up in the game. A part that is not hidden is declared as nothing new.</summary>
    [Fact]
    public void The_session_declares_the_hidden_part_and_the_subjects_hidden_bones()
    {
        using var g = new TempGame();
        var hidden = Export(g, "hidden", Shrunk);
        var propPrepared = g.At(Path.Combine("hidden", "prop.prepared.glb"));
        var bodyPrepared = g.At(Path.Combine("hidden", "body.prepared.glb"));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.PropGlb, null,
            PropSlot, propPrepared, null));
        Assert.True(Remold.App.ViewModels.MainWindowViewModel.PrepareSessionPartGlb(hidden.BodyGlb, null,
            BodySlot, bodyPrepared, null));

        var parts = new[]
        {
            (PropSlot, propPrepared, hidden.PropGlb, "prop1"),
            (BodySlot, bodyPrepared, hidden.BodyGlb, "body1"),
        }.Select(p =>
        {
            var (isHidden, facts) = Remold.App.ViewModels.MainWindowViewModel.SessionHiddenFacts(p.Item2, p.Item3);
            return Remold.App.ViewModels.MainWindowViewModel.SessionPartForBlender(p.Item1, false, true, false,
                null, null, "Edit 1", null, label: p.Item4, hidden: isHidden, hiddenFacts: facts);
        }).ToList();
        var session = g.At(Path.Combine("hidden", "composition.glb"));
        Remold.Core.Blender.BlenderBridge.WriteSession(session, null, parts);

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Remold.Core.Blender.BlenderBridge.SessionPath(session)));
        var entries = doc.RootElement.GetProperty("parts").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.True(entries[PropSlot].GetProperty("hidden").GetBoolean());
        Assert.False(entries[BodySlot].TryGetProperty("hidden", out _));
        foreach (var entry in entries.Values)
        {
            Assert.Equal(new[] { PropBone.ToString("x8") },
                entry.GetProperty("hiddenBones").EnumerateArray().Select(b => b.GetString()).ToArray());
            Assert.False(entry.TryGetProperty("stockMixes", out _));
        }
    }

    /// <summary>A part that is not hidden stays exactly where it was: its geometry and its own joints are
    /// unmoved in its lone file and in the combined session, while the hidden part beside it opens centred
    /// there too.</summary>
    [Fact]
    public void Other_parts_stay_where_they_are_beside_a_centred_hidden_part()
    {
        using var g = new TempGame();
        var shown = Export(g, "base", Matrix4x4.Identity);
        var hidden = Export(g, "hidden", Shrunk);

        Assert.Equal(MeshGltf.ImportGlb(shown.BodyGlb).Channels["Vertex"],
            MeshGltf.ImportGlb(hidden.BodyGlb).Channels["Vertex"]);
        foreach (var bone in BodyBones)
        {
            Assert.Equal(JointWorld(shown.BodyGlb, bone), JointWorld(hidden.BodyGlb, bone));
            Assert.Equal(JointWorld(shown.Combined, bone), JointWorld(hidden.Combined, bone));
        }
        Assert.Equal(MeshGltf.ImportGlb(shown.Combined, BodySlot).Channels["Vertex"],
            MeshGltf.ImportGlb(hidden.Combined, BodySlot).Channels["Vertex"]);

        Assert.Equal(Vector3.Zero, BoxCentre(MeshGltf.ImportGlb(hidden.Combined, PropSlot).Channels["Vertex"]));
        var moved = JointWorld(shown.Combined, PropBone)
            * AxisConvention.Reflect(Matrix4x4.CreateTranslation(-Centre));
        Assert.True(BindReference.SameBind(moved, JointWorld(hidden.Combined, PropBone)));
        // the body's own file offers the prop's bone standing where the centred prop shows it
        Assert.True(BindReference.SameBind(JointWorld(hidden.PropGlb, PropBone),
            JointWorld(hidden.BodyGlb, PropBone)));
    }
}
