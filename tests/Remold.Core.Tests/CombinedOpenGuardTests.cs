using System.Linq;
using Remold.Core.Export;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The cached combined-glb reuse gate, both halves: WHEN a cached session glb may be launched
/// as-is, and when it must be rebuilt — a changed part set, a changed edit, a game update, or a file this
/// app didn't publish — plus whether a fresh build may be left cached for the next open at all.</summary>
public class CombinedOpenGuardTests
{
    private static readonly string[] NoTextures = System.Array.Empty<string>();

    [Fact]
    public void CombinedFingerprint_StableForSameInputs_ChangesWhenThePartSetOrCatalogChanges()
    {
        var a = new (string, string, string, string?)[] { ("cloth1", "aa", "obj1", null), ("hair", "bb", "obj2", null) };
        Assert.Equal(
            AssetExporter.CombinedFingerprint("cat-1", a, NoTextures),
            AssetExporter.CombinedFingerprint("cat-1", a.ToArray(), NoTextures));   // identical inputs → identical fingerprint

        // a removed part, an added part, a re-addressed bundle, and a game update each change it.
        var baseline = AssetExporter.CombinedFingerprint("cat-1", a, NoTextures);
        Assert.NotEqual(baseline, AssetExporter.CombinedFingerprint("cat-1",
            new (string, string, string, string?)[] { ("cloth1", "aa", "obj1", null) }, NoTextures));
        Assert.NotEqual(baseline, AssetExporter.CombinedFingerprint("cat-1",
            new (string, string, string, string?)[] { ("cloth1", "aa", "obj1", null), ("hair", "bb", "obj2", null), ("face", "cc", "obj3", null) }, NoTextures));
        Assert.NotEqual(baseline, AssetExporter.CombinedFingerprint("cat-1",
            new (string, string, string, string?)[] { ("cloth1", "ZZ", "obj1", null), ("hair", "bb", "obj2", null) }, NoTextures));
        Assert.NotEqual(baseline, AssetExporter.CombinedFingerprint("cat-2", a, NoTextures));
    }

    /// <summary>The stale-glb regression on the TEXTURE side: a map repainted since the cached session was
    /// built must force a rebuild too. Without it the modder opens Blender on a glb with their pre-edit
    /// image baked in, and the send-back carries that image back over the outfit.</summary>
    [Fact]
    public void CombinedFingerprint_ChangesWhenAnEmbeddedTextureFileChanges()
    {
        using var temp = new TempWorkspace();
        var tex = System.IO.Path.Combine(temp.Root, "face.aa.png");
        var other = System.IO.Path.Combine(temp.Root, "body.aa.png");
        System.IO.File.WriteAllBytes(tex, new byte[] { 1, 2, 3, 4 });
        System.IO.File.WriteAllBytes(other, new byte[] { 5, 5 });
        var parts = new (string, string, string, string?)[] { ("cloth1", "aa", "obj1", null) };

        var before = AssetExporter.CombinedFingerprint("cat-1", parts, new[] { tex, other });

        // same inputs, nothing touched → the cache still reuses
        Assert.Equal(before, AssetExporter.CombinedFingerprint("cat-1", parts, new[] { tex, other }));
        // ...and the order the caller happened to enumerate in is not an input
        Assert.Equal(before, AssetExporter.CombinedFingerprint("cat-1", parts, new[] { other, tex }));

        System.IO.File.WriteAllBytes(tex, new byte[] { 9, 9, 9, 9, 9, 9 });

        Assert.NotEqual(before, AssetExporter.CombinedFingerprint("cat-1", parts, new[] { tex, other }));
        // a texture leaving or joining the embed set is a different spec too
        Assert.NotEqual(before, AssetExporter.CombinedFingerprint("cat-1", parts, new[] { other }));
    }

    /// <summary>Which files the fingerprint stamps: the subject's own materialized maps, keyed off the users
    /// the project recorded. Another subject's texture must not drag this session into a rebuild.</summary>
    [Fact]
    public void EmbeddedTexturePaths_TakesTheMeshesUsersAndAnythingUnattributed()
    {
        using var temp = new TempWorkspace();
        var project = new ModProject { RootDir = temp.Root };
        project.Targets.Add(new ProjectTarget
        { AssetType = "Texture2D", ObjectName = "face_d", ReplaceFile = "textures/face.aa.png", Users = new() { "obj1" } });
        project.Targets.Add(new ProjectTarget
        { AssetType = "Texture2D", ObjectName = "other_d", ReplaceFile = "textures/other.bb.png", Users = new() { "someone-else" } });
        project.Targets.Add(new ProjectTarget
        { AssetType = "Texture2D", ObjectName = "loose_d", ReplaceFile = "textures/loose.cc.png" });
        project.Targets.Add(new ProjectTarget
        { AssetType = "Mesh", ObjectName = "obj1", ReplaceFile = "char/obj1.glb", Users = new() { "obj1" } });

        var paths = AssetExporter.EmbeddedTexturePaths(project, new[] { "obj1" });

        Assert.Contains(project.Resolve("textures/face.aa.png"), paths);   // this session's map
        Assert.Contains(project.Resolve("textures/loose.cc.png"), paths);  // no users recorded — can't be ruled out
        Assert.DoesNotContain(project.Resolve("textures/other.bb.png"), paths);
        Assert.DoesNotContain(project.Resolve("char/obj1.glb"), paths);    // meshes are the other half of the key
    }

