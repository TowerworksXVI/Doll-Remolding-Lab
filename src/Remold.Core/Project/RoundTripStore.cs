using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remold.Core.Project;

/// <summary>The app-owned folders mods' round trips live in, one per mod. Blender and image editors are
/// handed files from here rather than from inside the mod, so renaming or moving a mod's folder never moves
/// a file one of them still holds.
///
/// <para>A folder is named by the mod's <see cref="AuthoredProject.RoundTripId"/> and records the mod folder
/// that owns it. The record is what tells a copy from a move: a copy of a mod carries the same id, and while
/// the mod it was copied from still carries that id in its own folder, the copy is refused the folder and
/// gets an id of its own. A mod whose recorded folder no longer carries the id has moved, and takes the
/// folder with it.</para></summary>
public sealed class RoundTripStore
{
    private const string OwnerFile = "owner.json";

    // One gate for every store in the process: two stores over one root would otherwise both mint.
    private static readonly object Gate = new();

    private sealed record Owner([property: JsonPropertyName("mod")] string Mod);

    public RoundTripStore(string root) => Root = Path.GetFullPath(root);

    /// <summary>The production root: beside the app, with the mods library and settings. Round trips hold
    /// the modder's unsent work, so they live with durable state rather than in the regenerable
    /// cache.</summary>
    public static string DefaultRoot => Path.Combine(LabPaths.DurableRoot, "round-trips");

    public string Root { get; }

    /// <summary>Whether <paramref name="id"/> has the shape of a round-trip id: 32 lower-case hex digits,
    /// which is also what keeps it a single safe folder name.</summary>
    public static bool IsId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>The round-trip folder for the mod in <paramref name="modRoot"/> that carries
    /// <paramref name="id"/>, made on first use; or null when another mod folder still carries the same id,
    /// which makes this mod a copy that needs an id of its own.
    ///
    /// <para>The owner record is written on every claim, which marks the folder as in use for
    /// <see cref="RoundTripSweep"/>: a sweep running while a mod opens finds the record newer than its scan
    /// and keeps the folder.</para></summary>
    public string? Claim(string id, string modRoot)
    {
        if (!IsId(id)) throw new ArgumentException("not a round-trip id", nameof(id));
        string mod = Normalize(modRoot);
        string folder = Path.Combine(Root, id);
        lock (Gate)
        {
            string? owner = ReadOwner(folder);
            bool ours = owner is not null && SamePath(owner, mod);
            if (owner is not null && !ours && StillCarries(owner, id)) return null;
            WriteOwner(folder, mod);
            return folder;
        }
    }

    /// <summary>Record that the mod carrying <paramref name="id"/> now lives in
    /// <paramref name="newRoot"/>. Without it, a copy made while the recorded folder is gone would take the
    /// round trips for itself.</summary>
    public void Retarget(string id, string newRoot)
    {
        if (!IsId(id)) return;
        string folder = Path.Combine(Root, id);
        lock (Gate)
            if (Directory.Exists(folder)) WriteOwner(folder, Normalize(newRoot));
    }

    /// <summary>The mod folder <paramref name="folder"/> belongs to, or null when it is not one of this
    /// store's round-trip folders.</summary>
    public string? OwnerOf(string folder)
    {
        string full = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase))
            return null;
        return ReadOwner(full);
    }

    /// <summary>The round-trip folder a path inside this store belongs to, or null for a path outside
    /// it.</summary>
    public string? FolderContaining(string path)
    {
        string full = Path.GetFullPath(path);
        string prefix = Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = full[prefix.Length..];
        int cut = rest.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return Path.Combine(Root, cut < 0 ? rest : rest[..cut]);
    }

    /// <summary>Whether the mod folder <paramref name="modRoot"/> still holds a mod carrying
    /// <paramref name="id"/>. A manifest that is there but cannot be read counts as carrying it: refusing a
    /// real move its folder costs that mod's pending round trips, while handing a copy the original's folder
    /// would land the original's saves in the wrong mod.</summary>
    private static bool StillCarries(string modRoot, string id)
    {
        string manifest = ModProject.ManifestPathFor(modRoot);
        if (!File.Exists(manifest)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("round_trip_id", out var carried)
                && carried.ValueKind == JsonValueKind.String
                && string.Equals(carried.GetString(), id, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    // An unreadable owner record names no mod, so the next mod to carry the folder's id takes the folder.
    private static string? ReadOwner(string folder)
    {
        try
        {
            string file = Path.Combine(folder, OwnerFile);
            return File.Exists(file) ? JsonSerializer.Deserialize<Owner>(File.ReadAllText(file))?.Mod : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The folder is made here, and made again once if it vanished mid-write: a sweep can remove a stale
    // folder at the moment a mod claims it.
    private static void WriteOwner(string folder, string mod)
    {
        string text = JsonSerializer.Serialize(new Owner(mod));
        Directory.CreateDirectory(folder);
        try { WriteAtomically(Path.Combine(folder, OwnerFile), text); }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(folder);
            WriteAtomically(Path.Combine(folder, OwnerFile), text);
        }
    }

    /// <summary>Replace <paramref name="file"/> with <paramref name="text"/> in one move, so a reader finds
    /// the old record or the new one and never half of either. A file another program has just touched can
    /// refuse the move for a moment, so it is retried for up to a second before the failure is
    /// thrown.</summary>
    public static void WriteAtomically(string file, string text)
    {
        string staged = file + ".tmp";
        File.WriteAllText(staged, text);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staged, file, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}
