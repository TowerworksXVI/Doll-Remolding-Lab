using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// A Replace whose new mesh weights none of the replaced part's own bones is drawn as part of a neighbouring
/// part, and it still builds every bone where the Blender export of the replaced part showed it: the bind
/// each bone is posed under is stated for the replaced part, never for the part the draw is hosted at.
/// </summary>
public class NeighbourAnchorReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-nar-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public NeighbourAnchorReferenceTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>The neighbour's bones: the part the draw is hosted at.</summary>
    private static readonly uint[] BodyBones = { 0x00000101, 0x00000102 };

    /// <summary>The replaced part's own bone. The new mesh weights none of it.</summary>
    private static readonly uint[] AccBones = { 0x00000103 };

    /// <summary>The one bone the new mesh is weighted to, which only the neighbour moves.</summary>
    private const uint Ridden = 0x00000102;

    private const string BodySlot = "c_doll01_body_lod0";
    private const string AccSlot = "c_doll01_acc_lod0";
    private const string LenderSlot = "c_doll01_lender_lod0";

    /// <summary>Where the replaced part's mesh space sits against the neighbour's.</summary>
    private static readonly Matrix4x4 AccPlacement = Matrix4x4.CreateTranslation(0f, 0.25f, 0.05f);

    private static float[] Cloud(int verts, int seed)
    {
        var pos = new float[verts * 3];
        for (int i = 0; i < pos.Length; i++)
        {
            uint h = (uint)(seed * 97) + (uint)i * 2654435761u;
            h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
            pos[i] = h % 1000 / 250f - 2f;
        }
        return pos;
    }

    private static int[] WrappedTris(int verts)
    {
        var tris = new int[verts * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = i % verts;
        return tris;
    }

    /// <summary>A two-part outfit. <paramref name="placementOf"/> states where each part's mesh space sits in
    /// its rig, or why that can't be read. <paramref name="neighbourLyingDown"/> gives the neighbour a rig of
    /// its own that stands it a quarter turn up, which states the union in scene-rest space.
    /// <paramref name="lenderBind"/> adds a third part ahead of both in roster order, moving only the ridden
    /// bone and binding it under that bind.</summary>
    private BuildEnv MakeEnv(Func<string, (Matrix4x4?, string?)> placementOf, bool neighbourLyingDown = false,
        Matrix4x4? lenderBind = null)
    {
        string bb = Path.Combine(_root, "sb.bundle");
        string ba = Path.Combine(_root, "sa.bundle");
        string bt = Path.Combine(_root, "st.bundle");
        if (neighbourLyingDown)
            SyntheticBundle.BuildSelfRiggedMesh(bb, BodySlot, Cloud(32, 5), WrappedTris(32), BodyBones,
                new[]
                {
                    new SyntheticBundle.RigNode("root", -1, 0f, 0f, 0f)
                        { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2) },
                    new SyntheticBundle.RigNode("hip", 0, 0f, 0f, 0f),
                    new SyntheticBundle.RigNode("chest", 0, 0f, 0f, 0f),
                },
                skinBones: new[] { 1, 2 });
        else SyntheticBundle.BuildOneSkinnedMesh(bb, BodySlot, Cloud(32, 5), WrappedTris(32), BodyBones);
        SyntheticBundle.BuildOneSkinnedMesh(ba, AccSlot, Cloud(20, 17), WrappedTris(20), AccBones);
        SyntheticBundle.BuildOneTexture(bt, "tex_doll_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);

        var bytes = new Dictionary<string, byte[]>
        {
            ["bundleB"] = File.ReadAllBytes(bb),
            ["bundleA"] = File.ReadAllBytes(ba),
            ["bundleT"] = File.ReadAllBytes(bt),
        };
        var albedo = new List<SubjectMap> { new("_BaseMap", "tex_doll_d", "bundleT") };
        var parts = new List<SubjectPart>
        {
            new("body", BodySlot, "addr_body", new[] { new SubjectMaterial("m_body", 1, "cab-body", albedo) }),
            new("acc", AccSlot, "addr_acc", new[] { new SubjectMaterial("m_acc", 1, "cab-acc", albedo) }),
        };
        if (lenderBind is { } lent)
        {
            string bl = Path.Combine(_root, "sl.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bl, LenderSlot, Cloud(12, 23), WrappedTris(12), new[] { Ridden },
                bindPoses: new Dictionary<uint, Matrix4x4> { [Ridden] = lent });
            bytes["bundleL"] = File.ReadAllBytes(bl);
            parts.Insert(0, new("lender", LenderSlot, "addr_lender",
                new[] { new SubjectMaterial("m_lender", 1, "cab-lender", albedo) }));
        }
        var model = new SubjectModel("Doll", "DollA01", SubjectSource.Prefab, parts,
            Skeleton: null, Problems: Array.Empty<string>());
        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundleB", ["addr_acc"] = "bundleA", ["addr_lender"] = "bundleL",
        };
        return new BuildEnv(
            (c, s) => c == "Doll" && s == "DollA01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0").Exact() with
        {
            PlacementOf = (_, part, _) => placementOf(part.SlotName),
        };
    }

    /// <summary>A quarter turn about Z: the stand the replaced part's file records, which is not the
    /// neighbour's.</summary>
    private static readonly Matrix4x4 QuarterTurnZ = new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    private ModProject NewProject(string name, Matrix4x4? recordedRest = null, string target = AccSlot,
        string bundle = "bundleA")
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = "Doll", Outfit = "DollA01" });
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = bundle, ObjectName = target,
            SubjectCharacter = "Doll", SubjectOutfit = "DollA01", ReplaceFile = "donor.glb",
            BakedRest = recordedRest is { } rest ? RestBake.ToList(rest) : null,
        });
        return p;
    }

    /// <summary>A send-back whose every vertex rides <paramref name="bone"/>, moved by minus
    /// <paramref name="centre"/> where the part it replaces opened centred.</summary>
    private void WriteDonorGlb(uint bone = Ridden, Vector3? centre = null)
    {
        const int verts = 6;
        var mesh = new UnityMesh
        {
            Name = "donor",
            VertexCount = verts,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = Cloud(verts, 11),
                ["Normal"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 1, 0 }).ToArray(),
                ["Tangent"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 1 }).ToArray(),
                ["TexCoord0"] = Enumerable.Range(0, verts).SelectMany(v => new float[] { v / 8f, v / 8f }).ToArray(),
                ["BlendWeight"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 0 }).ToArray(),
                ["BlendIndices"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 0, 0, 0 }).ToArray(),
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
                ["BlendWeight"] = 4, ["BlendIndices"] = 4,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
        };
        if (centre is { } c) mesh = RestBake.Shift(mesh, c);
        var skin = new MeshSkin { BoneHashes = new[] { bone }, BindPoses = new[] { Matrix4x4.Identity } };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, Path.Combine(_proj, "donor.glb"));
    }

    // ---- a part the game starts hidden ------------------------------------------------------------

    /// <summary>The replaced or lending part shrunk out of sight by its placement, a hand's height up.</summary>
    private static readonly Matrix4x4 Shrunk = Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateTranslation(0.5f, 1.25f, 0);

    /// <summary>Where the Blender export shows the hidden accessory: its stock geometry's centre at the
    /// origin.</summary>
    private static Vector3 AccCentre() => HiddenPart.Centre(new UnityMesh
    {
        Name = AccSlot, VertexCount = 20,
        Channels = new Dictionary<string, float[]> { ["Vertex"] = Cloud(20, 17) },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
        Submeshes = new List<int[]>(),
    }, null);

    /// <summary>The geometry asset marked as authored with hidden parts shown centred, and the centre its
    /// file was moved by where it has one.</summary>
    private static Action<AuthoredProject> Returned(Vector3? centre) => project =>
    {
        var asset = project.ProjectAssets.Single(a => a.Kind == ProjectAssetKind.Geometry);
        asset.HiddenCentred = true;
        asset.Shift = centre is { } c ? HiddenPart.ToList(c) : null;
    };

    /// <summary>Every file two builds wrote, compared byte for byte.</summary>
    private static void AssertSameOutput(string expected, string actual)
    {
        var names = Directory.GetFiles(expected).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(names, Directory.GetFiles(actual).Select(Path.GetFileName).OrderBy(n => n).ToList());
        foreach (var name in names)
            Assert.True(File.ReadAllBytes(Path.Combine(expected, name!))
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(actual, name!))), $"{name} differs");
    }

    /// <summary>The replaced part is one the game starts hidden, and the new mesh rides a bone only the body
    /// moves. Returned from a session in which the part opened centred, the bone is posed where Blender showed
    /// it beside the centred part: the body's bind carried into the part's space by its display placement.
    /// Returned before that, it is posed as it always was, which is exactly the build of the same part
    /// sitting unscaled with the body.</summary>
    [Fact]
    public void A_hidden_part_replaced_with_weight_on_a_body_bone_poses_it_where_Blender_showed_it()
    {
        var centre = AccCentre();
        WriteDonorGlb(centre: centre);
        var stamped = ReleasedBuild.Build(NewProject("HiddenTarget"),
            MakeEnv(slot => slot == AccSlot ? (Shrunk, null) : (Matrix4x4.Identity, null)),
            Path.Combine(_out, "stamped"), zip: false, adapted: Returned(centre));
        var (union, space) = RecordedUnion(stamped.OutDir);
        Assert.Equal("anchor", space);
        Assert.True(BindReference.SameBind(Matrix4x4.CreateTranslation(-centre), union[Ridden]),
            $"union bind {union[Ridden]} is not the display placement's {Matrix4x4.CreateTranslation(-centre)}");
        Assert.DoesNotContain(stamped.Warnings, warning => warning.StartsWith("The original mesh of"));

        WriteDonorGlb();
        var released = ReleasedBuild.Build(NewProject("HiddenTarget"),
            MakeEnv(slot => slot == AccSlot ? (Shrunk, null) : (Matrix4x4.Identity, null)),
            Path.Combine(_out, "released"), zip: false);
        Assert.Equal(Matrix4x4.Identity, RecordedUnion(released.OutDir).Binds[Ridden]);
        var together = ReleasedBuild.Build(NewProject("HiddenTarget"), MakeEnv(_ => (Matrix4x4.Identity, null)),
            Path.Combine(_out, "together"), zip: false);
        AssertSameOutput(together.OutDir, released.OutDir);
    }

    /// <summary>The replaced hidden part's file records a centre other than its original mesh's today (the
    /// game's mesh changed after the edit was sent back). The body bone is posed where Blender showed it,
    /// beside the part centred by the recorded centre, not the stock one; and the build says the original
    /// mesh changed, naming the part.</summary>
    [Fact]
    public void A_hidden_part_replaced_under_a_recorded_centre_is_posed_by_it_and_warns_when_the_original_changed()
    {
        var recorded = AccCentre() + new Vector3(0.25f, 0, -0.125f);
        WriteDonorGlb(centre: recorded);
        var built = ReleasedBuild.Build(NewProject("HiddenTarget"),
            MakeEnv(slot => slot == AccSlot ? (Shrunk, null) : (Matrix4x4.Identity, null)),
            Path.Combine(_out, "recorded"), zip: false, adapted: Returned(recorded));

        var union = RecordedUnion(built.OutDir).Binds;
        Assert.True(BindReference.SameBind(Matrix4x4.CreateTranslation(-recorded), union[Ridden]),
            $"union bind {union[Ridden]} is not the recorded display placement's {Matrix4x4.CreateTranslation(-recorded)}");
        var warning = Assert.Single(built.Warnings, w => w.StartsWith("The original mesh of"));
        Assert.Matches("^The original mesh of '[^']+' has changed since this edit was sent back from Blender, "
            + "so the new mesh may not appear where Blender showed it\\.$", warning);
    }

    /// <summary>The body is replaced, and the new mesh rides a bone only a part the game starts hidden
    /// moves. Returned from a session in which that part opened centred, the bone is posed where Blender
    /// showed it: the hidden part's bind carried into the body's space by the hidden part's display
    /// placement. Returned before, it keeps the bind it is stated under.</summary>
    [Fact]
    public void A_replacement_weighting_a_hidden_parts_bone_poses_it_where_Blender_showed_that_part()
    {
        var centre = AccCentre();
        uint hiddenBone = AccBones[0];
        WriteDonorGlb(bone: hiddenBone);
        var stamped = ReleasedBuild.Build(NewProject("HiddenSource", target: BodySlot, bundle: "bundleB"),
            MakeEnv(slot => slot == AccSlot ? (Shrunk, null) : (Matrix4x4.Identity, null)),
            Path.Combine(_out, "stamped"), zip: false, adapted: Returned(null));
        Assert.True(BindReference.SameBind(Matrix4x4.CreateTranslation(centre),
            RecordedUnion(stamped.OutDir).Binds[hiddenBone]));

        var released = ReleasedBuild.Build(NewProject("HiddenSource", target: BodySlot, bundle: "bundleB"),
            MakeEnv(slot => slot == AccSlot ? (Shrunk, null) : (Matrix4x4.Identity, null)),
            Path.Combine(_out, "released"), zip: false);
        Assert.Equal(Matrix4x4.Identity, RecordedUnion(released.OutDir).Binds[hiddenBone]);
        var together = ReleasedBuild.Build(NewProject("HiddenSource", target: BodySlot, bundle: "bundleB"),
            MakeEnv(_ => (Matrix4x4.Identity, null)), Path.Combine(_out, "together"), zip: false);
        AssertSameOutput(together.OutDir, released.OutDir);
    }

    /// <summary>The bind the Blender export of the replaced part states for each bone: its own for the bone
    /// it moves, the neighbour's carried by the two placements for the others.</summary>
    private static IReadOnlyDictionary<uint, Matrix4x4> ExportStatement()
    {
        var acc = new BindReference.Part(AccSlot, AccBones, new[] { Matrix4x4.Identity }, AccBones.ToHashSet(),
            new Lazy<(Matrix4x4?, string?)>(() => (AccPlacement, null)));
        var body = new BindReference.Part(BodySlot, BodyBones, new[] { Matrix4x4.Identity, Matrix4x4.Identity },
            BodyBones.ToHashSet(), new Lazy<(Matrix4x4?, string?)>(() => (Matrix4x4.Identity, null)));
        return BindReference.For(acc, new[] { body, acc }, carryHidden: true).Reference;
    }

    /// <summary>The union the repair record states, bone → bind (row-vector).</summary>
    private static (Dictionary<uint, Matrix4x4> Binds, string Space) RecordedUnion(string outDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "repair.json")));
        var union = doc.RootElement.GetProperty("changes")[0].GetProperty("geometry").GetProperty("union");
        var bones = union.GetProperty("bones").EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToList();
        var raw = Convert.FromBase64String(union.GetProperty("bind_poses").GetString()!);
        var binds = new Dictionary<uint, Matrix4x4>();
        for (int b = 0; b < bones.Count; b++)
        {
            var floats = new float[16];
            Buffer.BlockCopy(raw, b * 16 * sizeof(float), floats, 0, 16 * sizeof(float));
            binds[bones[b]] = BindSpace.FromUnityFloats(floats);
        }
        return (binds, union.GetProperty("space").GetString()!);
    }

    [Fact]
    public void A_replacement_drawn_at_a_neighbour_poses_its_bones_as_the_replaced_parts_export_states_them()
    {
        // The new mesh weights only a bone the neighbour moves, so the draw is hosted at the neighbour. The
        // replaced part sits elsewhere in the rig, so the bind its Blender export states for that bone is the
        // neighbour's carried by the two placements, and the build poses the bone under that statement. The
        // neighbour's own rows for the bone are converted onto it, in a copy of its operator.
        var env = MakeEnv(slot => slot == AccSlot ? (AccPlacement, null) : (Matrix4x4.Identity, null));
        var p = NewProject("NeighbourAnchor");
        WriteDonorGlb();

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        // the premise: the neighbour anchors, and the modder is told about its material
        Assert.Contains($"pool (doll_acc): {BodySlot} (anchor {BodySlot})", r.Diagnostics);
        Assert.Contains(r.Warnings, w => w.Contains("original material", StringComparison.Ordinal));

        var stated = ExportStatement();

        // an anchor-space union: the donor carries no rotation against the replaced part, so the union
        // states the export's bind as it is
        var (union, space) = RecordedUnion(r.OutDir);
        Assert.Equal("anchor", space);
        Assert.True(BindReference.SameBind(stated[Ridden], union[Ridden]),
            $"union bind {union[Ridden]} is not the export's {stated[Ridden]}");
        // and it is not the neighbour's own bind, which is what a reference stated for the neighbour gives
        Assert.False(BindReference.SameBind(Matrix4x4.Identity, union[Ridden]));

        // the neighbour's rows are converted onto that statement, in a copy of its operator
        Assert.Contains(r.Diagnostics, d => d.StartsWith("doll_acc: doll_body binds ", StringComparison.Ordinal)
            && d.Contains("elsewhere than the replacement is posed from", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "doll_body_cpinv_doll_acc.buf")));
    }

    [Fact]
    public void A_replacement_drawn_at_a_neighbour_that_ships_lying_down_poses_its_bones_where_its_export_stood_them()
    {
        // The neighbour's own rig stands it up, so the union is stated in scene-rest space, while the file sent
        // back from Blender was stood up by another turn. The donor keeps that turn against the replaced part,
        // so the union states the export's bind carried by the same turn: every vertex then skins exactly as
        // the export showed it.
        var env = MakeEnv(slot => slot == AccSlot ? (AccPlacement, null) : (Matrix4x4.Identity, null),
            neighbourLyingDown: true);
        var p = NewProject("NeighbourAnchorLying", recordedRest: QuarterTurnZ);
        WriteDonorGlb();

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains($"pool (doll_acc): {BodySlot} (anchor {BodySlot})", r.Diagnostics);
        var (union, space) = RecordedUnion(r.OutDir);
        Assert.Equal("scene_rest", space);
        var expected = Matrix4x4.Transpose(QuarterTurnZ) * ExportStatement()[Ridden];
        Assert.True(BindReference.SameBind(expected, union[Ridden]),
            $"union bind {union[Ridden]} is not the export's {expected}");
        Assert.Contains(r.Diagnostics, d => d.StartsWith("doll_acc: doll_body binds ", StringComparison.Ordinal)
            && d.Contains("elsewhere than the replacement is posed from", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "doll_body_cpinv_doll_acc.buf")));
    }

    [Fact]
    public void A_replacement_drawn_at_a_neighbour_poses_its_bone_under_the_first_lenders_bind_not_the_neighbours()
    {
        // Every part sits together. Two parts move the ridden bone: one ahead in roster order binds it 3 cm up,
        // and the neighbour, which hosts the draw, binds it as the skeleton does. The replaced part's Blender
        // export states the bone under the first part's bind, and the build poses it there too, converting
        // the neighbour's rows onto it, rather than taking the neighbour's own bind.
        var lenderBind = Matrix4x4.CreateTranslation(0f, 0.03f, 0f);
        var env = MakeEnv(_ => (Matrix4x4.Identity, null), lenderBind: lenderBind);
        var p = NewProject("NeighbourAnchorLender");
        WriteDonorGlb();

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains(r.Diagnostics, d => d.StartsWith("pool (doll_acc): ", StringComparison.Ordinal)
            && d.EndsWith($"(anchor {BodySlot})", StringComparison.Ordinal));
        var (union, _) = RecordedUnion(r.OutDir);
        Assert.True(BindReference.SameBind(lenderBind, union[Ridden]),
            $"union bind {union[Ridden]} is not the first lender's {lenderBind}");
        Assert.True(File.Exists(Path.Combine(r.OutDir, "doll_body_cpinv_doll_acc.buf")));
    }

    [Fact]
    public void A_replacement_drawn_at_a_neighbour_refuses_naming_the_replaced_part_when_it_cannot_be_placed()
    {
        // The neighbour's bones are stated in the replaced part's space, so an unreadable placement for the
        // replaced part leaves every bone it doesn't use with no bind. The refusal names the replaced part as
        // the one the game files don't place, and blames no neighbour.
        var env = MakeEnv(slot => slot == AccSlot ? (null, "its skeleton can't be read") : (Matrix4x4.Identity, null));
        var p = NewProject("NeighbourAnchorUnplaced");
        WriteDonorGlb();

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal("'acc' can't be built: the new mesh is weighted to bones only other parts use. The "
            + "game files don't say where 'acc' sits in its skeleton, so those bones can't be placed. In Blender, "
            + "weight the new mesh to bones 'acc' uses, or remove those weights", ex.Message);
        Assert.Contains(BuildLogDiagnostics.From(ex), d => d.Contains("its skeleton can't be read"));
    }

    [Fact]
    public void A_replacement_drawn_at_a_neighbour_refuses_naming_the_neighbour_when_it_cannot_be_placed()
    {
        // The replaced part places, the neighbour lending the ridden bone does not: the refusal names the
        // neighbour and where it can't be placed relative to.
        var env = MakeEnv(slot => slot == BodySlot ? (null, "its skeleton can't be read") : (AccPlacement, null));
        var p = NewProject("NeighbourUnplaced");
        WriteDonorGlb();

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal("'acc' can't be built: the new mesh is weighted to bones only 'body' uses. The game "
            + "files don't say where 'body' is relative to 'acc'. Remove those weights in Blender", ex.Message);
        Assert.Contains(BuildLogDiagnostics.From(ex), d => d.Contains("'c_doll01_body_lod0': its skeleton can't be read"));
    }
}
