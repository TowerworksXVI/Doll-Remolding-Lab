using System;
using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a 3DMigoto host's ini tree answers about the two things the app has to know: whether the host
/// starts the game itself (so this app must not start a second copy), and whether it carries the texture
/// hook a built mod fires through. Distributions split their configuration across includes, so both
/// questions are asked of the WHOLE tree.
/// </summary>
public class MigotoIniTests
{
    private const string Root = @"C:\3dmigoto\d3dx.ini";

    /// <summary>An ini tree served from memory, keyed the way the walk resolves paths.</summary>
    private static Func<string, string?> Tree(params (string Path, string Text)[] files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, text) in files) map[Path.GetFullPath(path)] = text;
        return p => map.TryGetValue(p, out var t) ? t : null;
    }

    // ---- does the host start the game itself ----

    [Fact]
    public void An_active_launch_under_Loader_says_the_host_starts_the_game()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, """
            [Loader]
            target = GF2_Exilium.exe
            launch = GF2_Exilium.exe
            """)));

        Assert.True(facts.Found);
        Assert.True(facts.StartsTheGame);
    }

    [Fact]
    public void A_commented_launch_is_not_a_setting()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, """
            [Loader]
            target = GF2_Exilium.exe
            ; launch = GF2_Exilium.exe
            """)));

        Assert.True(facts.Found);
        Assert.False(facts.StartsTheGame);
    }

    [Fact]
    public void A_launch_outside_the_Loader_section_is_not_the_hosts_own()
    {
        // the key is only the host's game start under [Loader]; the same word elsewhere is another
        // section's setting
        var facts = MigotoIni.Parse(Root, Tree((Root, """
            [Present]
            launch = something
            """)));

        Assert.False(facts.StartsTheGame);
    }

    [Fact]
    public void A_launch_with_no_value_starts_nothing()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, "[Loader]\nlaunch =\n")));

        Assert.False(facts.StartsTheGame);
    }

    // ---- does the host carry the texture hook ----

    /// <summary>The shape every measured host ships: the shader-regex section only NAMES a command list, and
    /// the hook commands stand in that list. Nothing keyed on the section holding the command would see
    /// this — which is the whole reason the question is asked of the tree rather than of a section.</summary>
    [Fact]
    public void A_shader_regex_section_that_runs_a_command_list_carrying_the_hook_counts()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, """
            [ShaderRegexEnableTextureOverrides]
            shader_model = vs_4_0 vs_4_1 vs_5_0 vs_5_1
            run = CommandListSkin

            [CommandListSkin]
            if $costume_mods
            	checktextureoverride = ps-t0
            	checktextureoverride = vb0
            endif
            """)));

        Assert.True(facts.Found);
        Assert.True(facts.HasTextureHook);
    }

    [Fact]
    public void The_hook_is_found_through_an_include()
    {
        // an SSMT profile keeps the hook in Core\GIMI\main.ini, which the per-game ini pulls in — and that
        // file carries the same two-section shape, its own libraries a level further down again
        var facts = MigotoIni.Parse(Root, Tree(
            (Root, "[Include]\ninclude = Core\\GIMI\\main.ini\ninclude_recursive = Mods\n"),
            (@"C:\3dmigoto\Core\GIMI\main.ini", """
                [Include Libraries]
                include = Libraries\Includes.ini

                [ShaderRegexEnableTextureOverrides]
                shader_model = vs_4_0 vs_4_1 vs_5_0 vs_5_1
                run = CommandListSkin
                """),
            (@"C:\3dmigoto\Core\GIMI\Libraries\Includes.ini", "include = ORFix.ini\n"),
            (@"C:\3dmigoto\Core\GIMI\Libraries\ORFix.ini", """
                [CommandListCheck]
                if $costume_mods
                	checktextureoverride = ps-t4
                endif
                """)));

        Assert.True(facts.HasTextureHook);
    }

    [Fact]
    public void A_commented_hook_is_no_hook()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, """
            [ShaderRegexEnableTextureOverrides]
            shader_model = vs_5_0
            run = CommandListSkin

            [CommandListSkin]
            ; checktextureoverride = ps-t0
            """)));

        Assert.True(facts.Found);
        Assert.False(facts.HasTextureHook);
    }

    /// <summary>The name has to end at a boundary, or a longer command starting with the same letters would
    /// answer for the hook.</summary>
    [Fact]
    public void A_longer_command_starting_with_the_same_letters_is_not_the_hook()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root,
            "[CommandListSkin]\ncheckte" + "xtureoverridesomething = ps-t0\n")));

        Assert.False(facts.HasTextureHook);
    }

    [Fact]
    public void A_host_with_neither_answers_no_to_both_and_still_reads_as_found()
    {
        var facts = MigotoIni.Parse(Root, Tree((Root, "[Rendering]\noverride_cursor = 1\n")));

        Assert.True(facts.Found);
        Assert.False(facts.StartsTheGame);
        Assert.False(facts.HasTextureHook);
    }

    [Fact]
    public void No_ini_at_all_is_not_found()
    {
        var facts = MigotoIni.Parse(Root, _ => null);

        Assert.False(facts.Found);
        Assert.False(facts.StartsTheGame);
        Assert.False(facts.HasTextureHook);
    }

    // ---- how far the walk goes ----

    [Fact]
    public void The_mods_tree_is_not_walked()
    {
        // include_recursive names the installed MODS, not the host's configuration: walking it would read
        // every mod on the machine to answer a question about the host — and a mod carrying either line
        // would answer for it. It is also the ONE place a hook command could stand without being the host's
        // own, which is what lets the hook be asked of the whole walked tree.
        var facts = MigotoIni.Parse(Root, Tree(
            (Root, "[Include]\ninclude_recursive = Mods\nexclude_recursive = Mods\\disabled\n"),
            (@"C:\3dmigoto\Mods", "[Loader]\nlaunch = x\n[CommandListSkin]\ncheckte" + "xtureoverride = ps-t0\n")));

        Assert.False(facts.StartsTheGame);
        Assert.False(facts.HasTextureHook);
    }

    [Fact]
    public void An_include_that_names_its_own_file_is_read_once()
    {
        var facts = MigotoIni.Parse(Root, Tree(
            (Root, "[Include]\ninclude = d3dx.ini\ninclude = other.ini\n"),
            (@"C:\3dmigoto\other.ini", "[Include]\ninclude = d3dx.ini\n[Loader]\nlaunch = GF2_Exilium.exe\n")));

        // the cycle is finite, and what stood past it is still read
        Assert.True(facts.StartsTheGame);
    }

    [Fact]
    public void The_walk_stops_at_its_depth_cap()
    {
        var files = new List<(string, string)> { (Root, "[Include]\ninclude = a1.ini\n") };
        for (int i = 1; i <= MigotoIni.MaxDepth + 2; i++)
            files.Add(($@"C:\3dmigoto\a{i}.ini", $"[Include]\ninclude = a{i + 1}.ini\n"));
        // the hook sits one level past the cap
        files.Add(($@"C:\3dmigoto\a{MigotoIni.MaxDepth + 1}.ini",
            "[CommandListSkin]\nchecktextureoverride = ps-t0\n"));

        Assert.False(MigotoIni.Parse(Root, Tree(files.ToArray())).HasTextureHook);
    }

    [Fact]
    public void An_include_naming_a_file_the_host_does_not_ship_leaves_the_rest_standing()
    {
        var facts = MigotoIni.Parse(Root, Tree(
            (Root, "[Include]\ninclude = gone.ini\n[Loader]\nlaunch = GF2_Exilium.exe\n")));

        Assert.True(facts.Found);
        Assert.True(facts.StartsTheGame);
    }

    // ---- an unset or unreadable host ----

    [Fact]
    public void An_unset_loader_reads_as_nothing_at_all()
    {
        var facts = MigotoIni.Read("");

        Assert.False(facts.Found);
        Assert.False(facts.StartsTheGame);
        Assert.False(facts.HasTextureHook);
    }

    // ---- what the launch does with the answer ----

    /// <summary>A host that starts the game itself starts the EXE, whatever this app's own resolved plan
    /// says: nothing hands it a steam:// uri, so a launcher answering with its own copy is the handoff the
    /// watch has to wait past rather than report as the game closing.</summary>
    [Fact]
    public void A_watch_behind_a_host_that_starts_the_game_follows_a_direct_start()
    {
        Assert.Equal(GameLauncher.LaunchKind.DirectExe,
            MainWindowViewModel.WatchedStartKind(GameLauncher.LaunchKind.Steam, loaderStartsGame: true));
        Assert.Equal(GameLauncher.LaunchKind.DirectExe,
            MainWindowViewModel.WatchedStartKind(GameLauncher.LaunchKind.DirectExe, loaderStartsGame: true));

        // …and this app's own start keeps the plan it resolved
        Assert.Equal(GameLauncher.LaunchKind.Steam,
            MainWindowViewModel.WatchedStartKind(GameLauncher.LaunchKind.Steam, loaderStartsGame: false));
    }
}
