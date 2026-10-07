using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Where a mod's round trips live and how an image-editor save is noticed. The folder an outside editor is
/// handed belongs to the mod, named by the id the mod carries, not to the folder the mod sits in: a moved
/// mod keeps it, a copy gets its own, and a new mod where a deleted one was starts empty. A save is taken
/// only once the editor has finished writing it.
/// </summary>
public class RoundTripFolderTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
    private const string Marker = "picture.json";

    [Fact]
    public void A_mod_keeps_the_round_trip_folder_named_by_its_id()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string mod = Mod(temp, "a", id);

        string folder = store.Claim(id, mod)!;

        Assert.Equal(Path.Combine(store.Root, id), folder);
        Assert.Equal(folder, store.Claim(id, mod));
        Assert.Equal(Path.GetFullPath(mod), store.OwnerOf(folder));
    }

    /// <summary>A copy carries the id of the mod it was copied from. While that mod still carries it, the
    /// copy is refused the folder and needs an id of its own.</summary>
    [Fact]
    public void A_copy_of_a_mod_is_refused_the_original_folder()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        store.Claim(id, Mod(temp, "original", id));

        Assert.Null(store.Claim(id, Mod(temp, "copy", id)));
    }

    /// <summary>A mod moved outside the app carries its id to its new folder, and the recorded folder no
    /// longer holds it: the mod takes its round trips along.</summary>
    [Fact]
    public void A_mod_moved_outside_the_app_takes_its_round_trips_along()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string before = Mod(temp, "before", id);
        string folder = store.Claim(id, before)!;
        string after = temp.At(Path.Combine("mods", "after"));
        Directory.Move(before, after);

        Assert.Equal(folder, store.Claim(id, after));
        Assert.Equal(Path.GetFullPath(after), store.OwnerOf(folder));
    }

    /// <summary>A mod deleted outside the app and a new mod made in the same folder are two mods. The new
    /// one carries no id or a different one, so it never reaches the deleted mod's round trips.</summary>
    [Fact]
    public void A_new_mod_where_a_deleted_one_was_starts_with_its_own_folder()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string deletedId = RoundTripStore.NewId();
        string path = Mod(temp, "same-name", deletedId);
        string deleted = store.Claim(deletedId, path)!;
        Directory.Delete(path, recursive: true);

        string newId = RoundTripStore.NewId();
        string fresh = store.Claim(newId, Mod(temp, "same-name", newId))!;

        Assert.NotEqual(deleted, fresh);
    }

    /// <summary>The app's own rename records the new folder, so a copy made next to the renamed mod is still
    /// told apart from it.</summary>
    [Fact]
    public void A_copy_made_after_a_rename_is_refused_the_renamed_mod_folder()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string before = Mod(temp, "before", id);
        store.Claim(id, before);
        string after = temp.At(Path.Combine("mods", "after"));
        Directory.Move(before, after);
        store.Retarget(id, after);

        Assert.Null(store.Claim(id, Mod(temp, "copy", id)));
    }

    /// <summary>Opening a mod marks its round-trip folder as in use, so a sweep that judged the folder stale
    /// a moment earlier keeps it.</summary>
    [Fact]
    public void A_claim_keeps_a_stale_folder_from_the_sweep()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string mod = Mod(temp, "a", id);
        string folder = store.Claim(id, mod)!;
        var old = DateTime.UtcNow.AddDays(-30);
        foreach (string file in Directory.EnumerateFiles(folder)) File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(folder, old);

        store.Claim(id, mod);
        RoundTripSweep.Run(store.Root, DateTime.UtcNow, TimeSpan.FromDays(7), (_, e) => throw e);

        Assert.True(Directory.Exists(folder));
        Assert.Equal(Path.GetFullPath(mod), store.OwnerOf(folder));
    }

    /// <summary>A mod whose round-trip folder a sweep removed gets it back the next time it opens.</summary>
    [Fact]
    public void A_claim_makes_a_swept_folder_again()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string mod = Mod(temp, "a", id);
        string folder = store.Claim(id, mod)!;
        Directory.Delete(folder, recursive: true);

        Assert.Equal(folder, store.Claim(id, mod));
        Assert.Equal(Path.GetFullPath(mod), store.OwnerOf(folder));
    }

    [Fact]
    public void A_round_trip_id_is_one_safe_folder_name()
    {
        Assert.True(RoundTripStore.IsId(RoundTripStore.NewId()));
        Assert.False(RoundTripStore.IsId(null));
        Assert.False(RoundTripStore.IsId(@".." + new string('a', 29)));
        Assert.False(RoundTripStore.IsId(new string('A', 32)));
    }

    [Fact]
    public void A_path_inside_the_store_names_its_round_trip_folder()
    {
        using var temp = new TempGame();
        var store = new RoundTripStore(temp.At("round-trips"));
        string id = RoundTripStore.NewId();
        string folder = store.Claim(id, Mod(temp, "a", id))!;

        Assert.Equal(folder,
            store.FolderContaining(Path.Combine(folder, "edit", "slot", "id", "outbound.png")));
        Assert.Null(store.FolderContaining(temp.At(Path.Combine("mods", "a", "mod.drlproj"))));
    }

    /// <summary>A mod folder whose manifest carries <paramref name="id"/>.</summary>
    private static string Mod(TempGame temp, string name, string id)
    {
        string folder = temp.At(Path.Combine("mods", name));
        Directory.CreateDirectory(folder);
        File.WriteAllText(ModProject.ManifestPathFor(folder), "{\"round_trip_id\":\"" + id + "\"}");
        return folder;
    }

    [Fact]
    public void A_save_is_reported_once_the_file_settles()
    {
        using var temp = new TempGame();
        string transport = temp.At(Path.Combine("round-trips", "mod", "edit", "slot", "id"));
        Directory.CreateDirectory(transport);
        File.WriteAllText(Path.Combine(transport, Marker), "{}");
        var saved = new BlockingCollection<string>();
        using var watcher = new PictureSaveWatcher(temp.At("round-trips"), Marker, saved.Add, _ => { },
            settleMs: 50);

        TestImages.WritePng(Path.Combine(transport, "outbound.png"), g: 10);

        Assert.True(saved.TryTake(out var folder, Settle), "the save was never reported");
        Assert.Equal(Path.GetFullPath(transport), Path.GetFullPath(folder!));
    }

    /// <summary>An editor writing a picture holds the file while it writes. The save is reported after the
    /// editor lets go, never while the file is half-written.</summary>
    [Fact]
    public void A_picture_still_being_written_is_waited_out()
    {
        using var temp = new TempGame();
        string transport = temp.At(Path.Combine("round-trips", "mod", "edit", "slot", "id"));
        Directory.CreateDirectory(transport);
        File.WriteAllText(Path.Combine(transport, Marker), "{}");
        string file = Path.Combine(transport, "outbound.png");
        var saved = new BlockingCollection<string>();
        using var watcher = new PictureSaveWatcher(temp.At("round-trips"), Marker, saved.Add, _ => { },
            settleMs: 50);

        byte[] png = File.ReadAllBytes(TestImages.WritePng(temp.At("source.png"), g: 20));
        using (var writing = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writing.Write(png, 0, png.Length / 2);
            writing.Flush(flushToDisk: true);
            Assert.False(saved.TryTake(out _, TimeSpan.FromMilliseconds(600)),
                "a save was reported while the editor still held the file");
            writing.Write(png, png.Length / 2, png.Length - png.Length / 2);
        }

        Assert.True(saved.TryTake(out _, Settle), "the save was never reported after the editor let go");
        Assert.Equal(png, File.ReadAllBytes(file));
    }

    /// <summary>Blender's files share the round-trip tree. A folder that is not an image-editor transport is
    /// never reported, and its files are never opened.</summary>
    [Fact]
    public void A_folder_that_is_not_a_picture_transport_is_left_alone()
    {
        using var temp = new TempGame();
        string blender = temp.At(Path.Combine("round-trips", "mod", "edit", "slot", "blender-id"));
        Directory.CreateDirectory(blender);
        var saved = new BlockingCollection<string>();
        using var watcher = new PictureSaveWatcher(temp.At("round-trips"), Marker, saved.Add, _ => { },
            settleMs: 50);

        TestImages.WritePng(Path.Combine(blender, "outbound.png"), g: 5);

        Assert.False(saved.TryTake(out _, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void A_disposed_watch_reports_nothing()
    {
        using var temp = new TempGame();
        string transport = temp.At(Path.Combine("round-trips", "mod", "edit", "slot", "id"));
        Directory.CreateDirectory(transport);
        File.WriteAllText(Path.Combine(transport, Marker), "{}");
        var saved = new BlockingCollection<string>();
        var watcher = new PictureSaveWatcher(temp.At("round-trips"), Marker, saved.Add, _ => { },
            settleMs: 50);
        watcher.Dispose();

        TestImages.WritePng(Path.Combine(transport, "outbound.png"), g: 30);

        Assert.False(saved.TryTake(out _, TimeSpan.FromMilliseconds(500)));
    }
}
