using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Remold.Core;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The rig cache's correctness boundary, before any open route consumes it.</summary>
public class RiggedGlbCacheTests
{
    private static RiggedGlbCache.Identity Id(string catalog = "24535", string subject = "subject-a",
        string roster = "roster-a") => new(catalog, subject, roster);

    private static (string Reads, IReadOnlyDictionary<string, string> Current) Reads(string content = "content-a")
    {
        var bundle = NameKey.Of("bundle-a");
        var value = NameKey.Of(content);
        return (bundle + value, new Dictionary<string, string>(StringComparer.Ordinal) { [bundle] = value });
    }

    private static string WriteGlb(TempGame g, string name, byte marker)
    {
        var path = g.At(Path.Combine("source", name + ".glb"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        bytes[12] = marker;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteMaps(TempGame g, string name, byte marker)
    {
        var path = g.At(Path.Combine("source", name + ".maps.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"marker\":" + marker + "}");
        return path;
    }

    private static RiggedGlbCache.Artifact Artifact(TempGame g, string key, byte marker, bool maps = true) =>
        new(key, WriteGlb(g, key + "-" + marker, marker), maps ? WriteMaps(g, key + "-" + marker, marker) : null);

    [Fact]
    public void A_complete_game_side_route_is_served_with_its_optional_sidecars()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var identity = Id();
        var reads = Reads();

        Assert.True(cache.TryStore(identity, reads.Reads, Artifact(g, "body", 0x21),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        Assert.True(cache.TryStore(identity, reads.Reads, Artifact(g, "hair", 0x42, maps: false),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));

        var destination = g.At(Path.Combine("run", "parts"));
        Assert.True(cache.TryServe(identity, reads.Current, new[]
        {
            new RiggedGlbCache.Request("body", "body.glb"),
            new RiggedGlbCache.Request("hair", "hair.glb"),
        }, destination));

        Assert.Equal(0x21, File.ReadAllBytes(Path.Combine(destination, "body.glb"))[12]);
        Assert.Equal(0x42, File.ReadAllBytes(Path.Combine(destination, "hair.glb"))[12]);
        Assert.True(File.Exists(Path.Combine(destination, "body.maps.json")));
        Assert.False(File.Exists(Path.Combine(destination, "hair.maps.json")));
    }

    [Fact]
    public void The_completion_manifest_states_the_game_side_purity_invariant()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        var identity = Id();
        Assert.True(cache.TryStore(identity, reads.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));

        var path = Path.Combine(cache.ArtifactDirectoryFor(identity, "body"), "complete.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var root = manifest.RootElement;
        Assert.Equal(RiggedGlbCache.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("game-side", root.GetProperty("purity").GetString());
        Assert.True(root.GetProperty("buildCompleted").GetBoolean());
        Assert.False(root.GetProperty("hadTransientFailures").GetBoolean());
        Assert.False(root.GetProperty("wasCanceled").GetBoolean());
        Assert.False(root.GetProperty("hadProjectAuthoredContent").GetBoolean());
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void An_impure_incomplete_or_degraded_build_is_never_published(bool gameSideOnly,
        bool transient, bool canceled, bool projectAuthored)
    {
        using var g = new TempGame();
        var root = g.At("rigs");
        var cache = new RiggedGlbCache(root);
        var reads = Reads();
        var state = new RiggedGlbCache.BuildState(gameSideOnly, transient, canceled, projectAuthored);

        Assert.False(cache.TryStore(Id(), reads.Reads, Artifact(g, "body", 1), state));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Catalog_subject_and_roster_identity_changes_are_disjoint_misses()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        Assert.True(cache.TryStore(Id(), reads.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));

        Assert.False(cache.TryServe(Id(catalog: "26932"), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("catalog-miss")));
        Assert.False(cache.TryServe(Id(subject: "subject-b"), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("subject-miss")));
        Assert.False(cache.TryServe(Id(roster: "roster-b"), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("roster-miss")));
        Assert.False(Directory.Exists(g.At("catalog-miss")));
        Assert.False(Directory.Exists(g.At("subject-miss")));
        Assert.False(Directory.Exists(g.At("roster-miss")));
    }

    [Fact]
    public void A_same_catalog_bundle_rewrite_is_revalidated_and_misses()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var written = Reads("old-content");
        Assert.True(cache.TryStore(Id(), written.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));

        var now = Reads("rewritten-in-place");
        var destination = g.At("bundle-miss");
        Assert.False(cache.TryServe(Id(), now.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, destination));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void An_unknown_catalog_version_can_neither_publish_nor_serve()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        var unknown = Id(catalog: GameInfo.UnknownVersion);

        Assert.False(cache.TryStore(unknown, reads.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        Assert.False(cache.TryServe(unknown, reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("unknown")));
    }

    [Fact]
    public void One_corrupt_member_makes_a_multi_part_route_miss_without_a_partial_destination()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        Assert.True(cache.TryStore(Id(), reads.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        Assert.True(cache.TryStore(Id(), reads.Reads, Artifact(g, "hair", 2),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        File.WriteAllText(Path.Combine(cache.ArtifactDirectoryFor(Id(), "hair"), "rig.glb"), "corrupt");

        var destination = g.At(Path.Combine("run", "parts"));
        Assert.False(cache.TryServe(Id(), reads.Current, new[]
        {
            new RiggedGlbCache.Request("body", "body.glb"),
            new RiggedGlbCache.Request("hair", "hair.glb"),
        }, destination));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateDirectories(g.At("run"), "*.tmp"));
    }

    [Fact]
    public void A_missing_sidecar_or_unknown_manifest_schema_is_a_miss()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        Assert.True(cache.TryStore(Id(), reads.Reads, Artifact(g, "body", 1),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        var entry = cache.ArtifactDirectoryFor(Id(), "body");

        File.Delete(Path.Combine(entry, "rig.maps.json"));
        Assert.False(cache.TryServe(Id(), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("missing-sidecar")));

        Assert.True(cache.TryStore(Id(), reads.Reads, Artifact(g, "body", 2),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        var complete = Path.Combine(entry, "complete.json");
        File.WriteAllText(complete, File.ReadAllText(complete).Replace(
            "\"schemaVersion\": 1", "\"schemaVersion\": 999", StringComparison.Ordinal));
        Assert.False(cache.TryServe(Id(), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("schema-miss")));
    }

    [Fact]
    public void Invalid_payloads_and_store_IO_failures_are_quiet_and_leave_no_valid_or_temp_entry()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        var invalid = g.At("source/not-a-glb.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
        File.WriteAllText(invalid, "not a glb");

        Assert.False(cache.TryStore(Id(), reads.Reads, new RiggedGlbCache.Artifact("body", invalid, null),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        var entry = cache.ArtifactDirectoryFor(Id(), "body");
        Assert.False(File.Exists(Path.Combine(entry, "complete.json")));
        Assert.Empty(Directory.EnumerateFiles(entry, "*.tmp", SearchOption.AllDirectories));

        var blockedRoot = g.At("blocked-root");
        File.WriteAllText(blockedRoot, "a file where the cache directory belongs");
        var blocked = new RiggedGlbCache(blockedRoot);
        Assert.False(blocked.TryStore(Id(), reads.Reads, Artifact(g, "hair", 2),
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
    }

    [Fact]
    public async Task Concurrent_publications_leave_one_complete_self_consistent_generation()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var reads = Reads();
        var first = Artifact(g, "body-first", 0x11);
        var second = Artifact(g, "body-second", 0x22);
        first = first with { Key = "body" };
        second = second with { Key = "body" };

        var stores = await Task.WhenAll(
            Task.Run(() => cache.TryStore(Id(), reads.Reads, first,
                RiggedGlbCache.BuildState.SuccessfulGameBuild)),
            Task.Run(() => cache.TryStore(Id(), reads.Reads, second,
                RiggedGlbCache.BuildState.SuccessfulGameBuild)));

        Assert.All(stores, Assert.True);
        var destination = g.At("concurrent-hit");
        Assert.True(cache.TryServe(Id(), reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, destination));
        Assert.Contains(File.ReadAllBytes(Path.Combine(destination, "body.glb"))[12], new byte[] { 0x11, 0x22 });
        Assert.Empty(Directory.EnumerateFiles(cache.SubjectDirectoryFor(Id()), "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Multi_part_publication_measures_the_tree_once_and_a_warm_serve_does_not_measure_it()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"));
        var identity = Id();
        var reads = Reads();
        var requests = new List<RiggedGlbCache.Request>();
        for (int i = 0; i < 12; i++)
        {
            string key = "part-" + i;
            Assert.True(cache.TryStore(identity, reads.Reads, Artifact(g, key, (byte)i),
                RiggedGlbCache.BuildState.SuccessfulGameBuild));
            requests.Add(new RiggedGlbCache.Request(key, key + ".glb"));
        }
        Assert.Equal(0, cache.FullTreeEnumerations);

        cache.CompleteSubjectPublication(identity);

        Assert.Equal(1, cache.FullTreeEnumerations);
        Assert.True(cache.TryServe(identity, reads.Current, requests, g.At("warm")));
        Assert.Equal(1, cache.FullTreeEnumerations);
    }

    [Fact]
    public void Pruning_is_whole_subject_LRU_and_never_removes_the_subject_being_published()
    {
        using var g = new TempGame();
        var cache = new RiggedGlbCache(g.At("rigs"), highWaterBytes: 1_100_000, pruneTargetBytes: 850_000);
        var reads = Reads();
        var a = Id(subject: "subject-a");
        var b = Id(subject: "subject-b");
        var c = Id(subject: "subject-c");

        Assert.True(cache.TryStore(a, reads.Reads, Artifact(g, "body-a", 1) with { Key = "body" },
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        Assert.True(cache.TryStore(a, reads.Reads, Artifact(g, "hair-a", 2) with { Key = "hair" },
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        File.WriteAllBytes(Path.Combine(cache.SubjectDirectoryFor(a), "filler.bin"), new byte[400_000]);

        Assert.True(cache.TryStore(b, reads.Reads, Artifact(g, "body-b", 3) with { Key = "body" },
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        File.WriteAllBytes(Path.Combine(cache.SubjectDirectoryFor(b), "filler.bin"), new byte[400_000]);
        File.SetLastWriteTimeUtc(Path.Combine(cache.SubjectDirectoryFor(a), ".access"),
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(cache.SubjectDirectoryFor(b), ".access"),
            new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // A real hit advances A, making untouched B the LRU subject.
        Assert.True(cache.TryServe(a, reads.Current,
            new[] { new RiggedGlbCache.Request("body", "body.glb") }, g.At("touch-a")));

        Assert.True(cache.TryStore(c, reads.Reads, Artifact(g, "body-c", 4) with { Key = "body" },
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        File.WriteAllBytes(Path.Combine(cache.SubjectDirectoryFor(c), "filler.bin"), new byte[400_000]);
        // Publishing another member triggers the over-high-water check while C is the protected subject.
        Assert.True(cache.TryStore(c, reads.Reads, Artifact(g, "hair-c", 5) with { Key = "hair" },
            RiggedGlbCache.BuildState.SuccessfulGameBuild));
        cache.CompleteSubjectPublication(c);

        Assert.True(Directory.Exists(cache.SubjectDirectoryFor(a)));
        Assert.False(Directory.Exists(cache.SubjectDirectoryFor(b)));
        Assert.True(Directory.Exists(cache.SubjectDirectoryFor(c)));
        Assert.False(Directory.Exists(cache.ArtifactDirectoryFor(b, "body")));
        Assert.True(Directory.Exists(cache.ArtifactDirectoryFor(a, "body")));
        Assert.True(Directory.Exists(cache.ArtifactDirectoryFor(a, "hair")));
    }
}
