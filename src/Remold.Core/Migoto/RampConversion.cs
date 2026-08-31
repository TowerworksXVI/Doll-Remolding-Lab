using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>
/// What every route that touches a toon ramp has to agree on.
///
/// <para><b>The extent.</b> A ramp is a lookup table the shader samples by lighting term, not a picture
/// sampled by UV, so its size IS part of the curve and one home states it.</para>
///
/// <para><b>The raw path.</b> A game ramp's VALUES are the shading curve, so they travel into the container
/// 3DMigoto loads byte for byte, level for level, under the tag they are already in — nothing resamples,
/// re-encodes or gamma-corrects. One writer, so a hand pick and a carried ramp cannot ship different bytes
/// for the same game texture.</para>
///
/// <para><b>The two questions asked of a donor row.</b> Whether its ramp slot is already settled, and which
/// part its maps were exported from — the join the build's content policy walks before the ramp-derivation
/// read, so that read cannot reach a subject it blocks.</para>
/// </summary>
public static class RampConversion
{
    /// <summary>The extent every toon ramp the game binds is authored at. A ramp is a lookup table the
    /// shader samples by lighting term, not a picture sampled by UV, so its extent IS part of the curve: a
    /// file of another size draws a different shading response rather than the same one at a lower
    /// resolution.</summary>
    public const int RampWidth = 256, RampHeight = 16;

    /// <summary>Whether a game texture is stored as the float format a toon ramp travels in. The one home
    /// for that question: a caller reading a ramp's VALUES — to carry it, to show it, or to offer it as a
    /// pick — must know the bytes mean what it is about to read them as, and the Unity format enum stays
    /// inside Core.</summary>
    public static bool IsFloatRamp(Bundles.BundleReader.TextureHashSource src) =>
        (AssetsTools.NET.Texture.TextureFormat)src.Format
        == AssetsTools.NET.Texture.TextureFormat.RGBAHalf;

    /// <summary>The game's own ramp, written into the container 3DMigoto loads: its bytes, level for level,
    /// under the tag they are already in. Nothing resamples, re-encodes or gamma-corrects — the values ARE
    /// the shading curve. A ramp in any other format is refused rather than mis-tagged; its EXTENT is not
    /// checked here, because writing bytes out is not what decides whether they are worth carrying — the
    /// caller recording a pick asks that question and declines the row instead.
    ///
    /// <para>Nothing is written unless the whole declared mip chain is there and nothing lies past it: a
    /// half-written ramp on disk is a file the build would ship and the runtime would draw with.</para></summary>
    public static bool WriteRaw(Bundles.BundleReader.TextureHashSource src, string dest)
    {
        if (!IsFloatRamp(src)) return false;
        var levels = new List<byte[]>();
        int at = 0, w = src.Width, h = src.Height;
        for (int i = 0; i < Math.Max(1, src.MipCount); i++)
        {
            long want = DdsWriter.LevelBytes(DdsWriter.R16G16B16A16_FLOAT, w, h);
            if (src.PictureData.Length - at < want) return false;
            levels.Add(src.PictureData[at..(at + (int)want)]);
            at += (int)want;
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        if (levels.Count == 0 || at != src.PictureData.Length) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var s = File.Create(dest);
        DdsWriter.Write(s, DdsWriter.R16G16B16A16_FLOAT, src.Width, src.Height, levels);
        return true;
    }

    /// <summary>Whether this row's ramp slot is SETTLED — a pick, or the recorded keep-the-game's. Nothing
    /// about a settled row is re-decided, and nothing reaches another subject on its behalf.</summary>
    internal static bool RampSettled(SubmeshTextures row) =>
        row.Ramp is not null || row.RampOrigin == SlotOrigin.VanillaOwn;

    /// <summary>Which part a donor submesh's maps were exported from, by whichever picture slot names a
    /// materialized game texture first. The three slots of one submesh are shaded by ONE material, so any of
    /// them answers the same part; the order only decides which is asked. The build asks it only while the
    /// ramp is unsettled, so the derivation read cannot reach a subject the content policy blocks.</summary>
    public static (TargetPart Part, DonorMapSlot Kind)? DonorSourceOf(AuthoredWorkspaceFacts workspace,
        SubmeshTextures row)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(row);
        if (workspace.PictureSourceOf(row.Albedo) is { } a) return (a, DonorMapSlot.BaseColor);
        if (workspace.PictureSourceOf(row.Normal) is { } n) return (n, DonorMapSlot.Normal);
        if (workspace.PictureSourceOf(row.Rmo) is { } r) return (r, DonorMapSlot.Rmo);
        return null;
    }
}
