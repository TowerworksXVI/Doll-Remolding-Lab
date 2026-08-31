using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a Blender open actually hands the modder for a part that already carries a mesh edit.
///
/// <para>The session's PREPARED glb is the one file both open routes go on to consume — the lone open
/// snapshots it through the part's transport, and the combined open assembles the composition out of it —
/// so which mesh it is written from decides what Blender shows on either route. It used to be written from
/// the stock rigged build on both: the edit's own maps were laid over the GAME's mesh, and a send from that
/// session replaced the modder's work with the copy they had been shown.</para>
///
/// <para>These drive the open's own chain over a synthetic install — the real rigged build, the real
/// prepare, the real transport and the real combined build — stopping where the launch itself begins.</para>
/// </summary>
public class SessionBlenderPreparedPartTests
{
    private const string Body = "c_vesna_body_lod0", Cloth = "cloth1_lod0", Hair = "hair1_lod0";
    private const string BodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
    private const string ClothLogical = "ccccccccccccccccccccccccccccccc1.bundle";
    private const string HairLogical = "hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh1.bundle";
    private const string BodyPhys = "55555555555555555555555555555555";
    private const string ClothPhys = "66666666666666666666666666666666";
    private const string HairPhys = "77777777777777777777777777777777";

    private static readonly float[] Tri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] Idx = { 0, 1, 2 };
    private const uint BodyBoneA = 11u, BodyBoneB = 22u, ClothBone = 33u, HairBone = 44u;
    private static readonly uint[] BodyBones = { BodyBoneA, BodyBoneB };
    private static readonly uint[] ClothBones = { ClothBone };
    private static readonly uint[] HairBones = { HairBone };

    /// <summary>How far the edit lifts the part — far enough that no round-trip noise can reach it, so
    /// "this is the edit" and "this is the game's copy" can never be confused for one another.</summary>
    private const float Lift = 5f;

    // ---------------------------------------------------------------- the lone open

    /// <summary>A part with an active mesh edit, opened ALONE.
    ///
    /// <para>Route: OpenSessionBlenderAsync → AssetExporter.BuildRiggedGlbs (the stock rigged build) →
    /// MainWindowViewModel.GeometryFile (the edit's own glb) → MainWindowViewModel.PrepareSessionPartGlb (the
    /// prepared glb) → ProjectAssetIngress.Begin, whose outbound snapshot is the file the lone open hands
    /// Blender.</para></summary>
    [Fact]
    public void ALoneOpenOfAnEditedPart_HandsBlenderTheEditsMesh_NotTheGames()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string modRoot = g.At("mod");
        var session = ModWithGeometryEdit(modRoot, install.Rigged(Body), Body);
        string edited = Path.Combine(modRoot, "meshes", "long.glb");

        // the shell's own read of the edit's bound file, and its own preparation of the part
        var geometry = MainWindowViewModel.GeometryFile(session.Slots("edit-long"), modRoot);
        Assert.Null(geometry.Missing);
        Assert.Equal(Path.GetFullPath(edited), geometry.Path);
        string prepared = install.Prepared(Body);
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Body), geometry.Path, Body,
            prepared, null));

        // the transport the lone open opens on the prepared file: its outbound snapshot IS what Blender loads
        var transport = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-geometry", prepared);

        Assert.Equal(Ys(edited, Body), Ys(transport.OutboundSnapshot, Body));
        Assert.NotEqual(Ys(install.Rigged(Body), Body), Ys(transport.OutboundSnapshot, Body));
    }

    /// <summary>A part with no edit still opens on the game's own mesh: the prepared file is a re-split of
    /// the rigged build and nothing else.
    ///
    /// <para>Route: OpenSessionBlenderAsync → AssetExporter.BuildRiggedGlbs →
    /// MainWindowViewModel.PrepareSessionPartGlb with no edited glb.</para></summary>
    [Fact]
    public void ABarePartStillOpensOnTheGamesOwnMesh()
    {
        using var g = new TempGame();
        var install = Installed(g);

        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Cloth), null, Cloth,
            install.Prepared(Cloth), null));

        Assert.Equal(Ys(install.Rigged(Cloth), Cloth), Ys(install.Prepared(Cloth), Cloth));
    }

    // ---------------------------------------------------------------- the combined open

    /// <summary>The several-parts open: the composition is built out of the prepared files, so the edited
    /// part joins it carrying the modder's mesh while its bare sibling carries the game's.
    ///
    /// <para>Route: OpenSessionBlenderAsync → BuildRiggedGlbs (stock) → PrepareSessionPartGlb per part →
    /// BuildRiggedGlbs again over the prepared files as each part's EditedGlb, writing composition.glb —
    /// the file the combined open hands Blender.</para></summary>
    [Fact]
    public void ACombinedOpenComposesTheEditedPartFromItsOwnMeshAndTheBareOneFromTheGames()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string edited = AuthoredEdit(install.Rigged(Body), Body, g.At("edit-body.glb"));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Body), edited, Body,
            install.Prepared(Body), null));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Cloth), null, Cloth,
            install.Prepared(Cloth), null));

        string composition = g.At("composition.glb");
        AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, glbOut: null, editedGlb: install.Prepared(Body)),
            Spec("cloth", ClothLogical, Cloth, glbOut: null, editedGlb: install.Prepared(Cloth)),
        }, install.MapsDir, combinedOut: composition);

        Assert.True(File.Exists(composition));
        Assert.Equal(Ys(edited, Body), Ys(composition, Body));
        Assert.NotEqual(Ys(install.Rigged(Body), Body), Ys(composition, Body));
        Assert.Equal(Ys(install.Rigged(Cloth), Cloth), Ys(composition, Cloth));
    }

    /// <summary>The gate the open's PER-PART build rests on: a spec that both names an edit and asks for a
    /// per-part glb writes the GAME's mesh to that glb. That file is the session's stock map record and the
    /// armature every prepared file is refitted to, so an edit reaching it would destroy the one thing the
    /// prepare needs the game's copy for. The open passes no edit on that call at all — this pins the gate
    /// underneath, which is what makes the omission safe rather than merely tidy.
    ///
    /// <para>Route: OpenSessionBlenderAsync's first AssetExporter.BuildRiggedGlbs call.</para></summary>
    [Fact]
    public void ThePerPartBuild_WritesTheGamesMesh_EvenWhereASpecNamesAnEdit()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string edited = AuthoredEdit(install.Rigged(Body), Body, g.At("edit-body.glb"));
        string rebuilt = g.At(Path.Combine("parts", "rebuilt.rigged.glb"));

        AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, rebuilt, edited),
        }, install.MapsDir);

        Assert.True(File.Exists(rebuilt));
        Assert.Equal(Ys(install.Rigged(Body), Body), Ys(rebuilt, Body));
        Assert.NotEqual(Ys(edited, Body), Ys(rebuilt, Body));
    }

    // ---------------------------------------------------------------- an edit that cannot be read

    /// <summary>An edit whose geometry file will not parse. The prepare answers "no" and writes nothing —
    /// it must never quietly stand the game's mesh under the edit's name, which is the whole failure the
    /// refusal exists for.
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.PrepareSessionPartGlb → the caller's
    /// refusal sentence, MainWindowViewModel.EditGeometryUnreadable.</para></summary>
    [Fact]
    public void AnEditThatCannotBeRead_RefusesTheOpenByName_AndStandsNoStockMeshInItsPlace()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string wrecked = g.At("edit-body.glb");
        File.WriteAllBytes(wrecked, new byte[] { 0x67, 0x6c, 0x54, 0x46, 1, 2, 3 });   // a glb header and junk

        Assert.False(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Body), wrecked, Body,
            install.Prepared(Body), null));

        Assert.False(File.Exists(install.Prepared(Body)));   // nothing stock landed where the edit belonged
        string refusal = MainWindowViewModel.EditGeometryUnreadable(new[] { "body" });
        Assert.Contains("body", refusal);
        Assert.Contains("was not opened", refusal);
        Assert.Contains("body and cloth",
            MainWindowViewModel.EditGeometryUnreadable(new[] { "body", "cloth" }));
    }

    /// <summary>The signal the combined open's own refusal reads: a part whose prepared file the build could
    /// not assemble from is NAMED, rather than silently opening as the game's copy. A skinless workspace glb
    /// is the shape that reaches it — the union armature has nothing to join.
    ///
    /// <para>Route: OpenSessionBlenderAsync's combined BuildRiggedGlbs call, whose vanillaFallbacks list the
    /// shell turns into the same refusal sentence.</para></summary>
    [Fact]
    public void TheCombinedBuild_NamesAPartItCouldNotAssembleFromItsPreparedFile()
    {
        using var g = new TempGame();
        var install = Installed(g);
        // a prepared file with no skin at all: real geometry, nothing the union armature can take
        MeshGltf.ExportGlb(MeshGltf.ImportGlb(install.Rigged(Body), Body, lenient: true),
            install.Prepared(Body));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Cloth), null, Cloth,
            install.Prepared(Cloth), null));
        var fallbacks = new List<string>();

        AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, glbOut: null, editedGlb: install.Prepared(Body)),
            Spec("cloth", ClothLogical, Cloth, glbOut: null, editedGlb: install.Prepared(Cloth)),
        }, install.MapsDir, combinedOut: g.At("composition.glb"), vanillaFallbacks: fallbacks);

        Assert.Equal(new[] { "body" }, fallbacks.ToArray());
    }

    // ---------------------------------------------------------------- the armature an edit opens on

    /// <summary>An edit is opened on the bones THIS run offers, not the ones its workspace glb froze.
    ///
    /// <para>A workspace glb carries whatever armature its last send left in it — a tail built when the
    /// subject's siblings were different, or (from a combined session) the whole outfit's union armature
    /// dragged into a one-part open. Re-exported as it stands, the modder would be shown a bone this build
    /// would refuse paint on, and would not be shown one it accepts.</para>
    ///
    /// <para>Route: OpenSessionBlenderAsync → AssetExporter.BuildRiggedGlbs (this run's rigged build, tail
    /// and all) → MainWindowViewModel.PrepareSessionPartGlb → MeshGltf.ReexportPartGlb's refit.</para>
    /// </summary>
    [Fact]
    public void ALoneOpenOfAnEditedPart_OffersThisRunsBones_NotTheOnesTheEditFroze()
    {
        using var g = new TempGame();
        var vfs = ThreePartInstall(g);
        // two rigged builds of the SAME part, differing only in which sibling's bone joins its tail
        string before = RiggedBodyBeside(g, vfs, "body.before.rigged.glb", ClothLogical, Cloth);
        string after = RiggedBodyBeside(g, vfs, "body.after.rigged.glb", HairLogical, Hair);
        Assert.Equal(new[] { BodyBoneA, BodyBoneB, ClothBone }, JointHashes(before, Body));
        Assert.Equal(new[] { BodyBoneA, BodyBoneB, HairBone }, JointHashes(after, Body));

        // the edit as the modder last sent it: the old session's armature, ridden by its own bones alone
        string edited = AuthoredEdit(before, Body, g.At("edit-body.glb"));
        string prepared = g.At(Path.Combine("parts", Body + ".glb"));

        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(after, edited, Body, prepared, null));

        // this run's tail is offered, the previous run's is gone, and the geometry is still the modder's
        Assert.Equal(new[] { BodyBoneA, BodyBoneB, HairBone }, JointHashes(prepared, Body));
        Assert.Equal(Ys(edited, Body), Ys(prepared, Body));
        Assert.NotEqual(Ys(after, Body), Ys(prepared, Body));
    }

    /// <summary>The other half of the same rule: the reduction keeps what the modder PAINTED. A bone they
    /// weighted rides through even when this run's build offers nothing like it — that paint is work the
    /// send would carry, and dropping it silently is the one thing worse than a stale armature.
    ///
    /// <para>Route: as above, with a vertex weighted onto the tail joint the old session offered.</para>
    /// </summary>
    [Fact]
    public void ALoneOpenOfAnEditedPart_KeepsABoneTheModderPainted_ThisRunOffersNoneOf()
    {
        using var g = new TempGame();
        var vfs = ThreePartInstall(g);
        string before = RiggedBodyBeside(g, vfs, "body.before.rigged.glb", ClothLogical, Cloth);
        string after = RiggedBodyBeside(g, vfs, "body.after.rigged.glb", HairLogical, Hair);

        // vertex 2 half on its own bone, half on the cloth bone standing in the OLD tail at joint 2
        string edited = AuthoredEdit(before, Body, g.At("edit-body.glb"), mesh =>
        {
            mesh.Channels["BlendWeight"][2 * 4] = 0.5f;
            mesh.Channels["BlendIndices"][2 * 4 + 1] = 2f;
            mesh.Channels["BlendWeight"][2 * 4 + 1] = 0.5f;
        });
        string prepared = g.At(Path.Combine("parts", Body + ".glb"));

        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(after, edited, Body, prepared, null));

        Assert.Contains(ClothBone, JointHashes(prepared, Body));
        Assert.Equal(new[] { BodyBoneA, BodyBoneB, ClothBone, HairBone }, JointHashes(prepared, Body));
        // …and the paint still names it
        var payload = MeshGltf.ImportPayload(prepared, Body, lenient: true);
        int joint = Array.IndexOf(payload.SkinJointHashes!, ClothBone);
        Assert.Equal(joint, payload.JointIndices![2 * 4 + 1]);
        Assert.Equal(0.5f, payload.JointWeights![2 * 4 + 1], 3);
    }

    /// <summary>An edit that came back with no armature at all. The combined route already refuses that file
    /// by name (see <see cref="TheCombinedBuild_NamesAPartItCouldNotAssembleFromItsPreparedFile"/>, whose
    /// prepared file is this same shape); the lone route used to answer YES and hand Blender an unrigged
    /// part under a posed part's name, so the two open routes disagreed about the same file.
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.PrepareSessionPartGlb → the caller's
    /// refusal sentence.</para></summary>
    [Fact]
    public void AnEditThatCameBackUnrigged_IsRefusedByTheLoneOpenToo()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string skinless = g.At("edit-body.glb");
        MeshGltf.ExportGlb(MeshGltf.ImportGlb(install.Rigged(Body), Body, lenient: true), skinless);

        Assert.False(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Body), skinless, Body,
            install.Prepared(Body), null));
        Assert.False(File.Exists(install.Prepared(Body)));   // nothing unrigged landed where the part belonged
    }

    /// <summary>"Couldn't read the edit on X" is an answer about the MODDER's file, and only about it. The
    /// map record the prepare reads, and the prepared file it writes, are this run's own — a damaged record
    /// beside the rigged build used to send the modder off to look at a healthy edit.
    ///
    /// <para>Route: OpenSessionBlenderAsync's prepare → MainWindowViewModel.PrepareSessionPartGlb →
    /// MeshGltf.ReexportPartGlb past its afterSourceRead, whose failures reach the open's generic
    /// "Couldn't prepare the Blender file" route instead.</para></summary>
    [Fact]
    public void ADamagedRecordOnTheRunsOwnBuild_IsNotAnsweredAsTheEditsFailure()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string edited = AuthoredEdit(install.Rigged(Body), Body, g.At("edit-body.glb"));
        File.WriteAllText(PreviewMaps.SidecarPath(install.Rigged(Body)), "{ this is not json");

        Assert.ThrowsAny<Exception>(() => MainWindowViewModel.PrepareSessionPartGlb(
            install.Rigged(Body), edited, Body, install.Prepared(Body), null));
        // the same edit, against an intact record, is read without complaint — so the throw above is the
        // record's and not the edit's
        File.Delete(PreviewMaps.SidecarPath(install.Rigged(Body)));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(install.Rigged(Body), edited, Body,
            install.Prepared(Body), null));
    }

    // ---------------------------------------------------------------- the open's own wiring

    /// <summary>The prepare pass the open runs over its parts: each part is prepared from its OWN edit, and
    /// only the parts whose edit could not be read are named back. This is the wiring itself — a revert that
    /// stopped passing each part's edit, or a refusal that stopped collecting, left every behavioural test
    /// in this suite green.
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.PrepareSessionParts →
    /// PrepareSessionPartGlb per part → EditRefusal, the sentence the open stops on.</para></summary>
    [Fact]
    public void ThePreparePass_PreparesEachPartFromItsOwnEdit_AndNamesOnlyTheUnreadableOnes()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string edited = AuthoredEdit(install.Rigged(Body), Body, g.At("edit-body.glb"));
        string wrecked = g.At("edit-cloth.glb");
        File.WriteAllBytes(wrecked, new byte[] { 0x67, 0x6c, 0x54, 0x46, 1, 2, 3 });

        var refused = MainWindowViewModel.PrepareSessionParts(new[]
        {
            Plan(install, "body", Body, edited),
            Plan(install, "cloth", Cloth, wrecked),
        });

        Assert.Equal(new[] { "cloth" }, refused.ToArray());
        // the readable part was prepared from its EDIT, not from the game's copy
        Assert.Equal(Ys(edited, Body), Ys(install.Prepared(Body), Body));
        Assert.NotEqual(Ys(install.Rigged(Body), Body), Ys(install.Prepared(Body), Body));
        // …and the open stops on that name
        Assert.Null(MainWindowViewModel.EditRefusal(Array.Empty<string>()));
        Assert.Contains("cloth", MainWindowViewModel.EditRefusal(refused)!);
    }

    /// <summary>A rigged glb the run failed to write is the RUN's failure, not an answer about an edit: the
    /// prepare throws rather than naming the part as unreadable, and the open says so in its own words.</summary>
    [Fact]
    public void ThePreparePass_ThrowsWhenTheRunsOwnBuildIsMissing()
    {
        using var g = new TempGame();
        var install = Installed(g);
        File.Delete(install.Rigged(Cloth));

        var missing = Assert.Throws<InvalidDataException>(() => MainWindowViewModel.PrepareSessionParts(
            new[] { Plan(install, "cloth", Cloth, null) }));
        Assert.Contains("cloth", missing.Message);
    }

    [Fact]
    public void Parallel_prepare_matches_serial_unreadable_and_game_side_results_for_a_mixed_batch()
    {
        using var g = new TempGame();
        var install = Installed(g);
        string wrecked = g.At("wrecked-cloth.glb");
        File.WriteAllBytes(wrecked, new byte[] { 0x67, 0x6c, 0x54, 0x46, 1, 2, 3 });
        var plans = new[]
        {
            Plan(install, "body", Body, null),
            Plan(install, "cloth", Cloth, wrecked),
            new MainWindowViewModel.SessionPartPlan("prop", "prop_lod0", g.At("missing-static.glb"),
                g.At("static.glb"), true, null, null),
        };
        var serialGameSide = new List<string>();
        var serialUnreadable = MainWindowViewModel.PrepareSessionParts(plans, serialGameSide,
            skipStatic: true, maxDegreeOfParallelism: 1);
        byte[] serialBody = File.ReadAllBytes(install.Prepared(Body));
        File.Delete(install.Prepared(Body));
        string serialSidecar = PreviewMaps.SidecarPath(install.Prepared(Body));
        if (File.Exists(serialSidecar)) File.Delete(serialSidecar);

        var parallelGameSide = new List<string>();
        var parallelUnreadable = MainWindowViewModel.PrepareSessionParts(plans, parallelGameSide,
            skipStatic: true, maxDegreeOfParallelism: 4);

        Assert.Equal(serialUnreadable, parallelUnreadable);
        Assert.Equal(serialGameSide, parallelGameSide);
        Assert.Equal(serialBody, File.ReadAllBytes(install.Prepared(Body)));
        Assert.Equal(new[] { "cloth" }, parallelUnreadable);
        Assert.Equal(new[] { install.Prepared(Body) }, parallelGameSide);
        Assert.False(File.Exists(g.At("static.glb")));
    }

    /// <summary>Which subject rows a return is about to change — what it marks as working for the length of
    /// the apply, in the same gate the subject's own Open takes. A mint-on-return row carries its own
    /// subject; an exact row carries only the edit it was opened on, and the session is what says whose part
    /// that edit is. Each subject once, whatever the return's fifteen parts add up to.</summary>
    [Fact]
    public void ABlenderReturn_NamesTheSubjectsItIsAboutToChange_OnceEach()
    {
        var part = new TargetPart { Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = Body };
        var edits = new List<EditDefinition>
        {
            new() { Id = "edit-long", Target = part },
        };
        var targets = new List<Remold.Core.Blender.BlenderSessionTarget>
        {
            // an exact row: the edit names it
            new(Body, "asset", "workspace.glb", "edit-long", "slot", @"C:\runs\out.glb"),
            // a mint-on-return row on the same subject: its own fields name it
            new(Cloth, "", "cloth.glb", Subject: "Vesna", Outfit: "VesnaSSR01"),
            // …and one on another subject entirely
            new(Hair, "", "hair.glb", Subject: "Aster", Outfit: "AsterSSR01"),
        };

        var owners = MainWindowViewModel.BlenderReturnSubjects(targets, edits);

        Assert.Equal(new[] { ("Vesna", "VesnaSSR01"), ("Aster", "AsterSSR01") }, owners.ToArray());

        // A return whose exact row names an edit this mod does not have marks nothing for it: there is no
        // row to mark, and the return is about to be refused anyway.
        Assert.Empty(MainWindowViewModel.BlenderReturnSubjects(
            new[] { targets[0] }, Array.Empty<EditDefinition>()));
    }

    /// <summary>The composition's own two answers, off the build's real signals: a part whose edit it had to
    /// fall back on refuses the open by name, and a part it never composed at all is named on the open's
    /// line. A STATIC part is neither — it is drawn unposed and no combined build has ever carried one.
    ///
    /// <para>Route: OpenSessionBlenderAsync's combined BuildRiggedGlbs → its vanillaFallbacks and its
    /// returned list → EditsLostToTheComposition / PartsMissingFromComposition → the open's status
    /// line.</para></summary>
    [Fact]
    public void TheCompositionsShortfall_NamesTheEditItLostAndThePartItNeverComposed()
    {
        var plans = new[]
        {
            new MainWindowViewModel.SessionPartPlan("body", Body, "r", "p", false, null, "edit.glb"),
            new MainWindowViewModel.SessionPartPlan("cloth", Cloth, "r", "p", false, null, null),
            new MainWindowViewModel.SessionPartPlan("prop", "prop_lod0", "r", "p", true, null, null),
        };

        Assert.Equal(new[] { "body" },
            MainWindowViewModel.EditsLostToTheComposition(plans, new[] { "body", "cloth" }).ToArray());
        Assert.Empty(MainWindowViewModel.EditsLostToTheComposition(plans, Array.Empty<string>()));
        // the prop is absent by construction; the cloth is a part that fell out of the build
        Assert.Equal(new[] { "cloth" },
            MainWindowViewModel.PartsMissingFromComposition(plans, new[] { "body" }).ToArray());
        Assert.Empty(MainWindowViewModel.PartsMissingFromComposition(plans, new[] { "body", "cloth" }));

        Assert.Empty(MainWindowViewModel.BlenderOpenNotices(Array.Empty<string>(), false,
            Array.Empty<string>()));
        Assert.Contains("cloth", Assert.Single(MainWindowViewModel.BlenderOpenNotices(
            new[] { "cloth" }, false, Array.Empty<string>())));
        Assert.Contains("body and cloth", Assert.Single(MainWindowViewModel.BlenderOpenNotices(
            new[] { "body", "cloth" }, false, Array.Empty<string>())));
    }

    /// <summary>…and the signal that comparison is wired off is the build's own. A part whose mesh the
    /// bundle does not hold is dropped by the build's per-part isolation: no fallback is recorded, the
    /// composition is written without it, and the ONLY place it shows is the list the build returns.
    ///
    /// <para>Route: OpenSessionBlenderAsync's combined AssetExporter.BuildRiggedGlbs →
    /// MainWindowViewModel.PartsMissingFromComposition.</para></summary>
    [Fact]
    public void ACombinedBuildThatDropsAPart_ReportsOnlyThePartsItComposed()
    {
        using var g = new TempGame();
        var vfs = ThreePartInstall(g);
        string parts = g.At("parts"), maps = g.At("textures");
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, Path.Combine(parts, Body + ".rigged.glb"), null),
            Spec("cloth", ClothLogical, Cloth, Path.Combine(parts, Cloth + ".rigged.glb"), null),
            Spec("hair", HairLogical, Hair, Path.Combine(parts, Hair + ".rigged.glb"), null),
        }, maps);
        var plans = new[]
        {
            Plan(parts, "body", Body, null), Plan(parts, "cloth", Cloth, null),
            Plan(parts, "hair", Hair, null),
        };
        Assert.Empty(MainWindowViewModel.PrepareSessionParts(plans));

        // the hair's spec names a mesh its bundle doesn't hold — the build skips it and keeps going
        var composed = AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, null, Path.Combine(parts, Body + ".glb")),
            Spec("cloth", ClothLogical, Cloth, null, Path.Combine(parts, Cloth + ".glb")),
            Spec("hair", HairLogical, "no_such_mesh", null, Path.Combine(parts, Hair + ".glb")),
        }, maps, combinedOut: g.At("composition.glb"));

        Assert.Equal(new[] { "body", "cloth" }, composed.ToArray());
        Assert.Equal(new[] { "hair" },
            MainWindowViewModel.PartsMissingFromComposition(plans, composed).ToArray());
        // the scene really is one part short, and the open still opens on the two that landed
        Assert.True(File.Exists(g.At("composition.glb")));
        Assert.DoesNotContain(Hair, MeshGltf.MeshNames(g.At("composition.glb")));
    }

    // ---------------------------------------------------------------- fixtures

    private static Outfit TheOutfit => new(0, "VesnaSSR01", OutfitKind.Base);

    private static (string Part, string SourceBundle, string MeshName, string? GlbOut,
        IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb) Spec(string part, string bundle,
        string mesh, string? glbOut, string? editedGlb) => (part, bundle, mesh, glbOut, null, 0L, editedGlb);

    private sealed record Fixture(GameVfs Vfs, string PartsDir, string MapsDir)
    {
        public string Rigged(string mesh) => Path.Combine(PartsDir, mesh + ".rigged.glb");
        public string Prepared(string mesh) => Path.Combine(PartsDir, mesh + ".glb");
    }

    /// <summary>A two-part synthetic install with both parts' STOCK rigged glbs already built — the state
    /// <see cref="MainWindowViewModel.PrepareSessionPartGlb"/> is reached in, since the open's first
    /// <see cref="AssetExporter.BuildRiggedGlbs"/> writes them before the prepare loop runs.</summary>
    private static Fixture Installed(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, BodyPhys + ".bundle"), Body, Tri, Idx,
            BodyBones, bundleName: BodyLogical);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, ClothPhys + ".bundle"), Cloth, Tri, Idx,
            ClothBones, bundleName: ClothLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (BodyLogical, BodyPhys), (ClothLogical, ClothPhys));
        var fixture = new Fixture(vfs, g.At("parts"), g.At("textures"));

        var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, fixture.Rigged(Body), null),
            Spec("cloth", ClothLogical, Cloth, fixture.Rigged(Cloth), null),
        }, fixture.MapsDir);

        Assert.Equal(new[] { "body", "cloth" }, done.ToArray());
        return fixture;
    }

    /// <summary>A three-part synthetic install, each part on a bone of its own — so which SIBLINGS a rigged
    /// build is given decides which bones join the part's appended tail.</summary>
    private static GameVfs ThreePartInstall(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, BodyPhys + ".bundle"), Body, Tri, Idx,
            BodyBones, bundleName: BodyLogical);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, ClothPhys + ".bundle"), Cloth, Tri, Idx,
            ClothBones, bundleName: ClothLogical);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, HairPhys + ".bundle"), Hair, Tri, Idx,
            HairBones, bundleName: HairLogical);
        return TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (BodyLogical, BodyPhys), (ClothLogical, ClothPhys), (HairLogical, HairPhys));
    }

    /// <summary>One rigged build of the BODY beside a single sibling. The sibling is read for its share of
    /// the subject's skeleton and nothing else, and its bone is what lands in the body's tail — so two calls
    /// naming different siblings write two rigged glbs offering different bones.</summary>
    private static string RiggedBodyBeside(TempGame g, GameVfs vfs, string outName,
        string siblingBundle, string siblingMesh)
    {
        string outPath = g.At(Path.Combine("parts", outName));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", new[]
        {
            Spec("body", BodyLogical, Body, outPath, null),
            Spec("sibling", siblingBundle, siblingMesh, null, null),
        }, g.At("textures"));
        return outPath;
    }

    /// <summary>One glb's skin joints by bone hash, in joint order — what a modder sees offered as paintable
    /// bones when the file opens in Blender.</summary>
    private static uint[] JointHashes(string glbPath, string meshName) =>
        MeshGltf.ImportPayload(glbPath, meshName, lenient: true).SkinJointHashes!;

    /// <summary>One displayed part as the open's prepare pass reads it.</summary>
    private static MainWindowViewModel.SessionPartPlan Plan(Fixture install, string token, string mesh,
        string? editedGlb) =>
        new(token, mesh, install.Rigged(mesh), install.Prepared(mesh), false, null, editedGlb);

    /// <inheritdoc cref="Plan(Fixture, string, string, string?)"/>
    private static MainWindowViewModel.SessionPartPlan Plan(string partsDir, string token, string mesh,
        string? editedGlb) =>
        new(token, mesh, Path.Combine(partsDir, mesh + ".rigged.glb"),
            Path.Combine(partsDir, mesh + ".glb"), false, null, editedGlb);

    /// <summary>An edit's own workspace glb: the part's rigged file with every vertex lifted, which is the
    /// shape a send back from Blender leaves in the mod folder. <paramref name="paint"/> gets the mesh
    /// before it is written, for an edit that also moves weight around.</summary>
    private static string AuthoredEdit(string riggedGlb, string meshName, string outPath,
        Action<UnityMesh>? paint = null)
    {
        var read = MeshGltf.ReadRiggedGlb(riggedGlb, meshName);
        Assert.NotNull(read);
        var (mesh, skin) = read!.Value;
        for (int v = 0; v < mesh.VertexCount; v++) mesh.Channels["Vertex"][v * 3 + 1] += Lift;
        paint?.Invoke(mesh);
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, outPath);
        return outPath;
    }

    /// <summary>A saved one-part mod whose active edit binds an authored geometry file for
    /// <paramref name="meshName"/> — the project shape a part with a mesh edit is opened from.</summary>
    private static AuthoredEditSession ModWithGeometryEdit(string modRoot, string riggedGlb, string meshName)
    {
        Directory.CreateDirectory(Path.Combine(modRoot, "meshes"));
        AuthoredEdit(riggedGlb, meshName, Path.Combine(modRoot, "meshes", "long.glb"));
        var project = AuthoredEditFixtures.Saved();
        project.RootDir = modRoot;
        return new AuthoredEditSession(project);
    }

    /// <summary>One glb's vertex heights, sorted and rounded — the round trip may re-order a vertex buffer
    /// and re-quantize every float, but it never MOVES a vertex, so the ordered set to three places is what
    /// "the same mesh" means across two files. The edit lifts by <see cref="Lift"/>, orders of magnitude
    /// above that rounding, so nothing here can confuse the two answers.</summary>
    private static double[] Ys(string glbPath, string meshName)
    {
        var mesh = MeshGltf.ImportGlb(glbPath, meshName, lenient: true);
        return Enumerable.Range(0, mesh.VertexCount)
            .Select(v => Math.Round((double)mesh.Channels["Vertex"][v * 3 + 1], 3))
            .OrderBy(y => y).ToArray();
    }
}
