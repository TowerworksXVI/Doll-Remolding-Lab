using System;
using System.IO;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Remold.Core.Migoto;

/// <summary>
/// Turns an author-supplied texture into the DDS form a generated mod binds. A <c>.dds</c> source is
/// trusted verbatim (the author already chose a format); anything ImageSharp decodes (PNG etc.) becomes
/// BC7 carrying a full mip chain down to 1×1, tagged <c>_SRGB</c> or UNORM per the slot it replaces.
///
/// <para>Compression and mips are done here because 3DMigoto binds the file verbatim: it compresses nothing
/// and generates no mips at runtime, so an uncompressed chainless map ships at four times the bytes and
/// aliases at distance. BC7 is emitted whatever BCn format the stock map uses — every channel matters here
/// (a patched albedo's alpha, and the packed-normal layout carrying X in alpha and Y in green, which
/// downsamples correctly because the chain is filtered per channel with no premultiply).</para>
///
/// <para>The sRGB tag is a property of the SLOT, not of the image: the shader's sRGB→linear conversion
/// happens on sample, so a colour map tagged UNORM reads washed out and a data map tagged <c>_SRGB</c> reads
/// over-dark. The caller reads the tag off the stock texture being replaced.</para>
///
/// <para>Row order is one rule for every consumer. The game samples with Unity Vs against bottom-up rows,
/// while every image in the workspace is top-down, so a decoded source always flips. A <c>.dds</c> source
/// never decodes at all.</para>
///
/// <para>Mip filtering happens in STORED space, which for an <c>_SRGB</c> map means over sRGB-encoded bytes
/// rather than linearised values: a 2×2 of black and white stores 127 in its 1×1 level where a linear-space
/// average would store ~188. Accepted, not a defect to chase — the darkening is confined to the small levels
/// a distant sample reaches.</para>
/// </summary>
public static class AuthoredDds
{
    /// <summary>True when the source ships verbatim instead of being encoded. The one home for that rule, so
    /// callers tell an encode from a copy without re-deciding it.</summary>
    public static bool IsPassthrough(string sourcePath) =>
        sourcePath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase);

    /// <summary>Encode (or pass through) <paramref name="sourcePath"/> to <paramref name="destPath"/>. A
    /// decoded source lands in Unity-native bottom-up row order as BC7 with a full mip chain; a <c>.dds</c>
    /// source passes through verbatim. Throws on an unreadable/undecodable source — a build must never
    /// silently ship a missing map.
    ///
    /// <para><paramref name="log"/> receives one progress line per ENCODE, once dimensions are known and
    /// passthrough is ruled out: a full-size map costs tens of seconds and silence reads as hung.</para>
    ///
    /// <para><paramref name="cacheDir"/> (null = don't) keeps the encoded blob keyed by source CONTENT, the
    /// sRGB tag and the codec that wrote it — everything that changes the bytes — so re-encoding an unchanged
    /// image across builds is a file copy, one reported through <paramref name="log"/> like the encode it
    /// replaces. A damaged entry is discarded and re-encoded rather than served, and entries an earlier codec
    /// wrote are reclaimed. <paramref name="taskCount"/> caps the worker count of the managed encoder
    /// <see cref="Bc7Encoder"/> falls back to; null leaves it at all cores. It does not change the
    /// bytes.</para></summary>
    public static void Encode(string sourcePath, string destPath, bool srgb, Action<string>? log = null,
        string? cacheDir = null, int? taskCount = null, string? sourceIdentity = null)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"'{Path.GetFileName(sourcePath)}' is missing from the mod folder", sourcePath);
        if (IsPassthrough(sourcePath))
        {
            // the extension is only the author's claim: a renamed PNG would ship as a container nothing can
            // bind, and the build would call itself a success
            if (!HasDdsMagic(sourcePath))
                throw new InvalidDataException(
                    $"'{Path.GetFileName(sourcePath)}' is named .dds but is not a DDS file");
            File.Copy(sourcePath, destPath, overwrite: true);
            return;
        }

        string? cached = null;
        if (cacheDir is not null)
        {
            SweepStaleCodec(cacheDir);
            cached = CachePath(cacheDir, sourcePath, srgb, sourceIdentity);
        }
        if (cached is not null && TryServe(cached, destPath))
        {
            log?.Invoke($"texture cache: reusing {Path.GetFileName(sourcePath)}");
            return;
        }

        using var image = Image.Load<Rgba32>(sourcePath);
        image.Mutate(x => x.Flip(FlipMode.Vertical));
        int w = image.Width, h = image.Height;
        log?.Invoke($"Encoding {Path.GetFileName(sourcePath)} ({w}×{h})…");
        var pixels = new byte[w * h * 4];
        image.CopyPixelDataTo(pixels);

        byte[][] levels = Bc7Encoder.EncodeMipChain(pixels, w, h, taskCount);
        using (var f = File.Create(destPath))
            DdsWriter.Write(f, srgb ? DdsWriter.BC7_UNORM_SRGB : DdsWriter.BC7_UNORM, w, h, levels);
        if (cached is not null) Publish(destPath, cached);
    }

    /// <summary>What two authored sources share exactly when their bytes are identical. The file NAME says
    /// nothing about identity: one authored image reaches a build under a name per submesh, so a caller
    /// keying on the path encodes and ships the same pixels once per name.
    ///
    /// <para>A missing source is refused in <see cref="Encode"/>'s wording: a caller identifying an image
    /// before encoding it must not report a deleted or renamed workspace file differently from one that
    /// reaches the encoder.</para></summary>
    public static string SourceIdentity(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(
                $"'{Path.GetFileName(sourcePath)}' is missing from the mod folder", sourcePath);
        using var s = File.OpenRead(sourcePath);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Bumped when the encoded bytes change for reasons the key cannot see — the container header,
    /// the row order, the mip rule, the target format, or an encoder upgrade.</summary>
    private const string CodecVersion = "bc7-mips-bottomup-2";

    /// <summary>Where an encode of this source under this tag lives. The source is keyed by CONTENT, so an
    /// edited image misses and a renamed or copied one hits.</summary>
    private static string CachePath(string cacheDir, string sourcePath, bool srgb, string? sourceIdentity) =>
        Path.Combine(cacheDir,
            $"{sourceIdentity ?? SourceIdentity(sourcePath)}.{(srgb ? "srgb" : "lin")}.{CodecVersion}.dds");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CodecSwept =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Delete the entries an earlier <see cref="CodecVersion"/> published, once per cache directory
    /// per run. A version bump makes every earlier entry unreachable — nothing ever names that key again — so
    /// without this they sit in the cache for the life of the install, at a full mip chain each. Best-effort
    /// and silent: an entry that will not delete is simply met again next run.</summary>
    private static void SweepStaleCodec(string cacheDir)
    {
        if (!CodecSwept.TryAdd(cacheDir, 0)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(cacheDir, "*.dds"))
            {
                // {content}.{srgb|lin}.{codec}.dds — the codec component is the one before the extension
                var parts = Path.GetFileName(f).Split('.');
                if (parts.Length >= 2 && parts[^2] != CodecVersion) Discard(f);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // no directory yet, or it vanished — best-effort
        }
    }

    /// <summary>Copy a finished encode into the cache atomically (unique temp + move), so a reader on
    /// another build never sees a partial file. A failed publish leaves no ENTRY — the build already has its
    /// output, and a half-written entry would be served as truth. The recorded length lands first, so an
    /// entry is never present without one; a length left without its entry is never served on its own and is
    /// overwritten by the next publish of the same key.</summary>
    private static void Publish(string encoded, string cachePath)
    {
        var id = Guid.NewGuid().ToString("N");
        var tmp = cachePath + "." + id + ".tmp";
        var lenTmp = LengthPath(cachePath) + "." + id + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(dir);
            CacheTemps.SweepOnce(dir);
            File.Copy(encoded, tmp, overwrite: true);
            File.WriteAllText(lenTmp,
                new FileInfo(tmp).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The DDS is the completion marker: its recorded length is published first, so an entry that
            // exists always has one to be checked against.
            File.Move(lenTmp, LengthPath(cachePath), overwrite: true);
            File.Move(tmp, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Publishing is best-effort in every one of its failure modes — a full disk, a locked file, a
            // path the cache root cannot hold: the build has its encode in hand either way, and the next
            // build re-encodes.
        }
        finally
        {
            foreach (var t in new[] { tmp, lenTmp })
                if (File.Exists(t)) { try { File.Delete(t); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Where the byte count of the entry at <paramref name="cachePath"/> is recorded.</summary>
    private static string LengthPath(string cachePath) => cachePath + ".len";

    /// <summary>Serve the cache entry at <paramref name="cachePath"/> to <paramref name="destPath"/>, or
    /// answer false when there is nothing sound to serve.
    ///
    /// <para>The entry ships into the mod verbatim and nothing downstream reads it again, so a damaged one
    /// would reach the author's game as a map that binds to nothing, under a build that called itself a
    /// success. An entry is served only when it is still the length its publish recorded and still opens
    /// with the DDS fourcc; anything else is deleted, and the caller's re-encode republishes it.</para></summary>
    private static bool TryServe(string cachePath, string destPath)
    {
        if (!File.Exists(cachePath)) return false;
        try
        {
            if (RecordedLength(cachePath) is { } expected
                && new FileInfo(cachePath).Length == expected
                && HasDdsMagic(cachePath))
            {
                File.Copy(cachePath, destPath, overwrite: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // unreadable for any reason — the encode below is always available
        }
        Discard(cachePath);
        return false;
    }

    /// <summary>The byte count recorded beside an entry at publish, or null when it is absent or not a
    /// number.</summary>
    private static long? RecordedLength(string cachePath)
    {
        var path = LengthPath(cachePath);
        if (!File.Exists(path)) return null;
        return long.TryParse(File.ReadAllText(path).Trim(),
            System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
            out long n) ? n : null;
    }

    /// <summary>Remove an entry that cannot be served, so the re-encode replaces it instead of every later
    /// build re-checking the same damage. Best-effort: a locked entry is simply missed again.</summary>
    private static void Discard(string cachePath)
    {
        foreach (var p in new[] { cachePath, LengthPath(cachePath) })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    /// <summary>True when the file opens with the <c>DDS&#160;</c> fourcc.</summary>
    private static bool HasDdsMagic(string path)
    {
        using var s = File.OpenRead(path);
        var magic = new byte[4];
        return s.ReadAtLeast(magic, 4, throwOnEndOfStream: false) == 4
            && magic[0] == 'D' && magic[1] == 'D' && magic[2] == 'S' && magic[3] == ' ';
    }
}
