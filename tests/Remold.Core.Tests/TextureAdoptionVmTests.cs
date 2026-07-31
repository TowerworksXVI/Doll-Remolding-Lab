using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The wiring around the adoption seam, driven on a live view-model: a run in flight holds an edit and the
/// run's end takes it, an edit made with no subject model in hand is skipped and the model's arrival takes
/// it, and one gesture that both adopts and finds a slot spoken for reports both halves.
/// </summary>
[Collection("Dispatcher")]
public class TextureAdoptionVmTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "gf2-adoptvm-" + Guid.NewGuid().ToString("N"));
    // the autosaves these seams run reach the recent-mods list, which lives in the real settings file
    private readonly Support.SettingsSnapshot _settings = new();

    public TextureAdoptionVmTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _settings.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string Character = "Vesna";
    private const string Stem = "VesnaSSR01";

    private static WorkbenchSubjectRef Subject() =>
        new(Character, Stem, "c_vesna01", new Outfit(0, Stem, OutfitKind.Other));

    private static SubjectMap Base() => new("_BaseMap", "tex_d", "bT");

    private static SubjectMaterial Mat(string name) => new(name, 1, "cab", new[] { Base() });

    private static SubjectPart Part(string token, string slot) =>
        new(token, slot, "addr_" + token, new[] { Mat("m_" + token) });

    private static SubjectModel Model(params SubjectPart[] parts) =>
        new(Character, Stem, SubjectSource.Prefab,
            parts.Length > 0 ? parts : new[] { Part("body", "c_vesna01_body_lod0") },
            Skeleton: null, Problems: Array.Empty<string>());

    /// <summary>A view-model with a mod folder under the temp root and one replaced part binding one edited
    /// texture. The subject model is NOT memoized — each test decides whether the seam has one in hand.</summary>
    private (MainWindowViewModel Vm, ProjectTarget Mesh, ProjectTarget Texture) Pane(int donorSubmeshes = 1)
    {
        var vm = new MainWindowViewModel(startLoad: false);
        // named for the folder it already sits in, so an autosave has no folder to rename
        vm.PackageName = Path.GetFileName(_root);
        var p = vm.OpenProject;
        p.RootDir = _root;
        p.Selection.Add(new SelectionEntry { Character = Character, Outfit = Stem });
        var mesh = AddMesh(p, "c_vesna01_body_lod0", "body.glb", donorSubmeshes);
        File.WriteAllText(Path.Combine(_root, "skin_d.png"), "png");
        var texture = new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bT", ObjectName = "tex_d", ReplaceFile = "skin_d.png",
            SubjectCharacter = Character, SubjectOutfit = Stem,
        };
        p.Targets.Add(texture);
        return (vm, mesh, texture);
    }

    private ProjectTarget AddMesh(ModProject p, string slot, string file, int donorSubmeshes)
    {
        File.WriteAllText(Path.Combine(_root, file), "glb-edited");
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = slot,
            SubjectCharacter = Character, SubjectOutfit = Stem,
            ReplaceFile = file, OriginalFile = null,   // no original on record = edited
            DonorMaterials = Enumerable.Range(0, donorSubmeshes).Select(i => $"m{i}").ToList(),
        };
        p.Targets.Add(t);
        return t;
    }

    private static void Memoize(MainWindowViewModel vm, SubjectModel model) =>
        vm.SubjectModels.GetOrBuild(Character, Stem, () => model);

    // ---- a run in flight holds the edit ----

    [Fact]
    public void A_run_in_flight_holds_the_adoption_and_its_end_takes_it()
    {
        var (vm, mesh, texture) = Pane();
        Memoize(vm, Model());

        vm.IsModBuilding = true;
        Assert.Null(vm.AdoptTextureEdit(texture));   // the run is reading the project; nothing is written
        Assert.Null(mesh.DonorTextures);

        vm.IsModBuilding = false;
        vm.TakeHeldAdoptions();

        var row = Assert.Single(mesh.DonorTextures!);
        Assert.Equal("skin_d.png", row.Albedo);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoOrigin);
        // …and the autosave the flush runs put it on disk, so a reopen finds the same rows
        Assert.Equal("skin_d.png",
            ModProject.Load(_root).Targets.Single(t => t.AssetType == "Mesh").DonorTextures!.Single().Albedo);
    }

    // ---- no model in hand ----

    [Fact]
    public void An_edit_made_with_no_model_in_hand_adopts_nothing_and_says_nothing()
    {
        // The seam PEEKS the model memo: building one on the UI thread costs bundle deobfuscation plus
        // prefab reads with the window frozen behind them. A subject nothing has opened yet has no model,
        // so the edit stays plain.
        var (vm, mesh, texture) = Pane();

        Assert.Null(vm.AdoptTextureEdit(texture));

        Assert.Null(mesh.DonorTextures);
        Assert.Equal(0, vm.SubjectModels.Count);
    }

    [Fact]
    public void The_model_arriving_takes_the_edit_the_cold_cache_skipped()
    {
        var (vm, mesh, texture) = Pane();
        vm.AdoptTextureEdit(texture);
        Assert.Null(mesh.DonorTextures);

        // what the Edit tree does when its off-thread build lands
        var line = vm.AdoptSubjectTextureEdits(Subject(), Model());

        Assert.Equal("skin_d.png", Assert.Single(mesh.DonorTextures!).Albedo);
        // the sweep hands the line back; the tree build owns the pane's one line
        Assert.Equal("Adopted as body's replacement Base color map.", line);
    }

    [Fact]
    public void A_voided_donor_record_takes_the_submesh_count_with_it_so_the_sweep_finds_nowhere_to_land()
    {
        // A send that overwrote the glb with bytes that won't read voids the donor record the previous mesh
        // left, and the adopted maps go with it. The same wipe takes the MATERIAL list, which is what says
        // how many submeshes the replacement has — so there is no row for a map to land on. The sweep reads
        // the PROJECT and stops there by choice: the count is recoverable from the glb on disk, but paying a
        // mesh read on the UI thread to recover it is exactly what this seam is shaped to avoid, and a send
        // that reads puts the list back anyway. The build says the one thing that fixes it.
        var (vm, mesh, texture) = Pane();
        Memoize(vm, Model());
        vm.AdoptTextureEdit(texture);
        Assert.NotNull(mesh.DonorTextures);

        vm.OpenProject.MarkFileReplaced(Path.Combine(_root, "body.glb"));
        Assert.Null(mesh.DonorTextures);
        Assert.Null(mesh.DonorMaterials);

        vm.AdoptSubjectTextureEdits(Subject(), Model());

        Assert.Null(mesh.DonorTextures);
        var warnings = new List<string>();
        VerbDerivation.DeriveAll(vm.OpenProject, (_, _) => Model(), warnings);
        Assert.Contains(warnings, w => w == "'body' is replaced. This map dresses submeshes body's "
            + "replacement doesn't have. Send body back from Blender to add them");
    }

    [Fact]
    public void A_record_wiped_with_its_material_list_intact_is_adopted_again_when_the_model_lands()
    {
        // A send-back that DID read restores the material list along with the record, so the sweep has a
        // shape to author against and the adopted maps come back with the next tree build.
        var (vm, mesh, texture) = Pane();
        Memoize(vm, Model());
        vm.AdoptTextureEdit(texture);
        Assert.NotNull(mesh.DonorTextures);

        mesh.DonorTextures = null;   // what the wipe leaves once the send's own materials are recorded

        vm.AdoptSubjectTextureEdits(Subject(), Model());

        Assert.Equal("skin_d.png", Assert.Single(mesh.DonorTextures!).Albedo);
    }

    [Fact]
    public void A_sweep_with_nothing_left_to_take_says_nothing()
    {
        var (vm, _, texture) = Pane();
        Memoize(vm, Model());
        vm.AdoptTextureEdit(texture);

        Assert.Null(vm.AdoptSubjectTextureEdits(Subject(), Model()));
    }

    // ---- both halves of one gesture ----

    [Fact]
    public void One_edit_that_adopts_on_one_part_and_is_held_on_another_reports_both()
    {
        // A shared atlas: the map dresses two replaced parts, and the modder already dropped a map on one of
        // them. Announcing the adoption alone would report a partial landing as plain success.
        var (vm, body, texture) = Pane();
        var hair = AddMesh(vm.OpenProject, "c_vesna01_hair_lod0", "hair.glb", donorSubmeshes: 1);
        hair.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/hair_s0_base.png", AlbedoOrigin = SlotOrigin.Authored },
        };
        Memoize(vm, Model(Part("body", "c_vesna01_body_lod0"), Part("hair", "c_vesna01_hair_lod0")));

        var line = vm.AdoptTextureEdit(texture);

        Assert.Equal("Adopted as body's replacement Base color map. "
            + "hair's replacement already carries its own Base color map, so this edit won't show. "
            + "Drop the edited image on the part's map card to use it instead.", line);
        Assert.Equal("skin_d.png", Assert.Single(body.DonorTextures!).Albedo);
        Assert.Equal("textures/hair_s0_base.png", Assert.Single(hair.DonorTextures!).Albedo);
    }
}
