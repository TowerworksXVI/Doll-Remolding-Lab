using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Tables;
using Remold.Core.Workbench;
using Xunit;
using Xunit.Abstractions;

namespace Remold.Core.Tests;

/// <summary>
/// The curated table against the REAL game install: the one claim the synthetic corpora cannot make, which
/// is that these routes point at something. Everything here is a fact about shipped data, so the test is
/// worth exactly as much as the install it ran against.
///
/// <para><b>Opt-in by environment.</b> With no locatable install the body does not run — a machine without
/// the game must not fail the suite. The same goes for a bundle held open by the running game, which
/// surfaces as a sharing violation and means BUSY, never a finding. Both outcomes are written to the test
/// output so a green run can be told apart from a skipped one.</para>
/// </summary>
public class CuratedSkinsLiveTests
{
    private readonly ITestOutputHelper _out;

    public CuratedSkinsLiveTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EveryCuratedSubject_ResolvesItsPrefabAtItsPinnedRoot_InTheLiveInstall()
    {
        if (GameLocator.Find() is not { } gameRoot)
        {
            _out.WriteLine("skipped: no GF2 install located on this machine");
            return;
        }
        GameVfs vfs;
        try
        {
            if (GameVfs.TryLoad(gameRoot) is not { } loaded)
            {
                _out.WriteLine($"skipped: no readable catalog or manifest under {gameRoot}");
                return;
            }
            vfs = loaded;
        }
        catch (IOException e)
        {
            _out.WriteLine($"skipped: the install is busy ({e.Message})");
            return;
        }
        _out.WriteLine($"catalog {vfs.CatalogVersion} at {gameRoot}");

        var misses = new List<string>();
        foreach (var skin in CuratedSkins.All)
        {
            var outfit = skin.ToOutfit();
            IReadOnlyList<SubjectCandidate> candidates;
            try
            {
                candidates = SubjectScope.Build(vfs.Catalog, vfs.TryDeobfuscateLogical, outfit).Candidates;
            }
            catch (IOException e)
            {
                _out.WriteLine($"skipped at '{skin.Stem}': the install is busy ({e.Message})");
                return;
            }
            var root = candidates.FirstOrDefault(c =>
                string.Equals(c.Root, skin.Stem, StringComparison.Ordinal));
            _out.WriteLine($"{skin.Display,-24} {skin.Stem,-20} "
                + $"{(root is null ? "MISS" : $"{root.Bundle} ({candidates.Count} candidate(s))")}");
            if (root is null)
                misses.Add($"'{skin.Display}' ({skin.Stem}) resolved no prefab at its own root");
        }

        // A miss means the shipped data moved under the table — a catalog update, or a wrong entry. Either
        // way the subject is unreachable in the app, so this is the one place it should be noticed.
        Assert.Empty(misses);
    }
}
