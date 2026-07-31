using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Mesh;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Project;

/// <summary>One of the three map slots a donor submesh ships, and the tag its file name carries. Naming a
/// slot rather than passing a tag string keeps the RMO's alpha rule attached to the slot it belongs
/// to.</summary>
public enum DonorMapSlot { BaseColor, Normal, Rmo }

/// <summary>
/// Turns the maps resolved out of a returned glb into a target's donor-texture list, writing authored
/// maps into the mod's <c>textures/</c> folder. A SIBLING part's vanilla map (deliberately linked in the
/// shader editor) is recorded as a reference to its existing workspace PNG, copying nothing. Every slot
/// carries its <see cref="SlotOrigin"/>, so the build reads the modder's intent instead of inferring one:
/// a stock image that came back untouched is <see cref="SlotOrigin.VanillaOwn"/> (keep the real map) and a
/// slot that never held an image is <see cref="SlotOrigin.None"/>, and those two are not the same ask.
/// An untouched return means "keep what Blender showed" — and when the workspace PNG behind the part's own
/// map carries a texture edit, what Blender showed is the edited PNG, not the game's map. That slot records
/// the workspace PNG as an authored reference, exactly like a sibling link, so the edit ships with the
/// replacement instead of silently reverting to the game's bytes.
/// Names derive from part and slot, so re-sending an edit overwrites instead of orphaning.
///
/// <para>All three slots are collected. An authored RMO takes its alpha — the emissive mask, which glTF has
/// no channel for — from the submesh's own stock RMO, and zero where the part has no stock RMO; only its
/// colour channels come back from Blender. It ships at the larger of the two sizes, so neither the mask nor
/// the authored colour is downsampled to fit the other.</para>
/// </summary>
public static class DonorTextureIntake
{
    /// <summary>Write the authored maps and build the list; null when the send-back asked for nothing (what
    /// <see cref="ProjectTarget.DonorTextures"/> wants for "none"). A submesh gets a row only when it asked
    /// for something on some slot — an authored map or a neutral — since a submesh that left every slot as
    /// it found it builds exactly as an unrecorded one does. <paramref name="relativize"/> maps an
    /// absolute path to the manifest's project-relative form. <paramref name="ownStockPngs"/> is the
    /// part's OWN stock workspace PNGs, which separates "bound to its own map" from a deliberate
    /// sibling link; null means ownership is unknowable and every vanilla match reads as the part's own.
    /// <paramref name="stockRmoPng"/> answers a submesh index with the stock RMO workspace PNG behind it,
    /// the alpha source for an authored one; null, or a null answer, ships a zero alpha. It is asked ONLY
    /// about a submesh whose RMO came back authored — the only slot with a mask to rebuild — so a caller may
    /// treat the question itself as the report-worthy event.
    /// <paramref name="report"/> receives what the intake had to give up on — a stock map that wouldn't
    /// read — so a degraded slot is said rather than merely survived.
    /// <paramref name="isEditedStock"/> answers whether a workspace stock PNG carries a texture edit;
    /// null treats every stock map as pristine.</summary>
    public static List<SubmeshTextures>? Collect(IReadOnlyList<IncomingMaps> maps, string texturesDir,
        string partStem, Func<string, string> relativize, IReadOnlySet<string>? ownStockPngs = null,
        Func<int, string?>? stockRmoPng = null, Action<string>? report = null,
        Func<string, bool>? isEditedStock = null)
    {
        var rows = new List<SubmeshTextures>();
        for (int i = 0; i < maps.Count; i++)
        {
            var albedo = Take(maps[i].BaseColor, texturesDir, partStem, i, Tag(DonorMapSlot.BaseColor),
                ownStockPngs, isEditedStock);
            var normal = Take(maps[i].Normal, texturesDir, partStem, i, Tag(DonorMapSlot.Normal),
                ownStockPngs, isEditedStock);
            var rmo = TakeRmo(maps[i].Rmo, texturesDir, partStem, i, ownStockPngs, stockRmoPng, report,
                isEditedStock);
            if (!albedo.Origin.IsAsk() && !normal.Origin.IsAsk() && !rmo.Origin.IsAsk()) continue;
            rows.Add(new SubmeshTextures
            {
                Submesh = i,
                Albedo = albedo.File is null ? null : relativize(albedo.File),
                Normal = normal.File is null ? null : relativize(normal.File),
                Rmo = rmo.File is null ? null : relativize(rmo.File),
                AlbedoOrigin = albedo.Origin,
                NormalOrigin = normal.Origin,
                RmoOrigin = rmo.Origin,
            });
        }
        return rows.Count > 0 ? rows : null;
    }

