using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Round-trip content nobody has touched for a week is removed; anything touched since stays, together with
/// everything that shares a folder with it. Nothing outside the round-trip root is ever touched, and a
/// delete that fails is reported and retried by the next run instead of stopping the sweep.
/// </summary>
public class RoundTripSweepTests
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    [Fact]
    public void Content_just_past_the_age_limit_goes_and_content_at_or_inside_it_stays()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string past = Mod(root, "past");
        string at = Mod(root, "at");
        string inside = Mod(root, "inside");
        Age(past, now - Week - TimeSpan.FromMinutes(1));
        Age(at, now - Week);
        Age(inside, now - Week + TimeSpan.FromMinutes(1));

        var log = Sweep(root, now);

        Assert.False(Directory.Exists(past));
        Assert.True(File.Exists(Path.Combine(at, "blender", "run", "body.glb")));
        Assert.True(File.Exists(Path.Combine(inside, "blender", "run", "body.glb")));
        Assert.Empty(log);
    }

    [Fact]
    public void A_fresh_save_deep_in_a_transport_keeps_the_whole_transport_and_its_old_files()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string mod = Mod(root, "mod");
        string source = Write(mod, "edit", "slot", "transport", "source.png");
        Write(mod, "edit", "slot", "transport", "picture.json");
        string outbound = Write(mod, "edit", "slot", "transport", "outbound.png");
        Age(mod, now - TimeSpan.FromDays(30));
        File.SetLastWriteTimeUtc(outbound, now - TimeSpan.FromHours(1));

        Sweep(root, now);

        Assert.True(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(source)!, "picture.json")));
        Assert.True(File.Exists(outbound));
        Assert.True(File.Exists(Path.Combine(mod, "owner.json")));
    }

    [Fact]
    public void A_stale_blender_run_goes_while_a_fresh_run_and_transport_in_the_same_mod_stay()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string mod = Mod(root, "mod");
        string staleRun = Path.Combine(mod, "blender", "run");
        Write(staleRun, "parts", "arm.glb");
        Write(staleRun, "textures", "body.png");
        string freshRun = Path.GetDirectoryName(Write(mod, "blender", "fresh-run", "body.glb"))!;
        string transport = Path.GetDirectoryName(Write(mod, "edit", "slot", "transport", "outbound.png"))!;
        DateTime old = now - TimeSpan.FromDays(30);
        Age(mod, old);
        Age(freshRun, now - TimeSpan.FromDays(2));
        Age(transport, now - TimeSpan.FromDays(2));

        Sweep(root, now);

        Assert.False(Directory.Exists(staleRun));
        Assert.True(File.Exists(Path.Combine(freshRun, "body.glb")));
        Assert.True(File.Exists(Path.Combine(transport, "outbound.png")));
        Assert.True(File.Exists(Path.Combine(mod, "owner.json")));
        // Removing the stale run is not a touch: the folder that held it keeps the time it had.
        Assert.Equal(old, Directory.GetLastWriteTimeUtc(Path.Combine(mod, "blender")));
    }

    [Fact]
    public void A_mod_folder_stale_as_a_whole_goes_whole_and_other_mods_are_left_alone()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string stale = Mod(root, "stale");
        Write(stale, "edit", "slot", "transport", "source.png");
        string guide = Write(stale, "guides", "uv.png");
        File.SetAttributes(guide, FileAttributes.ReadOnly);   // read-only content goes too
        string fresh = Mod(root, "fresh");
        Age(stale, now - TimeSpan.FromDays(8));

        Sweep(root, now);

        Assert.False(Directory.Exists(stale));
        Assert.True(File.Exists(Path.Combine(fresh, "owner.json")));
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void A_file_held_open_stays_is_reported_and_goes_on_the_next_run_once_released()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string mod = Mod(root, "mod");
        string held = Write(mod, "edit", "slot", "transport", "source.png");
        string beside = Write(mod, "edit", "slot", "transport", "picture.json");
        Age(mod, now - TimeSpan.FromDays(8));

        List<(string What, Exception Error)> log;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            log = Sweep(root, now);
            // A logger that itself fails does not break the sweep either.
            RoundTripSweep.Run(root, now, Week, (_, _) => throw new InvalidOperationException("logger down"));
        }

        Assert.True(File.Exists(held));
        Assert.Contains(log, entry => entry.What.Contains(held, StringComparison.OrdinalIgnoreCase));
        // What was not held is removed; the held file keeps only the folders holding it.
        Assert.False(File.Exists(beside));
        Assert.False(File.Exists(Path.Combine(mod, "owner.json")));

        Sweep(root, DateTime.UtcNow);

        Assert.False(Directory.Exists(mod));
    }

    [Fact]
    public void A_link_to_a_folder_outside_the_root_is_neither_followed_nor_emptied()
    {
        using var temp = new TempGame();
        string root = temp.At("round-trips");
        string mod = Mod(root, "mod");
        // One link inside a mod's Blender folder, to a folder holding one file that is fresh; one link standing
        // as a mod folder of its own, to a folder holding only stale content.
        string freshOutside = temp.At("fresh-outside");
        string freshOld = Write(freshOutside, "old.png");
        string freshNew = Write(freshOutside, "sub", "new.png");
        string staleOutside = temp.At("stale-outside");
        string staleOld = Write(staleOutside, "sub", "old.png");
        string nested = Path.Combine(mod, "blender", "linked-run");
        string top = Path.Combine(root, "linked-mod");
        if (!TryJunction(nested, freshOutside) || !TryJunction(top, staleOutside)) return;   // host refuses junctions
        // A month from now everything made in this test is stale, links included, except the one file stamped at
        // that moment. A scan that followed the nested link would find it and keep the mod; a delete that
        // followed the top link would empty the stale folder.
        DateTime now = DateTime.UtcNow + TimeSpan.FromDays(30);
        File.SetLastWriteTimeUtc(freshNew, now);

        Sweep(root, now);

        Assert.False(Directory.Exists(mod));
        Assert.False(Directory.Exists(top));
        Assert.True(File.Exists(freshOld));
        Assert.True(File.Exists(freshNew));
        Assert.True(File.Exists(staleOld));
    }

    [Fact]
    public void A_folder_the_sweep_cannot_read_counts_as_touched_and_keeps_its_mod()
    {
        using var temp = new TempGame();
        DateTime now = DateTime.UtcNow;
        string root = temp.At("round-trips");
        string mod = Mod(root, "mod");
        string locked = Path.GetDirectoryName(Write(mod, "edit", "slot", "transport", "source.png"))!;
        Age(mod, now - TimeSpan.FromDays(30));
        var info = new DirectoryInfo(locked);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory,
            AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var log = Sweep(root, now);

            Assert.True(File.Exists(Path.Combine(mod, "owner.json")));
            Assert.Contains(log, entry => entry.What.Contains(locked, StringComparison.OrdinalIgnoreCase));
            // Stale content beside it is still judged on its own.
            Assert.False(Directory.Exists(Path.Combine(mod, "blender")));
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_missing_or_empty_root_is_left_as_it_is()
    {
        using var temp = new TempGame();
        string missing = temp.At("never-made");
        string empty = temp.At("empty");
        Directory.CreateDirectory(empty);

        var log = Sweep(missing, DateTime.UtcNow);
        log.AddRange(Sweep(empty, DateTime.UtcNow));
        log.AddRange(Sweep("", DateTime.UtcNow));

        Assert.Empty(log);
        Assert.False(Directory.Exists(missing));
        Assert.True(Directory.Exists(empty));
    }

    private static List<(string What, Exception Error)> Sweep(string root, DateTime now)
    {
        var log = new List<(string, Exception)>();
        RoundTripSweep.Run(root, now, Week, (what, e) => log.Add((what, e)));
        return log;
    }

    /// <summary>A mod's round-trip folder with its owner record and one Blender run.</summary>
    private static string Mod(string root, string name)
    {
        string mod = Path.Combine(root, name);
        Write(mod, "owner.json");
        Write(mod, "blender", "run", "body.glb");
        return mod;
    }

    private static string Write(string folder, params string[] parts)
    {
        string file = Path.Combine(folder, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        return file;
    }

    /// <summary>Stamp a folder and everything in it, contents first so the folder's own time holds.</summary>
    private static void Age(string folder, DateTime when)
    {
        foreach (string file in Directory.GetFiles(folder)) File.SetLastWriteTimeUtc(file, when);
        foreach (string sub in Directory.GetDirectories(folder)) Age(sub, when);
        Directory.SetLastWriteTimeUtc(folder, when);
    }

    /// <summary>A directory junction at <paramref name="link"/> pointing at <paramref name="target"/>; false
    /// when this machine refuses to make one. Junctions need no elevation.</summary>
    private static bool TryJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
