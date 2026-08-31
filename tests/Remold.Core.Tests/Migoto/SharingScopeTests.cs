using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The sharing measurement deciding each edit's scope: a shared stock texture goes draw-scoped instead of
/// hash-global, a shared mesh anchor gets the outfit's presence latch, and every scoping decision is
/// disclosed in <see cref="ModBuilder.Result.Infos"/>. Driven end to end over the same synthetic world as
/// <see cref="ModBuilderTests"/>, with the index constructed from pre-measured maps.
/// </summary>
public class SharingScopeTests : IDisposable
{
    /// <summary>The build's emitted ini, checked for duplicate section names on the way out: 3DMigoto drops
    /// a duplicate-named section silently, so no assertion below may be read against one.</summary>
    private static string ReadIni(ModBuilder.Result r)
    {
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        return ini;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-ss-" + Guid.NewGuid().ToString("N"));
    private readonly ModBuilderTests _world = new();

    public void Dispose()
    {
        _world.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // The synthetic subject is (Vesna, VesnaSSR01); a second wearer represents the sharing.
    private static readonly SharingIndex.Wearer Self = new("Vesna", "Vesna", "VesnaSSR01", null);
    private static readonly SharingIndex.Wearer Other = new("Karst", "Karst", "KarstDorm", null);

    private static SharingIndex Index(string texHash, int[] texWearers,
        Dictionary<string, int[]>? mesh = null, string[]? witnesses = null, string[]? failed = null) =>
        SharingIndex.FromMeasurements("12345", new[] { Self, Other },
            new Dictionary<string, int[]> { [texHash] = texWearers },
            mesh ?? new Dictionary<string, int[]>(),
            witnesses is null ? new Dictionary<int, string[]>() : new Dictionary<int, string[]> { [0] = witnesses },
            failed);

    // ---- retexture tiering ------------------------------------------------------------------------

    [Fact]
    public void Private_stock_texture_keeps_the_global_rebind()
    {
        var env = _world.MakeEnv(out _, out _);
        env = env with { Sharing = Index(_world.StockTexHash, new[] { 0 }) };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        Assert.Contains("[TextureOverride_Retex_", ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        Assert.DoesNotContain("[TextureOverride_RetexTag_", ini);
        Assert.Empty(r.Infos);
    }

    [Fact]
    public void Shared_stock_texture_builds_a_draw_scoped_retexture()
    {
        var env = _world.MakeEnv(out string lod0, out string lod1);
        env = env with { Sharing = Index(_world.StockTexHash, new[] { 0, 1 }) };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        // no global rebind of the shared hash; the tag + one scoped section per shipped tier instead
        Assert.DoesNotContain("[TextureOverride_Retex_", ini);
        Assert.Contains($"[TextureOverride_RetexTag_{_world.StockTexHash}]\nhash = {_world.StockTexHash}\n"
            + $"filter_index = {MigotoEmitter.RetexTag(_world.StockTexHash)}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_RetexScope_vesna_body_lod0]\nhash = {lod0}\nmatch_priority = 0\n", ini);
        Assert.Contains($"[TextureOverride_RetexScope_vesna_body_lod1]\nhash = {lod1}\nmatch_priority = 0\n", ini);
        // the probe/bind/restore shape: save by ref, find the tagged slot, bind only there, restore post
        Assert.Contains("Resource_RtxSave0 = ref ps-t0\n", ini);
        Assert.Contains($"if $zz_rt == {MigotoEmitter.RetexTag(_world.StockTexHash)}\n$zz_rslot = 0\nendif\n", ini);
        Assert.Contains("if $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n", ini);
        Assert.Contains("post ps-t0 = Resource_RtxSave0\n", ini);
        // no latch: the anchors are private. And no disclosure — the scoping matches what the author
        // asked for, so there is nothing to say
        Assert.DoesNotContain("$zz_gate_", ini);
        Assert.Empty(r.Infos);
        // the sidecar's conflict surface covers the anchors and the stock hash
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { _world.StockTexHash, lod0, lod1 }.Order().ToArray(), hashes);
    }

    // ---- the presence latch -----------------------------------------------------------------------

    /// <summary>Three wearers of the subject's anchors: another playable outfit, and an enemy door
    /// carrying the subject's mesh set exactly. <paramref name="doorIsEnemy"/> is what puts the door
    /// inside the duplicate-door rule's reach.</summary>
    private SharingIndex WithATwinDoor(string lod0, string lod1, bool doorIsEnemy) =>
        SharingIndex.FromMeasurements("12345",
            new[]
            {
                Self,
                Other,
                new SharingIndex.Wearer("ElidDoor", null, "ElidDoor", null),
            },
            new Dictionary<string, int[]> { [_world.StockTexHash] = new[] { 0, 1 } },
            new Dictionary<string, int[]> { [lod0] = new[] { 0, 1, 2 }, [lod1] = new[] { 0, 2 } },
            new Dictionary<int, string[]> { [0] = new[] { lod1 } },
            enemyCharacters: doorIsEnemy ? new[] { "ElidDoor" } : null);

    [Fact]
    public void A_subject_whose_last_private_mesh_a_real_wearer_takes_says_so()
    {
        // The door counts as content here, so every mesh the subject has is somebody else's too and there
        // is nothing left to signal its presence with.
        var env = _world.MakeEnv(out string lod0, out string lod1);
        env = env with { Sharing = WithATwinDoor(lod0, lod1, doorIsEnemy: false) };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        Assert.Contains(r.Infos, i => i == "VesnaSSR01 has no mesh of its own, so the mod cannot tell "
            + "when it is on screen. Edits on its shared meshes apply wherever those meshes draw.");
        Assert.DoesNotContain("$zz_gate_", ReadIni(r));
    }

    [Fact]
    public void A_witness_taken_only_by_a_duplicate_door_comes_back()
    {
        // The same world with the door read as what it is: the mesh is the subject's alone again, so the
        // latch is built and the disclosure has nothing to disclose.
        var env = _world.MakeEnv(out string lod0, out string lod1);
        env = env with { Sharing = WithATwinDoor(lod0, lod1, doorIsEnemy: true) };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        Assert.DoesNotContain(r.Infos, i => i.Contains("no private mesh to witness"));
        Assert.Contains("$zz_gate_", ReadIni(r));
    }

    [Fact]
    public void Shared_anchor_gets_the_presence_latch()
    {
        var env = _world.MakeEnv(out string lod0, out string lod1);
        env = env with
        {
            Sharing = Index(_world.StockTexHash, new[] { 0, 1 },
                mesh: new Dictionary<string, int[]> { [lod0] = new[] { 0, 1 }, [lod1] = new[] { 0, 1 } },
                witnesses: new[] { "aabbccdd", "eeff0011" }),
        };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        // the latch: declared, committed in [Present], witnessed by the outfit's private meshes
        Assert.Contains("global $zz_gate_vesnassr01 = 0\nglobal $zz_seen_vesnassr01 = 0\n", ini);
        Assert.Contains("[Present]\n$zz_gate_vesnassr01 = $zz_seen_vesnassr01\n$zz_seen_vesnassr01 = 0\n", ini);
        Assert.Contains("[TextureOverride_Witness_vesnassr01_0]\nhash = aabbccdd\nmatch_priority = 0\n$zz_seen_vesnassr01 = 1\n", ini);
        Assert.Contains("[TextureOverride_Witness_vesnassr01_1]\nhash = eeff0011\nmatch_priority = 0\n$zz_seen_vesnassr01 = 1\n", ini);
        // the bind sits under the latch; the save/restore does not (a gated-off draw must not restore stale refs)
        Assert.Contains("if $zz_gate_vesnassr01 == 1\nif $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n", ini);
        int save = ini.IndexOf("Resource_RtxSave0 = ref ps-t0", StringComparison.Ordinal);
        int gate = ini.IndexOf("if $zz_gate_vesnassr01 == 1", StringComparison.Ordinal);
        Assert.True(save >= 0 && gate > save, "saves are unconditional and precede the gated binds");
        // the anchor is shared with ANOTHER character, whose copy visibly co-changes — disclosed
        Assert.Contains(r.Infos, i => i.Contains("Karst") && i.Contains("shows this edit too"));
    }

    /// <summary>Two of one doll's outfits sharing the face texture, each owning its own texture target and
    /// workspace file. <paramref name="images"/> gives each outfit's authored colour, so the pair is the
    /// same image or two deliberately different ones; <paramref name="texWearers"/> is the sharing
    /// measurement's wearer set for the stock texture (null = no index at all, so both outfits read
    /// unmeasured and take the game-wide rebind). <paramref name="keys"/> are the per-change toggle keys.
    /// <paramref name="sharedMesh"/> puts both outfits on ONE face mesh; false gives each its own, the
    /// shape a per-outfit disambiguation needs. Every anchor is measured as shared, so each outfit gets a
    /// presence latch either way.</summary>
    private ModBuilder.Result BuildTwoOutfitFaces((byte, byte, byte, byte)[] images,
        int[]? texWearers = null, string?[]? keys = null, bool sharedMesh = true)
    {
        string proj = Path.Combine(_root, "proj"), outRoot = Path.Combine(_root, "build");
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(outRoot);

        string b0 = Path.Combine(_root, "b0.bundle"), b1 = Path.Combine(_root, "b1.bundle"),
            bt = Path.Combine(_root, "bt.bundle");
        SyntheticBundle.BuildOneMesh(b0, "c_vesna01_face_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 });
        SyntheticBundle.BuildOneMesh(b1, "c_vesna02_face_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 }, new[] { 0, 1, 2, 0, 2, 3 });
        SyntheticBundle.BuildOneTexture(bt, "tex_face_d", 8, 8, 200, 100, 50, 255);
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundle1"] = File.ReadAllBytes(b1),
            ["bundleT"] = File.ReadAllBytes(bt),
        };
        FaceIb = BufferHash.Compute(bytes["bundle0"], "c_vesna01_face_lod0").Ib.ToString("x8");
        DormIb = sharedMesh ? FaceIb
            : BufferHash.Compute(bytes["bundle1"], "c_vesna02_face_lod0").Ib.ToString("x8");
        var stock = new Remold.Core.Bundles.BundleReader().GetTextureHashSource(bytes["bundleT"], "tex_face_d")!.Value;
        FaceTexHash = TextureHash.Compute(stock.PictureData, stock.Width, stock.Height, stock.MipCount,
            TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)stock.Format, stock.Srgb)!.Value).ToString("x8");

        // the outfit's own face mesh, and the address that resolves to the bundle holding it
        (string Mesh, string Address) Face(string stem) =>
            !sharedMesh && stem == "VesnaDorm" ? ("c_vesna02_face_lod0", "addr_face2")
                : ("c_vesna01_face_lod0", "addr_face");
        SubjectModel Model(string stem) => new("Vesna", stem, SubjectSource.Prefab, new[]
        {
            new Remold.Core.Workbench.SubjectPart("face", Face(stem).Mesh, Face(stem).Address, new[]
            {
                new SubjectMaterial("m_face", 1, "cab-face",
                    new[] { new SubjectMap("_BaseMap", "tex_face_d", "bundleT") }),
            }),
        }, Skeleton: null, Problems: Array.Empty<string>());

        // a third wearer on both face meshes, so each anchor is measured as shared and earns its latch
        var wearers = new[]
        {
            new SharingIndex.Wearer("Vesna", "Vesna", "VesnaSSR01", null),
            new SharingIndex.Wearer("Vesna", "Vesna", "VesnaDorm", null),
            new SharingIndex.Wearer("Karst", "Karst", "KarstDorm", null),
        };
        var meshWearers = new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            [FaceIb] = sharedMesh ? new[] { 0, 1 } : new[] { 0, 2 },
        };
        if (!sharedMesh) meshWearers[DormIb] = new[] { 1, 2 };
        var env = new BuildEnv(
            (c, s) => c == "Vesna" ? Model(s) : null,
            a => a == "addr_face" ? "bundle0" : a == "addr_face2" ? "bundle1" : null,
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0",
            texWearers is null ? null : SharingIndex.FromMeasurements("12345", wearers,
                new Dictionary<string, int[]> { [FaceTexHash] = texWearers },
                meshWearers,
                new Dictionary<int, string[]> { [0] = new[] { "aaaa0001" }, [1] = new[] { "aaaa0002" } })).Exact();

        var p = new Remold.Core.Project.ModProject { RootDir = proj };
        p.Info.Name = "Face";
        p.Selection.Add(new Remold.Core.Project.SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        p.Selection.Add(new Remold.Core.Project.SelectionEntry { Character = "Vesna", Outfit = "VesnaDorm" });
        // the same game texture materialized under BOTH outfits — one workspace file each
        var outfits = new[] { ("VesnaSSR01", "face.dds"), ("VesnaDorm", "face_dorm.dds") };
        for (int i = 0; i < outfits.Length; i++)
        {
            var (outfit, file) = outfits[i];
            FlatDds.Write(Path.Combine(proj, file), images[i]);
            p.Targets.Add(new Remold.Core.Project.ProjectTarget
            {
                AssetType = "Texture2D", Bundle = "bundleT", ObjectName = "tex_face_d",
                ReplaceFile = file, Users = new List<string> { Face(outfit).Mesh },
                SubjectCharacter = "Vesna", SubjectOutfit = outfit,
            });
            if (keys?[i] is { } k)
                p.SetChangeKey("Vesna", outfit, Face(outfit).Mesh, Remold.Core.Project.EditVerbs.Retexture, k);
        }

        return ReleasedBuild.Build(p, env, outRoot);
    }

    private string FaceIb = "", DormIb = "", FaceTexHash = "";

    [Fact]
    public void Same_character_outfits_sharing_an_anchor_each_get_their_own_gate()
    {
        // Two of one doll's outfits share the face mesh AND the face texture; each outfit owns its OWN
        // texture target and workspace file, so the build derives one retexture per outfit. Each claiming
        // outfit must contribute its OWN gated bind on the shared anchor — the first claimant's gate alone
        // would scope the edit to whichever outfit happened to derive first.
        var r = BuildTwoOutfitFaces(new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (1, 2, 3, 255) },
            texWearers: new[] { 0, 1 });

        string ini = ReadIni(r);
        // one section for the shared anchor, carrying BOTH outfits' gated binds
        Assert.Equal(1, CountOf(ini, "[TextureOverride_RetexScope_"));
        Assert.Contains("if $zz_gate_vesnassr01 == 1\n", ini);
        Assert.Contains("if $zz_gate_vesnadorm == 1\n", ini);
        // the same image under both outfits collapses to ONE shipped file and one bind target
        Assert.Contains("[Resource_Rtx0]", ini);
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        // and to one probe: the slot is read once, ahead of every bind that rides the answer
        Assert.Equal(1, CountOf(ini, "$zz_rslot = -1"));
        // both latches declared, committed, and witnessed independently
        Assert.Contains("[TextureOverride_Witness_vesnassr01_0]\nhash = aaaa0001\nmatch_priority = 0\n", ini);
        Assert.Contains("[TextureOverride_Witness_vesnadorm_0]\nhash = aaaa0002\nmatch_priority = 0\n", ini);
        // same character throughout — nothing to disclose
        Assert.Empty(r.Infos);
    }

