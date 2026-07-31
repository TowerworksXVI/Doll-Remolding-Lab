using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The workbench state → edit list mapping. An edited mesh target is a Replace; an edited texture becomes a
/// Retexture on the parts binding it; Hidden entries are Hides and WIN over edits on the same mesh. Loud:
/// stale mesh targets and unresolvable subjects with content throw, stale hides and unemitted slots warn.
/// </summary>
public class VerbDerivationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-vd-" + Guid.NewGuid().ToString("N"));

    public VerbDerivationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ModProject Project()
    {
        var p = new ModProject { RootDir = _root };
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        return p;
    }

    private static SubjectModel Model() => new("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
    {
        new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", new[]
        {
            new SubjectMaterial("m_body", 1, "cab",
                new[] { new SubjectMap("_BaseMap", "tex_d", "bT"), new SubjectMap("_BumpMap", "tex_n", "bT") }),
            new SubjectMaterial("m_trim", 2, "cab",
                new[] { new SubjectMap("_RampMap", "tex_ramp", "bT") }),
        }),
        new SubjectPart("hair", "c_vesna01_hair_lod0", "addr_hair", Array.Empty<SubjectMaterial>()),
    }, Skeleton: null, Problems: Array.Empty<string>());

    private ProjectTarget AddMesh(ModProject p, string mesh, bool edited, string file = "body.glb",
        string outfit = "VesnaSSR01")
    {
        File.WriteAllText(Path.Combine(_root, file), "glb-edited");
        string? orig = null;
        if (!edited)
        {
            orig = "orig_" + file;
            File.Copy(Path.Combine(_root, file), Path.Combine(_root, orig));
        }
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = mesh,
            SubjectCharacter = "Vesna", SubjectOutfit = outfit,
            ReplaceFile = file, OriginalFile = orig,
        };
        p.Targets.Add(t);
        return t;
    }

    /// <summary>A materialized texture, subject-owned like a mesh target: one game texture touched by two
    /// outfits is two targets, each with its own workspace file.</summary>
    private void AddTexture(ModProject p, string name, string bundle, string file,
        string outfit = "VesnaSSR01")
    {
        File.WriteAllText(Path.Combine(_root, file), "png");
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = bundle, ObjectName = name, ReplaceFile = file,
            SubjectCharacter = "Vesna", SubjectOutfit = outfit,
        });
    }

    private List<MeshEdit> Derive(ModProject p, out List<string> warnings)
    {
        var w = new List<string>();
        var edits = VerbDerivation.Derive(p, (c, s) => c == "Vesna" && s == "VesnaSSR01" ? Model() : null, w);
        warnings = w;
        return edits;
    }

    [Fact]
    public void An_edited_mesh_target_derives_a_replace_with_its_donor_textures()
    {
        var p = Project();
        var t = AddMesh(p, "c_vesna01_body_lod0", edited: true);
        t.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "a.png" } };

        var edits = Derive(p, out _);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Equal("body.glb", e.DonorFile);
        Assert.Equal("a.png", e.Textures!.Single().Albedo);
    }

    [Fact]
    public void An_unedited_mesh_target_derives_nothing()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: false);
        Assert.Empty(Derive(p, out _));
    }

    [Fact]
    public void A_hidden_mesh_derives_a_hide_and_wins_over_its_edit()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var edits = Derive(p, out var warnings);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Hide, e.Verb);
        Assert.Contains(warnings, w => w.Contains("hidden"));
    }

    [Fact]
    public void An_edited_texture_derives_retextures_by_material_slot_and_submesh()
    {
        var p = Project();
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        AddTexture(p, "tex_n", "bT", "skin_n.png");

        var edits = Derive(p, out _);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Retexture, e.Verb);
        Assert.Equal("c_vesna01_body_lod0", e.Mesh);
        var s = Assert.Single(e.Textures!);
        Assert.Equal(0, s.Submesh);
        Assert.Equal("skin_d.png", s.Albedo);
        Assert.Equal("skin_n.png", s.Normal);
    }

    [Fact]
    public void An_unemitted_texture_slot_warns_instead_of_silently_dropping()
    {
        var p = Project();
        AddTexture(p, "tex_ramp", "bT", "ramp.png");

        var edits = Derive(p, out var warnings);

        Assert.Empty(edits);
        Assert.Contains(warnings, w => w.Contains("_RampMap"));
    }

    [Fact]
    public void A_retexture_skips_parts_that_are_replaced_or_hidden()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");

        var edits = Derive(p, out _);

        Assert.Single(edits);   // the Replace only — its part doesn't also retexture
        Assert.Equal(EditVerbs.Replace, edits[0].Verb);
    }

    [Fact]
    public void A_replaced_part_ships_its_donor_maps_and_never_its_game_texture_edit()
    {
        // The Replace carries the send-back's authored maps, and the edit to the game texture that part
        // binds is not emitted AT ALL — which is why the card verbs edit the authored file instead.
        var p = Project();
        var t = AddMesh(p, "c_vesna01_body_lod0", edited: true);
        t.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "authored.png" } };
        AddTexture(p, "tex_d", "bT", "skin_d.png");

        var edits = Derive(p, out _);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Equal("authored.png", e.Textures!.Single().Albedo);
        Assert.DoesNotContain(edits, x => x.Verb == EditVerbs.Retexture);
    }

    [Fact]
    public void A_texture_edit_on_a_replaced_part_warns_instead_of_vanishing()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");   // binds on the part being replaced

        Derive(p, out var warnings);

        Assert.Contains(warnings,
            w => w == "'c_vesna01_body_lod0' is replaced. Its texture edit is not in this build");
    }

    [Fact]
    public void A_texture_edit_the_replacement_adopted_does_not_warn()
    {
        // the send-back recorded the edited workspace PNG as the replacement's own donor map, so the edit
        // ships with the Replace and there is nothing to warn about
        var p = Project();
        var t = AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        t.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "skin_d.png" } };

        var edits = Derive(p, out var warnings);

        var e = Assert.Single(edits);   // the Replace, carrying the map
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.DoesNotContain(warnings, w => w.Contains("texture edit"));
    }

    [Fact]
    public void A_texture_edit_the_replacement_did_not_adopt_still_warns()
    {
        // one bound edit rides the donor rows, the other does not — the loss is still said
        var p = Project();
        var t = AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        AddTexture(p, "tex_n", "bT", "skin_n.png");
        t.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "skin_d.png" } };

        Derive(p, out var warnings);

        Assert.Contains(warnings,
            w => w == "'c_vesna01_body_lod0' is replaced. Its texture edit is not in this build");
    }

    /// <summary>A Replace rebinds base colour, normal and RMO on its own submeshes and nothing else, so the
    /// part's other slots keep drawing their game textures. Blaming the replacement for an edit on one of
    /// those names the wrong reason: what stops it is the retexture emitter, which has no case for the
    /// slot.</summary>
    [Fact]
    public void A_texture_edit_on_a_slot_the_replacement_never_rebinds_is_not_blamed_on_the_replacement()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_ramp", "bT", "ramp.png");   // binds as _RampMap on the replaced part

        Derive(p, out var warnings);

        Assert.DoesNotContain(warnings,
            w => w == "'c_vesna01_body_lod0' is replaced. Its texture edit is not in this build");
        Assert.Contains(warnings,
            w => w == "'tex_ramp' binds as _RampMap on 'c_vesna01_body_lod0'. "
                    + "That slot isn't emitted yet; the edit doesn't show on this mesh");
    }

    [Fact]
    public void A_replaced_part_editing_both_kinds_of_slot_gets_both_reasons()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");     // _BaseMap: the replacement rebinds it
        AddTexture(p, "tex_ramp", "bT", "ramp.png");    // _RampMap: it does not

        var edits = Derive(p, out var warnings);

        Assert.Contains(warnings,
            w => w == "'c_vesna01_body_lod0' is replaced. Its texture edit is not in this build");
        Assert.Contains(warnings, w => w.Contains("_RampMap", StringComparison.Ordinal));
        // and neither warning invents a Retexture: the emitter still ships only the three slots
        Assert.DoesNotContain(edits, x => x.Verb == EditVerbs.Retexture);
    }

    [Fact]
    public void A_texture_edit_on_a_hidden_part_warns_instead_of_vanishing()
    {
        var p = Project();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        AddTexture(p, "tex_d", "bT", "skin_d.png");   // binds on the part being hidden

        Derive(p, out var warnings);

        Assert.Contains(warnings,
            w => w == "'c_vesna01_body_lod0' is hidden. Its texture edit is not in this build");
    }

    [Fact]
    public void A_replaced_part_that_binds_no_edited_texture_warns_about_nothing()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_hair_lod0", edited: true, file: "hair.glb");   // hair binds no materials
        AddTexture(p, "tex_d", "bT", "skin_d.png");                          // the edit binds on body

        var edits = Derive(p, out var warnings);

        Assert.Equal(2, edits.Count);   // the hair Replace and the body Retexture
        Assert.DoesNotContain(warnings, w => w.Contains("is replaced"));
    }

    [Fact]
    public void Retextures_derive_per_subject_and_are_not_name_deduped_here()
    {
        // Two subjects binding the same edited texture each derive their own Retexture: parts sharing a
        // NAME across outfits can carry different mesh bytes, so the dedupe that matters is ModBuilder's,
        // keyed on the ib hash. A name-keyed claim here would drop a distinct mesh's coverage.
        var p = Project();
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaAlt" });
        // one game texture, materialized under BOTH subjects — each outfit owns its own file
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        AddTexture(p, "tex_d", "bT", "skin_d.alt.png", outfit: "VesnaAlt");

        var w = new List<string>();
        var edits = VerbDerivation.Derive(p, (c, s) => Model(), w);

        Assert.Equal(2, edits.Count(x => x.Verb == EditVerbs.Retexture));
        Assert.All(edits, e => Assert.Equal("c_vesna01_body_lod0", e.Mesh));
    }

    /// <summary>Two selected subjects whose parts carry the SAME name — the shape two shipped builds of one
    /// character produce, and the one the Replace claim keys can't tell apart.</summary>
    private ModProject TwoBuildsOfOneCharacter()
    {
        var p = Project();
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaAlt" });
        return p;
    }

    private static List<MeshEdit> DeriveTwoBuilds(ModProject p, out List<string> warnings)
    {
        var w = new List<string>();
        var edits = VerbDerivation.Derive(p, (_, _) => Model(), w);
        warnings = w;
        return edits;
    }

    [Fact]
    public void A_second_subjects_replace_on_a_shared_part_name_warns_naming_both()
    {
        // The claim is keyed on name and path id, so two builds sharing a part name collide on it and the
        // second Replace is dropped. Dropping it quietly is the failure: the build-time hash check never
        // sees the second Replace, so nothing else in the pipeline can tell the author it went missing.
        var p = TwoBuildsOfOneCharacter();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddMesh(p, "c_vesna01_body_lod0", edited: true, file: "alt.glb", outfit: "VesnaAlt");

        var edits = DeriveTwoBuilds(p, out var warnings);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Equal("VesnaSSR01", e.Outfit);         // the first claim stands
        Assert.Contains(warnings, w => w == "'c_vesna01_body_lod0' is replaced on both Vesna · VesnaSSR01 "
            + "and Vesna · VesnaAlt. One replacement per part name ships, so Vesna · VesnaAlt's is not in "
            + "this build. Build the two subjects as separate mods");
    }

    [Fact]
    public void A_dropped_replace_does_not_swallow_that_subjects_texture_edit()
    {
        // One subject takes the Replace and the other's is dropped, so the loser's texture edit doesn't ship
        // either. Both facts have to be said, and said accurately: which subject took the draw, and that
        // this subject's texture edit went with the Replace it lost.
        var p = TwoBuildsOfOneCharacter();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        AddMesh(p, "c_vesna01_body_lod0", edited: true, file: "alt.glb", outfit: "VesnaAlt");
        // the same map, materialized under BOTH subjects — each outfit owns its own file
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        AddTexture(p, "tex_d", "bT", "skin_d.alt.png", outfit: "VesnaAlt");

        DeriveTwoBuilds(p, out var warnings);

        Assert.Contains(warnings, w => w.Contains("so Vesna · VesnaAlt's is not in this build",
            StringComparison.Ordinal));
        Assert.Contains(warnings, w => w == "'c_vesna01_body_lod0' is replaced by Vesna · VesnaSSR01. "
            + "Its texture edit is not in this build");
        // and the claiming subject still gets its own, differently worded, one
        Assert.Contains(warnings,
            w => w == "'c_vesna01_body_lod0' is replaced. Its texture edit is not in this build");
    }

    [Fact]
    public void Two_subjects_replacing_parts_with_distinct_path_ids_both_ship()
    {
        // The claim only collides where the key does. Renderer-resolved parts carry a path id, which tells
        // the two apart, and both Replaces derive with no warning.
        var p = TwoBuildsOfOneCharacter();
        AddMesh(p, "c_vesna01_body_lod0", edited: true).PathId = 11;
        AddMesh(p, "c_vesna01_body_lod0", edited: true, file: "alt.glb", outfit: "VesnaAlt").PathId = 22;

        var edits = DeriveTwoBuilds(p, out var warnings);

        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(EditVerbs.Replace, e.Verb));
        Assert.Empty(warnings);
    }

    [Fact]
    public void A_stale_hidden_entry_warns_and_derives_nothing()
    {
        var p = Project();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", true);

        var edits = Derive(p, out var warnings);

        Assert.Empty(edits);
        Assert.Contains(warnings, w => w.Contains("ghost"));
    }

    [Fact]
    public void An_edited_stale_mesh_target_throws()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_ghost_lod0", edited: true);
        Assert.Throws<InvalidOperationException>(() => Derive(p, out _));
    }

    [Fact]
    public void A_baked_rest_mesh_edit_derives_a_replace_carrying_the_bake()
    {
        // The build un-bakes the donor by the target's recorded uprighting, so the edit ships and
        // the matrix rides the derived entry to the build.
        var p = Project();
        var t = AddMesh(p, "c_vesna01_body_lod0", edited: true);
        t.BakedRest = new List<float> { 1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1 };
        var edits = Derive(p, out _);
        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Equal(t.BakedRest, e.BakedRest);
    }

    [Fact]
    public void A_build_excluded_replace_is_not_in_the_shipped_list()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, true);

        var edits = Derive(p, out var warnings);

        Assert.Empty(edits);
        Assert.Empty(warnings);   // the pane shows the unticked row; a warning would restate it
    }

    [Fact]
    public void A_build_excluded_retexture_is_not_in_the_shipped_list()
    {
        var p = Project();
        AddTexture(p, "tex_d", "bT", "skin_d.png");
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, true);

        Assert.Empty(Derive(p, out _));
    }

    [Fact]
    public void A_build_excluded_hide_is_not_in_the_shipped_list()
    {
        var p = Project();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Hide, true);

        Assert.Empty(Derive(p, out _));
    }

    [Fact]
    public void A_build_exclusion_outside_the_roster_changes_nothing()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", EditVerbs.Replace, true);
        p.SetBuildExcluded("Nobody", "NobodySSR01", "c_vesna01_body_lod0", EditVerbs.Replace, true);

        var e = Assert.Single(Derive(p, out var warnings));
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Empty(warnings);
    }

    [Fact]
    public void DeriveAll_keeps_build_excluded_entries_for_the_pane()
    {
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, true);

        var all = VerbDerivation.DeriveAll(p, (c, s) => Model(), new List<string>());

        Assert.Equal(EditVerbs.Replace, Assert.Single(all).Verb);
    }

    [Fact]
    public void An_exclusion_belongs_to_the_verb_it_was_ticked_against()
    {
        // The Replace is a change the modder never unticked, so it ships and the pane draws its row ticked.
        var p = Project();
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Hide, true);
        Assert.Empty(Derive(p, out _));

        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", false);

        var e = Assert.Single(Derive(p, out _));
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.False(p.IsBuildExcluded(e.Character, e.Outfit, e.Mesh, e.Verb));   // the row renders ticked
        Assert.True(p.IsBuildExcluded(e.Character, e.Outfit, e.Mesh, EditVerbs.Hide));   // the Hide's tick stands
    }

    [Fact]
    public void A_build_exclusion_round_trips_through_save_and_load()
    {
        var p = Project();
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, true);
        p.Save();

        var reloaded = ModProject.Load(_root);

        Assert.True(reloaded.IsBuildExcluded("vesna", "vesnassr01", "C_VESNA01_BODY_LOD0", EditVerbs.Replace));
        reloaded.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, false);
        Assert.Empty(reloaded.BuildExcluded);
    }

    [Fact]
    public void An_unresolvable_subject_with_content_throws_but_selection_only_skips()
    {
        var p = Project();
        var w = new List<string>();
        // selection-only: fine
        Assert.Empty(VerbDerivation.Derive(p, (_, _) => null, w));
        // with content: loud
        AddMesh(p, "c_vesna01_body_lod0", edited: true);
        Assert.Throws<InvalidOperationException>(() => VerbDerivation.Derive(p, (_, _) => null, w));
    }
}
