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
    /// <summary>Build the vfs. <paramref name="bundles"/> maps each logical bundle id to the 32-hex
    /// basename of its on-disk fixture file (whole-file singles, the fixture shape).</summary>
    public static GameVfs Create(string gameRoot,
        IEnumerable<(string Address, string OwnerBundle)> rows,
        IEnumerable<(string Address, string[] Deps)>? depRows = null,
        params (string Logical, string PhysHash)[] bundles)
    {
        var abw = Path.Combine(gameRoot, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var entries = bundles
            .Select((b, i) => (b.PhysHash + ".bundle", FakeGff.Stub(b.PhysHash, 0, 0, (byte)(i + 1))))
            .ToArray();
        var manifestPath = Path.Combine(abw, GffManifest.ManifestHash + ".bundle");
        FakeGff.Write(manifestPath, entries);
        var catalog = CatalogIndex.ForTest(rows, depRows,
            bundles.Select(b => (b.Logical, b.PhysHash + ".bundle")));
        return GameVfs.ForTest(abw, "test", catalog, GffManifest.Read(manifestPath));
    }
}