    /// <summary>The file-name tag one slot's maps carry. The three names the build's own donor encode and
    /// every re-send key off, so a re-authored map overwrites rather than orphaning.</summary>
    private static string Tag(DonorMapSlot slot) =>
        slot switch { DonorMapSlot.BaseColor => "base", DonorMapSlot.Normal => "nrm", _ => "rmo" };

    /// <summary>Where one submesh's map for one slot lives: THE naming convention, so every writer of a
    /// donor map lands on the same file.</summary>
    private static string MapPath(string texturesDir, string partStem, int submesh, string slot) =>
        Path.Combine(texturesDir, $"{partStem}_s{submesh}_{slot}.png");

    /// <summary>Author ONE slot of one donor submesh from an image on disk — the map-card drop's route into
    /// the same record a Blender session's map would have produced: the same file name, and for the RMO the
    /// same alpha rebuild (see <see cref="TakeRmo"/>). Returns the path written.</summary>
    /// <param name="stockRmoPng">the submesh's stock RMO workspace PNG, the emissive mask's source. Read only
    /// for <see cref="DonorMapSlot.Rmo"/>; null there ships a zero alpha.</param>
    /// <param name="report">receives what had to be given up on — a stock map that wouldn't read.</param>
    public static string TakeOne(string sourcePng, string texturesDir, string partStem, int submesh,
        DonorMapSlot slot, string? stockRmoPng = null, Action<string>? report = null)
    {
        Directory.CreateDirectory(texturesDir);
        var path = MapPath(texturesDir, partStem, submesh, Tag(slot));
        if (slot == DonorMapSlot.Rmo)
            File.WriteAllBytes(path, WithStockAlpha(File.ReadAllBytes(sourcePng), stockRmoPng, report));
        else
            File.Copy(sourcePng, path, overwrite: true);
        return path;
    }

    private static (string? File, SlotOrigin Origin) Take(ResolvedMap map, string texturesDir, string partStem,
        int submesh, string slot, IReadOnlySet<string>? ownStockPngs, Func<string, bool>? isEditedStock)
    {
        if (map.Origin == MapOrigin.Authored && map.AuthoredPng is not null)
        {
            Directory.CreateDirectory(texturesDir);
            var path = MapPath(texturesDir, partStem, submesh, slot);
            File.WriteAllBytes(path, map.AuthoredPng);
            return (path, SlotOrigin.Authored);
        }
        if (map.Origin == MapOrigin.Neutral) return (null, SlotOrigin.ExplicitNeutral);
        if (map.Origin == MapOrigin.Vanilla)
        {
            if (map.StockPng is { } stock)
            {
                // a sibling part's stock map, linked deliberately: it ships, referencing its workspace
                // PNG as-is
                if (ownStockPngs is not null && !ownStockPngs.Contains(Path.GetFullPath(stock)))
                    return (stock, SlotOrigin.Authored);
                // the part's own map with a texture edit behind it: the untouched return means "keep what
                // Blender showed", and Blender showed the edited PNG — it ships as the replacement's own map
                if (isEditedStock?.Invoke(stock) == true)
                    return (stock, SlotOrigin.Authored);
            }
            return (null, SlotOrigin.VanillaOwn);
        }
        return (null, SlotOrigin.None);
    }

