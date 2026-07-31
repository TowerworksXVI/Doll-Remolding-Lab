using System.Linq;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The first-touch materialization seam. The live game read inside it is a live-only route; these
/// pin the idempotency guards, the materialized-parts derivation, and the texture target's shape.</summary>
public class MaterializerTests
{
    private static ProjectTarget MeshTarget(string sub, string obj) => new()
    {
        AssetType = "Mesh", Bundle = "aa", ObjectName = obj, ReplaceFile = $"{sub}/meshes/x.glb",
    };

    [Fact]
    public void MaterializedParts_DerivesTokens_FromMeshTargets_InTheSubjectFolder()
    {
        var proj = new ModProject();
        proj.Targets.Add(MeshTarget("karst_karstssr01", "c_KarstSSR01_slg_cloth1_lod0"));
        proj.Targets.Add(MeshTarget("karst_karstssr01", "c_KarstSSR01_slg_hair_lod0"));
        // a DIFFERENT outfit's mesh (must not leak into KarstSSR01's set)
        proj.Targets.Add(MeshTarget("karst_karstdorm", "c_KarstDorm_slg_cloth1_lod0"));
        // a texture target (never counts as a materialized PART)
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", ObjectName = "c_KarstSSR01_slg_cloth1_d",
            ReplaceFile = "textures/c_KarstSSR01_slg_cloth1_d.png" });

        var parts = Materializer.MaterializedParts(proj, "Karst", "KarstSSR01", "c_KarstSSR01_slg_");
        Assert.Equal(new[] { "cloth1", "hair" }, parts.OrderBy(p => p).ToArray());
        Assert.True(Materializer.IsPartMaterialized(proj, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", "cloth1"));
        Assert.False(Materializer.IsPartMaterialized(proj, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", "cloth2"));
    }

    [Fact]
    public void ForeignPrefixedMeshTarget_ReadsAsTheShortPartToken_TheTreeAndRecipeName()
    {
        // A dorm skin reusing the base family's hair: the target's object name carries c_SableSSR01_slg_,
        // which the subject's own prefix cannot strip. The tree, the roster and the recipe all call this
        // part "hair", so the ledger has to as well or the part is invisible to every token lookup.
        var proj = new ModProject();
        proj.Targets.Add(MeshTarget("sable_sabledorm", "c_SableSSR01_slg_hair_lod0"));

        var parts = Materializer.MaterializedParts(proj, "Sable", "SableDorm", "c_SableDorm_slg_");

        Assert.Equal(new[] { "hair" }, parts.ToArray());
        Assert.True(Materializer.IsPartMaterialized(proj, "Sable", "SableDorm", "c_SableDorm_slg_", "hair"));
        Assert.Equal("c_SableSSR01_slg_hair_lod0",
            Materializer.PartMeshTarget(proj, "Sable", "SableDorm", "c_SableDorm_slg_", "hair")?.ObjectName);
    }

    [Fact]
    public void OwnMeshWinsTheShortToken_AndTheForeignOneKeepsItsFullName()
    {
        // Both families ship a "hair": merging them into one part would lose one, so the subject's OWN mesh
        // keeps the short token and the foreign one lists under its full name.
        var proj = new ModProject();
        proj.Targets.Add(MeshTarget("sable_sabledorm", "c_SableSSR01_slg_hair_lod0"));
        proj.Targets.Add(MeshTarget("sable_sabledorm", "c_SableDorm_slg_hair_lod0"));

        Assert.Equal("c_SableDorm_slg_hair_lod0",
            Materializer.PartMeshTarget(proj, "Sable", "SableDorm", "c_SableDorm_slg_", "hair")?.ObjectName);
        Assert.Equal("c_SableSSR01_slg_hair_lod0",
            Materializer.PartMeshTarget(proj, "Sable", "SableDorm", "c_SableDorm_slg_", "c_SableSSR01_slg_hair")?.ObjectName);
    }

    [Fact]
    public void TwoForeignFamiliesContestingOneToken_AnswerForNeither()
    {
        // A subject carrying two props from different families, both parsing to the part-less token under
        // their own prefix.
        // Which one wins the short token is settled by the order the names are read, and the tree reads
        // them in prefab slot order, which a target list cannot reproduce. Answering anyway would open the
        // scabbard under the sword's name; an unanswered part re-prepares and reports for itself.
        var proj = new ModProject();
        proj.Targets.Add(MeshTarget("sable_sablessr01", "c_SableSSR01_sword_slg_lod0"));
        proj.Targets.Add(MeshTarget("sable_sablessr01", "c_SableSSR01_scabbard_slg_lod0"));

        Assert.Null(Materializer.PartMeshTarget(proj, "Sable", "SableSSR01", "c_SableSSR01_slg_",
            SubjectModelBuilder.PartlessToken));
        Assert.Empty(Materializer.MaterializedParts(proj, "Sable", "SableSSR01", "c_SableSSR01_slg_"));
    }

    [Fact]
    public void SubjectFolder_MatchesTheAddWorkerLayout()
    {
        Assert.Equal("karst_KarstSSR01".ToLowerInvariant(),
            Materializer.SubjectFolder("Karst", "KarstSSR01").ToLowerInvariant());
        // the slug is the character half; the stem is preserved verbatim
        Assert.Equal("karst_KarstSSR01", Materializer.SubjectFolder("Karst", "KarstSSR01"));
    }

    [Fact]
    public void MaterializePart_AlreadyMaterialized_IsANoOp_ReturningTheExistingTarget()
    {
        var proj = new ModProject();
        var mesh = MeshTarget("karst_karstssr01", "c_KarstSSR01_slg_cloth1_lod0");
        proj.Targets.Add(mesh);
        var outfit = new Model.Outfit(0, "KarstSSR01", Model.OutfitKind.Other);

        // the guard short-circuits before the live export, so the unused vfs/gameDir/recipe are safe
        var recipe = new Export.RecipePart("cloth1", "c_KarstSSR01_slg_cloth1_lod0", "addr",
            System.Array.Empty<Export.RecipeTierSlot>());
        var r = Materializer.MaterializePart(proj, null!, "", outfit, "Karst", "/mod", recipe);

        Assert.Equal(MaterializeOutcome.AlreadyPresent, r.Outcome);
        Assert.Same(mesh, Assert.Single(r.Targets));
        Assert.Single(proj.Targets);   // nothing added
    }

    [Fact]
    public void CommitPart_ReChecksIdempotency_WhenAPeerCommittedFirst()
    {
        // Prepare runs off-thread, so two queued materializes can both pass the shell's outer guard before
        // either commits. CommitPart re-checks, making the second a no-op rather than a duplicate target.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };
        proj.Targets.Add(MeshTarget("karst_karstssr01", "c_KarstSSR01_slg_cloth1_lod0"));   // a peer already committed cloth1

        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", true, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth1");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth1");

        var r = Materializer.CommitPart(proj, prep);
        Assert.Equal(MaterializeOutcome.AlreadyPresent, r.Outcome);
        Assert.Single(proj.Targets);   // nothing added
    }

    [Fact]
    public void CommitPart_FreshPart_AddsTheMeshTarget_AndReportsCreated()
    {
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };
        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", true, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth1");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth1");

        var r = Materializer.CommitPart(proj, prep);
        Assert.Equal(MaterializeOutcome.Created, r.Outcome);
        Assert.Contains(r.Targets, t => t.AssetType == "Mesh");
        Assert.True(Materializer.IsPartMaterialized(proj, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", "cloth1"));
    }

    [Fact]
    public void CommitPart_ExportRanCleanButNoTargetCarriesTheToken_SaysSo_NotAReadFailure()
    {
        // The export succeeded, but the mesh's object name derives a DIFFERENT part token, so PartTargets
        // matches nothing. Blaming the game files would be a lie — the export carries no mesh for the token.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };
        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth2_lod0_Fight",
            root + "/karst_karstssr01/meshes/cloth2.glb", true, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth2");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth2");

        var r = Materializer.CommitPart(proj, prep);
        Assert.Equal(MaterializeOutcome.Failed, r.Outcome);
        Assert.Contains("cloth2", r.Error);
        Assert.DoesNotContain("from the game files", r.Error);   // the export was fine; the token wasn't
    }

    [Fact]
    public void CommitPart_MeshCouldNotBeRead_StillReportsTheReadFailure()
    {
        // the other half of the split: the export STAGED a failed mesh, so the read-failure wording stands
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };
        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", false, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth1");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth1");

        var r = Materializer.CommitPart(proj, prep);
        Assert.Equal(MaterializeOutcome.Failed, r.Outcome);
        Assert.Contains("from the game files", r.Error);
    }

    [Fact]
    public void CommitPartIfSelected_SubjectRemovedMidPrepare_DiscardsWork_AddsNoTargets()
    {
        // A part prepared off-thread, then the subject unchecked before the commit hop ran — the guarded
        // commit must NOT resurrect the removed subject's mesh target.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };   // NOTE: no SelectionEntry — the subject was removed
        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", true, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth1");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth1");

        Assert.Null(Materializer.CommitPartIfSelected(proj, prep));   // discarded — subject gone
        Assert.Empty(proj.Targets);                                   // nothing resurrected

        // once the subject IS selected, the same prep commits normally
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        var r = Materializer.CommitPartIfSelected(proj, prep);
        Assert.NotNull(r);
        Assert.Equal(MaterializeOutcome.Created, r!.Outcome);
        Assert.Contains(proj.Targets, t => t.AssetType == "Mesh");
    }

    [Fact]
    public void CommitTextureIfSelected_SubjectRemovedMidPrepare_DiscardsWork_AddsNoTargets()
    {
        // Same guard at the map grain. The triggering subject is passed explicitly — a texture prep carries
        // none of its own.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };   // no SelectionEntry
        var report = new ExportReport { OutputDir = root };
        report.Files.Add(new ExportedFile("texture", "c_KarstSSR01_slg_cloth1_d",
            root + "/textures/c_KarstSSR01_slg_cloth1_d.png", true, null, Bundle: "tt", Source: Materializer.MaterialSource));
        var prep = new TextureMaterializePrep(report, root, "c_KarstSSR01_slg_cloth1_d", "tt", "Karst", "KarstSSR01");

        Assert.Null(Materializer.CommitTextureIfSelected(proj, prep, "Karst", "KarstSSR01"));
        Assert.Empty(proj.Targets);

        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        var r = Materializer.CommitTextureIfSelected(proj, prep, "Karst", "KarstSSR01");
        Assert.NotNull(r);
        Assert.Contains(proj.Targets, t => t.AssetType == "Texture2D");
    }

    [Fact]
    public void CommitIfCurrentProject_ProjectSwitchedMidPrepare_DiscardsPartCommit_LeavingBothProjectsUntouched()
    {
        // Prepared off-thread against project A, then New Mod / Open swapped to B before the commit hop. The
        // guard must NOT commit into A even when B selects the SAME subject — reference identity, not value.
        const string root = "/proj";
        var captured = new ModProject { RootDir = root };
        captured.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });   // still selected on A
        var report = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        report.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", true, null, Bundle: "aa"));
        report.CompletedParts.Add("cloth1");
        var prep = new PartMaterializePrep(report, "Karst", "KarstSSR01", "c_KarstSSR01_slg_", root, "cloth1");

        // the app is now on a DIFFERENT project object that ALSO selects the same subject
        var current = new ModProject { RootDir = "/other" };
        current.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });

        var r = Materializer.CommitIfCurrentProject(captured, current, () => Materializer.CommitPartIfSelected(captured, prep));
        Assert.Null(r);                    // discarded — the project switched
        Assert.Empty(captured.Targets);    // the abandoned project was not mutated
        Assert.Empty(current.Targets);     // nor the new one

        // when the app is still on the captured project, the same commit lands normally
        var ok = Materializer.CommitIfCurrentProject(captured, captured, () => Materializer.CommitPartIfSelected(captured, prep));
        Assert.NotNull(ok);
        Assert.Equal(MaterializeOutcome.Created, ok!.Outcome);
        Assert.Contains(captured.Targets, t => t.AssetType == "Mesh");
    }

    [Fact]
    public void CommitIfCurrentProject_ProjectSwitchedMidPrepare_DiscardsTextureCommit()
    {
        // The same project-identity guard for the map grain.
        const string root = "/proj";
        var captured = new ModProject { RootDir = root };
        captured.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        var report = new ExportReport { OutputDir = root };
        report.Files.Add(new ExportedFile("texture", "c_KarstSSR01_slg_cloth1_d",
            root + "/textures/c_KarstSSR01_slg_cloth1_d.png", true, null, Bundle: "tt", Source: Materializer.MaterialSource));
        var prep = new TextureMaterializePrep(report, root, "c_KarstSSR01_slg_cloth1_d", "tt", "Karst", "KarstSSR01");

        var current = new ModProject { RootDir = "/other" };
        current.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });

        var r = Materializer.CommitIfCurrentProject(captured, current,
            () => Materializer.CommitTextureIfSelected(captured, prep, "Karst", "KarstSSR01"));
        Assert.Null(r);
        Assert.Empty(captured.Targets);
        Assert.Empty(current.Targets);

        var ok = Materializer.CommitIfCurrentProject(captured, captured,
            () => Materializer.CommitTextureIfSelected(captured, prep, "Karst", "KarstSSR01"));
        Assert.NotNull(ok);
        Assert.Contains(captured.Targets, t => t.AssetType == "Texture2D");
    }

    [Fact]
    public void CommitTexture_ReChecksIdempotency_WhenAPeerCommittedFirst()
    {
        // Same idempotency re-check at the map grain — a peer materialize may have landed it first.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", Bundle = "tt", ObjectName = "c_KarstSSR01_slg_cloth1_d",
            ReplaceFile = "textures/c_KarstSSR01_slg_cloth1_d.png", Source = "material",
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });

        var report = new ExportReport { OutputDir = root };
        report.Files.Add(new ExportedFile("texture", "c_KarstSSR01_slg_cloth1_d",
            root + "/textures/c_KarstSSR01_slg_cloth1_d.png", true, null, Bundle: "tt", Source: Materializer.MaterialSource));
        var prep = new TextureMaterializePrep(report, root, "c_KarstSSR01_slg_cloth1_d", "tt", "Karst", "KarstSSR01");

        var r = Materializer.CommitTexture(proj, prep);
        Assert.Equal(MaterializeOutcome.AlreadyPresent, r.Outcome);
        Assert.Single(proj.Targets);
    }

    [Fact]
    public void MaterializeTexture_AlreadyMaterialized_IsANoOp_ReturningTheExistingTarget()
    {
        var proj = new ModProject();
        var tex = new ProjectTarget { AssetType = "Texture2D", Bundle = "aa", ObjectName = "c_KarstSSR01_slg_cloth1_d",
            ReplaceFile = "textures/c_KarstSSR01_slg_cloth1_d.png", Source = "material",
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" };
        proj.Targets.Add(tex);

        // guard short-circuits before the deobfuscate/encode, so the null! vfs is safe
        var r = Materializer.MaterializeTexture(proj, null!, "/mod", "c_KarstSSR01_slg_cloth1_d", "aa",
            new[] { "c_KarstSSR01_slg_cloth1_lod0" }, "Karst", "KarstSSR01");

        Assert.Equal(MaterializeOutcome.AlreadyPresent, r.Outcome);
        Assert.Same(tex, Assert.Single(r.Targets));
        Assert.Single(proj.Targets);
    }

    [Fact]
    public void MergeTextureUsers_AddsOwnersOntoTheSubjectsOwnTarget_LeavingThePeerSubjectsUntouched()
    {
        // One game texture worn by two outfits is TWO targets — each subject materializes its own workspace
        // file, so an edit belongs to the outfit it was made on. A merge lands on the SUBJECT'S target only,
        // and removing one subject drops that subject's copy while the peer's map stands.
        var proj = new ModProject { RootDir = "/proj" };
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR02" });
        // A's body mesh + A's own copy of the body texture.
        proj.Targets.Add(new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = "c_KarstSSR01_slg_body_lod0",
            ReplaceFile = "karst_KarstSSR01/meshes/body.glb", SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", Bundle = "bb", ObjectName = "body_skin",
            ReplaceFile = "textures/body_skin.bb.karst_KarstSSR01.png", Users = new() { "c_KarstSSR01_slg_body_lod0" },
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });
        // B's body mesh + B's OWN copy of the same game texture.
        proj.Targets.Add(new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = "c_KarstSSR02_slg_body_lod0",
            ReplaceFile = "karst_KarstSSR02/meshes/body.glb", SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR02" });
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", Bundle = "bb", ObjectName = "body_skin",
            ReplaceFile = "textures/body_skin.bb.karst_KarstSSR02.png", Users = new() { "c_KarstSSR02_slg_body_lod0" },
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR02" });

        // a second B mesh binding the same map merges onto B's target (the exact call the VM makes)
        Assert.True(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR02", "bb", "body_skin",
            new[] { "c_KarstSSR02_slg_face_lod0" }));
        Assert.Equal(new[] { "c_KarstSSR02_slg_body_lod0", "c_KarstSSR02_slg_face_lod0" },
            Materializer.TextureTarget(proj, "Karst", "KarstSSR02", "bb", "body_skin")!.Users!.ToArray());
        Assert.Equal(new[] { "c_KarstSSR01_slg_body_lod0" },
            Materializer.TextureTarget(proj, "Karst", "KarstSSR01", "bb", "body_skin")!.Users!.ToArray());
        Assert.False(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR02", "bb", "body_skin",
            new[] { "c_KarstSSR02_slg_face_lod0" }));   // idempotent

        // removing A drops A's copy; B's own map is untouched
        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", "c_KarstSSR01_slg_");
        var tex = Assert.Single(proj.Targets, t => t.AssetType == "Texture2D");
        Assert.Equal("KarstSSR02", tex.SubjectOutfit);

        // removing B afterwards drops the last copy
        SubjectRemoval.Remove(proj, "Karst", "KarstSSR02", "c_KarstSSR02_slg_");
        Assert.DoesNotContain(proj.Targets, t => t.AssetType == "Texture2D");
    }

    [Fact]
    public void MergeTextureUsers_IntoNullUsers_CreatesTheList_MatchingProjectBuilder()
    {
        // A non-empty merge PROVES ownership, so it creates the list; an empty merge leaves null untouched
        // and a missing target no-ops.
        var proj = new ModProject { RootDir = "/proj" };
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", Bundle = "bb", ObjectName = "mystery",
            ReplaceFile = "textures/mystery.bb.karst_KarstSSR01.png", Users = null,
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });

        Assert.True(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR01", "bb", "mystery", new[] { "c_X_slg_body_lod0" }));
        Assert.Equal(new[] { "c_X_slg_body_lod0" }, proj.Targets.Single().Users!.ToArray());

        // an empty merge leaves a null-Users target null; a missing texture is a no-op
        proj.Targets.Add(new ProjectTarget { AssetType = "Texture2D", Bundle = "cc", ObjectName = "other",
            ReplaceFile = "textures/other.cc.karst_KarstSSR01.png", Users = null,
            SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });
        Assert.False(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR01", "cc", "other", System.Array.Empty<string>()));
        Assert.Null(proj.Targets.Single(t => t.ObjectName == "other").Users);
        Assert.False(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR01", "zz", "nope", new[] { "x" }));
        // ...and a merge aimed at a DIFFERENT subject finds no target of its own
        Assert.False(Materializer.MergeTextureUsers(proj, "Karst", "KarstSSR02", "bb", "mystery", new[] { "c_X_slg_body_lod0" }));
    }

    [Fact]
    public void MaterializeTexture_ThenAddOfTheSamePart_DoesNotDuplicateTheTexture()
    {
        // A map first-touch lands textures/<name>.png; a later Add of the owning part re-reports the same
        // replace file and must dedupe against it rather than ship two.
        const string root = "/proj";
        var proj = new ModProject { RootDir = root };

        var mapTouch = new ExportReport { OutputDir = root };
        mapTouch.Files.Add(new ExportedFile("texture", "c_KarstSSR01_slg_cloth1_d",
            root + "/textures/c_KarstSSR01_slg_cloth1_d.png", true, null, Bundle: "tt",
            Users: new[] { "c_KarstSSR01_slg_cloth1_lod0" }, Source: Materializer.MaterialSource));
        ProjectBuilder.AddExport(proj, mapTouch, root, "Karst", "KarstSSR01");
        Assert.Single(proj.Targets);

        // now the part is materialized mesh-grain and re-reports the same shared texture at the same path
        var partAdd = new ExportReport { OutputDir = root + "/karst_karstssr01" };
        partAdd.Files.Add(new ExportedFile("mesh", "c_KarstSSR01_slg_cloth1_lod0",
            root + "/karst_karstssr01/meshes/cloth1.glb", true, null, Bundle: "aa"));
        partAdd.Files.Add(new ExportedFile("texture", "c_KarstSSR01_slg_cloth1_d",
            root + "/textures/c_KarstSSR01_slg_cloth1_d.png", true, null, Bundle: "tt",
            Users: new[] { "c_KarstSSR01_slg_cloth1_lod0" }, Source: "renderer"));
        ProjectBuilder.AddExport(proj, partAdd, root, "Karst", "KarstSSR01");

        Assert.Equal(2, proj.Targets.Count);   // the one texture + the new mesh — no duplicate texture
        Assert.Single(proj.Targets, t => t.AssetType == "Texture2D");
    }
}