    /// <summary>The regression this gate exists for: an edit made since the cached session was built must
    /// force a rebuild. A stamp that didn't move with the file serves the modder their own pre-edit
    /// geometry, and the next send replaces the work with it.</summary>
    [Fact]
    public void CombinedFingerprint_ChangesWhenAnIncludedPartsEditedGlbChanges()
    {
        using var temp = new TempWorkspace();
        var ws = System.IO.Path.Combine(temp.Root, "cloth1.glb");
        System.IO.File.WriteAllBytes(ws, new byte[] { 1, 2, 3, 4 });

        (string, string, string, string?)[] Spec() => new[] { ("cloth1", "aa", "obj1", (string?)ws) };
        var before = AssetExporter.CombinedFingerprint("cat-1", Spec(), NoTextures);

        // unedited vs edited are different inputs, and so are two different edits of the same part
        Assert.NotEqual(before, AssetExporter.CombinedFingerprint("cat-1",
            new (string, string, string, string?)[] { ("cloth1", "aa", "obj1", null) }, NoTextures));
        System.IO.File.WriteAllBytes(ws, new byte[] { 9, 9, 9, 9, 9, 9 });
        Assert.NotEqual(before, AssetExporter.CombinedFingerprint("cat-1", Spec(), NoTextures));

        // …and nothing changing still reads as the same inputs, so an unchanged workspace reuses the cache
        Assert.Equal(AssetExporter.CombinedFingerprint("cat-1", Spec(), NoTextures),
                     AssetExporter.CombinedFingerprint("cat-1", Spec(), NoTextures));
    }

    // ---- a failed combined rebuild must never bless stale geometry ----

    [Fact]
    public void PublishCombined_BuildFailed_LeavesOldFileAndFingerprint_SoNextOpenRebuilds()
    {
        using var temp = new TempWorkspace();
        var combined = System.IO.Path.Combine(temp.Root, "_combined.glb");
        var fp = System.IO.Path.Combine(temp.Root, "_combined.fingerprint");
        System.IO.File.WriteAllText(combined, "STALE-GEOMETRY");   // an old cached combined from a prior part set
        System.IO.File.WriteAllText(fp, "old-fingerprint");
        var tmp = combined + ".deadbeef.tmp";                       // the build produced NOTHING (no temp)

        var published = AssetExporter.PublishCombined(tmp, combined, fp, "new-fingerprint");

        Assert.False(published);                                     // caller must not launch
        Assert.Equal("STALE-GEOMETRY", System.IO.File.ReadAllText(combined));   // old file untouched — never blessed
        Assert.Equal("old-fingerprint", System.IO.File.ReadAllText(fp));        // old sidecar kept…
        // …so the reuse gate still MISMATCHES the new spec → rebuild
        Assert.False(AssetExporter.CombinedCacheHit(combined, fp, "new-fingerprint"));
    }

    [Fact]
    public void PublishCombined_BuildSucceeded_AtomicallyReplacesFileThenBlessesFingerprint()
    {
        using var temp = new TempWorkspace();
        var combined = System.IO.Path.Combine(temp.Root, "_combined.glb");
        var fp = System.IO.Path.Combine(temp.Root, "_combined.fingerprint");
        System.IO.File.WriteAllText(combined, "STALE-GEOMETRY");
        System.IO.File.WriteAllText(fp, "old-fingerprint");
        var tmp = combined + ".cafef00d.tmp";
        System.IO.File.WriteAllText(tmp, "FRESH-GEOMETRY");          // the build wrote the fresh combined to temp

        var published = AssetExporter.PublishCombined(tmp, combined, fp, "new-fingerprint");

        Assert.True(published);
        Assert.Equal("FRESH-GEOMETRY", System.IO.File.ReadAllText(combined));   // fresh geometry now on disk
        Assert.True(AssetExporter.CombinedCacheHit(combined, fp, "new-fingerprint"));   // blessed after the move
        Assert.False(AssetExporter.CombinedCacheHit(combined, fp, "old-fingerprint"));
        Assert.False(System.IO.File.Exists(tmp));                               // temp consumed by the move
    }

