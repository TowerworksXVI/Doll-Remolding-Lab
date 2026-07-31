using System;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The subject-scoped remove behind both the Pick uncheck and the Edit subject header. Pins the
/// target/ledger/file effects, the solely-owned-vs-shared texture rule, and the confirm-decision
/// inputs.</summary>
public class SubjectRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-subjrm-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Write(string rel, string content)
    {
        var abs = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        return rel;
    }

    private string Touch(string rel) => Write(rel, "x");

    private ModProject NewProject() => new() { RootDir = _root };

    // An "edited" target differs from its original byte-for-byte, since ModProject.IsEdited is the
    // authoritative check. The mesh carries its PERSISTED subject identity — the sole ownership key the
    // remove uses — laid out under the subject folder as the real materialize does.
    private ProjectTarget MeshTarget(string character, string stem, string obj, bool edited = false)
    {
        var sub = Materializer.SubjectFolder(character, stem);
        var rel = $"{sub}/meshes/{obj}.glb";
        var orig = $"{sub}/originals/{obj}.glb";
        Write(rel, edited ? "modified" : "pristine"); Write(orig, "pristine");
        return new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = obj, ReplaceFile = rel, OriginalFile = orig,
            Edited = edited, SubjectCharacter = character, SubjectOutfit = stem };
    }

    private ProjectTarget TextureTarget(string name, string[] users, bool edited = false)
    {
        var rel = $"textures/{name}.png";
        var orig = $"originals/{name}.png";
        Write(rel, edited ? "modified" : "pristine"); Write(orig, "pristine");
        return new ProjectTarget { AssetType = "Texture2D", Bundle = "bb", ObjectName = name, ReplaceFile = rel, OriginalFile = orig, Users = users.ToList(), Edited = edited };
    }

    private const string Prefix = "c_KarstSSR01_slg_";

    [Fact]
    public void Remove_DropsSubjectMeshes_SolelyOwnedTexture_Originals_Files_AndLedgerRow()
    {
        var proj = NewProject();
        var sub = Materializer.SubjectFolder("Karst", "KarstSSR01");
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        var mesh = MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_cloth1_lod0", edited: true);
        proj.Targets.Add(mesh);
        // a texture whose only user is this subject's mesh → solely its → dropped
        var tex = TextureTarget("c_KarstSSR01_slg_cloth1_d", new[] { "c_KarstSSR01_slg_cloth1_lod0" }, edited: true);
        proj.Targets.Add(tex);

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        Assert.Empty(proj.Targets);                       // mesh + solely-owned texture both gone
        Assert.Empty(proj.Selection);                     // ledger row gone
        Assert.False(File.Exists(Path.Combine(_root, mesh.ReplaceFile)));
        Assert.False(File.Exists(Path.Combine(_root, mesh.OriginalFile!)));
        Assert.False(File.Exists(Path.Combine(_root, tex.ReplaceFile)));
        Assert.False(File.Exists(Path.Combine(_root, tex.OriginalFile!)));
        Assert.False(Directory.Exists(Path.Combine(_root, sub)));   // subject folder swept
    }

    [Fact]
    public void Remove_DropsPerMeshBuildState_SoAReAddStartsClean()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        proj.SetHidden("Karst", "KarstSSR01", Prefix + "cloth1_lod0", true);
        proj.SetBuildExcluded("Karst", "KarstSSR01", Prefix + "cloth1_lod0", EditVerbs.Hide, true);
        // another subject's state is untouched
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR02" });
        proj.SetBuildExcluded("Karst", "KarstSSR02", Prefix + "cloth1_lod0", EditVerbs.Replace, true);

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        Assert.Empty(proj.Hidden);
        Assert.Equal("KarstSSR02", Assert.Single(proj.BuildExcluded).Outfit);
    }

    [Fact]
    public void Remove_SharedTexture_SurvivesForTheOtherSubject_WithThisSubjectDroppedFromUsers()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        proj.Targets.Add(MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_body_lod0"));
        // a body texture bound by BOTH KarstSSR01 and KarstSSR02 — not solely this subject's
        var shared = TextureTarget("body_skin", new[] { "c_KarstSSR01_slg_body_lod0", "c_KarstSSR02_slg_body_lod0" });
        proj.Targets.Add(shared);

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        var survivor = Assert.Single(proj.Targets);       // the shared texture survives
        Assert.Equal("body_skin", survivor.ObjectName);
        Assert.Equal(new[] { "c_KarstSSR02_slg_body_lod0" }, survivor.Users!.ToArray());   // KarstSSR01 dropped from users
        Assert.True(File.Exists(Path.Combine(_root, shared.ReplaceFile)));                 // file kept
    }

    [Fact]
    public void Remove_TextureWithUntrackedUsers_IsKept_UnprovenOwnership()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        proj.Targets.Add(MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_cloth1_lod0"));
        var tex = new ProjectTarget { AssetType = "Texture2D", ObjectName = "mystery", ReplaceFile = "textures/mystery.png", Users = null };
        Touch(tex.ReplaceFile);
        proj.Targets.Add(tex);

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        Assert.Same(tex, Assert.Single(proj.Targets));    // null-Users texture is never dropped
    }

    [Fact]
    public void Remove_ImportedSubject_OwnedByStampedIdentity_NotByFolder_DropsMeshesAndLedger()
    {
        // An imported mod's payloads live under imported/, NOT the subject folder, so folder-based ownership
        // misses them and unchecking is a silent no-op with mesh targets still shipping. Ownership keys on
        // the persisted (character, stem) the reconstruction stamped instead.
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        // reconstructed-import shape: ReplaceFile under imported/, Edited=true, identity stamped (no OriginalFile).
        Touch("imported/cloth1.glb"); Touch("imported/hair.glb");
        proj.Targets.Add(new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = "c_KarstSSR01_slg_cloth1_lod0",
            ReplaceFile = "imported/cloth1.glb", Edited = true, SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });
        proj.Targets.Add(new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = "c_KarstSSR01_slg_hair_lod0",
            ReplaceFile = "imported/hair.glb", Edited = true, SubjectCharacter = "Karst", SubjectOutfit = "KarstSSR01" });

        Assert.True(SubjectRemoval.HasMaterializedContent(proj, "Karst", "KarstSSR01", Prefix));   // owned by identity

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        Assert.Empty(proj.Targets);     // both imported meshes dropped — nothing of the subject ships
        Assert.Empty(proj.Selection);   // ledger row gone
    }

    [Fact]
    public void Remove_UnstampedMeshTarget_BelongsToNoSubject_IsNotDropped()
    {
        // A mesh with no stamped identity belongs to NO subject: removal never guesses one from a workspace
        // path, so it survives an unrelated remove.
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        Touch("loose/thing.glb");
        var loose = new ProjectTarget { AssetType = "Mesh", Bundle = "aa", ObjectName = "c_KarstSSR01_slg_cloth1_lod0",
            ReplaceFile = "loose/thing.glb" };   // NO SubjectCharacter/SubjectOutfit
        proj.Targets.Add(loose);

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        Assert.Same(loose, Assert.Single(proj.Targets));   // unowned target untouched
    }

    [Fact]
    public void HasMaterializedContent_TrueWithTargets_FalseForSelectionOnly()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        Assert.False(SubjectRemoval.HasMaterializedContent(proj, "Karst", "KarstSSR01", Prefix));   // checkbox only

        proj.Targets.Add(MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_cloth1_lod0"));
        Assert.True(SubjectRemoval.HasMaterializedContent(proj, "Karst", "KarstSSR01", Prefix));
    }

    [Fact]
    public void EditedFileCount_CountsEditedMeshes_AndSolelyOwnedEditedTextures_NotUnedited()
    {
        var proj = NewProject();
        proj.Targets.Add(MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_cloth1_lod0", edited: true));    // counts
        proj.Targets.Add(MeshTarget("Karst", "KarstSSR01", "c_KarstSSR01_slg_hair_lod0", edited: false));     // materialized-but-unedited: not counted
        proj.Targets.Add(TextureTarget("c_KarstSSR01_slg_cloth1_d", new[] { "c_KarstSSR01_slg_cloth1_lod0" }, edited: true));   // counts (solely its)
        proj.Targets.Add(TextureTarget("shared", new[] { "c_KarstSSR01_slg_cloth1_lod0", "c_KarstSSR02_slg_body_lod0" }, edited: true)); // not solely its → not counted

        Assert.Equal(2, SubjectRemoval.EditedFileCount(proj, "Karst", "KarstSSR01", Prefix));
    }

    [Fact]
    public void Remove_SelectionOnlySubject_DropsLedgerRow_TouchesNothingElse()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" });
        proj.Selection.Add(new SelectionEntry { Character = "Wren", Outfit = "WrenSSR01" });

        SubjectRemoval.Remove(proj, "Karst", "KarstSSR01", Prefix);

        var remaining = Assert.Single(proj.Selection);
        Assert.Equal("Wren", remaining.Character);       // the other subject is untouched
    }

    // ---- cross-prefix (recovered-part) texture ownership ----------------------------------------------
    // An alt outfit reusing the BASE outfit's face: the alt's face mesh target carries the BASE recipe-slot
    // name, which does NOT match the alt's own prefix.
    private const string AltStem = "VeliraSSR0101", AltPrefix = "c_VeliraSSR0101_slg_";
    private const string BaseStem = "VeliraSSR01",  BasePrefix = "c_VeliraSSR01_slg_";
    private const string FaceUser = "c_VeliraSSR01_slg_face_lod0";
    // roster resolver the app supplies: the conventional prefix for these conventional outfits.
    private static string? Resolve(string character, string stem) => $"c_{stem}_slg_";

    [Fact]
    public void Remove_Alt_DropsRecoveredFaceTexture_WhenAltIsSoleOwner()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Velira", Outfit = AltStem });
        proj.Targets.Add(MeshTarget("Velira", AltStem, "c_VeliraSSR0101_slg_body_lod0"));
        proj.Targets.Add(MeshTarget("Velira", AltStem, FaceUser));            // the recovered base face — the alt owns it by identity
        // the face texture's only user is the recovered-face mesh name — a prefix test would MISS it.
        var face = TextureTarget("face_d", new[] { FaceUser }, edited: true);
        proj.Targets.Add(face);

        SubjectRemoval.Remove(proj, "Velira", AltStem, AltPrefix, Resolve);

        Assert.Empty(proj.Targets);                                            // alt meshes + its sole face texture gone
        Assert.False(File.Exists(Path.Combine(_root, face.ReplaceFile)));      // file actually deleted (not orphaned)
    }

    [Fact]
    public void Remove_Base_KeepsSharedRecoveredFaceTexture_DropsOnlyBaseOwnedUsers()
    {
        var proj = NewProject();
        proj.Selection.Add(new SelectionEntry { Character = "Velira", Outfit = BaseStem });
        proj.Selection.Add(new SelectionEntry { Character = "Velira", Outfit = AltStem });
        // base owns its face + body; alt reuses the base face (same recipe-slot name, owned by the alt's identity).
        proj.Targets.Add(MeshTarget("Velira", BaseStem, FaceUser));
        proj.Targets.Add(MeshTarget("Velira", BaseStem, "c_VeliraSSR01_slg_body_lod0"));
        var altFace = MeshTarget("Velira", AltStem, FaceUser);
        proj.Targets.Add(altFace);
        // the shared face texture — used by BOTH via the same recovered-face name.
        var face = TextureTarget("face_d", new[] { FaceUser }, edited: true);
        proj.Targets.Add(face);
        // a base-only body texture — no survivor claims it, so it must drop.
        var body = TextureTarget("body_d", new[] { "c_VeliraSSR01_slg_body_lod0" }, edited: true);
        proj.Targets.Add(body);

        SubjectRemoval.Remove(proj, "Velira", BaseStem, BasePrefix, Resolve);

        // shared face texture SURVIVES for the alt (deleting it would break the alt), still carrying the user.
        Assert.Contains(proj.Targets, t => t.ObjectName == "face_d");
        Assert.True(File.Exists(Path.Combine(_root, face.ReplaceFile)));
        Assert.Equal(new[] { FaceUser }, proj.Targets.First(t => t.ObjectName == "face_d").Users!.ToArray());
        // the alt's own recovered-face mesh target is untouched.
        Assert.Contains(proj.Targets, t => t == altFace);
        // the base-only body texture is dropped (only base-owned users removed).
        Assert.DoesNotContain(proj.Targets, t => t.ObjectName == "body_d");
        Assert.False(File.Exists(Path.Combine(_root, body.ReplaceFile)));
    }

    [Fact]
    public void EditedFileCount_UsesByteCompare_NotStaleEditedFlag()
    {
        // The stored Edited flag says edited but the workspace is byte-identical to the original, so the
        // byte-compare must WIN or the confirm overstates the edited-file count.
        var proj = NewProject();
        var sub = Materializer.SubjectFolder("Karst", "KarstSSR01");
        var rel = $"{sub}/meshes/c_KarstSSR01_slg_cloth1_lod0.glb";
        var orig = $"{sub}/originals/c_KarstSSR01_slg_cloth1_lod0.glb";
        Write(rel, "identical"); Write(orig, "identical");                     // stored flag stale, bytes equal
        proj.Targets.Add(new ProjectTarget { AssetType = "Mesh", Bundle = "aa",
            ObjectName = "c_KarstSSR01_slg_cloth1_lod0", ReplaceFile = rel, OriginalFile = orig, Edited = true });

        Assert.Equal(0, SubjectRemoval.EditedFileCount(proj, "Karst", "KarstSSR01", Prefix));
    }
}
