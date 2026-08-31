using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="RosterSnapshot"/> caches the launch confirm-fill. A game update must miss and refill, while a
/// mod operation that rewrites the catalog bytes under the same version must still HIT — and so must every
/// input that decides which subjects the fill was offered, because the snapshot branch does no reads and a
/// missing id reads as "not confirmed" rather than as "not asked".
/// </summary>
public class RosterSnapshotTests
{
    private static CatalogIndex CatalogFor(Outfit outfit, params string[] bundles)
    {
        string address = GameVfs.PrefabAddress("Character/Player", outfit.Stem);
        return CatalogIndex.ForTest(new[] { (address, bundles[0]) },
            new[] { (address, bundles) }, bundles.Select(bundle => (bundle, bundle + ".id")));
    }

    private static Func<string, string?> Content(string suffix = "A") =>
        internalId => internalId + "-" + suffix;

    private static Dictionary<long, List<string>> SampleRoster() => new()
    {
        [1071] = new() { "body", "hair", "face" },
        [1081] = new() { "body" },
    };

    [Fact]
    public void SameVersion_Hits_AndRoundTripsTheRoster()
    {
        using var g = new TempGame();
        var path = g.At("roster_24535.json");
        RosterSnapshot.Save(path, "24535", SampleRoster());

        var back = RosterSnapshot.TryLoad(path, "24535");
        Assert.NotNull(back);
        Assert.Equal(new[] { "body", "hair", "face" }, back![1071]);
        Assert.Equal(new[] { "body" }, back[1081]);
    }

    [Fact]
    public void Per_outfit_row_survives_a_catalog_version_change_when_shape_and_bundle_content_stand()
    {
        using var g = new TempGame();
        var outfit = new Outfit(1071, "VesnaSSR01", OutfitKind.Base);
        var catalog = CatalogFor(outfit, "prefab.bundle", "material.bundle");
        var row = RosterSnapshot.CreateRow(catalog, Content(), outfit,
            new[] { "prefab.bundle", "material.bundle" }, new[] { "body", "hair" });
        RosterSnapshot.SaveRows(g.At("roster_24535.json"), "24535", new[] { row });

        var reused = RosterSnapshot.LoadReusable(g.At("roster_24600.json"), catalog, Content(),
            new[] { outfit });

        var back = Assert.Single(reused).Value;
        Assert.True(back.Confirmed);
        Assert.Equal(new[] { "body", "hair" }, back.Parts);
    }

    [Fact]
    public void Per_outfit_row_invalidates_when_catalog_shape_or_read_bundle_content_moves()
    {
        using var g = new TempGame();
        var outfit = new Outfit(1071, "VesnaSSR01", OutfitKind.Base);
        var original = CatalogFor(outfit, "prefab.bundle", "material.bundle");
        var row = RosterSnapshot.CreateRow(original, Content(), outfit,
            new[] { "prefab.bundle", "material.bundle" }, new[] { "body" });
        RosterSnapshot.SaveRows(g.At("roster_24535.json"), "24535", new[] { row });
        var movedShape = CatalogFor(outfit, "prefab.bundle", "material.bundle", "extra.bundle");

        Assert.Empty(RosterSnapshot.LoadReusable(g.At("roster_24600.json"), movedShape, Content(),
            new[] { outfit }));
        Assert.Empty(RosterSnapshot.LoadReusable(g.At("roster_24600.json"), original, Content("B"),
            new[] { outfit }));
    }

    [Fact]
    public void Per_outfit_snapshot_preserves_a_cleanly_dropped_row_explicitly()
    {
        using var g = new TempGame();
        var outfit = new Outfit(1071, "VesnaSSR01", OutfitKind.Base);
        var catalog = CatalogFor(outfit, "prefab.bundle");
        var row = RosterSnapshot.CreateRow(catalog, Content(), outfit,
            new[] { "prefab.bundle" }, parts: null);
        RosterSnapshot.SaveRows(g.At("roster_24535.json"), "24535", new[] { row });

        var back = Assert.Single(RosterSnapshot.LoadReusable(g.At("roster_24600.json"), catalog,
            Content(), new[] { outfit })).Value;

        Assert.False(back.Confirmed);
        Assert.Null(back.Parts);
    }

