using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Installing a build into a 3DMigoto Mods folder: the conflict read (pure, from sidecar contents), the
/// prior-version read that recognises this same mod under an older folder name, the wording both produce,
/// the swap's behaviour when the destination can't be touched, and the Install action's enablement rule.
/// </summary>
public class ModInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-install-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string Sidecar(params string[] hashes) => SidecarFor("m", "TestAuthor", hashes);

    private static string SidecarFor(string name, string author, params string[] hashes) =>
        $"{{ \"schema\": 1, \"name\": \"{name}\", \"author\": \"{author}\", \"override_hashes\": ["
        + string.Join(", ", hashes.Select(h => $"\"{h}\"")) + "] }";

    private string WriteMod(string parent, string name, params string[] hashes) =>
        WriteSidecarMod(parent, name, Sidecar(hashes));

    private string WriteSidecarMod(string parent, string name, string sidecar)
    {
        var dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModInstall.SidecarName), sidecar);
        File.WriteAllText(Path.Combine(dir, "mod.ini"), $"; {name}\n");
        return dir;
    }

    private static string? TryJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            process.WaitForExit(15_000);
            return Directory.Exists(link) ? link : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---- reading the sidecar ----

    [Fact]
    public void Override_hashes_are_read_from_the_sidecar_and_lower_cased() =>
        Assert.Equal(new[] { "aaaa1111", "bbbb2222" },
            ModInstall.OverrideHashes(Sidecar("AAAA1111", "bbbb2222")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"schema\": 1 }")]
    [InlineData("{ \"override_hashes\": \"aaaa1111\" }")]   // wrong shape, not a list
    public void An_unreadable_sidecar_reports_no_hashes_rather_than_a_guess(string? json) =>
        Assert.Empty(ModInstall.OverrideHashes(json));

    [Fact]
    public void The_same_mod_is_recognised_by_name_and_author_whatever_the_case()
    {
        var ours = ModInstall.Identity(SidecarFor("Plum Fizz Remold", "TestAuthor"));
        Assert.True(ModInstall.SameMod(ours, ModInstall.Identity(SidecarFor("plum fizz remold", "testauthor"))));
        Assert.False(ModInstall.SameMod(ours, ModInstall.Identity(SidecarFor("Plum Fizz Remold", "Someone"))));
        Assert.False(ModInstall.SameMod(ours, ModInstall.Identity(SidecarFor("Another Mod", "TestAuthor"))));
    }

    [Fact]
    public void A_sidecar_with_no_name_is_nobodys_mod()
    {
        Assert.Null(ModInstall.Identity("{ \"schema\": 1 }"));
        Assert.Null(ModInstall.Identity("not json"));
        // and nothing matches it, so it is never taken for a prior version
        Assert.False(ModInstall.SameMod(ModInstall.Identity(SidecarFor("m", "TestAuthor")), null));
    }

    // ---- the conflict rule (pure) ----

    [Fact]
    public void An_installed_mod_sharing_any_hash_is_a_conflict()
    {
        var conflicts = ModInstall.Overlapping(new[] { "aaaa1111", "bbbb2222" }, new[]
        {
            ("other-character", (IReadOnlyList<string>)new[] { "cccc3333" }),
            ("same-character", new[] { "dddd4444", "BBBB2222" }),
            ("no-sidecar", Array.Empty<string>()),
        });

        Assert.Equal(new[] { "same-character" }, conflicts);
    }

    [Fact]
    public void A_build_with_no_hashes_of_its_own_conflicts_with_nothing() =>
        Assert.Empty(ModInstall.Overlapping(Array.Empty<string>(), new[]
        {
            ("anything", (IReadOnlyList<string>)new[] { "aaaa1111" }),
        }));

    [Fact]
    public void The_scan_reads_siblings_and_never_reports_the_folder_it_would_replace()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        WriteMod(mods, "mine_v1_0", "aaaa1111");        // the SAME name: replacing it is the point
        WriteSidecarMod(mods, "someone-elses", SidecarFor("Their Mod", "Someone", "aaaa1111"));
        WriteSidecarMod(mods, "unrelated", SidecarFor("Other", "Someone", "9999ffff"));
        Directory.CreateDirectory(Path.Combine(mods, "no-sidecar-here"));

        var scan = ModInstall.ScanConflicts(built, mods);

        Assert.Equal(new[] { "someone-elses" }, scan.Conflicts);
        Assert.Empty(scan.PriorVersions);
    }

    [Fact]
    public void The_scan_reaches_a_mod_nested_below_the_top_level()
    {
        // 3DMigoto loads Mods\ recursively, so a mod filed under a folder of someone's own making is
        // every bit as live as one at the top
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        WriteSidecarMod(Path.Combine(mods, "characters", "vesna"), "someone-elses",
            SidecarFor("Their Mod", "Someone", "aaaa1111"));

        var scan = ModInstall.ScanConflicts(built, mods);

        Assert.Equal(new[] { Path.Combine("characters", "vesna", "someone-elses") }, scan.Conflicts);
    }

    [Fact]
    public void An_older_version_of_this_same_mod_is_a_prior_version_not_a_conflict()
    {
        string built = WriteSidecarMod(_root, "mine_v1_1", SidecarFor("My Mod", "TestAuthor", "aaaa1111"));
        string mods = Path.Combine(_root, "Mods");
        WriteSidecarMod(mods, "mine_v1_0", SidecarFor("my mod", "testauthor", "aaaa1111"));
        WriteSidecarMod(mods, "someone-elses", SidecarFor("Their Mod", "Someone", "aaaa1111"));

        var scan = ModInstall.ScanConflicts(built, mods);

        Assert.Equal(new[] { "mine_v1_0" }, scan.PriorVersions);
        Assert.Equal(new[] { "someone-elses" }, scan.Conflicts);   // never both
    }

    [Fact]
    public void The_confirm_body_names_the_folders_and_says_what_goes_wrong()
    {
        Assert.Equal("alpha already changes some of the same files. "
            + "Two mods changing the same thing may not show correctly in game.",
            ModInstall.ConflictBody(new[] { "alpha" }));
        Assert.Equal("alpha and beta already change some of the same files. "
            + "Two mods changing the same thing may not show correctly in game.",
            ModInstall.ConflictBody(new[] { "alpha", "beta" }));
    }

    [Fact]
    public void The_prior_version_confirm_says_it_is_the_same_mod_and_when_the_old_one_goes()
    {
        Assert.Equal("Replace the installed mine_v1_0?", ModInstall.PriorVersionTitle(new[] { "mine_v1_0" }));
        Assert.Equal("This is an older version of the same mod. It is removed after the new one installs.",
            ModInstall.PriorVersionBody(new[] { "mine_v1_0" }));
        Assert.Equal("Replace the installed mine_v1_0 and mine_v1_1?",
            ModInstall.PriorVersionTitle(new[] { "mine_v1_0", "mine_v1_1" }));
        Assert.Equal("These are older versions of the same mod. They are removed after the new one installs.",
            ModInstall.PriorVersionBody(new[] { "mine_v1_0", "mine_v1_1" }));
    }

    // ---- the swap ----

    [Fact]
    public void Busy_retry_succeeds_after_transient_access_denied_and_sharing_violations()
    {
        foreach (var denied in new Exception[]
        {
            new UnauthorizedAccessException("access denied"),
            new IOException("access denied", unchecked((int)0x80070005)),
            new IOException("sharing violation", unchecked((int)0x80070020)),
        })
        {
            int calls = 0;
            var delays = new List<TimeSpan>();

            int attempts = ModInstall.RetryBusy(() =>
            {
                calls++;
                if (calls < 3) throw denied;
            }, delays.Add);

            Assert.Equal(3, attempts);
            Assert.Equal(3, calls);
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250) }, delays);
        }
    }

    [Fact]
    public void Busy_retry_exhaustion_surfaces_the_original_exception()
    {
        int calls = 0;
        var delays = new List<TimeSpan>();
        var thrown = new List<IOException>();

        var actual = Assert.Throws<IOException>(() => ModInstall.RetryBusy(() =>
        {
            calls++;
            var distinct = new IOException($"still busy on attempt {calls}", unchecked((int)0x80070020));
            thrown.Add(distinct);
            throw distinct;
        }, delays.Add));

        Assert.Same(thrown[0], actual);
        Assert.NotSame(thrown[^1], actual);
        Assert.Equal(4, calls);
        Assert.Equal(new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
        }, delays);
    }

    [Fact]
    public void Installing_replaces_a_same_named_folder_and_leaves_every_other_one_alone()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(built, "new-file.buf"), "fresh");
        string mods = Path.Combine(_root, "Mods");
        WriteMod(mods, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(mods, "mine_v1_0", "stale.buf"), "old");
        WriteSidecarMod(mods, "someone-elses", SidecarFor("Their Mod", "Someone", "aaaa1111"));

        var outcome = ModInstall.Install(built, mods);

        Assert.Equal(Path.Combine(mods, "mine_v1_0"), outcome.InstalledDir);
        Assert.Null(outcome.LeftBehind);
        Assert.True(File.Exists(Path.Combine(outcome.InstalledDir, "new-file.buf")));
        Assert.False(File.Exists(Path.Combine(outcome.InstalledDir, "stale.buf")));   // replaced, not merged
        Assert.True(Directory.Exists(Path.Combine(mods, "someone-elses")));   // never deleted to make room
        // neither the staging nor the sideline folder survives a successful install
        Assert.DoesNotContain(Directory.GetDirectories(mods), d => Path.GetFileName(d).StartsWith("."));
    }

    [Fact]
    public void A_read_only_file_in_leftover_staging_does_not_stop_the_install()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(built, "new-file.buf"), "fresh");
        string mods = Path.Combine(_root, "Mods");
        string staging = Path.Combine(mods, ".remold-installing-mine_v1_0");
        Directory.CreateDirectory(staging);
        string stale = Path.Combine(staging, "stale.buf");
        File.WriteAllText(stale, "left over");
        File.SetAttributes(stale, File.GetAttributes(stale) | FileAttributes.ReadOnly);

        var outcome = ModInstall.Install(built, mods);

        Assert.Equal("fresh", File.ReadAllText(Path.Combine(outcome.InstalledDir, "new-file.buf")));
        Assert.False(Directory.Exists(staging));
        Assert.Null(outcome.LeftBehind);
    }

    [Fact]
    public void A_staging_root_junction_is_unlinked_without_touching_its_target_attributes()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        string outside = Path.Combine(_root, "outside-root-link");
        Directory.CreateDirectory(outside);
        string foreign = Path.Combine(outside, "read-only.buf");
        File.WriteAllText(foreign, "not part of the Mods tree");
        File.SetAttributes(foreign, File.GetAttributes(foreign) | FileAttributes.ReadOnly);
        string staging = Path.Combine(mods, ".remold-installing-mine_v1_0");
        if (TryJunction(staging, outside) is null)
        {
            File.SetAttributes(foreign, FileAttributes.Normal);
            return;
        }

        try
        {
            var outcome = ModInstall.Install(built, mods);

            Assert.True(File.Exists(foreign));
            Assert.True((File.GetAttributes(foreign) & FileAttributes.ReadOnly) != 0);
            Assert.False(Directory.Exists(staging));
            Assert.True(Directory.Exists(outcome.InstalledDir));
        }
        finally
        {
            File.SetAttributes(foreign, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_child_junction_is_unlinked_without_touching_its_target_attributes()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        string staging = Path.Combine(mods, ".remold-installing-mine_v1_0");
        Directory.CreateDirectory(staging);
        string outside = Path.Combine(_root, "outside-child-link");
        Directory.CreateDirectory(outside);
        string foreign = Path.Combine(outside, "read-only.buf");
        File.WriteAllText(foreign, "not part of the staging tree");
        File.SetAttributes(foreign, File.GetAttributes(foreign) | FileAttributes.ReadOnly);
        string link = Path.Combine(staging, "escape");
        if (TryJunction(link, outside) is null)
        {
            File.SetAttributes(foreign, FileAttributes.Normal);
            return;
        }

        try
        {
            ModInstall.Install(built, mods);

            Assert.True(File.Exists(foreign));
            Assert.True((File.GetAttributes(foreign) & FileAttributes.ReadOnly) != 0);
            Assert.False(Directory.Exists(link));
        }
        finally
        {
            File.SetAttributes(foreign, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_retried_copy_over_a_partial_tree_clears_an_existing_read_only_file()
    {
        string source = Path.Combine(_root, "copy-source");
        string partial = Path.Combine(_root, "copy-partial");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(partial);
        string sourceFile = Path.Combine(source, "already-copied.buf");
        string partialFile = Path.Combine(partial, "already-copied.buf");
        File.WriteAllText(sourceFile, "fresh");
        File.WriteAllText(partialFile, "partial");
        File.SetAttributes(partialFile, File.GetAttributes(partialFile) | FileAttributes.ReadOnly);

        try
        {
            var copyTree = typeof(ModInstall).GetMethod("CopyTree",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(copyTree);
            copyTree.Invoke(null, new object[] { source, partial });

            Assert.Equal("fresh", File.ReadAllText(partialFile));
        }
        finally
        {
            File.SetAttributes(partialFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void An_install_succeeds_when_a_destination_hold_releases_mid_retry_and_logs_the_attempt()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(built, "new-file.buf"), "fresh");
        string mods = Path.Combine(_root, "Mods");
        string dest = WriteMod(mods, "mine_v1_0", "aaaa1111");
        string heldPath = Path.Combine(dest, "held.buf");
        File.WriteAllText(heldPath, "the game has this open briefly");
        var held = File.Open(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(300);
            held.Dispose();
        }) { IsBackground = true };
        release.Start();
        var log = new List<string>();

        var outcome = ModInstall.Install(built, mods, log.Add);
        Assert.True(release.Join(5_000), "the destination holder did not release");

        Assert.Equal("fresh", File.ReadAllText(Path.Combine(outcome.InstalledDir, "new-file.buf")));
        Assert.Null(outcome.LeftBehind);
        Assert.Contains("the Mods folder was busy moving the previous install aside; succeeded on attempt 3", log);
    }

    [Fact]
    public void A_clean_install_writes_no_retry_log_line()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        var log = new List<string>();

        ModInstall.Install(built, mods, log.Add);

        Assert.Empty(log);
    }

    [Fact]
    public void A_transient_staging_hold_logs_the_operation_and_successful_attempt()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        string mods = Path.Combine(_root, "Mods");
        string staging = Path.Combine(mods, ".remold-installing-mine_v1_0");
        Directory.CreateDirectory(staging);
        string heldPath = Path.Combine(staging, "held.buf");
        File.WriteAllText(heldPath, "brief hold");
        var held = File.Open(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(200);
            held.Dispose();
        }) { IsBackground = true };
        release.Start();
        var log = new List<string>();

        ModInstall.Install(built, mods, log.Add);
        Assert.True(release.Join(5_000), "the staging holder did not release");

        Assert.Equal(new[]
        {
            "the Mods folder was busy deleting the staging folder; succeeded on attempt 3",
        }, log);
    }

    [Fact]
    public void A_destination_the_game_is_holding_open_keeps_every_one_of_its_files()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(built, "new-file.buf"), "fresh");
        string mods = Path.Combine(_root, "Mods");
        string dest = WriteMod(mods, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(dest, "held.buf"), "the game has this open");

        using (File.Open(Path.Combine(dest, "held.buf"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var e = Assert.Throws<ModInstall.InstallFailedException>(() => ModInstall.Install(built, mods));
            // nothing was destroyed to make room, so the failure may say exactly that
            Assert.Equal(ModInstall.InstallFailedException.FolderUntouched, e.FolderState);
        }

        Assert.Equal("the game has this open", File.ReadAllText(Path.Combine(dest, "held.buf")));
        Assert.True(File.Exists(Path.Combine(dest, "mod.ini")));
        Assert.False(File.Exists(Path.Combine(dest, "new-file.buf")));   // the new build never landed
        Assert.DoesNotContain(Directory.GetDirectories(mods), d => Path.GetFileName(d).StartsWith("."));
    }

    [Fact]
    public void A_post_swap_sideline_delete_is_single_attempt_and_reports_left_behind_without_retry_delay()
    {
        string built = WriteMod(_root, "mine_v1_0", "aaaa1111");
        File.WriteAllText(Path.Combine(built, "new-file.buf"), "fresh");
        string mods = Path.Combine(_root, "Mods");
        string dest = WriteMod(mods, "mine_v1_0", "aaaa1111");
        string heldPath = Path.Combine(dest, "held.buf");
        File.WriteAllText(heldPath, "hold after rename");
        for (int i = 0; i < 1_000; i++)
            File.WriteAllBytes(Path.Combine(dest, $"filler-{i:D4}.buf"), Array.Empty<byte>());
        string sideline = Path.Combine(mods, ".remold-replaced-mine_v1_0");
        string sidelinedHeld = Path.Combine(sideline, "held.buf");
        using var stop = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        Exception? holderFailure = null;
        long acquiredAt = 0;
        var holder = new Thread(() =>
        {
            try
            {
                while (!stop.IsSet && !File.Exists(sidelinedHeld)) Thread.Yield();
                if (stop.IsSet) return;
                using var stream = File.Open(sidelinedHeld, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                acquiredAt = Stopwatch.GetTimestamp();
                acquired.Set();
                stop.Wait();
            }
            catch (Exception e)
            {
                holderFailure = e;
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        holder.Start();
        var log = new List<string>();

        var outcome = ModInstall.Install(built, mods, log.Add);
        TimeSpan afterHold = acquiredAt == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(acquiredAt);
        stop.Set();
        Assert.True(holder.Join(5_000), "the sideline holder did not stop");

        Assert.Null(holderFailure);
        Assert.True(acquired.IsSet, "the sideline was deleted before the test could hold it");
        Assert.Equal(".remold-replaced-mine_v1_0", outcome.LeftBehind);
        Assert.True(afterHold < TimeSpan.FromMilliseconds(1_500),
            $"the LeftBehind path waited {afterHold.TotalMilliseconds:F0} ms after the hold");
        Assert.Empty(log);
        Directory.Delete(sideline, recursive: true);
    }

    [Fact]
    public void Installing_a_build_that_is_gone_refuses_instead_of_creating_an_empty_mod()
    {
        string mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        Assert.Throws<DirectoryNotFoundException>(() =>
            ModInstall.Install(Path.Combine(_root, "never-built"), mods));
        Assert.Empty(Directory.GetDirectories(mods));
    }

    [Fact]
    public void A_version_bump_leaves_exactly_one_folder_once_the_old_one_is_removed()
    {
        string mods = Path.Combine(_root, "Mods");
        string v10 = WriteSidecarMod(_root, "mine_v1_0", SidecarFor("My Mod", "TestAuthor", "aaaa1111"));
        ModInstall.Install(v10, mods);

        string v11 = WriteSidecarMod(Path.Combine(_root, "next"), "mine_v1_1",
            SidecarFor("My Mod", "TestAuthor", "aaaa1111"));
        var scan = ModInstall.ScanConflicts(v11, mods);
        Assert.Equal(new[] { "mine_v1_0" }, scan.PriorVersions);

        ModInstall.Install(v11, mods);
        foreach (var folder in scan.PriorVersions) ModInstall.RemoveInstalled(mods, folder);

        Assert.Equal(new[] { "mine_v1_1" },
            Directory.GetDirectories(mods).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void A_prior_version_delete_clears_read_only_files_retries_a_hold_and_logs_the_attempt()
    {
        string mods = Path.Combine(_root, "Mods");
        string prior = WriteMod(mods, "mine_v1_0", "aaaa1111");
        string readOnly = Path.Combine(prior, "read-only.buf");
        File.WriteAllText(readOnly, "old");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        string heldPath = Path.Combine(prior, "held.buf");
        File.WriteAllText(heldPath, "brief hold");
        var held = File.Open(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(200);
            held.Dispose();
        }) { IsBackground = true };
        release.Start();
        var log = new List<string>();

        ModInstall.RemoveInstalled(mods, "mine_v1_0", log.Add);
        Assert.True(release.Join(5_000), "the prior-version holder did not release");

        Assert.False(Directory.Exists(prior));
        Assert.Equal(new[]
        {
            "the Mods folder was busy deleting 'mine_v1_0'; succeeded on attempt 3",
        }, log);
    }

    [Fact]
    public void A_removal_target_outside_the_mods_folder_is_refused()
    {
        string mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        Assert.Throws<InvalidOperationException>(() => ModInstall.RemoveInstalled(mods, ".."));
        Assert.Throws<InvalidOperationException>(() => ModInstall.RemoveInstalled(mods, Path.Combine("..", "elsewhere")));
        Assert.True(Directory.Exists(_root));
    }

}
