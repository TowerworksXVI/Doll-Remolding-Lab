using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A <see cref="GameVfs"/> over a TempGame's synthetic <c>AssetBundles_Windows</c>: a
/// <see cref="FakeGff"/> manifest with one whole-file stub per (logical, physical) pair, joined through
/// catalog bundle-name rows — so tests exercise the REAL forward-locate mechanics, not a stub.
/// </summary>
internal static class TestVfs
{
    /// <summary>One bundle of a fixture install: its logical id, the 32-hex basename of its on-disk file,
    /// what the manifest stub SAYS its content is (<see cref="SubSeed"/> fills all 16 bytes — the identity
    /// content-keyed caches read, so a fixture rewrites it to stand for a game update), and whether the
    /// manifest names it at all. <see cref="InManifest"/> false is a bundle the catalog maps and the
    /// manifest does not: unlocatable, so its bytes cannot be read either.</summary>
    public readonly record struct Bundle(string Logical, string PhysHash, byte SubSeed, bool InManifest);

    /// <summary>Build the vfs. <paramref name="bundles"/> maps each logical bundle id to the 32-hex
    /// basename of its on-disk fixture file (whole-file singles, the fixture shape), each with a distinct
    /// stub content identity.</summary>
    public static GameVfs Create(string gameRoot,
        IEnumerable<(string Address, string OwnerBundle)> rows,
        IEnumerable<(string Address, string[] Deps)>? depRows = null,
        params (string Logical, string PhysHash)[] bundles) =>
        CreateWith(gameRoot, rows, depRows,
            bundles.Select((b, i) => new Bundle(b.Logical, b.PhysHash, (byte)(i + 1), true)).ToArray());

    /// <summary>As <see cref="Create"/>, over bundles whose stub identity and manifest presence the caller
    /// chooses — the shapes a content-keyed cache turns on.</summary>
    public static GameVfs CreateWith(string gameRoot,
        IEnumerable<(string Address, string OwnerBundle)> rows,
        IEnumerable<(string Address, string[] Deps)>? depRows,
        params Bundle[] bundles)
    {
        var abw = Path.Combine(gameRoot, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var entries = bundles
            .Where(b => b.InManifest)
            .Select(b => (b.PhysHash + ".bundle", FakeGff.Stub(b.PhysHash, 0, 0, b.SubSeed)))
            .ToArray();
        var manifestPath = Path.Combine(abw, GffManifest.ManifestHash + ".bundle");
        FakeGff.Write(manifestPath, entries);
        // the catalog names every bundle, manifest entry or not: the join is what a locate walks first
        var catalog = CatalogIndex.ForTest(rows, depRows,
            bundles.Select(b => (b.Logical, b.PhysHash + ".bundle")));
        return GameVfs.ForTest(abw, "test", catalog, GffManifest.Read(manifestPath));
    }
}
