using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
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
        Assert.Equal("alpha touches the same character. Two mods on the same hashes fight over the draw.",
            ModInstall.ConflictBody(new[] { "alpha" }));
        Assert.Equal("alpha and beta touch the same character. Two mods on the same hashes fight over the draw.",
            ModInstall.ConflictBody(new[] { "alpha", "beta" }));
    }

    [Fact]
    public void The_prior_version_confirm_says_it_is_the_same_mod_and_when_the_old_one_goes()
    {
        Assert.Equal("Replace the installed mine_v1_0?", ModInstall.PriorVersionTitle(new[] { "mine_v1_0" }));
        Assert.Equal("Same mod, older version. It is removed after the new one installs.",
            ModInstall.PriorVersionBody(new[] { "mine_v1_0" }));
        Assert.Equal("Replace the installed mine_v1_0 and mine_v1_1?",
            ModInstall.PriorVersionTitle(new[] { "mine_v1_0", "mine_v1_1" }));
        Assert.Equal("Same mod, older versions. They are removed after the new one installs.",
            ModInstall.PriorVersionBody(new[] { "mine_v1_0", "mine_v1_1" }));
    }

    // ---- the swap ----

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
    public void A_removal_target_outside_the_mods_folder_is_refused()
    {
        string mods = Path.Combine(_root, "Mods");
        Directory.CreateDirectory(mods);
        Assert.Throws<InvalidOperationException>(() => ModInstall.RemoveInstalled(mods, ".."));
        Assert.Throws<InvalidOperationException>(() => ModInstall.RemoveInstalled(mods, Path.Combine("..", "elsewhere")));
        Assert.True(Directory.Exists(_root));
    }

    // ---- the Install action's enablement ----

    [Theory]
    [InlineData(false, null, false, InstallGate.NoBuild)]
    [InlineData(false, "C:/3dm/Run.exe", true, InstallGate.NoBuild)]
    [InlineData(true, null, false, InstallGate.NoLoader)]
    [InlineData(true, "   ", false, InstallGate.NoLoader)]
    public void Install_is_off_until_there_is_a_build_and_a_loader(
        bool hasBuild, string? loaderExe, bool exists, string reason) =>
        Assert.Equal(reason, InstallGate.Reason(hasBuild, loaderExe, exists, modsFolder: null));

    [Fact]
    public void A_loader_that_is_set_but_gone_names_the_path_it_looked_for() =>
        Assert.Equal(@"3DMigoto loader not found: D:\moved\Run.exe",
            InstallGate.Reason(hasBuild: true, @"D:\moved\Run.exe", loaderExists: false, modsFolder: null));

    [Fact]
    public void Install_is_on_with_a_build_and_a_loader_with_its_mods_folder() =>
        Assert.Null(InstallGate.Reason(true, "C:/3dm/Run.exe", true, "C:/3dm/Mods"));
}