    [Fact]
    public void PublishCombined_FirstBuild_NoPriorDestination_Publishes()
    {
        using var temp = new TempWorkspace();
        var combined = System.IO.Path.Combine(temp.Root, "_combined.glb");
        var fp = System.IO.Path.Combine(temp.Root, "_combined.fingerprint");
        var tmp = combined + ".0badf00d.tmp";
        System.IO.File.WriteAllText(tmp, "FRESH-GEOMETRY");

        Assert.True(AssetExporter.PublishCombined(tmp, combined, fp, "fp-1"));
        Assert.Equal("FRESH-GEOMETRY", System.IO.File.ReadAllText(combined));
        Assert.True(AssetExporter.CombinedCacheHit(combined, fp, "fp-1"));
    }

    /// <summary>Blender's Send exports over the very file the session was launched on, so matching inputs
    /// are not enough — the file must still be the one this app published, or the next open hands Blender a
    /// one-part send-back in place of the outfit.</summary>
    [Fact]
    public void CombinedCacheHit_FileReplacedSincePublish_Misses()
    {
        using var temp = new TempWorkspace();
        var combined = System.IO.Path.Combine(temp.Root, "_combined.glb");
        var fp = System.IO.Path.Combine(temp.Root, "_combined.fingerprint");
        var tmp = combined + ".1234abcd.tmp";
        System.IO.File.WriteAllText(tmp, "SESSION-GLB");
        Assert.True(AssetExporter.PublishCombined(tmp, combined, fp, "fp-1"));
        Assert.True(AssetExporter.CombinedCacheHit(combined, fp, "fp-1"));

        System.IO.File.WriteAllText(combined, "WHAT-BLENDER-SENT-BACK-INSTEAD");

        Assert.False(AssetExporter.CombinedCacheHit(combined, fp, "fp-1"));
    }

    [Fact]
    public void CombinedCacheHit_NoCachedFileOrNoSidecar_Misses()
    {
        using var temp = new TempWorkspace();
        var combined = System.IO.Path.Combine(temp.Root, "_combined.glb");
        var fp = System.IO.Path.Combine(temp.Root, "_combined.fingerprint");

        Assert.False(AssetExporter.CombinedCacheHit(combined, fp, "fp-1"));   // nothing cached at all
        System.IO.File.WriteAllText(combined, "SESSION-GLB");
        Assert.False(AssetExporter.CombinedCacheHit(combined, fp, "fp-1"));   // a file nobody blessed
    }

    // ---- the bless side: may a fresh build be LEFT cached for the next open? ----

    private static readonly string[] Nothing = System.Array.Empty<string>();
    private static readonly string[] OneRow = { "cloth1" };

    [Fact]
    public void CombinedCacheable_WhenNothingWasUnavailable()
    {
        // Also the regression this gate exists for: a row that measured unmeasurable degrades on EVERY
        // rerun (every character's face row does) while the catalog serves the same bytes each time, so
        // the cached tail is what a rebuild would write. Such a row is deliberately not one of the four
        // inputs — a build carrying only those degrades has to look exactly like a clean one here, which
        // is why this case is spelled with empty collections rather than a "degraded" argument.
        Assert.True(AssetExporter.CombinedCacheable(Nothing, hasRoster: true, Nothing, rosterInputsUnreadable: false));
    }

    [Fact]
    public void CombinedCacheable_AnyOneUnavailableInputBlocksReuse()
    {
        // a part carrying an edit that fell back to the game copy: the fingerprint would claim the edit
        Assert.False(AssetExporter.CombinedCacheable(OneRow, hasRoster: true, Nothing, rosterInputsUnreadable: false));
        // no roster at all
        Assert.False(AssetExporter.CombinedCacheable(Nothing, hasRoster: false, Nothing, rosterInputsUnreadable: false));
        // a row whose bytes were unavailable this run — it may read differently once the lock clears
        Assert.False(AssetExporter.CombinedCacheable(Nothing, hasRoster: true, OneRow, rosterInputsUnreadable: false));
        // the roster's own inputs didn't read whole
        Assert.False(AssetExporter.CombinedCacheable(Nothing, hasRoster: true, Nothing, rosterInputsUnreadable: true));
    }

    private sealed class TempWorkspace : System.IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "gf2-combined-guard-" + System.Guid.NewGuid().ToString("N"));
        public TempWorkspace() => System.IO.Directory.CreateDirectory(Root);

        public void Dispose() { try { System.IO.Directory.Delete(Root, recursive: true); } catch { } }
    }
}
