using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The per-subject resolution scope: formula-priority candidate ordering over a hand-authored catalog, the
/// lazy scope-bounded CAB→bundle walk, and the blacklist policy (an empty scope, enforced at this surface).
/// </summary>
public class SubjectScopeTests
{
    // ---- (a) candidate ordering: context hits first (formula priority), closure by root ordinal,
    //          duplicate roots deduped first-wins ----
    [Fact]
    public void Candidates_ContextHitsFirst_ThenClosureByRootOrdinal_DedupedByRoot()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        // the stem's own prefab (the formula hit) + a DUPLICATE copy of the same root in the closure
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "hit.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());
        WorkbenchPrefab.Build(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "dupe.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());
        // two closure-discovered sibling roots, authored out of name order on purpose
        WorkbenchPrefab.Build(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "sibZ.bundle", rootName: "c_TestySSR01_z_slg_skin_model",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_propz_lod0", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());
        WorkbenchPrefab.Build(Path.Combine(abw, new string('4', 32) + ".bundle"),
            "sibA.bundle", rootName: "c_TestySSR01_a_slg_skin_model",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_propa_lod0", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var address = GameVfs.PrefabAddress("Character/Player", "TestySSR01");
        var catalog = CatalogIndex.ForTest(
            new[] { (address, "hit.bundle") },
            // closure order deliberately z-before-a: candidate order must come from ROOT ordinal, not dep order
            new[] { (address, new[] { "hit.bundle", "sibZ.bundle", "sibA.bundle", "dupe.bundle" }) });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);

        var scope = SubjectScope.Build(catalog, deobfuscate, outfit);

        // hit bundle first in the scope, closure after, deduped
        Assert.Equal(new[] { "hit.bundle", "sibZ.bundle", "sibA.bundle", "dupe.bundle" }, scope.ScopeBundles);
        // the formula hit first, then closure roots in ORDINAL root order, the duplicate deduped by root
        Assert.Equal(new[] { "TestySSR01", "c_TestySSR01_a_slg_skin_model", "c_TestySSR01_z_slg_skin_model" },
            scope.Candidates.Select(c => c.Root).ToArray());
        Assert.Equal("hit.bundle", scope.Candidates[0].Bundle);   // not the dupe copy
        Assert.Empty(scope.Problems);
    }

    // ---- (b) BundleForCab: lazy, scope-bounded, cached; unknown CAB → null ----
    [Fact]
    public void BundleForCab_WalksTheScopeLazily_CachesPairs_UnknownCabIsNull()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            "prefab.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "mat1.bundle", materialName: "M_one", materialPathId: 21,
            texEnvs: Array.Empty<(string, int, long)>(), externalCabs: Array.Empty<string>(),
            cabName: "CAB-one");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "mat2.bundle", materialName: "M_two", materialPathId: 22,
            texEnvs: Array.Empty<(string, int, long)>(), externalCabs: Array.Empty<string>(),
            cabName: "CAB-two");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var address = GameVfs.PrefabAddress("Character/Player", "TestySSR01");
        var catalog = CatalogIndex.ForTest(
            new[] { (address, "prefab.bundle") },
            new[] { (address, new[] { "prefab.bundle", "mat1.bundle", "mat2.bundle" }) });
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);

        var deobfuscates = new Dictionary<string, int>(StringComparer.Ordinal);
        byte[]? CountingDeobfuscate(string logical)
        {
            deobfuscates[logical] = deobfuscates.GetValueOrDefault(logical) + 1;
            return deobfuscate(logical);
        }
        var scope = SubjectScope.Build(catalog, CountingDeobfuscate, outfit);

        // resolving the LAST bundle's CAB walks the whole scope once, caching the pairs on the way …
        Assert.Equal("mat2.bundle", scope.BundleForCab("CAB-two"));
        Assert.Equal(1, deobfuscates["mat1.bundle"]);
        // … so the earlier bundle's CAB answers from the cache — no bundle is deobfuscated again
        Assert.Equal("mat1.bundle", scope.BundleForCab("CAB-one"));
        Assert.Equal(1, deobfuscates["mat1.bundle"]);
        Assert.Equal(1, deobfuscates["mat2.bundle"]);

        // a CAB nothing in scope provides is null — the caller's loud FAILED-material path
        Assert.Null(scope.BundleForCab("CAB-not-here"));
    }

    // ---- (c) blacklisted stem → an EMPTY scope, even when the catalog resolves its prefab ----
    [Fact]
    public void BlacklistedStem_BuildsAnEmptyScope_WithoutReadingAnything()
    {
        // The catalog HAS a formula row for the blacklisted stem; the policy refuses here regardless. Do
        // not remove.
        var address = GameVfs.PrefabAddress("BarrackModel/Character", "Helena");
        var catalog = CatalogIndex.ForTest(
            new[] { (address, "ilse.bundle") },
            new[] { (address, new[] { "ilse.bundle" }) });
        var outfit = new Outfit(0, "Helena", OutfitKind.Other);

        byte[]? RefusingDeobfuscate(string logical) =>
            throw new InvalidOperationException($"an empty scope must never read a bundle (asked for '{logical}')");
        var scope = SubjectScope.Build(catalog, RefusingDeobfuscate, outfit);

        Assert.Empty(scope.ScopeBundles);
        Assert.Empty(scope.Candidates);
        Assert.Null(scope.BundleForCab("CAB-anything"));
        Assert.Empty(scope.Problems);
    }
}
