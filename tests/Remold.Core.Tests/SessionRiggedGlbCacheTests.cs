using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The Stage-2 seam from observed exporter work to per-part session cache reuse.</summary>
public class SessionRiggedGlbCacheTests
{
    private const string BodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
    private const string SiblingLogical = "ccccccccccccccccccccccccccccccc1.bundle";
    private const string BodyPhysical = "11111111111111111111111111111111";
    private const string SiblingPhysical = "22222222222222222222222222222222";
    private const string BodyMesh = "body_lod0";
    private const string SiblingMesh = "cloth_lod0";
    private static readonly Outfit Outfit = new(0, "VesnaSSR01", OutfitKind.Base);
    private static readonly float[] Triangle = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] Indices = { 0, 1, 2 };

    private sealed record Install(GameVfs Vfs, string BodyFile, string SiblingFile);

    private static Install Fixture(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var body = Path.Combine(abw, BodyPhysical + ".bundle");
        var sibling = Path.Combine(abw, SiblingPhysical + ".bundle");
        SyntheticBundle.BuildOneSkinnedMesh(body, BodyMesh, Triangle, Indices, new[] { 11u },
            bundleName: BodyLogical);
        SyntheticBundle.BuildOneSkinnedMesh(sibling, SiblingMesh, Triangle, Indices, new[] { 22u },
            bundleName: SiblingLogical);
        return new Install(TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (BodyLogical, BodyPhysical), (SiblingLogical, SiblingPhysical)), body, sibling);
    }

    private static List<(string Part, string SourceBundle, string MeshName, string? GlbOut,
        IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> Specs(TempGame g,
        string run = "cold", string? edited = null) => new()
    {
        ("body", BodyLogical, BodyMesh, g.At(Path.Combine(run, "parts", "body.rigged.glb")),
            null, 0, edited),
        ("cloth", SiblingLogical, SiblingMesh, null, null, 0, null),
    };

    private static MainWindowViewModel.SessionPartPlan Plan(TempGame g, string run = "cold") =>
        new("body", BodyMesh,
            g.At(Path.Combine(run, "parts", "body.rigged.glb")),
            g.At(Path.Combine(run, "parts", "body.glb")), false, null, null);

    private static List<(string Part, string SourceBundle, string MeshName, string? GlbOut,
        IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> AllSpecs(TempGame g,
        string run = "cold") => new()
    {
        ("body", BodyLogical, BodyMesh, g.At(Path.Combine(run, "parts", "body.rigged.glb")),
            null, 0, null),
        ("cloth", SiblingLogical, SiblingMesh, g.At(Path.Combine(run, "parts", "cloth.rigged.glb")),
            null, 0, null),
    };

    private static List<MainWindowViewModel.SessionPartPlan> AllPlans(TempGame g, string run = "cold") =>
        new()
        {
            new("body", BodyMesh, g.At(Path.Combine(run, "parts", "body.rigged.glb")),
                g.At(Path.Combine(run, "parts", "body.glb")), false, null, null),
            new("cloth", SiblingMesh, g.At(Path.Combine(run, "parts", "cloth.rigged.glb")),
                g.At(Path.Combine(run, "parts", "cloth.glb")), false, null, null),
        };

    private static List<(string Part, string SourceBundle, string MeshName, string? GlbOut,
        IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> CombinedSpecs(
        IReadOnlyList<MainWindowViewModel.SessionPartPlan> plans) => new()
    {
        (plans[0].Token, BodyLogical, plans[0].SlotName, null, null, 0, plans[0].Prepared),
        (plans[1].Token, SiblingLogical, plans[1].SlotName, null, null, 0, plans[1].Prepared),
    };

    private static (AssetExporter.RiggedBuildDiagnostics Diagnostics, IReadOnlyList<string> Built)
        Build(TempGame g, Install install,
            IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut,
                IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> specs,
            string run = "cold", string? combined = null, CancellationToken cancellationToken = default,
            IReadOnlyCollection<string>? observedGameSidePreparedGlbs = null,
            string? stockTextureCacheRoot = null)
    {
        var diagnostics = new AssetExporter.RiggedBuildDiagnostics();
        var built = AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs, Outfit, "Vesna", specs,
            g.At(Path.Combine(run, "textures")), combinedOut: combined, ct: cancellationToken,
            diagnostics: diagnostics, stockTextureCacheRoot: stockTextureCacheRoot,
            observedGameSidePreparedGlbs: observedGameSidePreparedGlbs);
        return (diagnostics, built);
    }

    [Fact]
    public void Diagnostics_trace_required_and_optional_reads_from_the_actual_build()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var result = Build(g, install, Specs(g));

        Assert.True(result.Diagnostics.Completed);
        Assert.True(result.Diagnostics.GameSideOnly);
        Assert.False(result.Diagnostics.HadTransientFailures);
        Assert.False(result.Diagnostics.WasCanceled);
        Assert.False(result.Diagnostics.HadProjectAuthoredContent);
        Assert.Contains(BodyLogical, result.Diagnostics.BundleReads);
        Assert.Contains(SiblingLogical, result.Diagnostics.BundleReads);
        Assert.Contains(BodyLogical, result.Diagnostics.RequiredBundleReads);
        Assert.DoesNotContain(SiblingLogical, result.Diagnostics.RequiredBundleReads);
        Assert.Equal(new[] { "body" }, result.Built.ToArray());
    }

    [Fact]
    public void Cancellation_and_game_file_failure_are_observed_not_inferred_by_the_caller()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var canceled = new AssetExporter.RiggedBuildDiagnostics();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs,
            Outfit, "Vesna", Specs(g, "cancel"), g.At(Path.Combine("cancel", "textures")),
            ct: stop.Token, diagnostics: canceled));
        Assert.True(canceled.WasCanceled);
        Assert.False(canceled.Completed);

        var failed = new AssetExporter.RiggedBuildDiagnostics();
        using (File.Open(install.BodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => AssetExporter.BuildRiggedGlbs(g.Root, install.Vfs, Outfit,
                "Vesna", Specs(g, "busy"), g.At(Path.Combine("busy", "textures")), diagnostics: failed));
        Assert.True(failed.HadTransientFailures);
        Assert.False(failed.Completed);
    }

    [Fact]
    public void Project_inputs_and_stock_composition_are_observed_as_distinct_facts()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var authored = Build(g, install, Specs(g, "authored", edited: g.At("project-edit.glb")), "authored");
        Assert.True(authored.Diagnostics.Completed);
        Assert.True(authored.Diagnostics.HadProjectAuthoredContent);
        Assert.False(authored.Diagnostics.GameSideOnly);

        var compositionSpecs = Specs(g, "composition").Select(spec => (
            Part: spec.Part,
            SourceBundle: spec.SourceBundle,
            MeshName: spec.MeshName,
            GlbOut: (string?)null,
            BakedRest: spec.BakedRest,
            PathId: spec.PathId,
            EditedGlb: spec.EditedGlb)).ToList();
        var composition = Build(g, install, compositionSpecs, "composition", g.At("composition.glb"));
        Assert.True(composition.Diagnostics.Completed);
        Assert.True(composition.Diagnostics.ProducedComposition);
        Assert.False(composition.Diagnostics.HadProjectAuthoredContent);
        Assert.True(composition.Diagnostics.GameSideOnly);
    }

    [Fact]
    public void Publish_uses_observed_impurity_and_declines_an_otherwise_valid_game_glb()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var specs = Specs(g, "authored", edited: g.At("project-edit.glb"));
        var result = Build(g, install, specs, "authored");
        var cache = new RiggedGlbCache(g.At("rigs"));
        var identity = MainWindowViewModel.SessionRiggedCacheIdentity(install.Vfs, Outfit, "Vesna", null,
            specs, wardrobeUnreadable: false);

        MainWindowViewModel.PublishSessionRiggedParts(cache, identity, install.Vfs,
            new StockTextureCache(g.At("stocktex")), result.Diagnostics,
            new[] { Plan(g, "authored") }, result.Built);

        var current = BundleReads.CurrentKeys(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest));
        Assert.False(cache.TryServe(identity, current,
            new[] { new RiggedGlbCache.Request(BodyMesh, "body.rigged.glb") }, g.At("must-miss")));
    }

    [Fact]
    public void Warm_restore_reads_no_game_bundle_and_serves_while_the_game_holds_them()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var specs = Specs(g);
        var result = Build(g, install, specs);
        var cache = new RiggedGlbCache(g.At("rigs"));
        var stock = new StockTextureCache(g.At("stocktex"));
        var identity = MainWindowViewModel.SessionRiggedCacheIdentity(install.Vfs, Outfit, "Vesna", null,
            specs, wardrobeUnreadable: false);
        MainWindowViewModel.PublishSessionRiggedParts(cache, identity, install.Vfs, stock,
            result.Diagnostics, new[] { Plan(g) }, result.Built);

        using (File.Open(install.SiblingFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(MainWindowViewModel.TryRestoreSessionRiggedParts(cache, identity, install.Vfs, stock,
                new[] { Plan(g) }, g.At("warm-optional-locked")));
        Assert.True(File.Exists(g.At(Path.Combine("warm-optional-locked", "parts", "body.rigged.glb"))));

        using (File.Open(install.BodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(MainWindowViewModel.TryRestoreSessionRiggedParts(cache, identity, install.Vfs, stock,
                new[] { Plan(g) }, g.At("warm-required-locked")));
        Assert.True(File.Exists(g.At(Path.Combine("warm-required-locked", "parts", "body.rigged.glb"))));
    }

    [Fact]
    public void Warm_restore_rehomes_hashed_stock_pngs_and_damage_is_an_all_or_nothing_miss()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var stock = new StockTextureCache(g.At("stocktex"));
        var decoded = new BundleReader.DecodedTexture(
            SyntheticBundle.SolidRgba32(4, 4, 10, 20, 30, 255), 4, 4, "RGBA32");
        var cached = stock.Publish(decoded, "content-a", "body_d", 42)!;
        var dependency = RiggedGlbCache.DescribeStockTexture(cached, "content-a", "body_d", 42,
            "body_d.png")!.Value;
        var source = WriteMinimalGlb(g.At("source.glb"));
        var sidecar = g.At("source.maps.json");
        File.WriteAllText(sidecar, "{}");
        var identity = new RiggedGlbCache.Identity("test", "subject", "roster");
        var content = BundleReads.ContentHashLookup(install.Vfs.Manifest);
        var reads = BundleReads.Of(install.Vfs.Catalog, content, new[] { BodyLogical });
        var cache = new RiggedGlbCache(g.At("rigs"));
        Assert.True(cache.TryStore(identity, reads,
            new RiggedGlbCache.Artifact(BodyMesh, source, sidecar, new[] { BodyLogical }, new[] { dependency }),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        var plan = new MainWindowViewModel.SessionPartPlan("body", BodyMesh, g.At("body.rigged.glb"),
            g.At("body.glb"), false, null, null);

        Assert.True(MainWindowViewModel.TryRestoreSessionRiggedParts(cache, identity, install.Vfs, stock,
            new[] { plan }, g.At("warm")));
        Assert.True(File.Exists(g.At(Path.Combine("warm", "textures", "body_d.png"))));
        Assert.True(File.Exists(g.At(Path.Combine("warm", "textures", PreviewMaps.NeutralN))));

        var damaged = File.ReadAllBytes(cached);
        damaged[damaged.Length / 2] ^= 0x5A; // signature and IEND remain whole; the rig's SHA catches the middle
        File.WriteAllBytes(cached, damaged);
        Assert.False(MainWindowViewModel.TryRestoreSessionRiggedParts(cache, identity, install.Vfs, stock,
            new[] { plan }, g.At("damaged")));
        Assert.False(Directory.Exists(g.At("damaged")));
        Assert.Null(stock.TryGet("content-a", "body_d", 42));
    }

    [Fact]
    public void Stock_combined_geometry_requires_the_full_visible_set_not_an_open_all_command()
    {
        using var g = new TempGame();
        var stock = AllPlans(g);
        string[] slots = { BodyMesh, SiblingMesh };

        Assert.True(MainWindowViewModel.StockCombinedGeometryCandidate(slots, slots, stock));
        Assert.False(MainWindowViewModel.StockCombinedGeometryCandidate(
            slots, new[] { BodyMesh }, new[] { stock[0] }));

        var authoredGeometry = stock.ToList();
        authoredGeometry[0] = authoredGeometry[0] with { EditedGlb = g.At("authored.glb") };
        Assert.False(MainWindowViewModel.StockCombinedGeometryCandidate(slots, slots, authoredGeometry));

        var authoredMap = stock.ToList();
        authoredMap[0] = authoredMap[0] with
        {
            Maps = new[]
            {
                (Base: (string?)g.At("authored.png"), Normal: (string?)null, Rmo: (string?)null),
            },
        };
        Assert.False(MainWindowViewModel.StockCombinedGeometryCandidate(slots, slots, authoredMap));

        Assert.True(MainWindowViewModel.StockCombinedCompositionMatches(stock,
            new[] { "body", "cloth" }));
        Assert.False(MainWindowViewModel.StockCombinedCompositionMatches(stock,
            new[] { "body" }));
        Assert.False(MainWindowViewModel.StockCombinedCompositionMatches(stock,
            new[] { "body", "other" }));
    }

    [Fact]
    public void Clean_stock_composition_publishes_and_restores_with_its_sidecar_without_a_second_build()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var allSpecs = AllSpecs(g);
        string stockRoot = g.At("stocktex");
        var perPart = Build(g, install, allSpecs, stockTextureCacheRoot: stockRoot);
        var plans = AllPlans(g);
        var gameSidePrepared = new List<string>();
        Assert.Equal(new[] { "body", "cloth" }, perPart.Built.ToArray());
        Assert.Empty(MainWindowViewModel.PrepareSessionParts(plans, gameSidePrepared));
        Assert.Equal(plans.Select(plan => plan.Prepared), gameSidePrepared);

        string composition = g.At(Path.Combine("combined", "composition.glb"));
        var combined = Build(g, install, CombinedSpecs(plans), "combined", composition,
            observedGameSidePreparedGlbs: gameSidePrepared,
            stockTextureCacheRoot: stockRoot);
        Assert.True(combined.Diagnostics.Completed);
        Assert.True(combined.Diagnostics.ProducedComposition);
        Assert.True(combined.Diagnostics.GameSideOnly);
        Assert.False(combined.Diagnostics.HadProjectAuthoredContent);
        Assert.True(MainWindowViewModel.StockCombinedCompositionMatches(plans, combined.Built));

        var cache = new RiggedGlbCache(g.At("rigs"));
        var stock = new StockTextureCache(stockRoot);
        var identity = MainWindowViewModel.SessionRiggedCacheIdentity(install.Vfs, Outfit, "Vesna", null,
            allSpecs, wardrobeUnreadable: false);
        Assert.True(File.Exists(composition));
        Assert.NotEmpty(combined.Diagnostics.RequiredBundleReads);
        Assert.All(combined.Diagnostics.StockTextures, dependency =>
            Assert.NotNull(stock.TryGet(dependency.BundleContentId, dependency.TextureName, dependency.PathId)));
        Assert.True(MainWindowViewModel.PublishSessionStockCombined(cache, identity, install.Vfs, stock,
            combined.Diagnostics, plans, combined.Built, composition));

        string run = g.At("warm");
        Directory.CreateDirectory(run);
        string? restored = MainWindowViewModel.TryRestoreSessionStockCombined(cache, identity, install.Vfs,
            stock, plans, run);

        Assert.NotNull(restored);
        Assert.Equal(File.ReadAllBytes(composition), File.ReadAllBytes(restored!));
        Assert.Equal(File.Exists(PreviewMaps.SidecarPath(composition)),
            File.Exists(PreviewMaps.SidecarPath(restored!)));
        Assert.Single(Directory.EnumerateDirectories(run));
        Assert.False(File.Exists(Path.Combine(run, "composition.glb")));
    }

    [Fact]
    public void Stock_target_with_references_hits_the_open_all_combined_artifact()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var plans = AllPlans(g, "with-references");
        string[] slots = { BodyMesh, SiblingMesh };
        Assert.True(MainWindowViewModel.StockCombinedGeometryCandidate(slots, slots, plans));
        var identity = new RiggedGlbCache.Identity("test", "subject", "all-parts");
        var cache = new RiggedGlbCache(g.At("rigs"));
        string reads = BundleReads.Of(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest), new[] { BodyLogical });
        string openAllComposition = WriteMinimalGlb(g.At("open-all-composition.glb"));
        Assert.True(cache.TryStore(identity, reads,
            new RiggedGlbCache.Artifact(MainWindowViewModel.StockCombinedArtifactKey(plans),
                openAllComposition, null, new[] { BodyLogical }),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        string run = g.At("stock-target-with-references");
        Directory.CreateDirectory(run);

        string? restored = MainWindowViewModel.TryRestoreSessionStockCombined(cache, identity, install.Vfs,
            new StockTextureCache(g.At("stocktex")), plans, run);

        Assert.NotNull(restored);
        Assert.Equal(File.ReadAllBytes(openAllComposition), File.ReadAllBytes(restored!));
    }

    [Fact]
    public void Authored_final_composition_hits_unchanged_and_misses_every_content_key_ingredient()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var identity = new RiggedGlbCache.Identity("test", "reference-scene", "all-parts-roster");
        string targetPrepared = WriteMinimalGlb(g.At("target-prepared.glb"));
        string referencePrepared = WriteMinimalGlb(g.At("reference-prepared.glb"));
        var target = new MainWindowViewModel.SessionPartPlan("body", BodyMesh,
            WriteMinimalGlb(g.At("body-offer.glb")), targetPrepared, false, null, g.At("authored-edit.glb"));
        var reference = new MainWindowViewModel.SessionPartPlan("cloth", SiblingMesh,
            WriteMinimalGlb(g.At("cloth-offer.glb")), referencePrepared, false, null, null);
        var plans = new[] { target, reference };
        string[] slots = { BodyMesh, SiblingMesh };
        Assert.True(MainWindowViewModel.AuthoredReferenceCompositionCandidate(withReferences: true,
            openAll: false, BodyMesh, slots, slots, plans));
        string baseline = MainWindowViewModel.AuthoredCombinedArtifactKey(identity, target, plans)!;
        string relocatedPrepared = g.At("relocated-target-prepared.glb");
        File.Copy(targetPrepared, relocatedPrepared);
        Assert.Equal(baseline, MainWindowViewModel.AuthoredCombinedArtifactKey(identity,
            target with { Prepared = relocatedPrepared }, plans));
        string composition = WriteMinimalGlb(g.At("authored-composition.glb"));
        string reads = BundleReads.Of(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest), new[] { BodyLogical });
        var cache = new RiggedGlbCache(g.At("rigs"));
        Assert.True(cache.TryStorePrepared(identity, reads,
            new RiggedGlbCache.PreparedArtifact(baseline, composition)));
        Assert.NotNull(MainWindowViewModel.TryRestoreSessionAuthoredCombined(cache, identity, install.Vfs,
            baseline, g.At("unchanged")));

        var mutations = new List<(RiggedGlbCache.Identity Identity, string Key)>
        {
            (identity with { CatalogVersion = "other-catalog" },
                MainWindowViewModel.AuthoredCombinedArtifactKey(
                    identity with { CatalogVersion = "other-catalog" }, target, plans)!),
            (identity with { SubjectFingerprint = "other-reference-scene" },
                MainWindowViewModel.AuthoredCombinedArtifactKey(
                    identity with { SubjectFingerprint = "other-reference-scene" }, target, plans)!),
            (identity with { RosterSpecFingerprint = "other-roster" },
                MainWindowViewModel.AuthoredCombinedArtifactKey(
                    identity with { RosterSpecFingerprint = "other-roster" }, target, plans)!),
            (identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity,
                target with { SlotName = "other_target_lod0" }, plans)!),
            (identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity, target,
                plans.Reverse().ToArray())!),
            (identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity, target,
                new[] { target })!),
            (identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity, target, plans,
                "combined-rigged-writer-v2")!),
        };
        string changedPrepared = WriteMinimalGlb(g.At("changed-target-prepared.glb"));
        var changedBytes = File.ReadAllBytes(changedPrepared);
        changedBytes[12] ^= 0x5A;
        File.WriteAllBytes(changedPrepared, changedBytes);
        mutations.Add((identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity,
            target with { Prepared = changedPrepared }, plans)!));
        File.WriteAllText(PreviewMaps.SidecarPath(targetPrepared), "{}");
        mutations.Add((identity, MainWindowViewModel.AuthoredCombinedArtifactKey(identity, target, plans)!));
        File.Delete(PreviewMaps.SidecarPath(targetPrepared));

        int index = 0;
        foreach (var mutation in mutations)
        {
            Assert.NotEqual(baseline, mutation.Key);
            Assert.Null(MainWindowViewModel.TryRestoreSessionAuthoredCombined(cache, mutation.Identity,
                install.Vfs, mutation.Key, g.At(Path.Combine("authored-misses", (++index).ToString()))));
        }
    }

    [Fact]
    public void Authored_set_mismatch_and_incomplete_combined_entries_are_quiet_misses()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var allSpecs = AllSpecs(g);
        string stockRoot = g.At("stocktex");
        Build(g, install, allSpecs, stockTextureCacheRoot: stockRoot);
        var plans = AllPlans(g);
        var gameSidePrepared = new List<string>();
        Assert.Empty(MainWindowViewModel.PrepareSessionParts(plans, gameSidePrepared));

        string authoredPath = g.At(Path.Combine("authored-combined", "composition.glb"));
        var authored = Build(g, install, CombinedSpecs(plans), "authored-combined", authoredPath);
        Assert.True(authored.Diagnostics.HadProjectAuthoredContent);
        Assert.False(authored.Diagnostics.GameSideOnly);
        var rejectedCache = new RiggedGlbCache(g.At("rejected-rigs"));
        var stock = new StockTextureCache(stockRoot);
        var identity = MainWindowViewModel.SessionRiggedCacheIdentity(install.Vfs, Outfit, "Vesna", null,
            allSpecs, wardrobeUnreadable: false);
        Assert.False(MainWindowViewModel.PublishSessionStockCombined(rejectedCache, identity, install.Vfs,
            stock, authored.Diagnostics, plans, authored.Built, authoredPath));
        Assert.False(Directory.Exists(rejectedCache.ArtifactDirectoryFor(identity,
            MainWindowViewModel.StockCombinedArtifactKey(plans))));

        string cleanPath = g.At(Path.Combine("clean-combined", "composition.glb"));
        var clean = Build(g, install, CombinedSpecs(plans), "clean-combined", cleanPath,
            observedGameSidePreparedGlbs: gameSidePrepared,
            stockTextureCacheRoot: stockRoot);
        File.WriteAllText(PreviewMaps.SidecarPath(cleanPath), "{}");
        var cache = new RiggedGlbCache(g.At("rigs"));
        Assert.False(MainWindowViewModel.PublishSessionStockCombined(cache, identity, install.Vfs, stock,
            clean.Diagnostics, plans, new[] { "body" }, cleanPath));
        Assert.True(MainWindowViewModel.PublishSessionStockCombined(cache, identity, install.Vfs, stock,
            clean.Diagnostics, plans, clean.Built, cleanPath));

        string entry = cache.ArtifactDirectoryFor(identity,
            MainWindowViewModel.StockCombinedArtifactKey(plans));
        File.Delete(Path.Combine(entry, "rig.maps.json"));
        string run = g.At("incomplete-warm");
        Directory.CreateDirectory(run);
        Assert.Null(MainWindowViewModel.TryRestoreSessionStockCombined(cache, identity, install.Vfs,
            stock, plans, run));
        Assert.Empty(Directory.EnumerateFileSystemEntries(run));
        Assert.DoesNotContain(Directory.EnumerateDirectories(g.Root), path =>
            Path.GetFileName(path).Contains("combined-rigcache", StringComparison.Ordinal));
    }

    [Fact]
    public void Combined_restore_skips_rehashing_maps_the_per_part_restore_just_validated()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var stock = new StockTextureCache(g.At("stocktex"));
        var decoded = new BundleReader.DecodedTexture(
            SyntheticBundle.SolidRgba32(4, 4, 10, 20, 30, 255), 4, 4, "RGBA32");
        var cached = stock.Publish(decoded, "content-a", "body_d", 42)!;
        var dependency = RiggedGlbCache.DescribeStockTexture(cached, "content-a", "body_d", 42,
            "body_d.png")!.Value;
        var source = WriteMinimalGlb(g.At("source.glb"));
        var identity = new RiggedGlbCache.Identity("test", "subject", "roster");
        var content = BundleReads.ContentHashLookup(install.Vfs.Manifest);
        var reads = BundleReads.Of(install.Vfs.Catalog, content, new[] { BodyLogical });
        var cache = new RiggedGlbCache(g.At("rigs"));
        var plans = AllPlans(g);
        Assert.True(cache.TryStore(identity, reads,
            new RiggedGlbCache.Artifact(MainWindowViewModel.StockCombinedArtifactKey(plans), source, null,
                new[] { BodyLogical }, new[] { dependency }),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));

        string run = g.At("warm");
        Directory.CreateDirectory(Path.Combine(run, "textures"));
        File.Copy(cached, Path.Combine(run, "textures", "body_d.png"));
        var damaged = File.ReadAllBytes(cached);
        damaged[damaged.Length / 2] ^= 0x5A; // signature and IEND remain whole; only the SHA can tell
        File.WriteAllBytes(cached, damaged);

        // Full revalidation hashes the durable entry and misses on the damage…
        Assert.Null(MainWindowViewModel.TryRestoreSessionStockCombined(cache, identity, install.Vfs,
            stock, plans, run));

        // …but a map this open's per-part restore placed and content-checked is trusted on the exact
        // dependency match — no rehash, no game-bundle read (the body bundle stays locked throughout).
        var validated = new Dictionary<string, RiggedGlbCache.StockTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [dependency.DestinationFileName] = dependency,
        };
        string? restored;
        using (File.Open(install.BodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
            restored = MainWindowViewModel.TryRestoreSessionStockCombined(cache, identity, install.Vfs,
                stock, plans, run, validated);
        Assert.NotNull(restored);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(restored!));
    }

    [Fact]
    public void Prepared_cache_hit_serves_byte_equivalent_self_contained_workspace_files()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var identity = new RiggedGlbCache.Identity("test", "subject", "roster");
        var cache = new RiggedGlbCache(g.At("rigs"));
        var current = BundleReads.CurrentKeys(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest));
        string reads = BundleReads.Of(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest), new[] { BodyLogical });
        string source = WriteMinimalGlb(g.At(Path.Combine("cold", "body.glb")));
        string authored = g.At(Path.Combine("project", "paint.png"));
        byte[] picture = { 1, 3, 5, 7, 9 };
        Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        File.WriteAllBytes(authored, picture);
        PreviewMaps.WriteSidecar(source,
            new[] { new PreviewMaps.Entry(PreviewMaps.Hash(picture), authored, MapKind.BaseColor,
                MapOrigin.Authored) },
            Array.Empty<PreviewMaps.SubmeshSource>());

        const string key = "prepared-body";
        Assert.True(cache.TryStorePrepared(identity, reads,
            new RiggedGlbCache.PreparedArtifact(key, source)));
        File.Delete(authored); // a cached authored workspace may not retain this project dependency

        string served = g.At(Path.Combine("warm", "parts", "body.glb"));
        Assert.True(cache.TryServePrepared(identity, current, key, served));
        string entry = cache.ArtifactDirectoryFor(identity, key);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(served));
        Assert.Equal(File.ReadAllBytes(Path.Combine(entry, "rig.maps.json")),
            File.ReadAllBytes(PreviewMaps.SidecarPath(served)));
        Assert.DoesNotContain(Path.GetFullPath(authored), File.ReadAllText(PreviewMaps.SidecarPath(served)),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(served)!,
            ".prepared-assets"), "*", SearchOption.AllDirectories));

        string dependency = Assert.Single(Directory.EnumerateFiles(Path.Combine(entry,
            ".prepared-assets"), "*", SearchOption.AllDirectories));
        File.Delete(dependency);
        Assert.False(cache.TryServePrepared(identity, current, key,
            g.At(Path.Combine("damaged", "parts", "body.glb"))));
    }

    [Fact]
    public void Prepared_key_mutations_for_identity_offer_content_bindings_and_spec_are_cache_misses()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var cache = new RiggedGlbCache(g.At("rigs"));
        var identity = new RiggedGlbCache.Identity("test", "subject", "roster");
        string rigged = WriteMinimalGlb(g.At("offer.glb"));
        string otherRigged = WriteMinimalGlb(g.At("other-offer.glb"));
        File.WriteAllBytes(otherRigged, File.ReadAllBytes(otherRigged).Append((byte)17).ToArray());
        string edit = WriteMinimalGlb(g.At("edit.glb"));
        string otherEdit = WriteMinimalGlb(g.At("other-edit.glb"));
        File.WriteAllBytes(otherEdit, File.ReadAllBytes(otherEdit).Append((byte)19).ToArray());
        string mapA = g.At("map-a.png"), mapB = g.At("map-b.png"), mapC = g.At("map-c.png");
        File.WriteAllBytes(mapA, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(mapB, new byte[] { 4, 5, 6 });
        File.WriteAllBytes(mapC, new byte[] { 7, 8, 9 });
        var plan = new MainWindowViewModel.SessionPartPlan("body", BodyMesh, rigged, g.At("body.glb"),
            false, new[] { (Base: (string?)mapA, Normal: (string?)null, Rmo: (string?)mapB) }, edit,
            new[]
            {
                new TextureTransportOverride(0, "_BaseMap", mapA, MapKind.BaseColor),
                new TextureTransportOverride(1, "_Mask", mapB, MapKind.Rmo),
            });
        string baseline = MainWindowViewModel.PreparedPartArtifactKey(identity, plan)!;
        string prepared = WriteMinimalGlb(g.At("prepared.glb"));
        string reads = BundleReads.Of(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest), new[] { BodyLogical });
        var current = BundleReads.CurrentKeys(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest));
        Assert.True(cache.TryStorePrepared(identity, reads,
            new RiggedGlbCache.PreparedArtifact(baseline, prepared)));

        var planMutations = new[]
        {
            plan with { Token = "other-token" },
            plan with { SlotName = SiblingMesh },
            plan with { Static = true },
            plan with { Rigged = otherRigged },
            plan with { EditedGlb = otherEdit },
            plan with { Maps = new[] { (Base: (string?)mapC, Normal: (string?)null, Rmo: (string?)mapB) } },
            plan with { TextureMaps = new[]
            {
                new TextureTransportOverride(2, "_BaseMap", mapA, MapKind.BaseColor),
                new TextureTransportOverride(1, "_Mask", mapB, MapKind.Rmo),
            } },
            plan with { TextureMaps = new[]
            {
                new TextureTransportOverride(0, "_DetailMap", mapA, MapKind.BaseColor),
                new TextureTransportOverride(1, "_Mask", mapB, MapKind.Rmo),
            } },
            plan with { TextureMaps = new[]
            {
                new TextureTransportOverride(0, "_BaseMap", mapA, MapKind.Normal),
                new TextureTransportOverride(1, "_Mask", mapB, MapKind.Rmo),
            } },
            plan with { TextureMaps = plan.TextureMaps!.Reverse().ToArray() },
            // the primitive a replacement's picture is authored for, and the label Blender lists it under
            plan with { TextureMaps = plan.TextureMaps!.Select((row, index) =>
                index == 0 ? row with { PrimitiveIndex = 1 } : row).ToArray() },
            plan with { TextureMaps = plan.TextureMaps!.Select((row, index) =>
                index == 0 ? row with { Label = "Painted" } : row).ToArray() },
        };
        int miss = 0;
        foreach (var mutation in planMutations)
        {
            string changed = MainWindowViewModel.PreparedPartArtifactKey(identity, mutation)!;
            Assert.NotEqual(baseline, changed);
            Assert.False(cache.TryServePrepared(identity, current, changed,
                g.At(Path.Combine("misses", (++miss).ToString(), "body.glb"))));
        }

        foreach (var changedIdentity in new[]
                 {
                     identity with { CatalogVersion = "other-catalog" },
                     identity with { SubjectFingerprint = "other-subject" },
                     identity with { RosterSpecFingerprint = "other-roster-or-bone-tail" },
                 })
        {
            string changed = MainWindowViewModel.PreparedPartArtifactKey(changedIdentity, plan)!;
            Assert.NotEqual(baseline, changed);
            Assert.False(cache.TryServePrepared(changedIdentity, current, changed,
                g.At(Path.Combine("misses", (++miss).ToString(), "body.glb"))));
        }
        string otherSpec = MainWindowViewModel.PreparedPartArtifactKey(identity, plan,
            "prepared-part-workspace-other")!;
        Assert.NotEqual(baseline, otherSpec);
        Assert.False(cache.TryServePrepared(identity, current, otherSpec,
            g.At(Path.Combine("misses", (++miss).ToString(), "body.glb"))));

        Dictionary<string, string>? changedBundle = null;
        foreach (var candidate in current.Keys)
        {
            var altered = current.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.Ordinal);
            altered[candidate] = new string(altered[candidate][0] == '0' ? '1' : '0',
                altered[candidate].Length);
            if (!BundleReads.StillCurrent(altered, reads)) { changedBundle = altered; break; }
        }
        Assert.NotNull(changedBundle);
        Assert.False(cache.TryServePrepared(identity, changedBundle, baseline,
            g.At(Path.Combine("misses", (++miss).ToString(), "body.glb"))));
    }

    [Fact]
    public void Prepared_restore_keeps_stock_reference_hits_when_the_authored_target_misses()
    {
        using var g = new TempGame();
        var install = Fixture(g);
        var identity = new RiggedGlbCache.Identity("test", "subject", "roster");
        var cache = new RiggedGlbCache(g.At("rigs"));
        string reads = BundleReads.Of(install.Vfs.Catalog,
            BundleReads.ContentHashLookup(install.Vfs.Manifest), new[] { BodyLogical });
        string bodyRig = WriteMinimalGlb(g.At("body-offer.glb"));
        string clothRig = WriteMinimalGlb(g.At("cloth-offer.glb"));
        string edit = WriteMinimalGlb(g.At("edit.glb"));
        var cold = new[]
        {
            new MainWindowViewModel.SessionPartPlan("body", BodyMesh, bodyRig,
                g.At(Path.Combine("warm", "body.glb")), false, null, edit),
            new MainWindowViewModel.SessionPartPlan("cloth", SiblingMesh, clothRig,
                g.At(Path.Combine("warm", "cloth.glb")), false, null, null),
        };
        var coldKeys = MainWindowViewModel.PreparedSessionPartKeys(identity, cold);
        Assert.True(cache.TryStorePrepared(identity, reads, new RiggedGlbCache.PreparedArtifact(
            coldKeys[cold[0].Prepared], WriteMinimalGlb(g.At("cold-body.glb")))));
        Assert.True(cache.TryStorePrepared(identity, reads, new RiggedGlbCache.PreparedArtifact(
            coldKeys[cold[1].Prepared], WriteMinimalGlb(g.At("cold-cloth.glb")))));

        File.WriteAllBytes(edit, File.ReadAllBytes(edit).Append((byte)23).ToArray());
        var warmKeys = MainWindowViewModel.PreparedSessionPartKeys(identity, cold);
        var restored = MainWindowViewModel.TryRestoreSessionPreparedParts(cache, identity, install.Vfs,
            cold, warmKeys);

        Assert.Equal(new[] { cold[1].Prepared }, restored);
        Assert.False(File.Exists(cold[0].Prepared));
        Assert.True(File.Exists(cold[1].Prepared));
    }

    [Fact]
    public void Static_combined_part_skips_preparation_while_its_session_metadata_survives()
    {
        using var g = new TempGame();
        string prepared = g.At("static.glb");
        var plan = new MainWindowViewModel.SessionPartPlan("prop", "prop_lod0", g.At("missing-rig.glb"),
            prepared, true, null, null);

        Assert.Empty(MainWindowViewModel.PrepareSessionParts(new[] { plan }, skipStatic: true));
        Assert.False(File.Exists(prepared));
        var session = MainWindowViewModel.SessionPartForBlender(plan.SlotName, edited: false,
            writable: false, unskinned: plan.Static, editId: null, edits: null,
            defaultEditName: null, viewportVisible: false);
        Assert.Equal("prop_lod0", session.Name);
        Assert.True(session.Unskinned);
        Assert.False(session.IsWritable);
        Assert.False(session.IsViewportVisible);
    }

    [Fact]
    public void Canonical_spec_fingerprint_ignores_run_paths_but_changes_with_the_route_and_roster()
    {
        using var g = new TempGame();
        var a = Specs(g, "run-a");
        var b = Specs(g, "run-b");
        var sameA = AssetExporter.RiggedBuildFingerprint(Outfit, "Vesna", null, a, false);
        var sameB = AssetExporter.RiggedBuildFingerprint(Outfit, "Vesna", null, b, false);
        Assert.Equal(sameA, sameB);

        var sibling = b[1];
        b[1] = (sibling.Part, sibling.SourceBundle, sibling.MeshName,
            g.At("now-visible.glb"), sibling.BakedRest, sibling.PathId, sibling.EditedGlb);
        Assert.NotEqual(sameA, AssetExporter.RiggedBuildFingerprint(Outfit, "Vesna", null, b, false));
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(BodyMesh, "body", BodyLogical, 0, true, VisibilityOverride.None),
        });
        Assert.NotEqual(sameA, AssetExporter.RiggedBuildFingerprint(Outfit, "Vesna", roster, a, false));
    }

    [Fact]
    public void Cache_instance_seam_reuses_one_instance_per_redirected_root()
    {
        using var g = new TempGame();
        var vm = new MainWindowViewModel(startLoad: false);
        int made = 0;
        vm.RiggedGlbCacheFactory = root => { made++; return new RiggedGlbCache(root); };

        var first = vm.RiggedGlbCacheAt(g.At("one"));
        Assert.Same(first, vm.RiggedGlbCacheAt(g.At("one")));
        Assert.NotSame(first, vm.RiggedGlbCacheAt(g.At("two")));
        Assert.Equal(2, made);
    }

    private static string WriteMinimalGlb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        bytes[12] = 7;
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
