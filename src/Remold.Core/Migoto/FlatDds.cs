using System.IO;

namespace Remold.Core.Migoto;

/// <summary>
/// Writes a tiny flat-colour R8G8B8A8 .dds with a DX10 header (no image library). srgb=true for colour
/// data (tags, overlap probe), srgb=false for data maps (neutral normal/RMO) the game samples linearly.
/// </summary>
public static class FlatDds
{
    /// <summary>Serialise a flat <paramref name="size"/>×<paramref name="size"/> RGBA image with the
    /// DX10 DDS header.</summary>
    public static byte[] Build((byte R, byte G, byte B, byte A) rgba, int size = 8, bool srgb = true)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = rgba.R; pixels[i + 1] = rgba.G; pixels[i + 2] = rgba.B; pixels[i + 3] = rgba.A;
        }
        using var ms = new MemoryStream();
        DdsWriter.Write(ms, srgb ? DdsWriter.R8G8B8A8_UNORM_SRGB : DdsWriter.R8G8B8A8_UNORM,
            size, size, new[] { pixels });
        return ms.ToArray();
    }

    /// <summary>Write the flat DDS directly to <paramref name="path"/>.</summary>
    public static void Write(string path, (byte R, byte G, byte B, byte A) rgba, int size = 8, bool srgb = true)
        => File.WriteAllBytes(path, Build(rgba, size, srgb));
}