    /// <summary>The RMO slot. Only the authored case differs from the others: the shipped map's alpha is
    /// rebuilt from the stock map <paramref name="stockRmoPng"/> names rather than taken from Blender, and it
    /// is the only case that asks. A sibling link needs no rebuild — it ships a stock map, alpha
    /// included.</summary>
    private static (string? File, SlotOrigin Origin) TakeRmo(ResolvedMap map, string texturesDir,
        string partStem, int submesh, IReadOnlySet<string>? ownStockPngs, Func<int, string?>? stockRmoPng,
        Action<string>? report, Func<string, bool>? isEditedStock)
    {
        if (map.Origin != MapOrigin.Authored || map.AuthoredPng is null)
            return Take(map, texturesDir, partStem, submesh, Tag(DonorMapSlot.Rmo), ownStockPngs, isEditedStock);
        Directory.CreateDirectory(texturesDir);
        var path = MapPath(texturesDir, partStem, submesh, Tag(DonorMapSlot.Rmo));
        File.WriteAllBytes(path, WithStockAlpha(map.AuthoredPng, stockRmoPng?.Invoke(submesh), report));
        return (path, SlotOrigin.Authored);
    }

    /// <summary>The authored RGB over the stock map's alpha. Alpha does not survive a Blender round trip, so
    /// the emissive mask is read off the stock map instead. The result is the LARGER of the two per axis, and
    /// whichever source is smaller is sampled nearest-neighbour: neither the authored colour nor the mask is
    /// ever downsampled, so a small map plugged over a full-size stock keeps the mask pixel for pixel. With no
    /// stock RMO to read, the authored size stands and the alpha is zero.</summary>
    private static byte[] WithStockAlpha(byte[] authoredPng, string? stockRmo, Action<string>? report)
    {
        using var img = Image.Load<Rgba32>(authoredPng);
        using var stock = LoadStockRmo(stockRmo, report);
        int w = Math.Max(img.Width, stock?.Width ?? 0);
        int h = Math.Max(img.Height, stock?.Height ?? 0);
        using var shipped = new Image<Rgba32>(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var p = img[Nearest(x, w, img.Width), Nearest(y, h, img.Height)];
                byte a = stock is null ? (byte)0 : stock[Nearest(x, w, stock.Width),
                                                         Nearest(y, h, stock.Height)].A;
                shipped[x, y] = new Rgba32(p.R, p.G, p.B, a);
            }
        using var ms = new MemoryStream();
        shipped.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>The stock map to read the mask from, or null when there is none. This is the one file the
    /// intake reads, and it may be missing or unreadable while everything else is fine — deleted, held open
    /// by the image editor the app itself launches, or damaged. Letting that throw would cost the part every
    /// authored map it arrived with and strand the ones already written, so the slot loses its mask alone and
    /// the caller is told which file it was. A named file that isn't there and one that won't decode are the
    /// same loss, so both report. Being handed NO file is not: the caller already knows whether that slot had
    /// a mask to lose. The AUTHORED image is not covered either — a map that won't decode has nothing left
    /// to ship.</summary>
    private static Image<Rgba32>? LoadStockRmo(string? stockRmo, Action<string>? report)
    {
        if (stockRmo is null) return null;
        if (!File.Exists(stockRmo))
        {
            report?.Invoke($"Couldn't find {Path.GetFileName(stockRmo)} for its emissive mask. "
                         + "The RMO ships with none.");
            return null;
        }
        try { return Image.Load<Rgba32>(stockRmo); }
        catch (Exception e)
        {
            report?.Invoke($"Couldn't read {Path.GetFileName(stockRmo)} for its emissive mask. "
                         + $"The RMO ships with none. ({e.Message})");
            return null;
        }
    }

    /// <summary>The source index a destination index samples, clamped inside the source.</summary>
    private static int Nearest(int i, int destSize, int sourceSize) =>
        destSize == sourceSize ? i : Math.Clamp((int)((long)i * sourceSize / destSize), 0, sourceSize - 1);
}
