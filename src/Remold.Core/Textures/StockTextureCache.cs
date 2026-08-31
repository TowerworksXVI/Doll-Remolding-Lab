using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Remold.Core.Bundles;

namespace Remold.Core.Textures;

/// <summary>
/// The durable, full-resolution stock-PNG cache behind every Blender open. Decoding a game texture and
/// encoding it as a PNG is ~95% of an open's cost (a 2048² map is a few hundred milliseconds on its own,
/// and a subject carries a dozen), and the answer is fixed by the bundle's content: the same bytes decode
/// to the same picture every open. So the picture is written once and every later open links it into the
/// run's <c>textures/</c> folder for nothing.
///
/// <para><b>Keying</b> is the bundle's CONTENT identity as the game's own manifest states it (the stub's
/// <c>SubHash</c>, which <see cref="Export.CandidacyCache"/> and sharing reuse already key on — one identity
/// home) plus the texture's name AND its path id. The path id is the game's own identity for a Texture2D and
/// the name is not (a bundle can ship many same-named textures), so a key without it would make one of two
/// same-named pictures durable under the other's name. No bundle bytes are hashed to mint a key, and no
/// catalog version enters it: a game update misses exactly the bundles it rewrote and every other entry still
/// stands. The cost is that content swapped underneath an unchanged manifest reads as unchanged, which is the
/// same trade the candidacy memo takes.</para>
///
/// <para><b>Nothing here may produce wrong pixels.</b> Every disk touch is best-effort: an unwritable cache
/// costs the next open a re-export, a file that isn't a whole PNG is re-exported rather than served, and a
/// publish that can't land leaves whatever was there. Writes are atomic (unique temp + move), so a
/// concurrent reader never sees a half file and two processes racing one key publish whole files.</para>
///
/// <para>The envelope check is not a decode, so an entry whose MIDDLE bytes are damaged still reads as whole
/// and is served. That case is answered where the damage shows: the picture fails to decode at the glb
/// writer, and the export deletes the entry through <see cref="Invalidate"/> and re-exports the map on the
/// next open. Checking by decode here would cost every hit what the cache exists to save.</para>
/// </summary>
public sealed class StockTextureCache
{
    /// <summary>The PNG file signature, and the trailing IEND chunk every complete PNG ends with. A cached
    /// file is checked against both before it is served: the atomic publish cannot leave a partial file, but
    /// a truncated disk, a killed antivirus restore or a hand-edited cache can, and serving half a picture
    /// as the game's own map is the one failure this cache must not have. Costs two short reads — a decode
    /// would cost as much as the export it is meant to save.</summary>
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] PngEnd = { 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 };

    private readonly string _root;
    private readonly HashSet<string> _sweptDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sweepLock = new();

    /// <param name="rootOverride">Cache root; defaults to the regenerable-cache stock-texture tree
    /// (<see cref="LabPaths.StockTextureRoot"/>). Tests pass a temp dir — nothing here may reach the running
    /// user's own cache.</param>
    public StockTextureCache(string? rootOverride = null) => _root = rootOverride ?? LabPaths.StockTextureRoot;

    /// <summary>The on-disk path one texture's cached PNG would live at, whether or not it exists. The key is
    /// a hash, so the file carries no game-derived name and every entry is the same width; the first two
    /// characters fan the tree out, since a full corpus is tens of thousands of files and one flat directory
    /// of those is slow to enumerate on every sweep.</summary>
    /// <param name="pathId">The Texture2D's own path id in that bundle — the game's identity for it, which
    /// the renderer pinned and the name does not supply.</param>
    public string PathFor(string bundleContentId, string textureName, long pathId)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            bundleContentId + "\n" + textureName + "\n" + pathId.ToString(CultureInfo.InvariantCulture))))
            .ToLowerInvariant();
        return Path.Combine(_root, key[..2], key + ".png");
    }

    /// <summary>The cached PNG's path if a WHOLE one is on disk for this texture, else null. Fast — one
    /// existence check and two short reads, no decode and no bundle touch.</summary>
    /// <inheritdoc cref="PathFor"/>
    public string? TryGet(string bundleContentId, string textureName, long pathId)
    {
        var path = PathFor(bundleContentId, textureName, pathId);
        return IsWholePng(path) ? path : null;
    }

    /// <summary>Drop this key's entry, so the next open exports the map from the game again. The one caller is
    /// the export's own recovery from a picture that would not decode: the entry passed every check this cache
    /// can afford and is still not a readable image, and nothing else would ever stop serving it. Best-effort
    /// — an entry that cannot be deleted costs another failed decode, never a wrong picture.</summary>
    /// <inheritdoc cref="PathFor"/>
    public void Invalidate(string bundleContentId, string textureName, long pathId)
    {
        try { File.Delete(PathFor(bundleContentId, textureName, pathId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException or PathTooLongException) { }
    }

    /// <summary>Publish a decoded texture as this key's cached PNG and return where it landed, or null when
    /// nothing could be written. The bytes are encoded once, into a unique temp beside the target, and moved
    /// over it — so a reader mid-publish either sees the previous whole file or none at all. A move that
    /// loses a race with another process publishing the SAME key keeps that file: the key is the content, so
    /// the two are the same picture.</summary>
    /// <inheritdoc cref="PathFor"/>
    public string? Publish(BundleReader.DecodedTexture texture, string bundleContentId, string textureName,
        long pathId)
    {
        var path = PathFor(bundleContentId, textureName, pathId);
        var dir = Path.GetDirectoryName(path)!;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            SweepOnce(dir);
            TextureExport.WritePng(texture, tmp);
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Another publisher of the same key holds the target open. Its bytes are this key's bytes,
                // so the entry is good either way — take it if it is whole, and otherwise leave the cache
                // without one rather than guessing.
                return IsWholePng(path) ? path : null;
            }
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException
                                     or ArgumentException or PathTooLongException)
        {
            return null;   // an unwritable cache is a slower next open, never a wrong one
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Put a cached PNG at <paramref name="destination"/>. A HARD LINK where the filesystem allows
    /// one — the run folder then costs no bytes at all, which is the whole point of caching full-resolution
    /// maps — and a copy anywhere it doesn't (a different volume, a filesystem without links, a link count at
    /// its limit). False when neither landed.
    ///
    /// <para>A hard link is a second NAME for one file, not a snapshot: whatever writes through the
    /// destination writes the cache entry too. <paramref name="destination"/> is inside the modder's own mod
    /// folder (<c>&lt;project&gt;\.ingress\blender\&lt;run&gt;\textures</c>), so the app is not the only thing
    /// that can reach it — an image tool, an antivirus restore or a sync client that edits a file in place
    /// would write the durable entry through the link. The trade is taken deliberately: nothing in the app
    /// writes there (the glb writer only reads it), the tools that realistically touch such a file REPLACE it,
    /// which breaks the link rather than following it, and an entry that does end up unreadable is deleted and
    /// re-exported the first time a map fails to decode (<see cref="Invalidate"/>). What a link buys is the
    /// whole point of caching full-resolution maps: the run folder costs no bytes at all.</para></summary>
    public static bool Place(string cached, string destination)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException or PathTooLongException) { return false; }
        try { if (File.Exists(destination)) File.Delete(destination); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        if (CreateHardLinkW(destination, cached, IntPtr.Zero)) return true;
        try { File.Copy(cached, destination, overwrite: true); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    /// <summary>Whether the file at <paramref name="path"/> is a complete PNG — signature at the front, IEND
    /// at the back. Anything else is treated as absent, so the caller exports afresh and publishes over it.</summary>
    private static bool IsWholePng(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (s.Length < PngSignature.Length + PngEnd.Length) return false;
            Span<byte> head = stackalloc byte[8];
            s.ReadExactly(head);
            for (int i = 0; i < PngSignature.Length; i++) if (head[i] != PngSignature[i]) return false;
            Span<byte> tail = stackalloc byte[12];
            s.Seek(-PngEnd.Length, SeekOrigin.End);
            s.ReadExactly(tail);
            for (int i = 0; i < PngEnd.Length; i++) if (tail[i] != PngEnd[i]) return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                     or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Clear orphaned publish temps the first time this instance writes into a directory — a
    /// process killed between the encode and the move leaves one behind and nothing ever names it
    /// again.</summary>
    private void SweepOnce(string dir)
    {
        lock (_sweepLock) { if (!_sweptDirs.Add(dir)) return; }
        CacheTemps.Sweep(dir);
    }

    /// <summary>Windows' own hard-link call. There is no BCL equivalent (<c>File.CreateSymbolicLink</c> makes
    /// a different thing, which a later game rescan deleting the cache would leave dangling). False is the
    /// ordinary answer on a cross-volume destination, and the caller copies.</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr reserved);
}
