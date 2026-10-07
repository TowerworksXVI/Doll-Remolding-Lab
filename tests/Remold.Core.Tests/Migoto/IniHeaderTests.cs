using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The comment header a generated mod.ini opens with tells whoever opens the file what the mod does and which
/// sections do it: the app that made it, one sentence of what the mod changes, how each replacement is drawn,
/// what the file's other section families do, and the keys. Each line is there only where its fact applies.
/// </summary>
public class IniHeaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-iniheader-" + Guid.NewGuid().ToString("N"));

    public IniHeaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // T ─ A ─ B          T ─ R ─ S
    //      └─ P ─ C ─ D
    private const uint T = 200, A = 101, B = 102, C = 103, D = 104, P = 106, R = 111, S = 112;

    private static readonly IReadOnlyDictionary<uint, string> Paths = new Dictionary<uint, string>
    {
        [T] = "T", [A] = "T/A", [B] = "T/A/B", [P] = "T/A/P", [C] = "T/A/P/C", [D] = "T/A/P/C/D",
        [R] = "T/R", [S] = "T/R/S",
    };

    private const string Posed =
        "; At each draw of a replaced mesh, custom shader passes read the game's posed vertices,\n"
        + "; recover that draw's bone matrices (CustomShaderGather_*, CustomShaderPosePalette_*),\n"
        + "; skin the replacement with them (CustomShaderPoseSkin_*) and draw it in place of the\n"
        + "; original (CommandListDraw_*), so each copy of the part on screen is posed from its own draw.\n";

    private static string Header(string ini) => ini[..(ini.IndexOf("\n\n", StringComparison.Ordinal) + 1)];

    /// <summary>The emission's own vocabulary never appears in the header outside a section name, and a
    /// variable only as the slot probe's.</summary>
    private static void AssertPlain(string header)
    {
        foreach (string word in new[] { "pooled", "pool", "anchor", "union", "chain", "stride", "render target", "->" })
            Assert.DoesNotContain(word, header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("palette", header.Replace("CustomShaderPosePalette_", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$", header.Replace("$zz_t in CommandListDraw_*", ""));
        Assert.All(header.TrimEnd('\n').Split('\n'), l => Assert.StartsWith("; ", l));
    }

    private string Dump(string name, int seed, params uint[] bones)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WritePartDump(dir, seed, 32, bones);
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        for (int v = 0; v < 32; v++)
        {
            BitConverter.GetBytes(((v + seed) * 13 % 17) / 4f).CopyTo(s0, v * 40);
            BitConverter.GetBytes(((v + seed) * 7 % 23) / 5f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes(((v + seed) * 11 % 29) / 6f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);
        return dir;
    }

    private string Build(PoolPart[] parts, int unionBones, int[]? weighted = null, string? modKey = null, string[]? hides = null)
    {
        string outDir = Path.Combine(_root, "out" + Guid.NewGuid().ToString("N"));
        string donorDir = Path.Combine(_root, "donor" + Guid.NewGuid().ToString("N"));
        SyntheticPool.WriteDonor(donorDir, verts: 8, unionBones: unionBones);
        if (weighted is { Length: > 0 }) SyntheticPool.WeightDonorOn(donorDir, weighted);
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            ToggleKey = modKey,
            HideHashes = hides,
            AppVersion = "1.2.3",
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = parts,
                    Anchor = parts[0].Name,
                    DonorDir = donorDir,
                    CaptureHashes = parts.Select((p, i) => (p.Name, Hash: $"{(char)('a' + i)}{(char)('a' + i)}0001"))
                        .ToDictionary(x => x.Name, x => x.Hash),
                    BonePaths = Paths,
                },
            },
        });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    [Fact]
    public void A_replacement_taking_bones_from_one_other_part_names_the_part_and_the_sections_that_record_it()
    {
        // beta shares alpha's root, so alpha's draw finds beta's record without a pick pass
        string header = Header(Build(new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, A, C)).Rooted(A, T),
        }, unionBones: 3, modKey: "F6", hides: new[] { "eeee0001" }));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n"
            + "; Replaces the mesh of 1 part and hides 2 original meshes.\n"
            + Posed
            + "; alpha's replacement is weighted to bones of beta as well: each draw of\n"
            + "; beta records its vertices and object-to-world matrix (CustomShaderRing*_beta),\n"
            + "; and alpha's draw takes the record standing where its own bones place that part.\n"
            + "; Key F6 turns the mod on and off.\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_replacement_taking_bones_from_two_parts_one_placed_by_a_bone_names_both_and_the_pick_sections()
    {
        // beta hangs under P, which alpha recovers, so a pick pass places it; gamma is looked for near alpha
        string bd = Dump("beta", 2, C, D);
        SyntheticPool.SetRestOrigin(bd, C, new Vector3(0.25f, 1.5f, -0.75f));
        string header = Header(Build(new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B, P)).Rooted(A, T),
            new PoolPart("beta", bd).Rooted(C, P, A, T),
            new PoolPart("gamma", Dump("gamma", 3, R, S)).Rooted(R, T),
        }, unionBones: 7, weighted: new[] { 0, 1, 3, 4, 5, 6 }));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n"
            + "; Replaces the mesh of 1 part and hides 2 original meshes.\n"
            + Posed
            + "; alpha's replacement is weighted to bones of beta and gamma as well: each draw of\n"
            + "; those parts records its vertices and object-to-world matrix (CustomShaderRing*_*),\n"
            + "; and alpha's draw takes, for each, the record standing where its own bones place that part\n"
            + "; (CustomShaderPosePick_*).\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_replacement_taking_bones_from_two_parts_none_placed_by_a_bone_names_no_pick_sections()
    {
        string header = MigotoEmitter.IniHeader("1.2.3", new MigotoEmitter.ModSummary(1, 2, 0, false, null, Array.Empty<string>())
        {
            Replaces = new[]
            {
                new MigotoEmitter.ReplaceSummary("alpha_body", MigotoEmitter.ReplaceRoute.Pooled, new[] { "beta_cloth1", "gamma_coat" }, false),
            },
        });

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n"
            + "; Replaces the mesh of 1 part and hides 2 original meshes.\n"
            + Posed
            + "; alpha_body's replacement is weighted to bones of beta_cloth1 and gamma_coat as well: each draw of\n"
            + "; those parts records its vertices and object-to-world matrix (CustomShaderRing*_*),\n"
            + "; and alpha_body's draw takes, for each, the record standing where its own bones place that part.\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_replacement_of_one_part_says_each_copy_is_posed_from_its_own_draw()
    {
        string header = Header(Build(new[] { new PoolPart("alpha", Dump("alpha", 1, A, B)) }, unionBones: 2));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n; Replaces the mesh of 1 part.\n" + Posed, header);
        AssertPlain(header);
    }

    [Fact]
    public void A_replacement_posed_once_a_frame_says_its_copies_share_that_pose_and_names_its_sections()
    {
        // the other part's renderer is not known, so the replacement keeps one pose for every copy
        string header = Header(Build(new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, A, C)),
        }, unionBones: 3));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n"
            + "; Replaces the mesh of 1 part and hides 1 original mesh.\n"
            + "; alpha's replacement is skinned once a frame from the bone matrices recovered at the last draw of each\n"
            + "; part it is weighted to (CustomShaderRecover_*, CustomShaderConvert_*, CustomShaderSkin_*), so every\n"
            + "; copy of it on screen shares that pose.\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_rigid_replacement_says_it_is_drawn_without_per_vertex_posing()
    {
        string header = MigotoEmitter.IniHeader(null, new MigotoEmitter.ModSummary(1, 0, 0, false, null, Array.Empty<string>())
        {
            Rigids = new[] { "crate_frame" },
        });

        Assert.Equal("; Generated by Doll Remolding Lab.\n; Replaces the mesh of 1 part.\n"
            + "; crate_frame's replacement is drawn in place of the original without per-vertex\n"
            + "; posing (CommandListRigid_*).\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_part_replaced_in_several_states_counts_once_and_is_described_once()
    {
        // one part, two states under one key: one replaced part and one sentence about it; one hidden part
        // whose two meshes the build labels as one: one hidden mesh
        string ad = Dump("alpha", 1, A, B), bd = Dump("beta", 2, A, C);
        var parts = new[] { new PoolPart("alpha", ad).Rooted(A, T), new PoolPart("beta", bd).Rooted(A, T) };
        var pipelines = new List<ReplacePipeline>();
        for (int state = 0; state < 2; state++)
        {
            string donor = Path.Combine(_root, $"donor-s{state}");
            SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 3);
            pipelines.Add(new ReplacePipeline
            {
                Suffix = $"swap_s{state}",
                Parts = parts,
                Anchor = "alpha",
                DonorDir = donor,
                CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                ToggleKey = new KeyRef("F7", state),
            });
        }
        string outDir = Path.Combine(_root, "states");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = pipelines,
            KeyCycles = new[] { new KeyCycle("F7", 2, 0) },
            HideHashes = new[] { "dddd4444", "dddd5555" },
            MeshLabels = new Dictionary<string, string> { ["dddd4444"] = "Skirt", ["dddd5555"] = "Skirt" },
            AppVersion = "1.2.3",
        });
        string header = Header(File.ReadAllText(Path.Combine(outDir, "mod.ini")));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n"
            + "; Replaces the mesh of 1 part and hides 2 original meshes.\n"
            + Posed
            + "; alpha's replacement is weighted to bones of beta as well: each draw of\n"
            + "; beta records its vertices and object-to-world matrix (CustomShaderRing*_beta),\n"
            + "; and alpha's draw takes the record standing where its own bones place that part.\n"
            + "; Key F7 switches between states.\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void A_mod_that_replaces_nothing_starts_its_sentence_with_what_it_does()
    {
        string outDir = Path.Combine(_root, "hides");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, hideHashes: new[] { "dddd4444", "dddd5555" },
            appVersion: "1.2.3");
        string header = Header(File.ReadAllText(Path.Combine(outDir, "mod.ini")));

        Assert.Equal("; Generated by Doll Remolding Lab 1.2.3.\n; Hides 2 original meshes.\n", header);
        AssertPlain(header);

        Assert.Equal("; Generated by Doll Remolding Lab.\n"
            + "; Changes 1 original texture and changes the shading of some original materials.\n",
            MigotoEmitter.IniHeader("", new MigotoEmitter.ModSummary(0, 0, 1, true, null, Array.Empty<string>())));
    }

    [Fact]
    public void The_what_line_joins_every_change_in_one_sentence_with_singulars_where_one()
    {
        Assert.Equal("; Generated by Doll Remolding Lab.\n"
            + "; Replaces the meshes of 3 parts, hides 1 original mesh, changes 2 original textures, and changes the "
            + "shading of some original materials.\n",
            MigotoEmitter.IniHeader(null, new MigotoEmitter.ModSummary(3, 1, 2, true, null, Array.Empty<string>())));
    }

    [Fact]
    public void Each_section_family_the_file_holds_is_named_with_what_it_does()
    {
        string header = MigotoEmitter.IniHeader(null, new MigotoEmitter.ModSummary(0, 0, 1, true, null, Array.Empty<string>())
        {
            SlotProbe = true,
            MaterialPasses = true,
            Retextures = true,
        });

        Assert.Equal("; Generated by Doll Remolding Lab.\n"
            + "; Changes 1 original texture and changes the shading of some original materials.\n"
            + "; The if-blocks on $zz_t in CommandListDraw_* find which slot the game bound each of the\n"
            + "; part's textures in, so the replacement's textures bind to the same slots.\n"
            + "; ShaderOverride_MaterialPass_* carry the shading changes, one per game material program.\n"
            + "; TextureOverride_Retex_* bind the replacement textures by the original's hash.\n", header);
        AssertPlain(header);
    }

    [Fact]
    public void The_section_families_are_read_off_the_file_the_header_opens()
    {
        // a slot probe counts in a replacement's draw list only; a texture tag is not a retexture
        Assert.Equal((true, true, true), MigotoEmitter.IniMarkers(
            "[CommandListDrawS0_swap]\n$zz_t = ps-t0\n\n[ShaderOverride_MaterialPass_abcd]\nhash = abcd\n\n"
            + "[TextureOverride_Retex_face]\nhash = ffff0000\n"));
        Assert.Equal((false, false, false), MigotoEmitter.IniMarkers(
            "[TextureOverride_Hide_aaaa]\n$zz_t = ps-t0\n\n[CommandListRigid_crate]\nvb0 = Resource_X\n\n"
            + "[TextureOverride_RetexTag_ffff0000]\nhash = ffff0000\n"));
    }

    [Fact]
    public void The_keys_are_one_sentence_naming_each_by_its_keycap()
    {
        static string Keys(string? modKey, params string[] stateKeys) =>
            MigotoEmitter.IniHeader(null, new MigotoEmitter.ModSummary(0, 0, 0, false, modKey, stateKeys))
                .Split('\n')[1];

        Assert.Equal("; Key 8 turns the mod on and off.", Keys("8"));
        Assert.Equal("; Key F9 switches between states.", Keys(null, "F9"));
        Assert.Equal("; Keys F7 and F8 switch between states.", Keys(null, "F7", "F8"));
        Assert.Equal("; Key CTRL . turns the mod on and off, and keys F7 and F8 switch between states.",
            Keys("CTRL PERIOD", "F7", "F8"));
    }
}