    [Fact]
    public void Two_outfits_disambiguating_one_shared_texture_each_bind_their_own_image()
    {
        // The per-outfit disambiguator: one doll's two outfits share a stock texture, want DIFFERENT images
        // on it, and draw faces of their OWN. The distinct anchors are what makes the pair expressible —
        // each outfit's mesh gets a section binding that outfit's image behind that outfit's gate.
        var r = BuildTwoOutfitFaces(new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (9, 8, 7, 255) },
            texWearers: new[] { 0, 1 }, sharedMesh: false);

        string ini = ReadIni(r);
        // one section per anchor mesh, each probing the one stock tag for itself
        Assert.Equal(2, CountOf(ini, "[TextureOverride_RetexScope_"));
        Assert.Equal(1, CountOf(ini, $"hash = {FaceIb}"));
        Assert.Equal(1, CountOf(ini, $"hash = {DormIb}"));
        Assert.Equal(2, CountOf(ini, "$zz_rslot = -1"));
        // two images shipped, each bound once, under the gate of the outfit that asked for it
        Assert.Contains("[Resource_Rtx1]", ini);
        Assert.DoesNotContain("[Resource_Rtx2]", ini);
        Assert.Contains("if $zz_gate_vesnassr01 == 1\nif $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n", ini);
        Assert.Contains("if $zz_gate_vesnadorm == 1\nif $zz_rslot == 0\nps-t0 = Resource_Rtx1\nendif\n", ini);
        Assert.Equal(1, CountOf(ini, "if $zz_gate_vesnassr01 == 1\n"));
        Assert.Equal(1, CountOf(ini, "if $zz_gate_vesnadorm == 1\n"));
        // the probe precedes the bind; the save/restore still brackets the lot unconditionally
        int probe = ini.IndexOf("$zz_rslot = -1", StringComparison.Ordinal);
        int first = ini.IndexOf("if $zz_gate_vesnassr01 == 1\nif $zz_rslot == 0", StringComparison.Ordinal);
        Assert.True(probe > 0 && first > probe, "the slot is probed before any image binds");
        Assert.Contains("Resource_RtxSave0 = ref ps-t0\n", ini);
        Assert.Contains("post ps-t0 = Resource_RtxSave0\n", ini);
        // each face mesh is drawn by another character too, so both edits disclose the co-change
        Assert.Equal(2, r.Infos.Count);
        Assert.All(r.Infos, i => Assert.Contains("Karst", i));
    }

    [Fact]
    public void Two_outfits_sharing_one_anchor_refuse_two_images()
    {
        // Both outfits draw the SAME face mesh. The gate that tells them apart is one verdict for the whole
        // frame, so that mesh's draws can't carry a different image per outfit: refuse rather than let the
        // later bind win for both.
        var ex = Assert.Throws<InvalidOperationException>(() => BuildTwoOutfitFaces(
            new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (9, 8, 7, 255) },
            texWearers: new[] { 0, 1 }));

        Assert.Equal("stock texture 'tex_face_d' is retextured with two different images on mesh "
            + "'c_vesna01_face_lod0': VesnaSSR01 and VesnaDorm both draw it, so one image would have to "
            + "win. Give both changes the same image", ex.Message);
    }

    [Theory]
    // no index at all: both claims read unmeasured and take the game-wide rebind
    [InlineData(null)]
    // measured as worn by the first outfit only: it claims game-wide, then the second wants draw-scoped
    [InlineData(new[] { 0 })]
    // ...and the mirror image, where the scoped claim lands first
    [InlineData(new[] { 1 })]
    public void Two_different_images_refuse_where_the_mechanism_is_not_draw_scoped(int[]? texWearers)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildTwoOutfitFaces(
            new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (9, 8, 7, 255) },
            texWearers));

        Assert.Equal("stock texture 'tex_face_d' is retextured with two different images: "
            + "'c_vesna01_face_lod0' on VesnaSSR01 and 'c_vesna01_face_lod0' on VesnaDorm. "
            + "This texture isn't scoped to one outfit, so one image would have to win. "
            + "Give both changes the same image", ex.Message);
    }

    /// <summary>The mixed path with ONE image between the two claims: the stock texture is measured as worn
    /// by the first outfit only, so that claim takes the game-wide rebind and the second — which measures
    /// as sharing the texture with the first — finds the section already spent. Route: ModBuilder.Build's
    /// retexture accumulation, the same one the two-image cases above refuse on.
    ///
    /// <para>The same image is refused too, keyed or not. The section shows the game's own picture wherever
    /// no line of it runs, so a second gate under it repaints every wearer outside this mod in the states
    /// that gate answers, where they showed the game's own picture before. Keyless the two claims come to
    /// one bind and nothing is added, and that shape is refused beside the keyed one rather than carved
    /// out: the claim asked for a mechanism this section cannot give it either way.</para></summary>
    [Theory]
    [InlineData(null)]
    [InlineData(new object[] { new[] { "F6", "F7" } })]
    public void One_image_refuses_where_the_second_claim_measures_as_shared(string?[]? keys)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildTwoOutfitFaces(
            new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (1, 2, 3, 255) },
            texWearers: new[] { 0 }, keys: keys));

        Assert.Equal(ModBuilder.SharedTextureAlreadyWide("tex_face_d",
            "'c_vesna01_face_lod0' on VesnaSSR01", "'c_vesna01_face_lod0' on VesnaDorm").Message,
            ex.Message);
    }

    /// <summary>The legitimate neighbour of the case above: NEITHER claim measures as shared (no index at
    /// all), so both take the game-wide rebind and the second joins the first's section with a gate of its
    /// own. Route: the same. Nothing here asked for the draw-scoped mechanism, so nothing is refused — one
    /// file ships and each key binds it.</summary>
    [Fact]
    public void An_unmeasured_second_claim_on_one_image_keeps_its_own_gate()
    {
        var r = BuildTwoOutfitFaces(new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (1, 2, 3, 255) },
            texWearers: null, keys: new string?[] { "F6", "F7" });

        string ini = ReadIni(r);
        // the game-wide route, since nothing measured either outfit as sharing the texture
        Assert.Contains("[TextureOverride_Retex_", ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        // one image between the two claims, so one file ships — and one bind per key, since dropping the
        // second would leave that outfit's change showing the game's own picture
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        Assert.Contains("if $zz_key_f6 == 0\nthis = Resource_Rtx0\nendif\n", ini);
        Assert.Contains("if $zz_key_f7 == 0\nthis = Resource_Rtx0\nendif\n", ini);
    }

    [Fact]
    public void One_outfit_asking_a_scoped_texture_for_two_images_refuses()
    {
        // Two stock textures with identical content hash to ONE resource, so one outfit's two materials
        // reach it with two authored images on ONE mesh. One draw binds one image, so the second would
        // overwrite the first unannounced.
        string proj = Path.Combine(_root, "twin"), outRoot = Path.Combine(_root, "twinbuild");
        Directory.CreateDirectory(proj);
        Directory.CreateDirectory(outRoot);

        string b0 = Path.Combine(_root, "tw0.bundle"), bt = Path.Combine(_root, "twt.bundle");
        SyntheticBundle.BuildOneMesh(b0, "c_vesna01_face_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 });
        var pixels = SyntheticBundle.SolidRgba32(8, 8, 200, 100, 50, 255);
        SyntheticBundle.Build(bt,
            new SyntheticBundle.TextureSpec("tex_twin_a", 8, 8, pixels, ColorSpace: 0),
            new SyntheticBundle.TextureSpec("tex_twin_b", 8, 8, pixels, ColorSpace: 0));
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundleT"] = File.ReadAllBytes(bt),
        };
        var stock = new Remold.Core.Bundles.BundleReader().GetTextureHashSource(bytes["bundleT"], "tex_twin_a")!.Value;
        string texHash = TextureHash.Compute(stock.PictureData, stock.Width, stock.Height, stock.MipCount,
            TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)stock.Format, stock.Srgb)!.Value).ToString("x8");

        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new Remold.Core.Workbench.SubjectPart("face", "c_vesna01_face_lod0", "addr_face", new[]
            {
                new SubjectMaterial("m_a", 1, "cab-a", new[] { new SubjectMap("_BaseMap", "tex_twin_a", "bundleT") }),
                new SubjectMaterial("m_b", 2, "cab-b", new[] { new SubjectMap("_BaseMap", "tex_twin_b", "bundleT") }),
            }),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var env = new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => a == "addr_face" ? "bundle0" : null,
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345", AppVersion: "test-1.0",
            SharingIndex.FromMeasurements("12345",
                new[]
                {
                    new SharingIndex.Wearer("Vesna", "Vesna", "VesnaSSR01", null),
                    new SharingIndex.Wearer("Vesna", "Vesna", "VesnaDorm", null),
                },
                new Dictionary<string, int[]> { [texHash] = new[] { 0, 1 } },
                new Dictionary<string, int[]>(), new Dictionary<int, string[]>())).Exact();

        var p = new Remold.Core.Project.ModProject { RootDir = proj };
        p.Info.Name = "Twin";
        p.Selection.Add(new Remold.Core.Project.SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        foreach (var (tex, file, rgba) in new[]
                 {
                     ("tex_twin_a", "twin_a.dds", ((byte)1, (byte)2, (byte)3, (byte)255)),
                     ("tex_twin_b", "twin_b.dds", ((byte)9, (byte)8, (byte)7, (byte)255)),
                 })
        {
            FlatDds.Write(Path.Combine(proj, file), rgba);
            p.Targets.Add(new Remold.Core.Project.ProjectTarget
            {
                AssetType = "Texture2D", Bundle = "bundleT", ObjectName = tex,
                ReplaceFile = file, Users = new List<string> { "c_vesna01_face_lod0" },
                SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            });
        }

        var ex = Assert.Throws<InvalidOperationException>(() => ReleasedBuild.Build(p, env, outRoot));

        Assert.Equal("stock texture 'tex_twin_a' is retextured with two different images on VesnaSSR01's "
            + "'c_vesna01_face_lod0': 'twin_a.dds' and 'twin_b.dds'. One draw binds one image. "
            + "Give both changes the same image", ex.Message);
    }

    [Fact]
    public void Two_keyed_scoped_images_each_toggle_on_their_own_key()
    {
        // Two images, two keys: each image honours the key of the change that authored it, so the two
        // outfits' disambiguators switch independently.
        var r = BuildTwoOutfitFaces(new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (9, 8, 7, 255) },
            texWearers: new[] { 0, 1 }, keys: new string?[] { "F6", "F7" }, sharedMesh: false);

        string ini = ReadIni(r);
        Assert.Contains("global $zz_key_f6 = 0\n", ini);
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        Assert.Contains("[Key_zz_key_f6]\nkey = no_modifiers F6\nrun = CommandListKey_zz_key_f6\n", ini);
        Assert.Contains("[Key_zz_key_f7]\nkey = no_modifiers F7\nrun = CommandListKey_zz_key_f7\n", ini);
        // each image's bind sits under its OWN key and its own outfit gate
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_gate_vesnassr01 == 1\nif $zz_rslot == 0\n"
            + "ps-t0 = Resource_Rtx0\nendif\n", ini);
        Assert.Contains("if $zz_key_f7 == 0\nif $zz_gate_vesnadorm == 1\nif $zz_rslot == 0\n"
            + "ps-t0 = Resource_Rtx1\nendif\n", ini);
        // nothing about one key applying to both: the section carries each image's own
        Assert.DoesNotContain(r.Warnings, w => w.Contains("different toggle keys"));
    }

    [Fact]
    public void One_image_under_two_keys_stays_two_binds_rather_than_collapsing_onto_one_key()
    {
        // The same image authored under both outfits but bound to different keys: collapsing them would
        // silently drop one key, so the image ships once and binds twice, once per key and gate.
        var r = BuildTwoOutfitFaces(new (byte, byte, byte, byte)[] { (1, 2, 3, 255), (1, 2, 3, 255) },
            texWearers: new[] { 0, 1 }, keys: new string?[] { "F6", "F7" });

        string ini = ReadIni(r);
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_gate_vesnassr01 == 1\nif $zz_rslot == 0\n"
            + "ps-t0 = Resource_Rtx0\nendif\n", ini);
        Assert.Contains("if $zz_key_f7 == 0\nif $zz_gate_vesnadorm == 1\nif $zz_rslot == 0\n"
            + "ps-t0 = Resource_Rtx0\nendif\n", ini);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("different toggle keys"));
    }

    private static int CountOf(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Shared_anchor_without_witness_ships_ungated_and_says_so()
    {
        var env = _world.MakeEnv(out string lod0, out _);
        env = env with
        {
            Sharing = Index(_world.StockTexHash, new[] { 0, 1 },
                mesh: new Dictionary<string, int[]> { [lod0] = new[] { 0, 1 } }),
        };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        Assert.DoesNotContain("$zz_gate_", ini);
        Assert.DoesNotContain("[TextureOverride_Witness_", ini);
        Assert.Contains(r.Infos, i => i.Contains("has no mesh of its own"));
    }

    [Fact]
    public void Hidden_shared_mesh_gets_the_latch()
    {
        var env = _world.MakeEnv(out string lod0, out string lod1);
        env = env with
        {
            Sharing = Index("00000000", new[] { 0 },
                mesh: new Dictionary<string, int[]> { [lod0] = new[] { 0, 1 } },
                witnesses: new[] { "aabbccdd" }),
        };
        var p = _world.NewProject("Hide");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        // the shared tier's skip sits under the latch; the private tier's does not
        Assert.Contains($"hash = {lod0}\nmatch_priority = 0\nif $zz_gate_vesnassr01 == 1\nhandling = skip\nendif\n", ini);
        Assert.Contains($"hash = {lod1}\nmatch_priority = 0\nhandling = skip\n", ini);
        Assert.Contains(r.Infos, i => i.Contains("Karst") && i.Contains("hide"));
    }

    // ---- honesty when unmeasured ------------------------------------------------------------------

    [Fact]
    public void Unmeasured_subject_ships_unscoped_and_says_so()
    {
        var env = _world.MakeEnv(out _, out _);
        // an index that covers a different subject entirely
        env = env with
        {
            Sharing = SharingIndex.FromMeasurements("12345", new[] { Other },
                new Dictionary<string, int[]>(), new Dictionary<string, int[]>(),
                new Dictionary<int, string[]>()),
        };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        string ini = ReadIni(r);
        Assert.Contains("[TextureOverride_Retex_", ini);
        Assert.Contains(r.Infos, i => i.Contains("haven't been measured"));
    }

    [Theory]
    [InlineData(new object[] { new[] { "mirel|MirelSSR01" } })]
    [InlineData(new object[] { new[] { "mirel|MirelSSR01", "sable|SableDorm" } })]
    public void An_index_carrying_unmeasured_outfits_notes_the_floor_in_the_log_only(string[] failed)
    {
        // The failed outfit is uncovered, so a texture it also wears reads private here and the edit
        // rebinds game-wide. Which texture that is can't be known from an index missing it, and the
        // modder can't act on a bare count - so the floor is a build-log fact, named per outfit, and
        // never a user-facing line.
        var env = _world.MakeEnv(out _, out _);
        env = env with { Sharing = Index(_world.StockTexHash, new[] { 0 }, failed: failed) };
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        Assert.DoesNotContain(r.Infos, i => i.Contains("unmeasured"));
        Assert.Contains(r.Diagnostics, d => d.Contains("sharing reach is a floor")
            && failed.All(d.Contains));
        // and the edit still ships on the evidence there is
        Assert.Contains("[TextureOverride_Retex_", ReadIni(r));
    }

    [Fact]
    public void A_failed_whole_index_says_every_edit_ships_unscoped_in_user_infos()
    {
        var env = _world.MakeEnv(out _, out _);
        var p = _world.NewProject("Retex");
        _world.AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _world.OutRoot);

        Assert.Contains("Shared meshes and textures haven't been measured. Every edit applies wherever "
            + "its mesh or texture draws in game.", r.Infos);
    }

    // ---- the tag derivation -----------------------------------------------------------------------

    [Theory]
    [InlineData("00000000")]
    [InlineData("ffffffff")]
    [InlineData("a8d20afb")]
    public void Retex_tag_is_deterministic_and_float_exact(string hash)
    {
        int tag = MigotoEmitter.RetexTag(hash);
        Assert.Equal(tag, MigotoEmitter.RetexTag(hash));
        Assert.InRange(tag, 1_000_000, 15_999_999);
    }
}
