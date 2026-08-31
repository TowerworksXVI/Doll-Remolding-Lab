using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Remold.Core;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a force rescan is allowed to delete. The sweep runs over the modder's own folders, so the line
/// between "the app rebuilds this" and "the modder would lose this" is the whole contract: the derived cache
/// trees and the combined-rig fingerprint sidecars go, and settings, project manifests, workspace glbs,
/// textures and the shipped seed stay. Every fixture here is synthetic — the sweep never needs real content
/// to be exercised, only the shapes.
/// </summary>
public class CacheResetTests
{
    // ---- the derived-cache trees ----

    /// <summary>A cache root laid out the way the app writes it, plus a stray the sweep must not claim.</summary>
    private static string FakeCache(string parent)
    {
        var cache = Path.Combine(parent, "cache");
        foreach (var folder in LabPaths.DerivedCacheFolders)
        {
            var d = Path.Combine(cache, folder);
            Directory.CreateDirectory(Path.Combine(d, "nested"));
            File.WriteAllText(Path.Combine(d, "top.bin"), "x");
            File.WriteAllText(Path.Combine(d, "nested", "deep.bin"), "x");
        }
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "launch_timing.log"), "opt-in log");
        return cache;
    }

    [Fact]
    public void The_sweep_takes_every_rebuilt_tree_whole()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);

        CacheReset.ClearDerivedCaches(cache);

        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder)), folder + " survived the sweep");
        Assert.True(Directory.Exists(cache));   // the root itself is not the app's to remove
    }

    [Fact]
    public void The_rigged_glb_tree_is_part_of_the_force_rescan_sweep()
    {
        using var g = new TempGame();
        var cache = Path.Combine(g.Root, "cache");
        var rigs = LabPaths.RiggedGlbRootIn(cache);
        Directory.CreateDirectory(Path.Combine(rigs, "v1", "catalog", "subject"));
        File.WriteAllText(Path.Combine(rigs, "v1", "catalog", "subject", "complete.json"), "derived");

        CacheReset.ClearDerivedCaches(cache);

        Assert.False(Directory.Exists(rigs));
    }

    /// <summary>The launch-timing log is opt-in instrumentation, not a correctness cache: nothing re-derives
    /// it, so a sweep that took it would silently end a measurement the modder turned on.</summary>
    [Fact]
    public void The_opt_in_log_beside_the_trees_is_left_alone()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);

        CacheReset.ClearDerivedCaches(cache);

        Assert.True(File.Exists(Path.Combine(cache, "launch_timing.log")));
    }

    /// <summary>The sweep is named in folder names, and the durable paths live nowhere near them — this is
    /// the structural half of "mods, projects and edits are kept", independent of any fixture.</summary>
    [Fact]
    public void Nothing_durable_sits_under_a_folder_the_sweep_names()
    {
        var swept = LabPaths.DerivedCacheFolders
            .Select(f => Path.Combine(LabPaths.CacheRoot, f) + Path.DirectorySeparatorChar)
            .ToList();
        var durable = new[]
        {
            LabPaths.SettingsFile, LabPaths.FirstRunAcceptanceFile, LabPaths.DefaultLibraryRoot,
            // both halves of the shipped seed: the sweep clears this install's own memo, and re-minting it
            // from the seed's is exactly what makes a force rescan cheap — sweeping the SHIPPED one would
            // make it expensive forever
            LabPaths.SharingSeedFile, LabPaths.AssetHashSeedFile, LabPaths.LaunchTimingLog,
        };

        foreach (var d in durable)
            foreach (var s in swept)
                Assert.False(Path.GetFullPath(d).StartsWith(s, StringComparison.OrdinalIgnoreCase),
                    d + " is under " + s);
    }

    /// <summary>A file another process holds is the ordinary case (a thumbnail being read, an operator blob
    /// mid-write), and the whole point of the item-by-item walk: it is skipped, everything else still goes,
    /// and the caller hears nothing.</summary>
    [Fact]
    public void A_held_file_is_skipped_and_the_rest_of_the_tree_still_goes()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);
        var held = Path.Combine(cache, "thumbs", "top.bin");
        using (var hold = File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CacheReset.ClearDerivedCaches(cache);   // must not throw

            Assert.True(File.Exists(held));                                                  // the lock stands
            Assert.False(File.Exists(Path.Combine(cache, "thumbs", "nested", "deep.bin")));  // its neighbours don't
            Assert.False(Directory.Exists(Path.Combine(cache, "index")));                    // nor the other trees
            Assert.False(Directory.Exists(Path.Combine(cache, "operators")));
            Assert.False(Directory.Exists(Path.Combine(cache, "textures")));
        }
    }

    [Fact]
    public void A_cache_root_that_was_never_written_sweeps_quietly()
    {
        using var g = new TempGame();
        CacheReset.ClearDerivedCaches(Path.Combine(g.Root, "never-created"));
        CacheReset.ClearDerivedCaches("");
    }

    // ---- attributes and links: what the walk must not skip, and what it must not follow ----

    /// <summary>A directory junction at <paramref name="link"/> pointing at <paramref name="target"/>, or
    /// null when this machine refuses to make one. Junctions need no elevation (symlinks do), so the null
    /// path is the locked-down-host case rather than the ordinary one.</summary>
    private static string? TryJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit(15000);
            return Directory.Exists(link) ? link : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>A junction inside a cache tree points wherever the modder pointed it — a project folder, a
    /// documents folder, a drive root. Walking through one takes the sweep out of the roots it was handed
    /// and deletes somebody else's files; the LINK is the app's to remove, never what it names.</summary>
    [Fact]
    public void A_junction_in_a_cache_tree_is_unlinked_and_what_it_points_at_survives()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);
        var outside = Path.Combine(g.Root, "not-the-apps");
        Directory.CreateDirectory(Path.Combine(outside, "sub"));
        File.WriteAllText(Path.Combine(outside, "mine.txt"), "the modder's");
        File.WriteAllText(Path.Combine(outside, "sub", "deep.txt"), "the modder's");
        var link = Path.Combine(cache, "thumbs", "escape");
        if (TryJunction(link, outside) is null) return;   // host refuses junctions — nothing to pin

        CacheReset.ClearDerivedCaches(cache);

        Assert.True(File.Exists(Path.Combine(outside, "mine.txt")), "the sweep followed the junction");
        Assert.True(File.Exists(Path.Combine(outside, "sub", "deep.txt")));
        Assert.True(Directory.Exists(outside));
        Assert.False(Directory.Exists(link));                            // the link itself is the app's
        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder))); // and the rest of the tree still went
    }

    /// <summary>A junction pointing back into the tree being swept: the walk must end, and everything that
    /// is really under the root must still go.</summary>
    [Fact]
    public void A_junction_loop_ends_the_walk_instead_of_ending_the_sweep()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);
        var link = Path.Combine(cache, "index", "nested", "loop");
        if (TryJunction(link, Path.Combine(cache, "index")) is null) return;

        CacheReset.ClearDerivedCaches(cache);   // must not throw, must not hang

        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder)));
    }

    /// <summary>Hidden and system entries are ordinary cache content as far as the sweep is concerned —
    /// the walk's default is to SKIP them, and one hidden <c>desktop.ini</c> would then keep its whole
    /// folder standing for good.</summary>
    [Fact]
    public void Hidden_and_system_entries_are_swept_like_any_other()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);
        var ini = Path.Combine(cache, "thumbs", "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        var hiddenDir = Path.Combine(cache, "index", "hidden");
        Directory.CreateDirectory(hiddenDir);
        File.WriteAllText(Path.Combine(hiddenDir, "blob.bin"), "x");
        new DirectoryInfo(hiddenDir).Attributes |= FileAttributes.Hidden;

        CacheReset.ClearDerivedCaches(cache);

        Assert.False(Directory.Exists(Path.Combine(cache, "thumbs")));
        Assert.False(Directory.Exists(Path.Combine(cache, "index")));
    }

    /// <summary>A read-only file refuses <see cref="File.Delete"/> outright — silently, on this route — and
    /// takes its folder with it. The bit is not a reason to keep a cache the app rebuilds.</summary>
    [Fact]
    public void A_read_only_cache_file_is_swept()
    {
        using var g = new TempGame();
        var cache = FakeCache(g.Root);
        var locked = Path.Combine(cache, "textures", "nested", "deep.bin");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        CacheReset.ClearDerivedCaches(cache);

        Assert.False(File.Exists(locked));
        Assert.False(Directory.Exists(Path.Combine(cache, "textures")));
    }

    // ---- the combined-rig fingerprint sidecars ----

    /// <summary>A mod project as the app writes one: the manifest at the root, and one subject folder holding
    /// the combined glb, its fingerprint sidecar and a texture.</summary>
    private static string FakeProject(string libraryRoot, string name)
    {
        var root = Path.Combine(libraryRoot, name);
        var meshes = Path.Combine(root, "subject", "meshes");
        Directory.CreateDirectory(meshes);
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        File.WriteAllText(ModProject.ManifestPathFor(root), "{}");
        File.WriteAllText(Path.Combine(meshes, AssetExporter.CombinedGlbName), "glb");
        File.WriteAllText(Path.Combine(meshes, CacheReset.CombinedFingerprintName), "fingerprint");
        File.WriteAllText(Path.Combine(root, "textures", "map.png"), "png");
        return root;
    }

    /// <summary>The sidecar name is what released installs already have on disk, so it is pinned as a
    /// literal rather than only as a derivation of the glb name.</summary>
    [Fact]
    public void The_sidecar_the_sweep_hunts_is_the_one_the_build_writes()
    {
        Assert.Equal("_combined.fingerprint", CacheReset.CombinedFingerprintName);
        Assert.Equal(Path.ChangeExtension(AssetExporter.CombinedGlbName, ".fingerprint"),
            CacheReset.CombinedFingerprintName);
    }

    [Fact]
    public void Sidecars_go_and_everything_beside_them_stays()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var a = FakeProject(lib, "one");
        var b = FakeProject(lib, "two");

        int removed = CacheReset.ClearCombinedFingerprints(CacheReset.ProjectRoots(lib, null));

        Assert.Equal(2, removed);
        foreach (var root in new[] { a, b })
        {
            var meshes = Path.Combine(root, "subject", "meshes");
            Assert.False(File.Exists(Path.Combine(meshes, CacheReset.CombinedFingerprintName)));
            Assert.True(File.Exists(Path.Combine(meshes, AssetExporter.CombinedGlbName)));   // never the glb
            Assert.True(File.Exists(ModProject.ManifestPathFor(root)));
            Assert.True(File.Exists(Path.Combine(root, "textures", "map.png")));
        }
    }

    /// <summary>Only the app's own sidecar name is deletable: a file the modder happened to name with the
    /// same extension is theirs.</summary>
    [Fact]
    public void A_look_alike_name_in_a_project_is_not_the_apps_to_delete()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var root = FakeProject(lib, "one");
        var mine = Path.Combine(root, "subject", "meshes", "mine.fingerprint");
        File.WriteAllText(mine, "not the app's");

        CacheReset.ClearCombinedFingerprints(CacheReset.ProjectRoots(lib, null));

        Assert.True(File.Exists(mine));
    }

    /// <summary>A folder under the library that isn't a project is somebody else's — the sweep never walks
    /// into it, so nothing named like a sidecar inside it is touched.</summary>
    [Fact]
    public void A_folder_without_a_manifest_is_not_a_project_and_is_never_entered()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        FakeProject(lib, "one");
        var stranger = Path.Combine(lib, "not-a-project");
        Directory.CreateDirectory(stranger);
        var strandedSidecar = Path.Combine(stranger, CacheReset.CombinedFingerprintName);
        File.WriteAllText(strandedSidecar, "someone else's");

        var roots = CacheReset.ProjectRoots(lib, null);
        CacheReset.ClearCombinedFingerprints(roots);

        Assert.Single(roots);
        Assert.True(File.Exists(strandedSidecar));
    }

    /// <summary>A project opened from outside the library still carries the app's sidecars, so the recents
    /// list is the second source of roots — and a recent entry that is no longer a project contributes
    /// nothing.</summary>
    [Fact]
    public void Recents_reach_projects_outside_the_library_and_only_projects()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        FakeProject(lib, "one");
        var outside = FakeProject(Path.Combine(g.Root, "elsewhere"), "loose");
        var gone = Path.Combine(g.Root, "elsewhere", "deleted-since");
        var plain = Path.Combine(g.Root, "elsewhere", "plain-folder");
        Directory.CreateDirectory(plain);

        var roots = CacheReset.ProjectRoots(lib, new[] { outside, gone, plain, "" });

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => string.Equals(r, Path.GetFullPath(outside), StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, CacheReset.ClearCombinedFingerprints(roots));
    }

    /// <summary>A project listed in recents AND sitting in the library is one project, not two sweeps.</summary>
    [Fact]
    public void A_project_named_twice_is_swept_once()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var root = FakeProject(lib, "one");

        var roots = CacheReset.ProjectRoots(lib, new[] { root });

        Assert.Single(roots);
    }

    [Fact]
    public void A_held_sidecar_is_skipped_and_the_other_projects_still_clear()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var a = FakeProject(lib, "one");
        var b = FakeProject(lib, "two");
        var held = Path.Combine(a, "subject", "meshes", CacheReset.CombinedFingerprintName);

        using (var hold = File.Open(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int removed = CacheReset.ClearCombinedFingerprints(CacheReset.ProjectRoots(lib, null));

            Assert.Equal(1, removed);
            Assert.True(File.Exists(held));
            Assert.False(File.Exists(Path.Combine(b, "subject", "meshes", CacheReset.CombinedFingerprintName)));
        }
    }

    /// <summary>The sidecar hunt walks the modder's OWN folders, so a junction inside a project is the same
    /// escape hatch as one inside a cache tree: a file named like the app's sidecar on the far side of it
    /// belongs to whatever the link points at, not to this project.</summary>
    [Fact]
    public void A_junction_in_a_project_is_not_walked_through()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var root = FakeProject(lib, "one");
        var outside = Path.Combine(g.Root, "somebody-elses");
        Directory.CreateDirectory(outside);
        var foreignSidecar = Path.Combine(outside, CacheReset.CombinedFingerprintName);
        File.WriteAllText(foreignSidecar, "not this project's");
        if (TryJunction(Path.Combine(root, "linked"), outside) is null) return;

        int removed = CacheReset.ClearCombinedFingerprints(CacheReset.ProjectRoots(lib, null));

        Assert.True(File.Exists(foreignSidecar), "the hunt followed the junction out of the project");
        Assert.Equal(1, removed);   // the project's own sidecar, and only that one
        Assert.False(File.Exists(Path.Combine(root, "subject", "meshes", CacheReset.CombinedFingerprintName)));
    }

    /// <summary>The library enumeration and a recents entry rarely spell a path the same way, and
    /// <see cref="Path.GetFullPath(string)"/> keeps a trailing separator — so the same folder can arrive
    /// twice and be walked twice unless the dedupe sees through it.</summary>
    [Fact]
    public void A_root_named_with_and_without_a_trailing_separator_is_one_root()
    {
        using var g = new TempGame();
        var lib = Path.Combine(g.Root, "mods");
        var root = FakeProject(lib, "one");

        var roots = CacheReset.ProjectRoots(null, new[] { root, root + Path.DirectorySeparatorChar });

        Assert.Single(roots);
    }

    [Fact]
    public void No_library_and_no_recents_is_no_roots_and_no_sweep()
    {
        Assert.Empty(CacheReset.ProjectRoots(null, null));
        Assert.Equal(0, CacheReset.ClearCombinedFingerprints(Array.Empty<string>()));
    }
}