    [Fact]
    public void Roster_fill_cache_single_flights_reads_and_stays_within_its_byte_budget()
    {
        int loads = 0;
        var cache = new RosterFillCache(_ =>
        {
            Interlocked.Increment(ref loads);
            Thread.SpinWait(100_000);
            return new byte[6];
        }, byteBudget: 8);

        Parallel.For(0, 16, _ => Assert.Equal(6, cache.Read("shared")!.Length));
        cache.Read("second");

        Assert.Equal(2, loads);
        Assert.True(cache.CachedBytes <= cache.ByteBudget);
    }

    [Fact]
    public void DifferentVersion_Misses()
    {
        // a game update bumps the catalog version → the snapshot for the old version must not be served.
        using var g = new TempGame();
        var path = g.At("roster.json");
        RosterSnapshot.Save(path, "24535", SampleRoster());
        Assert.Null(RosterSnapshot.TryLoad(path, "24600"));
    }

    [Fact]
    public void SameVersion_Hits_EvenAfterTheFileIsRewritten()
    {
        // A mod operation rewrites the bytes but not the version, so the invalidation must key on the
        // VERSION and never the file bytes.
        using var g = new TempGame();
        var path = g.At("roster_24535.json");
        RosterSnapshot.Save(path, "24535", SampleRoster());
        RosterSnapshot.Save(path, "24535", new Dictionary<long, List<string>> { [9001] = new() { "body" } });

        var back = RosterSnapshot.TryLoad(path, "24535");
        Assert.NotNull(back);
        Assert.True(back!.ContainsKey(9001));
        Assert.False(back.ContainsKey(1071));
    }

    [Fact]
    public void TwoOutfitsSharingAModelStem_AreSeparateEntries()
    {
        // Two summon rows can name the SAME model stem and still be two subjects. Keyed by stem they share
        // one entry and whichever was written second answers for both — including for a row the fill
        // dropped. At the id grain each answers only for itself.
        using var g = new TempGame();
        var path = g.At("roster_24535.json");
        RosterSnapshot.Save(path, "24535", new Dictionary<long, List<string>>
        {
            [106101] = new() { "addon" },              // OttilieSSR01_Summon, the defense construct
            [106111] = new() { "addon", "cloth1" },    // the offense construct — same stem, its own parts
        });

        var back = RosterSnapshot.TryLoad(path, "24535");
        Assert.NotNull(back);
        Assert.Equal(new[] { "addon" }, back![106101]);
        Assert.Equal(new[] { "addon", "cloth1" }, back[106111]);
        Assert.False(back.ContainsKey(10641));       // and an id nobody wrote is simply absent
    }

    [Fact]
    public void MissingFile_Misses()
    {
        using var g = new TempGame();
        Assert.Null(RosterSnapshot.TryLoad(g.At("nope.json"), "24535"));
    }

    // ---- the curated set is part of the key -------------------------------------------------------

    /// <summary>Rewrite one property of a saved snapshot, standing in for a file written by a build whose
    /// curated table differed from this one's.</summary>
    private static void Rewrite(string path, System.Action<JsonObject> edit)
    {
        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(doc);
        File.WriteAllText(path, doc.ToJsonString());
    }

    [Fact]
    public void ADifferentCuratedSet_Misses_SoAnAddedSubjectCannotStayInvisible()
    {
        // The curated table is CODE and moves independently of the catalog, so on an unchanged catalog a
        // snapshot written before an entry existed carries no row for it — and no row reads as NOT
        // CONFIRMED, which drops that subject from Pick on every launch, forever. There is no read to
        // re-derive it from: the miss has to come from the key.
        using var g = new TempGame();
        var path = g.At("roster_24535.json");
        RosterSnapshot.Save(path, "24535", SampleRoster());
        Assert.NotNull(RosterSnapshot.TryLoad(path, "24535"));

        Rewrite(path, o => o["CuratedSet"] = "0000000000000000");
        Assert.Null(RosterSnapshot.TryLoad(path, "24535"));
    }

    [Fact]
    public void ASnapshotWithNoCuratedSetAtAll_Misses()
    {
        // A file written before the curated set joined the key: it cannot say which subjects it was filled
        // over, so it can only be refilled.
        using var g = new TempGame();
        var path = g.At("roster_24535.json");
        RosterSnapshot.Save(path, "24535", SampleRoster());

        Rewrite(path, o => o.Remove("CuratedSet"));
        Assert.Null(RosterSnapshot.TryLoad(path, "24535"));
    }
}
