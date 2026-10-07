using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Reading a BUILT mod folder back into a project. The bar is the ROUND TRIP: a mod built from a project,
/// imported, and built again must produce the same files byte for byte — anything the reconstruction gets
/// wrong shows up as a different buffer, a different section or a different sidecar.
///
/// <para>Everything else here pins a refusal by the sentence it shows, because a refusal that says the
/// wrong thing sends the person holding the mod somewhere they cannot get to.</para>
/// </summary>
public class ModImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-mi-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;
    private readonly string _temp;

    public ModImportTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        _temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- the world ---------------------------------------------------------------------------------

    private static readonly uint[] BodyBones = { 0x00000101, 0x00000102 };
    private static readonly uint[] MateBones = { 0x00000105, 0x00000106 };

    /// <summary>Posed only by the two scene-context siblings, which are never pool candidates — so a donor
    /// riding it can only build through a coverage group, whose palette slots sit past the union.</summary>
    private const uint GroupBone = 0x0000020A;

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

    /// <param name="lyingDown">the body part ships with a rig of its own that stands it a quarter turn up,
    /// which is what makes the compile state its union in scene-rest space.</param>
    /// <param name="bareBody">the body's material binds no picture at all, so an edit on it has no map rows
    /// and ships nothing but what it does to the material's shading.</param>
    /// <param name="rampOnlyMate">the mate's material binds its toon ramp and no other picture, so a ramp
    /// picked on it is its edit's only change.</param>
    private BuildEnv MakeEnv(bool contextSiblings = false, bool ramps = false, bool lyingDown = false,
        bool bareBody = false, bool rampOnlyMate = false)
    {
        var bytes = new Dictionary<string, byte[]>();
        void Mesh(string bundleKey, string file, string mesh, int verts, int seed, uint[] bones)
        {
            string path = Path.Combine(_root, file);
            SyntheticBundle.BuildOneSkinnedMesh(path, mesh, Cloud(verts, seed), WrappedTris(verts), bones);
            bytes[bundleKey] = File.ReadAllBytes(path);
        }
        if (lyingDown)
        {
            string path = Path.Combine(_root, "s0.bundle");
            SyntheticBundle.BuildSelfRiggedMesh(path, "c_alpha01_body_lod0", Cloud(32, 5),
                WrappedTris(32), BodyBones,
                new[]
                {
                    new SyntheticBundle.RigNode("root", -1, 0f, 0f, 0f)
                        { Rotation = System.Numerics.Quaternion.CreateFromAxisAngle(
                            System.Numerics.Vector3.UnitX, -MathF.PI / 2) },
                    new SyntheticBundle.RigNode("hip", 0, 0f, 0f, 0f),
                    new SyntheticBundle.RigNode("chest", 0, 0f, 0f, 0f),
                },
                skinBones: new[] { 1, 2 });
            bytes["bundle0"] = File.ReadAllBytes(path);
        }
        else Mesh("bundle0", "s0.bundle", "c_alpha01_body_lod0", 32, 5, BodyBones);
        Mesh("bundle1", "s1.bundle", "c_alpha01_body_lod1", 24, 9, BodyBones);
        Mesh("bundleM", "sm.bundle", "c_alpha01_mate_lod0", 20, 17, MateBones);
        string bt = Path.Combine(_root, "st.bundle");
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);
        bytes["bundleT"] = File.ReadAllBytes(bt);
        if (ramps)
        {
            string br = Path.Combine(_root, "sr.bundle");
            SyntheticBundle.Build(br, new SyntheticBundle.TextureSpec("tex_ramp",
                ModBuilder.RampWidth, ModBuilder.RampHeight,
                SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 1),
                Format: SyntheticBundle.RgbaHalf));
            bytes["bundleR"] = File.ReadAllBytes(br);
        }

        var albedo = new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
        if (ramps) albedo = new List<SubjectMap>
        {
            new("_BaseMap", "tex_body_d", "bundleT"),
            new("_RampMap", "tex_ramp", "bundleR"),
        };
        SubjectMaterial Mat(string name, string cab) => new(name, 1, cab, albedo);
        var parts = new List<SubjectPart>
        {
            new("body", "c_alpha01_body_lod0", "addr_body",
                new[] { bareBody ? new SubjectMaterial("m_body", 1, "cab-body", new List<SubjectMap>())
                    : Mat("m_body", "cab-body") },
                SiblingTiers: new[] { new RecipeTierSlot("c_alpha01_body_lod1", "addr_body_l1") }),
            new("mate", "c_alpha01_mate_lod0", "addr_mate", new[]
            {
                rampOnlyMate
                    ? new SubjectMaterial("m_mate", 1, "cab-mate",
                        new List<SubjectMap> { new("_RampMap", "tex_ramp", "bundleR") })
                    : Mat("m_mate", "cab-mate"),
            }),
        };
        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1", ["addr_mate"] = "bundleM",
        };
        if (contextSiblings)
        {
            Mesh("bundleF", "sf.bundle", "c_alpha01_scarf_lod0_Fight", 18, 23, new[] { GroupBone, 0x0000020Bu });
            Mesh("bundleD", "sd.bundle", "c_alpha01_scarf_lod0_Dorm", 16, 29, new[] { GroupBone, 0x0000020Bu });
            parts.Add(new SubjectPart("scarf_Fight", "c_alpha01_scarf_lod0_Fight", "addr_scarf_f",
                new[] { Mat("m_scarf", "cab-scarf-f") }));
            parts.Add(new SubjectPart("scarf_Dorm", "c_alpha01_scarf_lod0_Dorm", "addr_scarf_d",
                new[] { Mat("m_scarf", "cab-scarf-d") }));
            addresses["addr_scarf_f"] = "bundleF";
            addresses["addr_scarf_d"] = "bundleD";
        }

        var model = new SubjectModel("Alpha", "AlphaSSR01", SubjectSource.Prefab, parts,
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Alpha" && s == "AlphaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0",
            BundleContentHash: id => id == "bundle0" ? "cafebabe" : null).Exact();
    }

    private ModProject NewProject(string name = "Import", string? author = "Someone Else")
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Info.Author = author;
        p.Selection.Add(new SelectionEntry { Character = "Alpha", Outfit = "AlphaSSR01" });
        return p;
    }

    private ProjectTarget AddReplace(ModProject p, List<SubmeshTextures>? textures = null)
    {
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_alpha01_body_lod0",
            SubjectCharacter = "Alpha", SubjectOutfit = "AlphaSSR01", ReplaceFile = "donor.glb",
            DonorTextures = textures,
            OriginalVerts = 32,
            DonorMaterials = new List<string> { "mat_shell", "mat_trim" },
        };
        p.Targets.Add(t);
        return t;
    }

    /// <summary>A quarter turn about X: the stand a prefab-shipped body's scene rig applies, and the one
    /// shape <see cref="RestBake"/> records.</summary>
    private static readonly System.Numerics.Matrix4x4 QuarterTurn =
        new(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);

    /// <param name="normal">the one normal every vertex carries; the default is the +Y the other cases
    /// use.</param>
    /// <param name="idleBone">a SECOND influence on every vertex, carrying no weight, under this index into
    /// <paramref name="bones"/>. Absent by default, which leaves every vertex on one bone.</param>
    private void WriteDonorGlb(uint[] bones, System.Numerics.Matrix4x4? uprighting = null,
        string file = "donor.glb", int seed = 11, float[]? normal = null, int idleBone = 0,
        System.Numerics.Vector3? centre = null)
    {
        const int verts = 6;
        normal ??= new float[] { 0, 1, 0 };
        var mesh = new UnityMesh
        {
            Name = "donor",
            VertexCount = verts,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = Cloud(verts, seed),
                ["Normal"] = Enumerable.Range(0, verts).SelectMany(_ => normal).ToArray(),
                ["Tangent"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 1 }).ToArray(),
                ["TexCoord0"] = Enumerable.Range(0, verts).SelectMany(v => new float[] { v / 8f, v / 8f }).ToArray(),
                ["BlendWeight"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 0 }).ToArray(),
                ["BlendIndices"] = Enumerable.Range(0, verts)
                    .SelectMany(v => new float[] { v % bones.Length, idleBone, 0, 0 }).ToArray(),
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
                ["BlendWeight"] = 4, ["BlendIndices"] = 4,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
        };
        var skin = new MeshSkin
        {
            BoneHashes = bones,
            BindPoses = bones.Select(_ => System.Numerics.Matrix4x4.Identity).ToArray(),
        };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, Path.Combine(_proj, file),
            uprighting: uprighting, shift: centre);
    }

    // ---- the round trip ----------------------------------------------------------------------------

    /// <summary>Reconstruct one inspected mod against the fixture install. The install answers with the same
    /// bundle content the mod was built on, so nothing is reported as changed unless a case says so.</summary>
    /// <param name="roster">the outfits the install's roster holds for each character; by default none
    /// beyond the ones the mod itself names.</param>
    private static ImportResult Import(ImportableMod mod, string dest, BuildEnv env,
        Func<string, string?>? installNow = null, bool includeRepairData = true,
        IReadOnlyList<Remold.Core.Model.Character>? roster = null)
    {
        var resolver = new LegacyProjectResolver(env);
        var characters = roster ?? Array.Empty<Remold.Core.Model.Character>();
        return ModImport.Materialize(mod, dest, installNow ?? env.BundleContentHash ?? (_ => null),
            resolver.ResolvePart, resolver.RosterSlots,
            character => Remold.Core.Model.RosterLookup.OutfitStems(characters, character), includeRepairData);
    }

    /// <summary>Import the built folder and build the reconstruction, returning both output folders.
    /// <paramref name="adapted"/> states what only the authored project can carry before the first
    /// build.</summary>
    private (string First, string Second) RoundTrip(ModProject project, BuildEnv env,
        Action<AuthoredProject>? adapted = null) =>
        RoundTrip(ReleasedBuild.Build(project, env, _out, zip: false, adapted: adapted).OutDir, env);

    /// <summary>The second half of the round trip, off a folder a build has already produced.</summary>
    /// <param name="effectEvidence">the material effects the install's own programs admit — what the plan
    /// needs to rebuild an edit that disables one.</param>
    /// <param name="roster">the install's roster, as <see cref="Import"/> takes it.</param>
    private (string First, string Second) RoundTrip(string first, BuildEnv env,
        Func<TargetSlot, IReadOnlyList<MaterialEffectOperation>?>? effectEvidence = null,
        IReadOnlyList<Remold.Core.Model.Character>? roster = null)
    {
        var inspection = ModImport.Inspect(first, gameFilesRead: true, _temp);
        Assert.Null(inspection.Refusal?.Message);
        using var mod = inspection.Mod!;

        string dest = Path.Combine(_root, "imported-" + Guid.NewGuid().ToString("N"));
        var resolver = new LegacyProjectResolver(env);
        var result = Import(mod, dest, env, roster: roster);

        var imported = AuthoredProjectSerializer.Load(result.ProjectFolder);
        Assert.Empty(AuthoredProjectValidator.Errors(imported));
        var plan = AuthoredBuildPlanner.Plan(imported,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart, effectEvidence: effectEvidence));
        string outTwo = Path.Combine(_root, "rebuild-" + Guid.NewGuid().ToString("N"));
        var second = ModBuilder.Build(AuthoredBuildExecution.Create(imported, plan), env, outTwo,
            log: null, zip: false);
        return (first, second.OutDir);
    }

    /// <summary>What a rebuild has to reproduce, in the two ways the emitted files admit.
    ///
    /// <para>BYTE FOR BYTE: the geometry buffers, the sections that bind them, the shaders, and the sidecar
    /// a mod manager reads. Anything the reconstruction got wrong shows up as a different buffer, a
    /// different section or a different sidecar.</para>
    ///
    /// <para>BY DECODED PIXEL: the shipped <c>.dds</c> maps. A picture comes back out of the mod as a PNG
    /// and is encoded again on the way out, and a block-compressed encode is not a function of its input
    /// alone — the encoder picks partitions and endpoints, so two encodes of the same pixels differ in
    /// bytes. So both base levels are decoded and compared channel by channel, within the tolerance stated
    /// on <see cref="BlockEncodeTolerance"/>.</para></summary>
    private static void AssertRebuildsIdentically(string first, string second)
    {
        string[] Emitted(string dir) => Directory.GetFiles(dir)
            .Select(Path.GetFileName).OfType<string>()
            .Where(Pinned).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        static bool Pinned(string name) =>
            name.EndsWith(".buf", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "gf2mod.json", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(Emitted(first), Emitted(second));
        foreach (string name in Emitted(first))
        {
            var before = File.ReadAllBytes(Path.Combine(first, name));
            var after = File.ReadAllBytes(Path.Combine(second, name));
            if (before.SequenceEqual(after)) continue;
            // A buffer says nothing readable, but the sections and the sidecar do: name the first line that
            // moved, so a failure points at what the reconstruction got wrong rather than at the file.
            string detail = name.EndsWith(".buf", StringComparison.OrdinalIgnoreCase)
                ? $"{before.Length} bytes became {after.Length}"
                : FirstDifferingLine(File.ReadAllLines(Path.Combine(first, name)),
                    File.ReadAllLines(Path.Combine(second, name)));
            Assert.Fail($"'{name}' differs after the mod was imported and built again: {detail}");
        }

        AssertMapsDecodeAlike(first, second);
    }

    /// <summary>How far one channel of a re-encoded map may sit from the original's. BC7 is lossy and its
    /// encoder is free to pick another partition for the same pixels, so a byte comparison says nothing;
    /// this is wide enough for that freedom and far too narrow for a map that came back wrong — a flipped,
    /// swizzled or mis-decoded picture moves whole channels.</summary>
    private const int BlockEncodeTolerance = 8;

    /// <summary>The shipped maps, compared as pictures rather than as files.</summary>
    private static void AssertMapsDecodeAlike(string first, string second)
    {
        string[] Maps(string dir) => Directory.GetFiles(dir, "*.dds")
            .Select(Path.GetFileName).OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(Maps(first), Maps(second));

        foreach (string name in Maps(first))
        {
            var before = DdsReader.Read(Path.Combine(first, name), DdsAccepts.AnythingTheBuildWrites);
            var after = DdsReader.Read(Path.Combine(second, name), DdsAccepts.AnythingTheBuildWrites);
            Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
            // A ramp travels byte for byte and has already matched above; only a re-encoded map reaches
            // the decode. Each side is decoded under its OWN tag: a map the modder supplied uncompressed
            // comes back through the app's picture encoder and ships block-compressed, so the container
            // can change even when the picture does not.
            if (before.Levels[0].SequenceEqual(after.Levels[0])) continue;
            var a = TextureCodec.DecodeToRgba(before.Levels[0], before.Width, before.Height,
                Decoded(before.DxgiFormat));
            var b = TextureCodec.DecodeToRgba(after.Levels[0], after.Width, after.Height,
                Decoded(after.DxgiFormat));
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
                if (Math.Abs(a[i] - b[i]) > BlockEncodeTolerance)
                    Assert.Fail($"'{name}' decodes differently after the mod was imported and built "
                        + $"again: channel {i % 4} of pixel {i / 4} was {a[i]} and is now {b[i]}");
        }

        static AssetsTools.NET.Texture.TextureFormat Decoded(uint dxgi) =>
            dxgi is DdsWriter.BC7_UNORM or DdsWriter.BC7_UNORM_SRGB
                ? AssetsTools.NET.Texture.TextureFormat.BC7
                : AssetsTools.NET.Texture.TextureFormat.RGBA32;
    }

    private static string FirstDifferingLine(string[] before, string[] after)
    {
        for (int i = 0; i < Math.Max(before.Length, after.Length); i++)
        {
            string a = i < before.Length ? before[i] : "(end)";
            string b = i < after.Length ? after[i] : "(end)";
            if (!string.Equals(a, b, StringComparison.Ordinal))
                return $"line {i + 1} was '{a}' and is now '{b}'";
        }
        return "the lines match but the bytes do not";
    }

    /// <summary>The record itself, compared with the asset list in a settled order: the ids are stable but
    /// the order they are first reached in is not part of what the record means.</summary>
    private static void AssertRepairDataMatches(string first, string second)
    {
        static string Normalized(string dir)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "repair.json")))!.AsObject();
            if (root["intent_assets"] is JsonArray assets)
            {
                var sorted = assets.Select(node => node!.ToJsonString())
                    .OrderBy(text => text, StringComparer.Ordinal).ToList();
                root["intent_assets"] = new JsonArray(sorted.Select(text => JsonNode.Parse(text)).ToArray());
            }
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        Assert.Equal(Normalized(first), Normalized(second));
    }

    [Fact]
    public void A_pooled_replace_with_maps_rebuilds_to_the_same_files()
    {
        // The whole bar in one case: the shipped streams are read back into a donor, the shipped maps into
        // authored pictures, and the rebuild lands on the same bytes.
        var env = MakeEnv();
        var p = NewProject();
        WriteDonorGlb(BodyBones.Append(MateBones[0]).ToArray());
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0.png"));
        AddReplace(p, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "s0.png" },
            new() { Submesh = 1 },
        });

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_picture_edit_a_replacement_supersedes_is_left_out_of_the_record_and_the_mod_imports_as_built()
    {
        // A replacement draws its own maps, so a picture the same edit holds on the part's original map takes
        // no effect. The record lists only what the build gave a state: without the picture, a read of the
        // mod does not look for a file the mod never shipped, and comes back as the mod was built.
        var env = MakeEnv();
        var p = NewProject("Superseded");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30, 255)))
            img.SaveAsPng(Path.Combine(_proj, "stock_edit.png"));

        void Supersede(AuthoredProject project)
        {
            var edit = project.EditDefinitions.Single(e => e.Kind == EditDefinitionKind.Content);
            var geometry = project.TargetSlots.Single(slot => edit.Bindings.Any(b => b.SlotId == slot.Id)
                && slot.Input == TargetInputKind.Geometry && slot.Domain == TargetSlotDomain.Game
                && (slot.Tier is null || slot.Tier == "lod0"));
            project.TargetSlots.Add(new TargetSlot
            {
                Id = "slot-stock-picture", Part = geometry.Part, Tier = geometry.Tier,
                SubmeshIndex = 0, MaterialSlotIndex = 0, Input = TargetInputKind.BaseColor,
                Domain = TargetSlotDomain.Game, Renderer = geometry.Renderer, Mesh = geometry.Mesh,
                Material = new LegacyProjectResolver(env).ResolvePart(edit.Target)!.Materials[0].Material,
            });
            project.ProjectAssets.Add(new ProjectAsset
            {
                Id = "asset-stock-picture", Kind = ProjectAssetKind.Picture, Label = "tex_body_d",
                File = "stock_edit.png",
            });
            edit.Bindings.Add(new Binding
            {
                SlotId = "slot-stock-picture", Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "asset-stock-picture",
            });
        }

        var (first, second) = RoundTrip(p, env, adapted: Supersede);

        var record = RepairData.Read(first);
        Assert.DoesNotContain(record.Changes.SelectMany(change => change.Intent!.Bindings),
            binding => binding.SlotId == "slot-stock-picture");
        Assert.DoesNotContain(record.IntentAssets ?? Array.Empty<RepairData.IntentAssetRecord>(),
            asset => asset.Id == "asset-stock-picture");
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_structured_value_on_a_replacement_rebuilds_to_the_same_files()
    {
        // A shading answer is a VALUE, not a file: the record carries the semantic and the value, and the
        // reconstruction has to put both back where the build reads them.
        var env = MakeEnv();
        var p = NewProject("Values");
        WriteDonorGlb(BodyBones);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(120, 120, 120, 200)))
            img.SaveAsPng(Path.Combine(_proj, "rmo.png"));
        AddReplace(p, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Rmo = "rmo.png", RmoAlpha = RmoAlphaAnswer.ShipAsAuthored },
            new() { Submesh = 1 },
        });

        var (first, second) = RoundTrip(p, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        Assert.Contains(imported.ProjectAssets, asset =>
            asset.Kind == ProjectAssetKind.StructuredValue && asset.Value?.Value == "ship-as-authored");
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_replace_over_a_coverage_group_rebuilds_to_the_same_files()
    {
        // The group bones ride palette slots past the union, and only the record says which bone each slot
        // stands for. Read back against the union alone they would name the wrong bones — silently.
        var env = MakeEnv(contextSiblings: true);
        var p = NewProject("Group");
        WriteDonorGlb(BodyBones.Append(GroupBone).ToArray());
        AddReplace(p);

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void An_influence_carrying_no_weight_rebuilds_to_the_same_files()
    {
        // An influence with no weight moves no vertex, but the index under it is still part of the skin
        // stream the mod ships. Read back as bone 0 it would come out of the rebuild as a different bone —
        // a whole channel of the shipped buffer differing over influences nothing can see.
        var env = MakeEnv();
        var p = NewProject("Idle");
        WriteDonorGlb(BodyBones, idleBone: 1);
        AddReplace(p);

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_normal_already_of_unit_length_rebuilds_to_the_same_files()
    {
        // Unit length, and NOT a fixed point of dividing by its own length: measured, this value moves by
        // one bit each time it is renormalized. A normal the export renormalizes ships different bytes on
        // the rebuild than the mod carries, and the outline baked into the vertex colour moves with it.
        var env = MakeEnv();
        var p = NewProject("Unit normal");
        WriteDonorGlb(BodyBones, normal: new[] { 0.2822924f, -0.71455365f, 0.6400969f });
        AddReplace(p);

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
    }

    /// <summary>A part with no per-vertex influences at all: the swap is direct, the mod ships no skin
    /// stream, and the record's union is absent. Nothing about the geometry is posed, so the read back has
    /// no bone order to map and must not invent one.</summary>
    private BuildEnv MakeRigidEnv()
    {
        string b0 = Path.Combine(_root, "r0.bundle");
        SyntheticBundle.BuildOneMesh(b0, "p_cube_frame_lod0", Cloud(32, 5), WrappedTris(32));
        string bt = Path.Combine(_root, "rt.bundle");
        SyntheticBundle.BuildOneTexture(bt, "tex_cube_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundleT"] = File.ReadAllBytes(bt),
        };
        var parts = new List<SubjectPart>
        {
            new("frame", "p_cube_frame_lod0", "addr_frame", new[]
            {
                new SubjectMaterial("m_frame", 1, "cab-frame",
                    new List<SubjectMap> { new("_BaseMap", "tex_cube_d", "bundleT") }),
            }),
        };
        var model = new SubjectModel("Cube", "CubeMk2", SubjectSource.Prefab, parts,
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Cube" && s == "CubeMk2" ? model : null,
            a => a == "addr_frame" ? "bundle0" : null,
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0").Exact();
    }

    [Fact]
    public void A_rigid_replace_rebuilds_to_the_same_files()
    {
        var env = MakeRigidEnv();
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = "Rigid";
        p.Info.Author = "Someone Else";
        p.Selection.Add(new SelectionEntry { Character = "Cube", Outfit = "CubeMk2" });
        WriteDonorGlb(BodyBones);
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "p_cube_frame_lod0",
            SubjectCharacter = "Cube", SubjectOutfit = "CubeMk2", ReplaceFile = "donor.glb",
            OriginalVerts = 32,
        });

        var (first, second) = RoundTrip(p, env);

        Assert.Equal("rigid", JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.GetProperty("changes")[0].GetProperty("route").GetString());
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_hide_rebuilds_to_the_same_files()
    {
        var env = MakeEnv();
        var p = NewProject("Hide");
        p.SetHidden("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", true);

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_retexture_rebuilds_to_the_same_files()
    {
        var env = MakeEnv();
        var p = NewProject("Retex");
        FlatDds.Write(Path.Combine(_proj, "skin.dds"), (1, 2, 3, 255));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundleT", ObjectName = "tex_body_d",
            ReplaceFile = "skin.dds", SubjectCharacter = "Alpha", SubjectOutfit = "AlphaSSR01",
            Users = new List<string> { "c_alpha01_body_lod0", "c_alpha01_mate_lod0" },
        });

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
    }

    /// <summary>A ramp-shaped fp16 DDS, in the container a shipped ramp goes out in.</summary>
    private string WriteRamp(string name = "picked_ramp.dds")
    {
        string path = Path.Combine(_proj, name);
        var pixels = new byte[256 * 16 * 8];
        for (int i = 0; i < pixels.Length; i += 2)
            BitConverter.TryWriteBytes(pixels.AsSpan(i, 2), (Half)(i / 2 % 31 / 30f));
        using var stream = File.Create(path);
        DdsWriter.Write(stream, DdsWriter.R16G16B16A16_FLOAT, 256, 16, new[] { pixels });
        return name;
    }

    [Fact]
    public void A_stock_ramp_pick_rebuilds_to_the_same_files()
    {
        // A pick on a part the mod does not otherwise change: nothing about its geometry or its pictures
        // moves, so the record's only statement is which material shades with which file.
        var env = MakeEnv(ramps: true);
        var p = NewProject("Ramp");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());

        var (first, second) = RoundTrip(p, env);

        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.TryGetProperty("stock_ramps", out _), "the fixture shipped no ramp pick");
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_mod_key_and_a_change_key_survive_the_import()
    {
        var env = MakeEnv();
        var p = NewProject("Keys");
        p.Info.ToggleKey = "F6";
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        p.SetChangeKey("Alpha", "AlphaSSR01", "c_alpha01_body_lod0", EditVerbs.Replace, "F7",
            hideWhenOff: true, startsOff: true);

        var (first, second) = RoundTrip(p, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_replacement_with_a_measured_rest_pose_rebuilds_to_the_same_files()
    {
        // The part stands up in the workspace, so the file the modder edited is in scene-rest space and the
        // project records the stand. The build takes it back off; the import has to put it back on and
        // record it again, or the next build takes off a stand that is no longer there.
        var env = MakeEnv();
        var p = NewProject("Stood");
        WriteDonorGlb(BodyBones, uprighting: QuarterTurn);
        AddReplace(p).BakedRest = RestBake.ToList(QuarterTurn);

        var (first, second) = RoundTrip(p, env);

        var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.GetProperty("changes")[0];
        Assert.True(record.TryGetProperty("baked_rest", out _), "the fixture recorded no rest pose");
        Assert.Equal("anchor", record.GetProperty("geometry").GetProperty("union")
            .GetProperty("space").GetString());
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_replacement_on_a_part_that_ships_lying_down_rebuilds_to_the_same_files()
    {
        // The other arm: the part's OWN rig stands it up, so the union is stated in scene-rest space and
        // the shipped floats are already stood up. The recorded rest is still what the file says about
        // itself, and the import must carry it through without standing anything up a second time.
        var env = MakeEnv(lyingDown: true);
        var p = NewProject("Lying");
        WriteDonorGlb(BodyBones, uprighting: QuarterTurn);
        AddReplace(p).BakedRest = RestBake.ToList(QuarterTurn);

        var (first, second) = RoundTrip(p, env);

        var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.GetProperty("changes")[0];
        Assert.True(record.TryGetProperty("baked_rest", out _), "the fixture recorded no rest pose");
        Assert.Equal("scene_rest", record.GetProperty("geometry").GetProperty("union")
            .GetProperty("space").GetString());
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    /// <summary>A replacement of a part the game starts hidden was authored centred at full size. The build
    /// takes the centre off first, so what ships sits where the part was modelled; the import writes that
    /// geometry as it shipped, records no centre and keeps the relation the replacement was authored under,
    /// so the edit rebuilds to the same files. The centre is one whose shift does not undo exactly on these
    /// floats (the premise checks it), which a file rewritten centred could not rebuild from byte for
    /// byte.</summary>
    [Fact]
    public void A_centred_replacement_imports_where_it_shipped_and_rebuilds_to_the_same_files()
    {
        // the middle of a part spanning -0.1 to 0.2 on every axis
        var centre = new System.Numerics.Vector3(0.05f, 0.05f, 0.05f);
        var env = MakeEnv();
        var p = NewProject("Centred");
        WriteDonorGlb(BodyBones, uprighting: QuarterTurn);
        var upright = MeshGltf.ImportPayload(Path.Combine(_proj, "donor.glb"), lenient: true).Mesh;
        Assert.NotEqual(upright.Channels["Vertex"],
            RestBake.Unshift(RestBake.Shift(upright, centre), centre).Channels["Vertex"]);
        WriteDonorGlb(BodyBones, uprighting: QuarterTurn, centre: centre);
        AddReplace(p).BakedRest = RestBake.ToList(QuarterTurn);

        var (first, second) = RoundTrip(p, env, project =>
        {
            var asset = project.ProjectAssets.Single(a => a.Kind == ProjectAssetKind.Geometry);
            asset.Shift = Remold.Core.Workbench.HiddenPart.ToList(centre);
            asset.HiddenCentred = true;
        });

        var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.GetProperty("changes")[0];
        Assert.Equal(new[] { 0.05f, 0.05f, 0.05f },
            record.GetProperty("shift").EnumerateArray().Select(e => e.GetSingle()).ToArray());
        Assert.True(record.GetProperty("hidden_centred").GetBoolean());
        AssertRebuildsIdentically(first, second);
        // the rebuilt record says the same, less the centre the imported edit no longer carries
        var rebuilt = JsonDocument.Parse(File.ReadAllText(Path.Combine(second, "repair.json")))
            .RootElement.GetProperty("changes")[0];
        Assert.False(rebuilt.TryGetProperty("shift", out _));
        Assert.True(rebuilt.GetProperty("hidden_centred").GetBoolean());

        using var mod = ModImport.Inspect(first, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "centred");
        Import(mod, dest, env);
        var imported = AuthoredProjectSerializer.Load(dest);
        var geometry = imported.ProjectAssets.Single(asset => asset.Kind == ProjectAssetKind.Geometry);
        Assert.Null(geometry.Shift);
        Assert.True(geometry.HiddenCentred);
        Assert.Null(PreviewMaps.ReadShift(imported.Resolve(geometry.File!)));
    }

    /// <summary>A mod built from a replacement authored before hidden parts opened centred states neither
    /// the centre nor the relation, and its import records neither, so it keeps building as it did.</summary>
    [Fact]
    public void A_replacement_authored_before_centring_imports_without_a_centre_or_the_relation()
    {
        var env = MakeEnv();
        var p = NewProject("Uncentred");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(built.OutDir, "repair.json")))
            .RootElement.GetProperty("changes")[0];
        Assert.False(record.TryGetProperty("shift", out _));
        Assert.False(record.TryGetProperty("hidden_centred", out _));

        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "uncentred");
        Import(mod, dest, env);
        var geometry = AuthoredProjectSerializer.Load(dest).ProjectAssets
            .Single(asset => asset.Kind == ProjectAssetKind.Geometry);
        Assert.Null(geometry.Shift);
        Assert.Null(geometry.HiddenCentred);
    }

    /// <summary>A quarter turn about Z: a stand no rig in this fixture measures.</summary>
    private static readonly System.Numerics.Matrix4x4 QuarterTurnZ =
        new(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    [Fact]
    public void A_replacement_on_its_own_part_builds_the_files_it_always_has_whatever_rest_its_file_records()
    {
        // Pins that a Replace hosted at its own part builds the same files it built before, whatever rest its
        // file records, and that the import rebuilds them. It does not pin that those files draw where the
        // Blender export showed: here the recorded rest is not the one the part's rig measures, and the new
        // mesh draws rotated by the recorded rest times the transpose of the measured one (row-vector) against
        // the export. That difference between the record and the measurement is a separate matter.
        var env = MakeEnv(lyingDown: true);
        var p = NewProject("LyingElsewhere");
        WriteDonorGlb(BodyBones, uprighting: QuarterTurnZ);
        AddReplace(p).BakedRest = RestBake.ToList(QuarterTurnZ);

        var (first, second) = RoundTrip(p, env);

        Assert.Equal("scene_rest", JsonDocument.Parse(File.ReadAllText(Path.Combine(first, "repair.json")))
            .RootElement.GetProperty("changes")[0].GetProperty("geometry").GetProperty("union")
            .GetProperty("space").GetString());
        ModBuilderTests.AssertOutputMatchesGolden(first, "own_part_recorded_rest_build_v1.json");
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void An_imported_replacement_stands_its_rig_up_with_its_mesh()
    {
        // The stand moves geometry AND joints. Put on the mesh alone it would leave the armature in bind
        // space, and the modder would open a mesh standing beside its own rig.
        var env = MakeEnv();
        var p = NewProject("Rig");
        WriteDonorGlb(BodyBones, uprighting: QuarterTurn);
        AddReplace(p).BakedRest = RestBake.ToList(QuarterTurn);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "rig");

        Import(mod, dest, env);

        var imported = AuthoredProjectSerializer.Load(dest);
        var geometry = imported.ProjectAssets.Single(asset => asset.Kind == ProjectAssetKind.Geometry);
        Assert.Equal(RestBake.ToList(QuarterTurn), geometry.BakedRest);
        // the writer records the space it wrote the file in, and it records it only when it moved the rig
        Assert.Equal(RestBake.ToList(QuarterTurn),
            PreviewMaps.ReadBakedRest(imported.Resolve(geometry.File!)));
    }

    [Fact]
    public void A_replacement_that_disables_a_material_effect_rebuilds_to_the_same_files()
    {
        // A disabled effect is carried by the record's intent alone: nothing about the shipped geometry or
        // pictures says it. The fixtures built from a schema-1 project cannot express one, so this case
        // authors the edit against the same install through the session that owns the rule.
        var env = MakeEnv();
        var p = NewProject("Effects");
        WriteDonorGlb(BodyBones);
        AddReplace(p);

        var resolver = new LegacyProjectResolver(env);
        var adapted = LegacyProjectAdapter.Adapt(p, resolver.ResolvePart).Project;
        var session = new AuthoredEditSession(adapted);
        session.SetRootDir(_proj);
        string editId = adapted.EditDefinitions
            .Single(edit => edit.Kind == EditDefinitionKind.Content).Id;
        var part = adapted.EditDefinitions.Single(edit => edit.Id == editId).Target;
        // The programs the install's material would be shaded by, stated rather than reflected: a synthetic
        // bundle carries no compiled shader for the rules to read. The outline is the one effect that is
        // turned off by not drawing its pass, so it needs no numeric evidence beside it.
        var operations = new[]
        {
            new MaterialEffectOperation("outline", new[] { "0123456789abcdef" },
                Array.Empty<MaterialEffectBufferPatch>(), Array.Empty<MaterialEffectTexture>(),
                SkipDraw: true),
        };
        session.ApplyMaterialShading(editId, part, 0, Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("outline", false) }, resolver.ResolvePart,
            effectOperations: operations);

        var project = session.Snapshot();
        var plan = AuthoredBuildPlanner.Plan(project, new ProductionAuthoredBuildBackend(
            resolver.ResolvePart, effectEvidence: _ => operations));
        var built = ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env,
            Path.Combine(_root, "effects-build"), log: null, zip: false);
        Assert.Equal(new[] { "outline" },
            RepairData.Read(built.OutDir).Changes.Single().Intent!.DisabledMaterialEffects!
                .Select(effect => effect.EffectId).ToArray());

        var (first, second) = RoundTrip(built.OutDir, env, effectEvidence: _ => operations);

        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void An_effect_disable_on_a_part_the_mod_does_not_replace_rebuilds_to_the_same_files()
    {
        // No mesh edit and no picture on the material: the disable acts at the part's own draws, so the
        // record's stock-material entry is the only statement of the edit, and of the key position it
        // answers.
        var env = MakeEnv(bareBody: true);
        var p = NewProject("StockEffects");
        var resolver = new LegacyProjectResolver(env);
        var adapted = LegacyProjectAdapter.Adapt(p, resolver.ResolvePart).Project;
        var session = new AuthoredEditSession(adapted);
        session.SetRootDir(_proj);
        var part = new TargetPart { Subject = "Alpha", Outfit = "AlphaSSR01", RendererSlot = "c_alpha01_body_lod0" };
        session.EnsurePartSlots(part, resolver.ResolvePart);
        string editId = session.CreateEdit(part);
        var operations = new[]
        {
            new MaterialEffectOperation("outline", new[] { "0123456789abcdef" },
                Array.Empty<MaterialEffectBufferPatch>(), Array.Empty<MaterialEffectTexture>(),
                SkipDraw: true),
        };
        session.ApplyMaterialShading(editId, part, 0, Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("outline", false) }, resolver.ResolvePart,
            effectOperations: operations);
        var project = session.Snapshot();
        project.Keyed(part, "F7");

        var plan = AuthoredBuildPlanner.Plan(project, new ProductionAuthoredBuildBackend(
            resolver.ResolvePart, effectEvidence: _ => operations));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts.Concat(plan.Bindings
            .Where(binding => binding.Decision.BlocksBuild).Select(binding => binding.Decision.Reason))));
        var built = ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env,
            Path.Combine(_root, "stock-effects-build"), log: null, zip: false);
        var repair = RepairData.Read(built.OutDir);
        Assert.Empty(repair.Changes);
        var record = Assert.Single(repair.StockMaterials!);
        Assert.Equal(editId, record.Intent!.EditDefinitionId);
        Assert.Equal(new[] { "outline" },
            record.Intent.DisabledMaterialEffects!.Select(effect => effect.EffectId).ToArray());
        var group = Assert.Single(record.KeyGroups!);
        Assert.Equal(editId, group.States[0].EditDefinitionId);
        Assert.Null(RepairData.FirstUnknownProperty(built.OutDir));

        var (first, second) = RoundTrip(built.OutDir, env, effectEvidence: _ => operations);

        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    private static readonly TargetPart Mate = new()
    {
        Subject = "Alpha", Outfit = "AlphaSSR01", RendererSlot = "c_alpha01_mate_lod0",
    };

    [Fact]
    public void A_keyed_ramp_pick_on_a_part_the_mod_does_not_replace_keeps_its_position()
    {
        // A pick that is its edit's only change: the ramp record is the only statement of the edit, and of the
        // key position it answers.
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("KeyedRamp");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());

        var (first, second) = RoundTrip(p, env, adapted: project => project.Keyed(Mate, "F7"));

        var record = Assert.Single(RepairData.Read(first).StockRamps!);
        var group = Assert.Single(record.KeyGroups!);
        Assert.Equal(record.Intent!.EditDefinitionId, group.States[0].EditDefinitionId);
        Assert.Empty(RepairData.Read(first).Changes);
        Assert.Null(RepairData.FirstUnknownProperty(first));
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_keyed_ramp_pick_a_0_4_record_leaves_unplaced_takes_its_position_from_the_mods_ini()
    {
        // The record states no position for the pick and nothing else on the part does; the mod binds the
        // ramp under the key's first position, and the key's own sections say how it steps.
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("KeyedRampFrom04");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());
        string built = ReleasedBuild.Build(p, env, _out, zip: false,
            adapted: project => project.Keyed(Mate, "F7")).OutDir;
        AsZeroFourRecord(built);

        var (first, second) = RoundTrip(built, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_hide_beside_a_ramp_only_edit_keeps_both_on_import()
    {
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("RampAndHide");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());

        var (first, second) = RoundTrip(p, env,
            adapted: project => project.Keyed(Mate, "F7", offState: CompositionState.Hidden));

        var hide = Assert.Single(RepairData.Read(first).Changes);
        Assert.Equal("hidden", hide.Intent!.Disposition);
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_hide_a_0_4_record_states_under_a_ramp_only_edits_name_imports_as_the_hide()
    {
        // 0.4 wrote the hide under the ramp edit's name and gave the pick no positions. The hide is the edit
        // the record's own key group names as hidden, and the group names the ramp edit's position.
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("RampAndHideFrom04");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());
        string built = ReleasedBuild.Build(p, env, _out, zip: false,
            adapted: project => project.Keyed(Mate, "F7", offState: CompositionState.Hidden)).OutDir;
        AsZeroFourRecord(built);

        var (first, second) = RoundTrip(built, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void Ramp_picks_of_two_edits_on_one_part_keep_their_own_positions()
    {
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("TwoRamps");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());

        var (first, second) = RoundTrip(p, env, adapted: project => TwoRampPositions(project, hidden: true));

        var picks = RepairData.Read(first).StockRamps!;
        Assert.Equal(2, picks.Select(pick => pick.Intent!.EditDefinitionId).Distinct().Count());
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    [Fact]
    public void A_ramp_a_0_4_record_states_under_the_parts_first_edit_returns_to_the_edit_that_picked_it()
    {
        // 0.4 recorded both picks under the first edit's name. The mod binds each ramp in its own position,
        // and the part's hide record names the edit answering each position.
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("TwoRampsFrom04");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());
        string built = ReleasedBuild.Build(p, env, _out, zip: false,
            adapted: project => TwoRampPositions(project, hidden: true)).OutDir;
        AsZeroFourRecord(built);

        var (first, second) = RoundTrip(built, env);

        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void Ramp_picks_a_0_4_record_places_nowhere_keep_the_positions_the_mod_binds_them_in()
    {
        // Nothing but the two picks is recorded on the part, and both name the first edit. Each ramp shows in
        // the position the mod binds it under.
        var env = MakeEnv(ramps: true, rampOnlyMate: true);
        var p = NewProject("TwoRampsAloneFrom04");
        p.SetStockRamp("Alpha", "AlphaSSR01", "c_alpha01_mate_lod0", "m_mate", WriteRamp());
        string built = ReleasedBuild.Build(p, env, _out, zip: false,
            adapted: project => TwoRampPositions(project, hidden: false)).OutDir;
        AsZeroFourRecord(built);

        var (first, second) = RoundTrip(built, env);

        AssertRebuildsIdentically(first, second);
    }

    /// <summary>The mate answered by two edits on one key, each picking a toon ramp of its own on the mate's
    /// material: the first in position 1, a second in position 2, and with <paramref name="hidden"/> a hide
    /// in position 3.</summary>
    private void TwoRampPositions(AuthoredProject project, bool hidden)
    {
        var picked = project.EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Content);
        var rampSlot = project.TargetSlots.Single(slot => slot.Part.SameAs(Mate)
            && slot.Domain == TargetSlotDomain.Game && slot.Input == TargetInputKind.Ramp);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "asset-second-ramp", Kind = ProjectAssetKind.Ramp, Label = "Second",
            File = WriteRamp("second_ramp.dds"),
        });
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-second",
            Kind = EditDefinitionKind.Content,
            Target = new TargetPart { Subject = Mate.Subject, Outfit = Mate.Outfit, RendererSlot = Mate.RendererSlot },
            Label = "Second",
            Bindings = picked.Bindings.Select(binding => new Binding
            {
                SlotId = binding.SlotId,
                Kind = binding.Kind,
                ProjectAssetId = binding.SlotId == rampSlot.Id ? "asset-second-ramp" : binding.ProjectAssetId,
            }).ToList(),
        });
        project.Always.Remove(picked.Id);
        var states = new List<KeyGroupState>
        {
            new() { Id = "state-0001", ActiveEditIds = new List<string> { picked.Id } },
            new() { Id = "state-0002", ActiveEditIds = new List<string> { "edit-second" } },
        };
        if (hidden) states.Add(new() { Id = "state-0003", ActiveEditIds = new List<string> { project.Hide(Mate) } });
        project.KeyGroups.Add(new KeyGroup { Id = "group-0001", Key = "F7", States = states });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    /// <summary>The record a 0.4 build wrote for the same mod: a ramp pick states no key groups and the intent
    /// of the part's first content edit, and so does a hide recorded beside edits that ship no change of
    /// their own.</summary>
    private static void AsZeroFourRecord(string modFolder) => RewriteRecord(modFolder, record =>
    {
        var picks = record["stock_ramps"]!.AsArray();
        var firstIntent = picks[0]!["intent"]!.DeepClone();
        foreach (var pick in picks)
        {
            pick!.AsObject().Remove("key_groups");
            pick["intent"] = firstIntent.DeepClone();
        }
        foreach (var change in record["changes"]!.AsArray())
            if (change!["verb"]!.GetValue<string>() == "hide") change["intent"] = firstIntent.DeepClone();
    });

    // ---- what the folder says about itself ---------------------------------------------------------

    private string Folder(string name)
    {
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteSidecar(string dir, string? appVersion, string name = "Mod",
        string? author = null) =>
        File.WriteAllText(Path.Combine(dir, "gf2mod.json"), JsonSerializer.Serialize(new
        {
            schema = 1,
            name,
            version = "1.0",
            author,
            app_version = appVersion,
        }));

    private static void WriteRepair(string dir, int schema) =>
        File.WriteAllText(Path.Combine(dir, "repair.json"),
            $"{{\"schema\":{schema},\"subjects\":[],\"changes\":[]}}");

    [Fact]
    public void A_folder_with_no_sidecar_is_not_a_mod_folder()
    {
        string dir = Folder("bare");

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.NotAModFolder, inspection.Refusal!.Cause);
        Assert.Equal("'bare' is not a mod folder.", inspection.Refusal.Message);
    }

    [Fact]
    public void Nothing_imports_until_the_game_files_have_been_read()
    {
        // An import needs the install: a hide states only that a part does not draw, and which objects
        // that is has no source but the game files. Asked before they are read, it refuses first — nothing
        // about the folder is looked at and nothing is written.
        string dir = Folder("during-scan");

        var inspection = ModImport.Inspect(dir, gameFilesRead: false, _temp);

        Assert.Equal(ImportRefusalCause.ScanRunning, inspection.Refusal!.Cause);
        Assert.Equal("Cannot import a mod while the game files are being read. "
            + "Try again when the scan finishes.", inspection.Refusal.Message);
    }

    [Fact]
    public void A_folder_holding_one_mod_folder_imports_that_mod()
    {
        // The folder a modder downloaded into names one mod as surely as the mod folder itself does.
        var env = MakeEnv();
        var p = NewProject("Nested");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string outer = Folder("download");
        CopyTree(built.OutDir, Path.Combine(outer, Path.GetFileName(built.OutDir)));

        using var mod = ModImport.Inspect(outer, gameFilesRead: true, _temp).Mod;

        Assert.NotNull(mod);
        Assert.Equal("Nested", mod!.ModName);
    }

    [Fact]
    public void A_folder_holding_two_mods_names_no_one_mod_to_import()
    {
        var env = MakeEnv();
        var p = NewProject("Pair");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string outer = Folder("two-folders");
        CopyTree(built.OutDir, Path.Combine(outer, "mod-a"));
        CopyTree(built.OutDir, Path.Combine(outer, "mod-b"));

        var inspection = ModImport.Inspect(outer, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.MoreThanOneMod, inspection.Refusal!.Cause);
        Assert.Equal("'two-folders' contains more than one mod. Import one at a time.",
            inspection.Refusal.Message);
    }

    [Fact]
    public void A_mod_built_before_repair_data_names_the_version_that_built_it()
    {
        string dir = Folder("old");
        WriteSidecar(dir, "0.3.2");

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.BuiltBeforeRepairData, inspection.Refusal!.Cause);
        Assert.Equal("'old' is not editable. It was built by version 0.3.2, and only its author can "
            + "rebuild it.", inspection.Refusal.Message);
    }

    [Fact]
    public void A_mod_new_enough_to_carry_the_record_and_missing_it_says_so_about_the_folder()
    {
        // Not a refusal by the author: nothing here can tell a cleared checkbox from a project that never
        // had it on, and phrasing it as the author's choice would send the person to ask them.
        string dir = Folder("withheld");
        WriteSidecar(dir, "0.4.1");

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.BuiltWithoutRepairData, inspection.Refusal!.Cause);
        Assert.Equal("'withheld' is not editable.", inspection.Refusal.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-version")]
    public void A_mod_that_says_nothing_about_its_version_says_only_that_it_has_no_record(string? stamp)
    {
        string dir = Folder("silent");
        WriteSidecar(dir, stamp);

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.NoRepairData, inspection.Refusal!.Cause);
        Assert.Equal("'silent' is not editable, and it does not say which version built it.",
            inspection.Refusal.Message);
    }

    [Fact]
    public void A_schema_one_record_is_refused_as_a_pre_release_build()
    {
        string dir = Folder("prerelease");
        WriteSidecar(dir, "0.4.0");
        WriteRepair(dir, 1);

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.PreReleaseBuild, inspection.Refusal!.Cause);
        Assert.Equal("'prerelease' is not editable. It was built by a pre-release version, and only its "
            + "author can rebuild it.", inspection.Refusal.Message);
    }

    [Fact]
    public void A_record_from_a_newer_app_asks_for_the_newer_app()
    {
        string dir = Folder("newer");
        WriteSidecar(dir, "9.9.9");
        WriteRepair(dir, 3);

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.NewerApp, inspection.Refusal!.Cause);
        Assert.Equal("'newer' was built by a newer version of Doll Remolding Lab. "
            + "Update the app to import it.", inspection.Refusal.Message);
    }

    [Fact]
    public void A_record_the_reader_refuses_carries_the_reason_without_a_path()
    {
        string dir = Folder("broken");
        WriteSidecar(dir, "0.4.1");
        File.WriteAllText(Path.Combine(dir, "repair.json"), "{\"schema\":2,\"subjects\":[],");

        var inspection = ModImport.Inspect(dir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.UnreadableRepairData, inspection.Refusal!.Cause);
        Assert.StartsWith("Couldn't read 'broken': ", inspection.Refusal.Message);
        Assert.DoesNotContain(dir, inspection.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_file_the_record_names_and_the_folder_does_not_hold_is_named()
    {
        var env = MakeEnv();
        var p = NewProject("Missing");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string gone = Directory.GetFiles(built.OutDir, "combined_skin_*.buf").Single();
        File.Delete(gone);

        var inspection = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.MissingFile, inspection.Refusal!.Cause);
        Assert.Contains($"is missing the file '{Path.GetFileName(gone)}'.", inspection.Refusal.Message);
    }

    // ---- zips ---------------------------------------------------------------------------------------

    private string Zip(string name, Action<string> fill)
    {
        string staging = Folder("zip-src-" + name);
        fill(staging);
        string path = Path.Combine(_root, name + ".zip");
        ZipFile.CreateFromDirectory(staging, path);
        return path;
    }

    [Fact]
    public void A_zip_holding_one_nested_mod_folder_imports_it_and_sweeps_the_extraction()
    {
        var env = MakeEnv();
        var p = NewProject("Zipped");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string zip = Zip("one", staging => CopyTree(built.OutDir,
            Path.Combine(staging, Path.GetFileName(built.OutDir))));

        var inspection = ModImport.Inspect(zip, gameFilesRead: true, _temp);
        Assert.Null(inspection.Refusal);
        string extracted = inspection.Mod!.Folder;
        Assert.True(Directory.Exists(extracted));
        inspection.Mod.Dispose();

        Assert.False(Directory.Exists(extracted), "the extraction outlived the import");
    }

    [Fact]
    public void A_zip_whose_mod_files_sit_at_its_root_imports_them()
    {
        var env = MakeEnv();
        var p = NewProject("Flat");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string zip = Zip("flat", staging => CopyTree(built.OutDir, staging));

        using var mod = ModImport.Inspect(zip, gameFilesRead: true, _temp).Mod;

        Assert.NotNull(mod);
        Assert.Equal("Flat", mod!.ModName);
    }

    [Fact]
    public void A_zip_holding_two_mods_names_no_one_mod_to_import()
    {
        var env = MakeEnv();
        var p = NewProject("Two");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string zip = Zip("two", staging =>
        {
            CopyTree(built.OutDir, Path.Combine(staging, "mod-a"));
            CopyTree(built.OutDir, Path.Combine(staging, "mod-b"));
        });

        var inspection = ModImport.Inspect(zip, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.MoreThanOneMod, inspection.Refusal!.Cause);
        Assert.Equal("'two.zip' contains more than one mod. Import one at a time.",
            inspection.Refusal.Message);
    }

    [Fact]
    public void A_zip_with_no_mod_in_it_says_so_and_leaves_nothing_behind()
    {
        string zip = Zip("empty", staging => File.WriteAllText(Path.Combine(staging, "readme.txt"), "hi"));

        var inspection = ModImport.Inspect(zip, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.NoModInZip, inspection.Refusal!.Cause);
        Assert.Equal("'empty.zip' does not contain a mod.", inspection.Refusal.Message);
        Assert.Empty(Directory.GetDirectories(_temp));
    }

    [Fact]
    public void An_unreadable_zip_names_the_file_it_could_not_open()
    {
        string path = Path.Combine(_root, "junk.zip");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        var inspection = ModImport.Inspect(path, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.ZipUnreadable, inspection.Refusal!.Cause);
        Assert.StartsWith("Couldn't open 'junk.zip': ", inspection.Refusal.Message);
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    // ---- the author question ------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_settings_author_always_asks(string? settings)
    {
        // A blank setting names nobody, so it can never stand in for a match — including against a mod
        // that lists no author either.
        Assert.True(ModImport.AuthorDiffers("Someone", settings));
        Assert.True(ModImport.AuthorDiffers(null, settings));
    }

    [Theory]
    [InlineData("Avery", "avery")]
    [InlineData("Avery", "  Avery  ")]
    [InlineData("  Avery", "AVERY")]
    public void The_same_name_in_another_case_or_with_spaces_is_the_same_author(string listed, string mine) =>
        Assert.False(ModImport.AuthorDiffers(listed, mine));

    [Theory]
    [InlineData("Avery", "Alpha")]
    [InlineData(null, "Alpha")]
    [InlineData("", "Alpha")]
    public void Another_name_or_none_at_all_asks(string? listed, string mine) =>
        Assert.True(ModImport.AuthorDiffers(listed, mine));

    // ---- what an imported project holds --------------------------------------------------------------

    [Fact]
    public void An_imported_project_records_the_mods_own_author_and_the_game_it_was_made_for()
    {
        var env = MakeEnv();
        var p = NewProject("Identity");
        p.Info.Description = "A body swap.";
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "identity");

        Import(mod, dest, env, includeRepairData: false);
        var imported = AuthoredProjectSerializer.Load(dest);

        Assert.Equal("Identity", imported.Info.Name);
        Assert.Equal("Someone Else", imported.Info.Author);
        Assert.Equal("A body swap.", imported.Info.Description);
        Assert.True(imported.Info.Imported);
        // what the imported project's own builds ship is this app's setting, not the mod's
        Assert.False(imported.Info.IncludeRepairData);
        Assert.Equal("12345", imported.AuthoredAgainst!.CatalogVersion);
        Assert.Equal(new[] { ("Alpha", "AlphaSSR01") }, imported.WorkspaceIndex!.Selection
            .Select(entry => (entry.Character, entry.Outfit)).ToArray());
    }

    [Fact]
    public void An_imported_picture_comes_back_the_way_up_the_workspace_holds_it()
    {
        // The build writes a map bottom-up, because the game samples with Unity Vs. A picture that came
        // back the same way up would be upside down everywhere the app shows it.
        var env = MakeEnv();
        var p = NewProject("Flip");
        WriteDonorGlb(BodyBones);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(10, 10, 10, 255)))
        {
            img[0, 0] = new Rgba32(240, 20, 20, 255);   // top-left only
            img.SaveAsPng(Path.Combine(_proj, "corner.png"));
        }
        AddReplace(p, new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "corner.png" } });
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "flip");

        Import(mod, dest, env);
        var imported = AuthoredProjectSerializer.Load(dest);
        var picture = imported.ProjectAssets.First(asset => asset.Kind == ProjectAssetKind.Picture);
        using var back = Image.Load<Rgba32>(imported.Resolve(picture.File));

        Assert.True(back[0, 0].R > back[0, 7].R,
            "the imported picture is upside down: the marked corner came back at the bottom");
    }

    [Fact]
    public void A_failed_import_leaves_no_folder_behind()
    {
        var env = MakeEnv();
        var p = NewProject("Broken");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        // the buffer is named by the record and present at inspection, then gone by the time it is read
        File.Delete(Directory.GetFiles(built.OutDir, "combined_bind_*.buf").Single());
        string dest = Path.Combine(_root, "half");

        Assert.ThrowsAny<Exception>(() => Import(mod, dest, env));

        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void A_part_whose_game_files_moved_is_named_by_its_own_identity()
    {
        var env = MakeEnv();
        var p = NewProject("Moved");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;

        var quiet = Import(mod, Path.Combine(_root, "quiet"), env);
        var moved = Import(mod, Path.Combine(_root, "moved"), env, installNow: _ => "deadbeef");

        Assert.Empty(quiet.ChangedTargets);
        var part = Assert.Single(moved.ChangedTargets);
        Assert.Equal(("Alpha", "AlphaSSR01", "c_alpha01_body_lod0"),
            (part.Subject, part.Outfit, part.RendererSlot));
    }

    [Fact]
    public void A_file_name_that_points_outside_the_mod_is_refused_as_unreadable()
    {
        // The record travelled with the mod and is as untrusted as the mod is: a name that walks out of
        // the folder would otherwise let an import read whatever it pointed at.
        var env = MakeEnv();
        var p = NewProject("Escape");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        RewriteFirstIndexFile(built.OutDir, @"..\outside.buf");

        var inspection = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.UnreadableRepairData, inspection.Refusal!.Cause);
        Assert.Contains(@"the file name '..\outside.buf' points outside the mod",
            inspection.Refusal.Message);
    }

    [Fact]
    public void A_recorded_rest_pose_the_app_cannot_put_back_is_refused_by_part()
    {
        // A sheared matrix is not a rotation, so the un-bake a rebuild does cannot invert it. Left to the
        // reconstruction it would ship skewed geometry the modder never authored.
        var env = MakeEnv();
        var p = NewProject("Skew");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        RewriteRecord(built.OutDir, record => record["changes"]!.AsArray()[0]!.AsObject()["baked_rest"] =
            new JsonArray(1f, 0f, 0f, 0f, 0.3f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f));

        var inspection = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.UnreadableRest, inspection.Refusal!.Cause);
        Assert.Equal($"Couldn't import '{Path.GetFileName(built.OutDir)}': the rest pose recorded for "
            + "'c_alpha01_body_lod0' can't be read.", inspection.Refusal.Message);
    }

    [Fact]
    public void An_unnamed_edit_is_numbered_from_one_for_its_part()
    {
        var env = MakeEnv();
        var p = NewProject("Named");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "named");

        Import(mod, dest, env);

        var imported = AuthoredProjectSerializer.Load(dest);
        Assert.Equal("Edit 1", imported.EditDefinitions
            .Single(edit => edit.Kind == EditDefinitionKind.Content).Label);
    }

    [Fact]
    public async Task An_imported_mod_that_lists_no_author_opens_with_the_author_blank()
    {
        // A blank author on an IMPORTED project is the mod's own answer: it listed nobody. Filling it in
        // from Settings the way a new project does would put this person's name on someone else's work.
        using var settings = new SettingsSnapshot();
        new LabSettings { Author = "Avery" }.Save();
        var env = MakeEnv();
        var p = NewProject("Anonymous", author: null);
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "anonymous");
        Import(mod, dest, env);

        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await vm.OpenModAsync(dest));

        Assert.Equal("", vm.PackageAuthor);
    }

    // ---- the ramp a replacement is shaded by --------------------------------------------------------

    [Fact]
    public void A_replacement_shaded_by_the_original_ramp_rebuilds_to_the_same_files()
    {
        // The build shaded the new mesh with the original material's ramp and shipped that ramp. The mod
        // states the carry in ONE place, the replacement's own ramp slot, so an import that dropped it
        // would rebuild a mod with no ramp line and no ramp file at all.
        var env = MakeEnv(ramps: true);
        var p = NewProject("Carried ramp");
        WriteDonorGlb(BodyBones);
        AddReplace(p, new List<SubmeshTextures> { new() { Submesh = 0 }, new() { Submesh = 1 } });
        p.Targets.Single(target => target.AssetType == "Mesh").DonorTextures!
            .Single(row => row.Submesh == 0)
            .SetRamp(WriteRamp("carried_ramp.dds"),
                new CarriedRamp { Bundle = "bundleR", Name = "tex_ramp", PathId = 4200 });
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        Assert.Equal(RepairData.CarriedFromDonor, RepairData.Read(built.OutDir).Changes
            .SelectMany(change => change.Textures ?? Array.Empty<RepairData.SubmeshRecord>())
            .Select(row => row.Ramp?.Origin).First(origin => origin is not null));

        var (first, second) = RoundTrip(built.OutDir, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var edit = imported.EditDefinitions.Single(candidate => candidate.Kind == EditDefinitionKind.Content);
        var ramps = imported.TargetSlots.Where(slot => slot.Input == TargetInputKind.Ramp
            && edit.Bindings.Any(binding => string.Equals(binding.SlotId, slot.Id,
                StringComparison.Ordinal))).ToList();
        Assert.Contains(ramps, slot => slot.Domain == TargetSlotDomain.Game
            && slot.SubmeshIndex == 0);
        // The replacement's own ramp comes back as the file the mod ships, and its history names the
        // original ramp it was taken off — which is what has the next build carry it again rather than
        // read it as a ramp the modder picked.
        var carried = ramps.Single(slot => slot.Domain == TargetSlotDomain.EditOutput
            && slot.SubmeshIndex == 0);
        var ramp = imported.ProjectAssets.Single(asset => asset.Id == edit.Bindings
            .Single(binding => string.Equals(binding.SlotId, carried.Id, StringComparison.Ordinal))
            .ProjectAssetId);
        Assert.Equal(ProjectAssetKind.Ramp, ramp.Kind);
        Assert.Equal("tex_ramp", ramp.Source!.GameAsset!.Name);
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    // ---- a value taken from another part -----------------------------------------------------------

    /// <summary>An install where a second part is shaded alongside the one a mod replaces. The ramp on
    /// that second part's material is what the replacement's ramp route is pointed at below.</summary>
    /// <param name="sharedMaterial">whether the second part is shaded by the same material as the
    /// replaced part, which is what leaves a record that names its slot and nothing else something to
    /// read that slot back off.</param>
    /// <param name="secondMaterialPathId">which object the second part's own material is, when it has one.
    /// An install whose second part is shaded by another object no longer carries the material a mod
    /// built on the first one read its value off.</param>
    /// <param name="secondOutfit">the character's other outfit the second part belongs to, when it is not
    /// the replaced part's own. The install then holds two outfits of the one character.</param>
    private BuildEnv MakeSourcePartEnv(bool sharedMaterial, long secondMaterialPathId = 7003,
        string? secondOutfit = null)
    {
        var bytes = new Dictionary<string, byte[]>();
        void Mesh(string bundleKey, string file, string mesh, int verts, int seed, uint[] bones)
        {
            string path = Path.Combine(_root, file);
            SyntheticBundle.BuildOneSkinnedMesh(path, mesh, Cloud(verts, seed), WrappedTris(verts), bones);
            bytes[bundleKey] = File.ReadAllBytes(path);
        }
        string armsSlot = secondOutfit is null ? "c_alpha01_arms_lod0" : OtherOutfitArms;
        Mesh("bundle0", "p0.bundle", "c_alpha01_body_lod0", 32, 5, BodyBones);
        Mesh("bundleA", "pa.bundle", armsSlot, 18, 23, MateBones);
        string bt = Path.Combine(_root, "pt.bundle");
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);
        bytes["bundleT"] = File.ReadAllBytes(bt);
        string br = Path.Combine(_root, "pr.bundle");
        SyntheticBundle.Build(br, new SyntheticBundle.TextureSpec("tex_ramp",
            ModBuilder.RampWidth, ModBuilder.RampHeight,
            SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 1),
            Format: SyntheticBundle.RgbaHalf));
        bytes["bundleR"] = File.ReadAllBytes(br);

        var maps = new List<SubjectMap>
        {
            new("_BaseMap", "tex_body_d", "bundleT"),
            new("_RampMap", "tex_ramp", "bundleR"),
        };
        var skin = new SubjectMaterial("m_skin", 7001, "cab-skin", maps);
        var body = new SubjectPart("body", "c_alpha01_body_lod0", "addr_body", new[] { skin });
        var arms = new SubjectPart("arms", armsSlot, "addr_arms", new[]
        {
            sharedMaterial ? skin : new SubjectMaterial("m_arms", secondMaterialPathId, "cab-arms", maps),
        });
        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundle0", ["addr_arms"] = "bundleA",
        };
        var model = new SubjectModel("Alpha", "AlphaSSR01", SubjectSource.Prefab,
            secondOutfit is null ? new List<SubjectPart> { body, arms } : new List<SubjectPart> { body },
            Skeleton: null, Problems: Array.Empty<string>());
        var other = secondOutfit is null ? null
            : new SubjectModel("Alpha", secondOutfit, SubjectSource.Prefab, new List<SubjectPart> { arms },
                Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c != "Alpha" ? null : s == "AlphaSSR01" ? model : s == secondOutfit ? other : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0",
            BundleContentHash: id => id == "bundle0" ? "cafebabe" : null).Exact();
    }

    /// <summary>Build a mod whose replacement takes its toon ramp from a part the mod never otherwise
    /// touches. The project is adapted, the other part's slots are opened, and the replacement's own ramp
    /// route is pointed at that part's — the shape the Edit page writes when a ramp is copied across.
    /// </summary>
    /// <param name="sourceOutfit">the outfit the part the ramp is taken from belongs to, when it is not the
    /// replaced part's own (see <see cref="MakeSourcePartEnv"/>).</param>
    private string BuildRampFromAnotherPart(BuildEnv env, string? sourceOutfit = null)
    {
        var p = NewProject("Ramp from another part");
        WriteDonorGlb(BodyBones);
        AddReplace(p, new List<SubmeshTextures> { new() { Submesh = 0 }, new() { Submesh = 1 } });
        var resolver = new LegacyProjectResolver(env);
        var project = LegacyProjectAdapter.Adapt(p, resolver.ResolvePart, resolver.RosterSlots).Project;
        var source = new TargetPart
        {
            Subject = "Alpha", Outfit = sourceOutfit ?? "AlphaSSR01",
            RendererSlot = sourceOutfit is null ? "c_alpha01_arms_lod0" : OtherOutfitArms,
        };
        AuthoredEditSession.EnsurePartSlots(project, source, resolver.ResolvePart(source)!);
        TargetSlot Ramp(TargetPart part) => project.TargetSlots.Single(slot => slot.Part.SameAs(part)
            && slot.Domain == TargetSlotDomain.Game && slot.Input == TargetInputKind.Ramp
            && slot.MaterialSlotIndex == 0);
        var edit = project.EditDefinitions.Single(candidate => candidate.Kind == EditDefinitionKind.Content);
        var onto = edit.Bindings.Single(binding => string.Equals(binding.SlotId, Ramp(edit.Target).Id,
            StringComparison.Ordinal));
        onto.Kind = BindingKind.SourceSlot;
        onto.SourceSlot = new BindingSourceSlot { SlotId = Ramp(source).Id };
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        return ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env, _out,
            log: null, zip: false).OutDir;
    }

    /// <summary>Take the whole slot back off every request in the record, leaving the id the request
    /// always carried. That is what a mod built before a request recorded its slot holds.</summary>
    private static void StripRequestedSlots(string modFolder) => RewriteRecord(modFolder, record =>
    {
        foreach (var intent in record["changes"]!.AsArray()
                     .Select(change => change!["intent"]).OfType<JsonNode>())
            foreach (var binding in intent["bindings"]!.AsArray().OfType<JsonNode>())
                binding["requested_source_slot"]?.AsObject().Remove("authored_slot");
    });

    [Fact]
    public void A_ramp_taken_from_another_part_rebuilds_to_the_same_files()
    {
        // The other part is nowhere else in the record — the mod changes nothing about it — so the slot
        // its value comes from is stated only by the request that names it.
        var env = MakeSourcePartEnv(sharedMaterial: true);
        string built = BuildRampFromAnotherPart(env);
        Assert.Equal("c_alpha01_arms_lod0", RequestedSlot(built).AuthoredSlot!.Part.RendererSlot);

        var (first, second) = RoundTrip(built, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        Assert.Contains(imported.TargetSlots, slot =>
            string.Equals(slot.Part.RendererSlot, "c_alpha01_arms_lod0", StringComparison.Ordinal)
            && slot.Input == TargetInputKind.Ramp);
        AssertRebuildsIdentically(first, second);
        AssertRepairDataMatches(first, second);
    }

    /// <summary>The one source-slot request the fixtures above build.</summary>
    private static RepairData.IntentSourceSlotRecord RequestedSlot(string modFolder) =>
        RepairData.Read(modFolder).Changes.SelectMany(change => change.Intent!.Bindings)
            .Select(binding => binding.RequestedSourceSlot).OfType<RepairData.IntentSourceSlotRecord>()
            .Single();

    [Fact]
    public void A_ramp_taken_from_a_part_named_by_id_alone_rebuilds_to_the_same_files()
    {
        // The request names a slot and nothing else about it. The material the value was read off is the
        // one the replaced part is shaded by, so the record describes that place elsewhere and the slot is
        // put back from it.
        var env = MakeSourcePartEnv(sharedMaterial: true);
        string built = BuildRampFromAnotherPart(env);
        StripRequestedSlots(built);
        Assert.Null(RequestedSlot(built).AuthoredSlot);

        var (first, second) = RoundTrip(built, env);

        // The slot is put back under the id the request names, filed against the part the record does
        // describe that material on — which is what the value resolves through on the next build.
        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var restored = imported.TargetSlots.Single(slot => string.Equals(slot.Id,
            RequestedSlot(built).SlotId, StringComparison.Ordinal));
        Assert.Equal("c_alpha01_body_lod0", restored.Part.RendererSlot);
        Assert.Equal(TargetInputKind.Ramp, restored.Input);
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_ramp_taken_from_a_part_the_record_does_not_describe_is_found_on_the_install()
    {
        // Nothing in the record sits on the material the value was read off: the request names its slot by
        // id alone and the other part is shaded by a material of its own. The install still carries that
        // material on the other part, so the slot is put back from there.
        var env = MakeSourcePartEnv(sharedMaterial: false);
        string built = BuildRampFromAnotherPart(env);
        StripRequestedSlots(built);
        Assert.Null(RequestedSlot(built).AuthoredSlot);

        var (first, second) = RoundTrip(built, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var restored = imported.TargetSlots.Single(slot => string.Equals(slot.Id,
            RequestedSlot(built).SlotId, StringComparison.Ordinal));
        Assert.Equal("c_alpha01_arms_lod0", restored.Part.RendererSlot);
        Assert.Equal(TargetInputKind.Ramp, restored.Input);
        Assert.Equal(TargetSlotDomain.Game, restored.Domain);
        Assert.Equal(0, restored.MaterialSlotIndex);
        Assert.Equal(0, restored.SubmeshIndex);
        Assert.Equal(7003, restored.Material!.PathId);
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_ramp_taken_from_a_material_neither_the_record_nor_the_install_carries_is_refused()
    {
        // The install the mod is read on shades the other part with another object, so no part of the
        // mod's subjects carries the material the value was read off and there is nothing to put the slot
        // back from. Nothing is left behind.
        string built = BuildRampFromAnotherPart(MakeSourcePartEnv(sharedMaterial: false));
        StripRequestedSlots(built);
        var install = MakeSourcePartEnv(sharedMaterial: false, secondMaterialPathId: 7004);
        var inspection = ModImport.Inspect(built, gameFilesRead: true, _temp);
        Assert.Null(inspection.Refusal?.Message);
        using var mod = inspection.Mod!;
        string dest = Path.Combine(_root, "refused");

        var failure = Assert.Throws<InvalidDataException>(() => Import(mod, dest, install));

        Assert.Equal("Couldn't import 'Ramp from another part': 'c_alpha01_body_lod0' takes a value from a "
            + "part its repair data does not describe.",
            ModImport.ImportFailedMessage(mod.ModName, ModImport.FailureReason(failure, built)));
        Assert.False(Directory.Exists(dest));
    }

    /// <summary>The character's other outfit in <see cref="MakeSourcePartEnv"/>, and the one part it has.
    /// </summary>
    private const string OtherOutfit = "AlphaDorm";

    /// <inheritdoc cref="OtherOutfit"/>
    private const string OtherOutfitArms = "c_alpha01dorm_arms_lod0";

    /// <summary>The install's roster for the two-outfit install: one character holding both outfits.
    /// </summary>
    private static IReadOnlyList<Remold.Core.Model.Character> TwoOutfitRoster() => new[]
    {
        new Remold.Core.Model.Character(CharId: 1, Name: "Alpha", Family: "", GunId: 1, DormModelConfigId: 199,
            Outfits: new List<Remold.Core.Model.Outfit>
            {
                new(101, "AlphaSSR01", Remold.Core.Model.OutfitKind.Alt),
                new(199, OtherOutfit, Remold.Core.Model.OutfitKind.Dorm),
            }),
    };

    [Fact]
    public void A_ramp_taken_from_another_outfit_of_the_same_character_is_found_on_the_install()
    {
        // The mod changes one outfit and reads its ramp off a part of the character's other outfit, which it
        // never changes: the record names only the first outfit and says nothing about the part but its
        // slot's id. The install's roster lists the other outfit for the character, and that outfit's part
        // carries the material, so the slot is put back from there.
        var env = MakeSourcePartEnv(sharedMaterial: false, secondOutfit: OtherOutfit);
        string built = BuildRampFromAnotherPart(env, sourceOutfit: OtherOutfit);
        StripRequestedSlots(built);
        Assert.Null(RequestedSlot(built).AuthoredSlot);
        Assert.Equal(new[] { ("Alpha", "AlphaSSR01") },
            RepairData.Read(built).Subjects.Select(subject => (subject.Character, subject.Outfit)));

        var (first, second) = RoundTrip(built, env, roster: TwoOutfitRoster());

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var restored = imported.TargetSlots.Single(slot => string.Equals(slot.Id,
            RequestedSlot(built).SlotId, StringComparison.Ordinal));
        Assert.Equal("Alpha", restored.Part.Subject);
        Assert.Equal(OtherOutfit, restored.Part.Outfit);
        Assert.Equal(OtherOutfitArms, restored.Part.RendererSlot);
        Assert.Equal(TargetInputKind.Ramp, restored.Input);
        Assert.Equal(TargetSlotDomain.Game, restored.Domain);
        Assert.Equal(0, restored.MaterialSlotIndex);
        Assert.Equal(7003, restored.Material!.PathId);
        AssertRebuildsIdentically(first, second);
    }

    [Fact]
    public void A_ramp_taken_from_a_material_no_outfit_of_the_character_carries_is_refused()
    {
        // The install shades the other outfit's part with another object, so neither outfit of the
        // character carries the material the value was read off, and the mod is refused by the requesting
        // part's name. Nothing is left behind.
        string built = BuildRampFromAnotherPart(
            MakeSourcePartEnv(sharedMaterial: false, secondOutfit: OtherOutfit), sourceOutfit: OtherOutfit);
        StripRequestedSlots(built);
        var install = MakeSourcePartEnv(sharedMaterial: false, secondMaterialPathId: 7004,
            secondOutfit: OtherOutfit);
        var inspection = ModImport.Inspect(built, gameFilesRead: true, _temp);
        Assert.Null(inspection.Refusal?.Message);
        using var mod = inspection.Mod!;
        string dest = Path.Combine(_root, "refused");

        var failure = Assert.Throws<InvalidDataException>(() =>
            Import(mod, dest, install, roster: TwoOutfitRoster()));

        Assert.Equal("Couldn't import 'Ramp from another part': 'c_alpha01_body_lod0' takes a value from a "
            + "part its repair data does not describe.",
            ModImport.ImportFailedMessage(mod.ModName, ModImport.FailureReason(failure, built)));
        Assert.False(Directory.Exists(dest));
    }

    // ---- what an asset's history comes back as -----------------------------------------------------

    /// <summary>Give every bound geometry asset a two-deep history ending at the game object the work
    /// started from, and every bound picture a two-deep history ending nowhere — the two shapes a project
    /// that has been edited more than once records. No edit binds the earlier assets, so the mod ships no
    /// file for any of them.</summary>
    private static void GiveEveryAssetADeepHistory(string modFolder) => RewriteRecord(modFolder, record =>
    {
        var assets = record["intent_assets"]!.AsArray();
        var earlier = new List<JsonNode>();
        foreach (var node in assets)
        {
            var asset = node!.AsObject();
            string id = asset["id"]!.GetValue<string>();
            string kind = asset["kind"]!.GetValue<string>();
            if (kind is not ("geometry" or "picture")) continue;
            var first = new JsonObject { ["id"] = id + "-first", ["kind"] = kind, ["label"] = "First" };
            if (asset["source"]?["game_asset"] is { } game)
                first["source"] = new JsonObject { ["game_asset"] = game.DeepClone() };
            earlier.Add(first);
            earlier.Add(new JsonObject
            {
                ["id"] = id + "-then", ["kind"] = kind, ["label"] = "Then",
                ["source"] = new JsonObject { ["project_asset_id"] = id + "-first" },
            });
            asset["source"] = new JsonObject { ["project_asset_id"] = id + "-then" };
        }
        foreach (var node in earlier) assets.Add(node);
    });

    [Fact]
    public void An_assets_history_comes_back_as_the_game_object_the_work_started_from()
    {
        // An imported project holds only the assets the mod's edits bind, so a history naming assets it
        // does not hold is not one it can state. What survives is the game object at the end of the
        // chain; a chain that ends at no game object leaves the asset with no history at all.
        var env = MakeEnv();
        var p = NewProject("History");
        WriteDonorGlb(BodyBones.Append(MateBones[0]).ToArray());
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0.png"));
        AddReplace(p, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "s0.png" },
            new() { Submesh = 1 },
        });
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        GiveEveryAssetADeepHistory(built.OutDir);

        var (first, second) = RoundTrip(built.OutDir, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var geometry = imported.ProjectAssets.Single(asset => asset.Kind == ProjectAssetKind.Geometry);
        Assert.Equal("c_alpha01_body_lod0", geometry.Source!.GameAsset!.Name);
        Assert.All(imported.ProjectAssets.Where(asset => asset.Kind == ProjectAssetKind.Picture),
            asset => Assert.Null(asset.Source));
        AssertRebuildsIdentically(first, second);
    }

    // ---- a part answered two ways -------------------------------------------------------------------

    /// <summary>A part answered two ways: two content edits, each with its own replacement, in the two
    /// positions of one key group. The second position carries a name of its own.</summary>
    private AuthoredProject TwoStateProject(BuildEnv env, string secondGlb)
    {
        var p = NewProject("Two states");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var project = LegacyProjectAdapter.Adapt(p, new LegacyProjectResolver(env).ResolvePart).Project;
        project.RootDir = _proj;

        var first = project.EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Content);
        var geometrySlot = project.TargetSlots.Single(slot => slot.Input == TargetInputKind.Geometry);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "mesh-second", Kind = ProjectAssetKind.Geometry, Label = "Second", File = secondGlb,
        });
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-second",
            Kind = EditDefinitionKind.Content,
            Target = new TargetPart
            {
                Subject = first.Target.Subject,
                Outfit = first.Target.Outfit,
                RendererSlot = first.Target.RendererSlot,
            },
            Label = "Second",
            Bindings = first.Bindings.Select(binding => new Binding
            {
                SlotId = binding.SlotId,
                Kind = binding.Kind,
                ProjectAssetId = string.Equals(binding.SlotId, geometrySlot.Id, StringComparison.Ordinal)
                    ? "mesh-second" : binding.ProjectAssetId,
            }).ToList(),
        });
        project.Always.Remove(first.Id);
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "group-0001",
            Key = "F7",
            States = new List<KeyGroupState>
            {
                new() { Id = "state-0001", ActiveEditIds = new List<string> { first.Id } },
                new()
                {
                    Id = "state-0002",
                    Label = "Second look",
                    ActiveEditIds = new List<string> { "edit-second" },
                },
            },
        });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    /// <summary>Build a schema-2 project the way the app does.</summary>
    private string BuildAuthored(AuthoredProject project, BuildEnv env, string outName)
    {
        var resolver = new LegacyProjectResolver(env);
        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        return ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env,
            Path.Combine(_root, outName), log: null, zip: false).OutDir;
    }

    /// <summary>Point every geometry binding in the record at ONE asset id. A part replaced differently in
    /// two positions records one change per position, each shipping its own buffers, and the two can name
    /// the same asset.</summary>
    private static void NameOneAssetForEveryEdit(string modFolder) => RewriteRecord(modFolder, record =>
    {
        string? kept = null;
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in record["changes"]!.AsArray())
            foreach (var binding in change!["intent"]!["bindings"]!.AsArray())
            {
                if (binding!["input"]!.GetValue<string>() != "geometry") continue;
                foreach (string field in new[]
                             { "requested_project_asset_id", "effective_project_asset_id" })
                {
                    if (binding[field]?.GetValue<string>() is not { } id) continue;
                    kept ??= id;
                    if (!string.Equals(id, kept, StringComparison.Ordinal)) replaced.Add(id);
                    binding[field] = kept;
                }
            }
        var assets = record["intent_assets"]!.AsArray();
        for (int i = assets.Count - 1; i >= 0; i--)
            if (replaced.Contains(assets[i]!["id"]!.GetValue<string>())) assets.RemoveAt(i);
    });

    [Fact]
    public void Two_edits_naming_one_asset_come_back_with_the_mesh_each_of_them_ships()
    {
        // One asset, two positions, two different meshes: each position's own buffers are in the mod, and
        // reading one mesh back for both of them answers one of the two positions with the other's work.
        var env = MakeEnv();
        WriteDonorGlb(BodyBones, file: "donor2.glb", seed: 29);
        string built = BuildAuthored(TwoStateProject(env, "donor2.glb"), env, "two-states");
        NameOneAssetForEveryEdit(built);

        var (first, second) = RoundTrip(built, env);

        var imported = AuthoredProjectSerializer.Load(
            Directory.GetDirectories(_root, "imported-*").Single());
        var meshes = imported.EditDefinitions
            .Where(edit => edit.Kind == EditDefinitionKind.Content)
            .Select(edit => ReplacementPositions(imported, edit)).ToList();
        Assert.Equal(2, meshes.Count);
        Assert.False(meshes[0].SequenceEqual(meshes[1]),
            "both positions came back with the same mesh");
        AssertRebuildsIdentically(first, second);
    }

    /// <summary>The vertex positions of the replacement one edit binds.</summary>
    private static float[] ReplacementPositions(AuthoredProject project, EditDefinition edit)
    {
        var geometry = project.ProjectAssets.First(asset =>
            asset.Kind == ProjectAssetKind.Geometry
            && edit.Bindings.Any(binding => string.Equals(binding.ProjectAssetId, asset.Id,
                StringComparison.Ordinal)));
        return MeshGltf.ImportGlb(project.Resolve(geometry.File)).Channels["Vertex"];
    }

    [Fact]
    public void A_named_key_group_position_comes_back_with_its_name()
    {
        var env = MakeEnv();
        WriteDonorGlb(BodyBones, file: "donor2.glb", seed: 29);
        string built = BuildAuthored(TwoStateProject(env, "donor2.glb"), env, "named-state");
        using var mod = ModImport.Inspect(built, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "named-state-project");

        Import(mod, dest, env);

        var imported = AuthoredProjectSerializer.Load(dest);
        var group = Assert.Single(imported.KeyGroups);
        Assert.Equal(new string?[] { null, "Second look" },
            group.States.Select(state => state.Label).ToArray());
    }

    /// <summary>A state's shortcut is built as a key that sets the group's position, recorded on that
    /// state, and comes back on the same state when the mod is imported.</summary>
    [Fact]
    public void A_state_shortcut_is_built_recorded_and_comes_back_on_its_state()
    {
        var env = MakeEnv();
        WriteDonorGlb(BodyBones, file: "donor2.glb", seed: 29);
        var project = TwoStateProject(env, "donor2.glb");
        project.KeyGroups.Single().States[1].Shortcut = "CTRL F9";
        string built = BuildAuthored(project, env, "shortcut");

        string ini = File.ReadAllText(Path.Combine(built, "mod.ini"));
        Assert.Contains("[Key_zz_key_ctrl_f9]\nkey = CTRL F9\nrun = CommandListKey_zz_key_ctrl_f9\n", ini);
        Assert.Contains("[CommandListKey_zz_key_ctrl_f9]\n$zz_key_f7 = 1\n", ini);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(built, "repair.json"))))
            foreach (var change in doc.RootElement.GetProperty("changes").EnumerateArray())
            {
                var states = Assert.Single(change.GetProperty("key_groups").EnumerateArray())
                    .GetProperty("states").EnumerateArray().ToList();
                Assert.False(states[0].TryGetProperty("shortcut", out _));
                Assert.Equal("CTRL F9", states[1].GetProperty("shortcut").GetString());
            }

        using var mod = ModImport.Inspect(built, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "shortcut-project");
        Import(mod, dest, env);

        var group = Assert.Single(AuthoredProjectSerializer.Load(dest).KeyGroups);
        Assert.Equal(new string?[] { null, "CTRL F9" }, group.States.Select(state => state.Shortcut).ToArray());
    }

    // ---- the record's own shape ---------------------------------------------------------------------

    [Fact]
    public void A_record_carrying_a_field_this_app_never_writes_is_not_editable()
    {
        // Another build wrote the record under the same schema number. The reader would ignore the field
        // it does not know and hand back a record smaller than it is, which is how a mod's key positions
        // come back collapsed into one answer.
        var env = MakeEnv();
        var p = NewProject("Shape");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Null(ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Refusal);
        RewriteRecord(built.OutDir, record =>
            record["changes"]!.AsArray()[0]!.AsObject()["key_group"] = new JsonObject());
        var inspection = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp);

        Assert.Equal(ImportRefusalCause.PreReleaseBuild, inspection.Refusal!.Cause);
        Assert.Equal($"'{Path.GetFileName(built.OutDir)}' is not editable. It was built by a pre-release "
            + "version, and only its author can rebuild it.", inspection.Refusal.Message);
    }

    // ---- the mod's own key --------------------------------------------------------------------------

    private ModProject KeepsItsKeyProject()
    {
        var p = NewProject("Persist");
        p.Info.ToggleKey = "F6";
        p.Info.PersistToggleKey = true;
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        return p;
    }

    [Fact]
    public void A_mod_key_that_keeps_its_position_comes_back_keeping_it()
    {
        var env = MakeEnv();
        var built = ReleasedBuild.Build(KeepsItsKeyProject(), env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "persist");

        Import(mod, dest, env);

        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(built.OutDir, "repair.json")))
            .RootElement.GetProperty("toggle_key_persist").GetBoolean());
        Assert.True(AuthoredProjectSerializer.Load(dest).Info.PersistToggleKey);
    }

    [Fact]
    public void A_record_that_does_not_say_whether_the_key_persists_reads_the_mods_own_sections()
    {
        // Mods built before the record carried the answer still have it: the key is declared once in the
        // mod's own sections, and one that keeps its position is declared as keeping it.
        var env = MakeEnv();
        var built = ReleasedBuild.Build(KeepsItsKeyProject(), env, _out, zip: false);
        RewriteRecord(built.OutDir, record => record.Remove("toggle_key_persist"));
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Path.Combine(_root, "persist-from-sections");

        Import(mod, dest, env);

        Assert.True(AuthoredProjectSerializer.Load(dest).Info.PersistToggleKey);
    }

    // ---- the preview picture ------------------------------------------------------------------------

    [Fact]
    public void A_mod_whose_preview_picture_is_gone_still_imports()
    {
        // The picture is how a mod manager lists the mod, not part of what the mod changes: losing it is
        // no reason to refuse everything the mod does carry.
        var env = MakeEnv();
        var p = NewProject("Preview");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(10, 200, 10, 255)))
            img.SaveAsPng(Path.Combine(_proj, "cover.png"));
        p.Info.Preview = "cover.png";
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        string shipped = Path.Combine(built.OutDir, "preview.png");
        Assert.True(File.Exists(shipped), "the fixture shipped no preview picture");
        File.Delete(shipped);

        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod;

        Assert.NotNull(mod);
        Assert.Null(mod!.PreviewFile);
        Assert.Equal("preview.png", mod.MissingPreviewFile);
        string dest = Path.Combine(_root, "no-preview");
        Import(mod, dest, env);
        Assert.Null(AuthoredProjectSerializer.Load(dest).Info.Preview);
    }

    // ---- the destination ----------------------------------------------------------------------------

    [Fact]
    public void An_import_refuses_a_destination_that_already_exists()
    {
        // A failure takes the destination with it, so the destination may only ever be this import's own.
        var env = MakeEnv();
        var p = NewProject("Occupied");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        var built = ReleasedBuild.Build(p, env, _out, zip: false);
        using var mod = ModImport.Inspect(built.OutDir, gameFilesRead: true, _temp).Mod!;
        string dest = Folder("occupied");
        File.WriteAllText(Path.Combine(dest, "keep.txt"), "mine");

        Assert.Throws<ArgumentException>(() => Import(mod, dest, env));

        Assert.True(File.Exists(Path.Combine(dest, "keep.txt")));
    }

    /// <summary>Rewrite the built record in place, for the cases that pin what a record this app's writer
    /// does not produce does: a hand-edited or damaged one, or one an older build wrote.</summary>
    private static void RewriteRecord(string modFolder, Action<JsonObject> edit)
    {
        string path = Path.Combine(modFolder, "repair.json");
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(record);
        File.WriteAllText(path, record.ToJsonString());
    }

    /// <inheritdoc cref="RewriteRecord"/>
    private static void RewriteFirstIndexFile(string modFolder, string file) =>
        RewriteRecord(modFolder, record => record["changes"]!.AsArray()[0]!
            .AsObject()["geometry"]!.AsObject()["index_file"] = file);
}
