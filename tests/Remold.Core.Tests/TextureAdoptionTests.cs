using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A texture edit on a slot a Replace rebinds: the moment the edit is marked, the replacement takes it over as
/// one of its own donor maps — a reference to the texture's workspace PNG, on every submesh the map dresses
/// that the modder has not already spoken for. The routes that genuinely can't adopt (a hidden part, a submesh
/// past the replacement, a slot already carrying a map or a deliberate blank) leave the edit plain, and the
/// build's derivation warns about each with its own reason. The candidates are re-asked where they are
/// written, since a run in flight holds them and the project can move in between.
/// </summary>
public class TextureAdoptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-adopt-" + Guid.NewGuid().ToString("N"));

    public TextureAdoptionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private ModProject Project()
    {
        var p = new ModProject { RootDir = _root };
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        return p;
    }

    private static SubjectMap Base(string tex = "tex_d") => new("_BaseMap", tex, "bT");
    private static SubjectMap Normal(string tex = "tex_n") => new("_BumpMap", tex, "bT");

    private static SubjectMaterial Mat(string name, params SubjectMap[] maps) =>
        new(name, 1, "cab", maps);

    /// <summary>One part, one submesh, one base-colour map — the shape the plain adoption reads on.</summary>
    private static SubjectModel Model(params SubjectMaterial[] materials) =>
        new("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body",
                materials.Length > 0 ? materials : new[] { Mat("m_body", Base(), Normal()) }),
        }, Skeleton: null, Problems: Array.Empty<string>());

    private ProjectTarget AddMesh(ModProject p, int donorSubmeshes = 1, string file = "body.glb",
        string mesh = "c_vesna01_body_lod0", string outfit = "VesnaSSR01")
    {
        File.WriteAllText(Path.Combine(_root, file), "glb-edited");
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = mesh,
            SubjectCharacter = "Vesna", SubjectOutfit = outfit,
            ReplaceFile = file, OriginalFile = null,   // no original on record = edited
            DonorMaterials = Enumerable.Range(0, donorSubmeshes).Select(i => $"m{i}").ToList(),
        };
        p.Targets.Add(t);
        return t;
    }

    private ProjectTarget AddTexture(ModProject p, string name = "tex_d", string file = "skin_d.png",
        string bundle = "bT", string outfit = "VesnaSSR01")
    {
        File.WriteAllText(Path.Combine(_root, file), "png");
        var t = new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = bundle, ObjectName = name, ReplaceFile = file,
            SubjectCharacter = "Vesna", SubjectOutfit = outfit,
        };
        p.Targets.Add(t);
        return t;
    }

    /// <summary>What the edit-time seam asks for one texture: the adoptions it opens, and the slots that had
    /// somewhere to land and were already spoken for.</summary>
    private static List<TextureAdoption> Candidates(ModProject p, SubjectModel model, ProjectTarget texture,
        out List<AdoptionBlocked> blocked)
    {
        blocked = new List<AdoptionBlocked>();
        return TextureAdoptions.CandidatesFor(p, model, texture, blocked).ToList();
    }

    private static List<TextureAdoption> Candidates(ModProject p, SubjectModel model, ProjectTarget texture) =>
        Candidates(p, model, texture, out _);

    /// <summary>What the Build pane's derivation says about the same state.</summary>
    private static List<string> Warnings(ModProject p, SubjectModel model)
    {
        var w = new List<string>();
        VerbDerivation.DeriveAll(p, (_, _) => model, w);
        return w;
    }

    private static List<MeshEdit> Derive(ModProject p, SubjectModel model, out List<string> warnings)
    {
        var w = new List<string>();
        var edits = VerbDerivation.DeriveAll(p, (_, _) => model, w);
        warnings = w;
        return edits;
    }

    private const string ReplacedWarning = "'body' is replaced. Its texture edit is not in this build. "
        + "Save the texture again in ② Edit, or drop the edited image on the part's map card";

    /// <summary>What a map dressing only submeshes the replacement doesn't have says — the drop's own
    /// refusal for that state, worded for the warnings list.</summary>
    private const string NoSuchSubmeshWarning =
        "'body' is replaced. This map dresses submeshes body's replacement doesn't have. "
        + "Send body back from Blender to add them";

    // ---- what the rule finds ----

    [Fact]
    public void An_edit_the_replacement_rebinds_is_a_candidate()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);

        var a = Assert.Single(Candidates(p, Model(), texture, out var blocked));

        Assert.Same(mesh, a.Mesh);
        Assert.Same(texture, a.Texture);
        Assert.Equal(DonorMapSlot.BaseColor, a.Slot);
        Assert.Equal(new[] { 0 }, a.Submeshes);
        Assert.Equal("body", a.Part);   // the token ② Edit labels the part by, not the game's slot name
        Assert.Empty(blocked);
    }

    [Fact]
    public void Each_rebound_slot_of_the_same_part_is_its_own_adoption()
    {
        var p = Project();
        AddMesh(p);
        var albedo = AddTexture(p);
        var normal = AddTexture(p, "tex_n", "skin_n.png");

        Assert.Equal(DonorMapSlot.BaseColor, Assert.Single(Candidates(p, Model(), albedo)).Slot);
        Assert.Equal(DonorMapSlot.Normal, Assert.Single(Candidates(p, Model(), normal)).Slot);
    }

    [Fact]
    public void One_map_dressing_several_material_slots_adopts_onto_every_one()
    {
        // The binding rule the map cards use: material order IS submesh order, and one stock map on three of
        // the part's slots is one image on three submeshes.
        var p = Project();
        AddMesh(p, donorSubmeshes: 3);
        var texture = AddTexture(p);
        var model = Model(Mat("m_a", Base()), Mat("m_b", Normal("tex_other")), Mat("m_c", Base()));

        var a = Assert.Single(Candidates(p, model, texture));

        Assert.Equal(new[] { 0, 2 }, a.Submeshes);
        Assert.Equal(DonorMapBinding.BoundSubmeshesByMap(model.Parts[0].Materials)
                [(DonorMapSlot.BaseColor, "tex_d", "bT")],
            a.Submeshes);
    }

    [Fact]
    public void A_submesh_past_the_replacement_is_dropped_from_the_landing_and_the_rest_adopts()
    {
        // The replacement came back with fewer submeshes than the game part had: the ones it doesn't have
        // can't carry a row, exactly as the map-card drop refuses outside that range.
        var p = Project();
        AddMesh(p, donorSubmeshes: 1);
        var texture = AddTexture(p);

        var a = Assert.Single(Candidates(p, Model(Mat("m_a", Base()), Mat("m_b", Base())), texture,
            out var blocked));

        Assert.Equal(new[] { 0 }, a.Submeshes);
        Assert.Empty(blocked);
    }

    [Fact]
    public void An_edit_with_no_donor_submesh_to_land_on_is_no_candidate_and_the_build_warns()
    {
        // A replacement carrying no material list at all has no shape to author against, so nothing adopts.
        // Nothing is BLOCKED either — no slot was spoken for, there is simply nowhere to put the map — and
        // the loss is said at build time, naming the way out.
        var p = Project();
        AddMesh(p).DonorMaterials = null;
        var texture = AddTexture(p);

        Assert.Empty(Candidates(p, Model(), texture, out var blocked));

        Assert.Empty(blocked);
        Assert.Contains(Warnings(p, Model()), w => w == NoSuchSubmeshWarning);
    }

    // ---- a slot the modder already spoke for ----

    [Fact]
    public void A_slot_carrying_a_dropped_map_of_its_own_is_never_adopted_over()
    {
        // The modder dropped an image on this card: a different file on the very slot the edit would take.
        // The adoption would overwrite it silently, so the submesh drops out of the landing and ② Edit says
        // what holds the slot.
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png", AlbedoOrigin = SlotOrigin.Authored },
        };

        Assert.Empty(Candidates(p, Model(), texture, out var blocked));

        Assert.Equal("textures/body_s0_base.png", mesh.DonorTextures[0].Albedo);
        // the generated donor file name is the build's vocabulary, so the line names the ROLE the modder
        // authored instead
        Assert.Equal("body's replacement already carries its own Base color map, so this edit won't show. "
            + "Drop the edited image on the part's map card to use it instead.",
            TextureAdoptions.SlotTaken(blocked));
        Assert.Contains(Warnings(p, Model()), w => w == "'body' is replaced. Its replacement "
            + "already carries its own Base color map, so the texture edit is not in this build. "
            + "Drop the edited image on the part's map card to use it instead");
    }

    [Fact]
    public void A_slot_blanked_on_purpose_survives_an_adoption()
    {
        // ExplicitNeutral is an ask like any other: the slot was blanked on purpose, and an RMO edit arriving
        // later doesn't answer that away.
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p, "tex_rmo", "skin_rmo.png");
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, RmoOrigin = SlotOrigin.ExplicitNeutral },
        };
        var model = Model(Mat("m_body", new SubjectMap("_RMOTex", "tex_rmo", "bT")));

        Assert.Empty(Candidates(p, model, texture, out var blocked));

        Assert.Equal(SlotOrigin.ExplicitNeutral, mesh.DonorTextures[0].RmoAsk);
        Assert.Equal("body's replacement blanks that slot on purpose, so this edit won't show. Drop the "
            + "edited image on the part's map card to use it instead.",
            TextureAdoptions.SlotTaken(blocked));
        Assert.Contains(Warnings(p, model), w => w == "'body' is replaced. Its replacement "
            + "blanks that slot on purpose, so the texture edit is not in this build. "
            + "Drop the edited image on the part's map card to use it instead");
    }

    [Fact]
    public void An_occupied_slot_takes_only_its_own_submesh_out_of_the_landing()
    {
        // The rule is per (slot, submesh): the submeshes the modder didn't speak for still adopt, and a
        // landing that survives is not a blocked slot.
        var p = Project();
        var mesh = AddMesh(p, donorSubmeshes: 2);
        var texture = AddTexture(p);
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png", AlbedoOrigin = SlotOrigin.Authored },
        };

        var a = Assert.Single(Candidates(p, Model(Mat("m_a", Base()), Mat("m_b", Base())), texture,
            out var blocked));

        Assert.Equal(new[] { 1 }, a.Submeshes);
        Assert.Empty(blocked);
    }

    [Fact]
    public void An_edit_the_replacement_already_carries_is_neither_adopted_nor_warned()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "skin_d.png", AlbedoOrigin = SlotOrigin.Authored },
        };

        Assert.Empty(Candidates(p, Model(), texture, out var blocked));

        Assert.Empty(blocked);
        Assert.DoesNotContain(Warnings(p, Model()), w => w == ReplacedWarning);
    }

    [Fact]
    public void A_slot_the_replacement_never_rebinds_is_not_an_adoption()
    {
        // A Replace rebinds base colour, normal and RMO and nothing else; the part's other slots keep drawing
        // their game textures, and what stops that edit is the retexture emitter, not the replacement.
        var p = Project();
        AddMesh(p);
        var texture = AddTexture(p, "tex_ramp", "ramp.png");
        var model = Model(Mat("m_body", new SubjectMap("_RampMap", "tex_ramp", "bT")));

        Assert.Empty(Candidates(p, model, texture));

        var warnings = Warnings(p, model);
        Assert.DoesNotContain(warnings, w => w == ReplacedWarning);
        Assert.Contains(warnings, w => w.Contains("_RampMap", StringComparison.Ordinal));
    }

    [Fact]
    public void A_texture_edit_no_replace_touches_derives_its_retexture_and_adopts_nothing()
    {
        var p = Project();
        var texture = AddTexture(p);

        var edits = Derive(p, Model(), out var warnings);

        Assert.Empty(Candidates(p, Model(), texture));
        Assert.Empty(warnings);
        Assert.Equal(EditVerbs.Retexture, Assert.Single(edits).Verb);
    }

    [Fact]
    public void A_part_another_subjects_replace_claimed_still_warns()
    {
        // This build ships one replacement for that mesh, so the second subject's edit has no draw of its own
        // left to land on. The claim spans every selected subject and both readings of it — the build's and
        // the edit-time one — ask ReplaceClaims, so the loser adopts nothing and only the warning speaks.
        var p = Project();
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaAlt" });
        var winner = AddMesh(p);
        AddMesh(p, file: "alt.glb", outfit: "VesnaAlt");
        var winnerTexture = AddTexture(p);
        var loserTexture = AddTexture(p, file: "skin_d.alt.png", outfit: "VesnaAlt");

        Assert.Contains(Warnings(p, Model()), w => w == "'body' is replaced by Vesna · VesnaSSR01. "
            + "Its texture edit is not in this build");

        // the loser's edit has nothing to adopt onto, and nothing to say either — the warning above is the
        // one voice on it
        var loserModel = new SubjectModel("Vesna", "VesnaAlt", SubjectSource.Prefab, Model().Parts,
            Skeleton: null, Problems: Array.Empty<string>());
        Assert.Empty(Candidates(p, loserModel, loserTexture, out var loserBlocked));
        Assert.Empty(loserBlocked);
        // …while the subject holding the claim adopts as it always did
        var won = Assert.Single(Candidates(p, Model(), winnerTexture));
        Assert.Same(winner, won.Mesh);
    }

    [Fact]
    public void A_base_colour_edit_on_the_other_shader_slot_adopts_and_is_not_called_un_emitted()
    {
        // _MainTex is base colour by MaterialResolver's rule, which the map cards, the adoption and the
        // derivation all read. One predicate, one answer: an edit that adopts here is never the derivation's
        // "that slot isn't emitted yet".
        var p = Project();
        AddMesh(p);
        var texture = AddTexture(p);
        var model = Model(Mat("m_body", new SubjectMap("_MainTex", "tex_d", "bT")));

        var a = Assert.Single(Candidates(p, model, texture));
        Assert.Equal(DonorMapSlot.BaseColor, a.Slot);

        // before the adoption the derivation calls it a stray REBOUND edit, never an un-emitted slot
        var before = Warnings(p, model);
        Assert.Contains(before, w => w == ReplacedWarning);
        Assert.DoesNotContain(before, w => w.Contains("_MainTex", StringComparison.Ordinal));

        TextureAdoptions.Apply(p, Candidates(p, model, texture));
        Assert.Empty(Warnings(p, model));
        Assert.Equal("skin_d.png", Assert.Single(Derive(p, model, out _)).Textures!.Single().Albedo);
    }

    [Fact]
    public void A_hidden_parts_texture_edit_is_no_candidate_and_still_warns()
    {
        // A hidden part draws nothing, so its replacement has nothing to carry the map on.
        var p = Project();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        AddMesh(p);
        var texture = AddTexture(p);

        Assert.Empty(Candidates(p, Model(), texture, out var blocked));

        Assert.Empty(blocked);
        Assert.Contains(Warnings(p, Model()),
            w => w == "'body' is hidden. Its texture edit is not in this build");
    }

    [Fact]
    public void The_derivation_warns_for_a_rebound_edit_no_donor_row_carries()
    {
        // The backstop: the derivation writes nothing, so an edit the seam never saw — one made with no
        // subject model in hand — is reported rather than silently dropped.
        var p = Project();
        AddMesh(p);
        AddTexture(p);
        var warnings = new List<string>();

        VerbDerivation.Derive(p, (_, _) => Model(), warnings);

        Assert.Contains(warnings, w => w == ReplacedWarning);
    }

    // ---- what applying one writes ----

    [Fact]
    public void Applying_records_the_workspace_png_as_the_replacements_own_map()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);

        Assert.NotEmpty(TextureAdoptions.Apply(p, Candidates(p, Model(), texture)));

        var row = Assert.Single(mesh.DonorTextures!);
        Assert.Equal(0, row.Submesh);
        Assert.Equal("skin_d.png", row.Albedo);           // a REFERENCE to the file the modder edits
        Assert.Equal(SlotOrigin.Authored, row.AlbedoOrigin);
        // nothing was copied and nothing written: textures/ is untouched
        Assert.False(Directory.Exists(Path.Combine(_root, "textures")));
    }

    [Fact]
    public void An_adopted_slot_is_recorded_in_the_same_form_the_send_back_intake_writes()
    {
        // The intake's own route for a map Blender returned untouched with an edit behind it: it records the
        // workspace PNG as the replacement's own map. The adoption writes the identical file and origin.
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        TextureAdoptions.Apply(p, Candidates(p, Model(), texture));

        var stock = Path.GetFullPath(p.Resolve(texture.ReplaceFile));
        var intake = DonorTextureIntake.Collect(
            new[] { new IncomingMaps(new ResolvedMap(MapOrigin.Vanilla, stock), default) },
            Path.Combine(_root, "textures"), "body",
            abs => Path.GetRelativePath(_root, abs).Replace('\\', '/'),
            ownStockPngs: new HashSet<string> { stock },
            isEditedStock: _ => true);

        var written = Assert.Single(intake!);
        var adopted = Assert.Single(mesh.DonorTextures!);
        Assert.Equal(written.Albedo, adopted.Albedo);
        Assert.Equal(written.AlbedoOrigin, adopted.AlbedoOrigin);
        // The slots the adoption did not touch differ by design: a row it MINTS says the part's own stock
        // maps still draw there, where the intake reports what that send-back's slots actually asked for.
        Assert.Equal(SlotOrigin.VanillaOwn, adopted.NormalOrigin);
        Assert.Equal(SlotOrigin.VanillaOwn, adopted.RmoOrigin);
    }

    [Fact]
    public void Applying_leaves_the_other_slots_of_a_row_the_replacement_already_had()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p, "tex_n", "skin_n.png");
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png", AlbedoOrigin = SlotOrigin.Authored },
        };

        TextureAdoptions.Apply(p, Candidates(p, Model(), texture));

        var row = Assert.Single(mesh.DonorTextures!);
        Assert.Equal("textures/body_s0_base.png", row.Albedo);
        Assert.Equal("skin_n.png", row.Normal);
        Assert.Equal(SlotOrigin.Authored, row.NormalOrigin);
    }

    [Fact]
    public void Applying_lands_on_every_bound_submesh_and_keeps_the_rows_in_submesh_order()
    {
        var p = Project();
        var mesh = AddMesh(p, donorSubmeshes: 3);
        var texture = AddTexture(p);
        mesh.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 2, Rmo = "r.png" } };
        var model = Model(Mat("m_a", Base()), Mat("m_b", Base()), Mat("m_c", Base()));

        TextureAdoptions.Apply(p, Candidates(p, model, texture));

        Assert.Equal(new[] { 0, 1, 2 }, mesh.DonorTextures!.Select(r => r.Submesh).ToArray());
        Assert.All(mesh.DonorTextures!, r => Assert.Equal("skin_d.png", r.Albedo));
        Assert.Equal("r.png", mesh.DonorTextures![2].Rmo);   // the row that was already there keeps its own
    }

    [Fact]
    public void Applying_nothing_reports_nothing_to_persist()
    {
        var p = Project();
        var texture = AddTexture(p);

        Assert.Empty(TextureAdoptions.Apply(p, Candidates(p, Model(), texture)));
    }

    // ---- a run in flight held the candidates, and the project moved under them ----

    [Fact]
    public void A_replacement_that_shrank_since_the_candidates_takes_only_the_submeshes_it_still_has()
    {
        // A build held the adoption back; a Blender send-back landing before the run ended can come back with
        // fewer submeshes. A row past the count is what blocks every later build, so the landing is re-asked
        // where it is written rather than trusted.
        var p = Project();
        var mesh = AddMesh(p, donorSubmeshes: 3);
        var texture = AddTexture(p);
        var held = Candidates(p, Model(Mat("m_a", Base()), Mat("m_b", Base()), Mat("m_c", Base())), texture);
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(held).Submeshes);

        mesh.DonorMaterials = new List<string> { "m0" };   // the send-back that landed in between
        var taken = TextureAdoptions.Apply(p, held);

        Assert.Equal(new[] { 0 }, Assert.Single(taken).Submeshes);
        Assert.Equal(new[] { 0 }, mesh.DonorTextures!.Select(r => r.Submesh).ToArray());
    }

    [Fact]
    public void A_slot_taken_since_the_candidates_is_left_alone_at_apply_time()
    {
        var p = Project();
        var mesh = AddMesh(p, donorSubmeshes: 2);
        var texture = AddTexture(p);
        var held = Candidates(p, Model(Mat("m_a", Base()), Mat("m_b", Base())), texture);

        // a drop landed on submesh 0's base colour while the run held the candidates
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png", AlbedoOrigin = SlotOrigin.Authored },
        };
        var taken = TextureAdoptions.Apply(p, held);

        Assert.Equal(new[] { 1 }, Assert.Single(taken).Submeshes);
        Assert.Equal("textures/body_s0_base.png", mesh.DonorTextures[0].Albedo);
        Assert.Equal("skin_d.png", mesh.DonorTextures[1].Albedo);
    }

    [Fact]
    public void A_part_no_longer_replaced_takes_nothing()
    {
        // A Revert landed while the run held the adoption: there is no replacement to carry the map, and the
        // edit goes back to being the game texture's own.
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        var held = Candidates(p, Model(), texture);
        Assert.Single(held);

        // what the Revert leaves: the pristine glb back in the workspace, matching its original byte for byte
        File.WriteAllText(Path.Combine(_root, "orig_body.glb"), "glb-edited");
        mesh.OriginalFile = "orig_body.glb";

        Assert.Empty(TextureAdoptions.Apply(p, held));
        Assert.Null(mesh.DonorTextures);
    }

    // ---- what ② Edit says ----

    [Fact]
    public void One_adopted_map_names_the_part_and_the_role()
    {
        var p = Project();
        AddMesh(p);
        var texture = AddTexture(p);

        var taken = TextureAdoptions.Apply(p, Candidates(p, Model(), texture));

        Assert.Equal("Adopted as body's replacement Base color map.", TextureAdoptions.Adopted(taken));
    }

    [Fact]
    public void Several_adopted_maps_are_one_line_that_names_each_one()
    {
        // One gesture, one line: a map dressing two parts is still the single thing the modder just did —
        // and the line says WHERE it landed, so it can be checked against the tree without hunting.
        var p = Project();
        AddMesh(p);
        AddMesh(p, file: "hair.glb", mesh: "c_vesna01_hair_lod0");
        var texture = AddTexture(p);
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", new[] { Mat("m_body", Base()) }),
            new SubjectPart("hair", "c_vesna01_hair_lod0", "addr_hair", new[] { Mat("m_hair", Base()) }),
        }, Skeleton: null, Problems: Array.Empty<string>());

        var taken = TextureAdoptions.Apply(p, Candidates(p, model, texture));

        Assert.Equal(2, taken.Count);
        Assert.Equal("Adopted as body's Base color and hair's Base color maps.",
            TextureAdoptions.Adopted(taken));
    }

    [Fact]
    public void Past_the_naming_cap_the_adopted_line_counts_instead()
    {
        // Naming them is only useful while the list can be read at a glance; past that the count is the
        // honest summary and the tree is where the detail lives.
        var p = Project();
        var parts = new List<SubjectPart>();
        foreach (var token in new[] { "body", "hair", "cloth1", "cloth2" })
        {
            AddMesh(p, file: token + ".glb", mesh: "c_vesna01_" + token + "_lod0");
            parts.Add(new SubjectPart(token, "c_vesna01_" + token + "_lod0", "addr_" + token,
                new[] { Mat("m_" + token, Base()) }));
        }
        var texture = AddTexture(p);
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, parts, Skeleton: null,
            Problems: Array.Empty<string>());

        var taken = TextureAdoptions.Apply(p, Candidates(p, model, texture));

        Assert.Equal(4, taken.Count);
        Assert.Equal("Adopted as 4 replacement maps.", TextureAdoptions.Adopted(taken));
    }

    [Fact]
    public void Two_parts_holding_a_slot_the_same_way_state_the_reason_and_the_remedy_once()
    {
        var blocked = new[]
        {
            new AdoptionBlocked("body", "blanks that slot on purpose"),
            new AdoptionBlocked("hair", "blanks that slot on purpose"),
            new AdoptionBlocked("body", "blanks that slot on purpose"),
        };

        Assert.Equal("body's replacement blanks that slot on purpose, so this edit won't show. "
            + "hair's replacement blanks that slot on purpose, so this edit won't show. "
            + "Drop the edited image on the part's map card to use it instead.",
            TextureAdoptions.SlotTaken(blocked));
    }

    [Fact]
    public void Nothing_blocked_says_nothing()
    {
        Assert.Equal("", TextureAdoptions.SlotTaken(Array.Empty<AdoptionBlocked>()));
    }

    // ---- the pass after the adoption ----

    [Fact]
    public void The_pass_after_an_adoption_finds_no_candidate_and_the_build_stops_warning()
    {
        // The adoption consumes exactly what the warning keys on, so the second pass is stable: a candidate
        // surviving it would mean the write didn't settle the question the warning asks.
        var p = Project();
        AddMesh(p, donorSubmeshes: 2);
        var albedo = AddTexture(p);
        var normal = AddTexture(p, "tex_n", "skin_n.png");
        var model = Model(Mat("m_a", Base(), Normal()), Mat("m_b", Base()));
        TextureAdoptions.Apply(p, Candidates(p, model, albedo));
        TextureAdoptions.Apply(p, Candidates(p, model, normal));

        var edits = Derive(p, model, out var warnings);

        Assert.Empty(Candidates(p, model, albedo));
        Assert.Empty(Candidates(p, model, normal));
        Assert.Empty(warnings);
        var replace = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, replace.Verb);
        // and the edits now ship WITH the Replace, which is the whole point
        Assert.Equal(new[] { "skin_d.png", "skin_d.png" }, replace.Textures!.Select(t => t.Albedo).ToArray());
        Assert.Equal("skin_n.png", replace.Textures![0].Normal);
    }

    [Fact]
    public void An_adoption_leaves_another_parts_retexture_of_the_same_edit_alone()
    {
        // The texture edit itself is untouched by the adoption, so a part that is NOT replaced still binds it
        // through its own Retexture.
        var p = Project();
        AddMesh(p);
        var texture = AddTexture(p);
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", new[] { Mat("m_body", Base()) }),
            new SubjectPart("hair", "c_vesna01_hair_lod0", "addr_hair", new[] { Mat("m_hair", Base()) }),
        }, Skeleton: null, Problems: Array.Empty<string>());
        TextureAdoptions.Apply(p, Candidates(p, model, texture));

        var edits = Derive(p, model, out var warnings);

        Assert.Empty(Candidates(p, model, texture));
        Assert.Empty(warnings);
        var retexture = Assert.Single(edits, e => e.Verb == EditVerbs.Retexture);
        Assert.Equal("c_vesna01_hair_lod0", retexture.Mesh);
        Assert.Equal("skin_d.png", retexture.Textures!.Single().Albedo);
    }

    [Fact]
    public void Reverting_the_part_takes_the_adopted_rows_with_the_replacement()
    {
        // The part's Revert clears the donor record, which is what carries an adopted map — no separate
        // teardown, and the edit goes back to being the game texture's own.
        var p = Project();
        var mesh = AddMesh(p);
        File.WriteAllText(Path.Combine(_root, "orig_body.glb"), "glb-original");
        mesh.OriginalFile = "orig_body.glb";
        var texture = AddTexture(p);
        TextureAdoptions.Apply(p, Candidates(p, Model(), texture));

        // what RevertPartAsync does: the original mesh comes back and the donor record goes with the edit
        File.Copy(Path.Combine(_root, "orig_body.glb"), Path.Combine(_root, "body.glb"), overwrite: true);
        mesh.Edited = false;
        mesh.DonorTextures = null;
        mesh.DonorMaterials = null;

        var edits = Derive(p, Model(), out _);

        Assert.Empty(Candidates(p, Model(), texture));
        Assert.Equal(EditVerbs.Retexture, Assert.Single(edits).Verb);
    }

    // ---- taking an adoption back (the texture's own Revert) ----

    [Fact]
    public void Reverting_the_texture_takes_the_adoption_back()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        TextureAdoptions.Apply(p, Candidates(p, Model(), texture));

        // what RevertMapAsync does around the file restore: the flag drops and the adoption is taken back
        Assert.True(TextureAdoptions.CarriesAdoption(new[] { mesh }, texture));
        Assert.Equal(1, TextureAdoptions.Unadopt(new[] { mesh }, texture));

        // the only row asked for nothing else, so it is pruned and the record is null again
        Assert.Null(mesh.DonorTextures);
        Assert.False(TextureAdoptions.CarriesAdoption(new[] { mesh }, texture));
    }

    [Fact]
    public void Unadopt_leaves_other_files_and_deliberate_blanks_alone()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        mesh.DonorTextures = new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0,
                Albedo = texture.ReplaceFile, AlbedoOrigin = SlotOrigin.Authored,      // the adoption
                Normal = "textures/body_s0_n.png", NormalOrigin = SlotOrigin.Authored, // a dropped map
                RmoOrigin = SlotOrigin.ExplicitNeutral,                                // a deliberate blank
            },
        };

        Assert.Equal(1, TextureAdoptions.Unadopt(new[] { mesh }, texture));

        var row = Assert.Single(mesh.DonorTextures!);
        Assert.Equal(SlotOrigin.VanillaOwn, row.AlbedoAsk);    // back to the stock map
        Assert.Equal("textures/body_s0_n.png", row.Normal);    // the drop is not this texture's to take back
        Assert.Equal(SlotOrigin.ExplicitNeutral, row.RmoAsk);  // the blank stands
    }

    [Fact]
    public void Unadopt_of_an_unadopted_texture_changes_nothing()
    {
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);

        Assert.False(TextureAdoptions.CarriesAdoption(new[] { mesh }, texture));
        Assert.Equal(0, TextureAdoptions.Unadopt(new[] { mesh }, texture));
        Assert.Null(mesh.DonorTextures);
    }

    [Fact]
    public void An_unadopted_edit_that_stays_edited_is_offered_again()
    {
        // Unadopt pairs with the texture revert, which drops the edit. Without that pairing the next edit of
        // the same file is adopted again — pinned so the pairing stays deliberate.
        var p = Project();
        var mesh = AddMesh(p);
        var texture = AddTexture(p);
        TextureAdoptions.Apply(p, Candidates(p, Model(), texture));
        TextureAdoptions.Unadopt(new[] { mesh }, texture);

        Assert.Single(Candidates(p, Model(), texture));
    }
}
