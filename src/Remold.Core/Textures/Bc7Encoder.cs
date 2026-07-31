using System;
using System.Runtime.InteropServices;
using BCnEncoder.Encoder;
using DirectXTexNet;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Textures;

/// <summary>
/// The BC7 mip-chain encoder a shipped <c>.dds</c> is built with: DirectXTex compressing on a Direct3D 11
/// device, over a managed encoder that stands in where no device can be had.
///
/// <para>DEVICE LADDER, resolved once and reused: a hardware device, else WARP (present on every Windows
/// install), else nothing — and with nothing, <see cref="TextureCodec.EncodeMipChain"/> encodes instead.
/// The managed encoder is the same one that decodes every texture the app reads, so the fallback is the
/// shipped, proven path rather than a degraded guess; it is only far slower. A compress that FAILS on a
/// device drops the resolution, so a device lost under the app (a driver reset, a removed adapter) costs
/// the encode that met it and not every encode after it.</para>
///
/// <para>A device is created lazily, because a session that builds no mod should not pay for one, and it
/// outlives each encode: creation costs more than a small map's compress. Encodes on it are serialised,
/// because DirectXTex compresses through the device's immediate context and that is not free-threaded.
/// Serialising costs nothing here — the build encodes its textures one after another.</para>
///
/// <para>Mips are generated over the STORED bytes (no WIC, no sRGB conversion), matching what the managed
/// encoder does and what the container's tag then claims.</para>
/// </summary>
public static class Bc7Encoder
{
    /// <summary>Which rung of the ladder answered.</summary>
    internal enum Rung { Hardware, Warp, None }

    /// <summary>Guards both the one-time device resolution and every compress on it.</summary>
    private static readonly object Gate = new();
    private static bool _resolved;
    private static IntPtr _device;
    private static Rung _rung = Rung.None;
    private static int _resolutions;

    /// <summary>Which rung the last resolution landed on, so a test can tell an encode that ran on a device
    /// from one that quietly took the managed path — the two differ only in how long they take.</summary>
    internal static Rung Resolved { get { lock (Gate) { Device(); return _rung; } } }

    /// <summary>How many times the ladder has been walked. Monotonic, so a test can tell a resolution that
    /// was dropped and re-made from one that was never touched — reading the latch itself would race a
    /// concurrent encode re-making it.</summary>
    internal static int Resolutions { get { lock (Gate) return _resolutions; } }

    /// <summary>Encode bottom-up <paramref name="rgba"/> (8-bit RGBA, row-major) as BC7, returning the full
    /// mip chain down to 1×1 largest-first and NOT flattened — one array per level, as
    /// <see cref="TextureCodec.EncodeMipChain"/> answers, whichever rung produced it.
    ///
    /// <para><paramref name="fallbackTaskCount"/> caps the managed encoder's workers and nothing else; the
    /// device path has no such knob. Its quality is fixed at <see cref="CompressionQuality.Balanced"/> — the
    /// measured setting, and the only one anything asks for.</para></summary>
    public static byte[][] EncodeMipChain(byte[] rgba, int width, int height, int? fallbackTaskCount = null)
    {
        if (rgba is null) throw new ArgumentNullException(nameof(rgba));
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"invalid texture dimensions {width}x{height}");
        long need = (long)width * height * 4;
        if (rgba.Length < need)
            throw new ArgumentException($"rgba buffer is {rgba.Length} bytes, need {need} for {width}x{height} RGBA");

        // Only the ABSENCE of a device falls through to the managed encoder. A compress that fails on a
        // device which did come up throws: it is a defect in this path, and swallowing it would bury
        // that under an encode that merely takes minutes.
        if (TryOnDevice(rgba, width, height, OnDevice) is { } chain) return chain;
        return TextureCodec.EncodeMipChain(rgba, width, height, ATTextureFormat.BC7,
            CompressionQuality.Balanced, fallbackTaskCount);
    }

    /// <summary>Compress on the resolved device, or null when no rung came up. <paramref name="compress"/>
    /// is the work, taken as a parameter so the failure policy below can be driven without a device that
    /// actually dies.
    ///
    /// <para>A compress that throws invalidates the resolution before the throw propagates: the device is
    /// gone (a driver reset, a removed adapter), and a latched dead handle would fail every later encode the
    /// same way until the app restarts. This encode still fails, and says so; the next one walks the ladder
    /// again and lands on whatever the machine can still offer.</para></summary>
    internal static byte[][]? TryOnDevice(byte[] rgba, int width, int height,
        Func<byte[], int, int, IntPtr, byte[][]> compress)
    {
        lock (Gate)
        {
            var device = Device();
            if (device == IntPtr.Zero) return null;
            try { return compress(rgba, width, height, device); }
            catch { Invalidate(); throw; }
        }
    }

    /// <summary>Drop the device. The next encode resolves the ladder again.</summary>
    public static void Release()
    {
        lock (Gate) Invalidate();
    }

    /// <summary>Release the device and unlatch the resolution. Caller holds <see cref="Gate"/>.</summary>
    private static void Invalidate()
    {
        if (_device != IntPtr.Zero) Marshal.Release(_device);
        _device = IntPtr.Zero;
        _rung = Rung.None;
        _resolved = false;
    }

    /// <summary>The resolved device, or <see cref="IntPtr.Zero"/> when neither rung came up. Caller holds
    /// <see cref="Gate"/>. Resolution is attempted ONCE: a machine that cannot make a device would
    /// otherwise pay two failed creations per texture.</summary>
    private static IntPtr Device()
    {
        if (_resolved) return _device;
        _resolved = true;
        _resolutions++;
        try
        {
            // DirectXTex's implementation assembly is mixed-mode and can fail to load outright (a
            // single-file publish is exactly that case); a device would be no use without it.
            _ = TexHelper.Instance;
            (_device, _rung) = Ladder(CreateDevice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            (_device, _rung) = (IntPtr.Zero, Rung.None);
        }
        return _device;
    }

    /// <summary>Walk the ladder with <paramref name="create"/>, answering the first device it makes and
    /// which rung that was. A rung that throws counts as a rung that failed.</summary>
    internal static (IntPtr Device, Rung Rung) Ladder(Func<int, IntPtr> create)
    {
        foreach (var (driverType, rung) in new[] { (DriverTypeHardware, Rung.Hardware), (DriverTypeWarp, Rung.Warp) })
        {
            IntPtr device;
            try { device = create(driverType); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (device != IntPtr.Zero) return (device, rung);
        }
        return (IntPtr.Zero, Rung.None);
    }

    // ── the device ────────────────────────────────────────────────────────────

    private const int DriverTypeHardware = 1, DriverTypeWarp = 5;
    /// <summary>Feature level 11_0, asked for explicitly: BC7 compression runs as a compute shader that a
    /// lower level cannot host, so a device below it would create and then fail every encode.</summary>
    private const int FeatureLevel110 = 0xb000;
    private const uint SdkVersion = 7;

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        int[] featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);

    /// <summary>Create a device of <paramref name="driverType"/> against the default adapter, or
    /// <see cref="IntPtr.Zero"/> when the driver refuses. The immediate context is released straight away:
    /// DirectXTex takes the device and asks it for its own.</summary>
    internal static IntPtr CreateDevice(int driverType)
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero, 0,
            new[] { FeatureLevel110 }, 1, SdkVersion, out var device, out _, out var context);
        if (context != IntPtr.Zero) Marshal.Release(context);
        if (hr >= 0 && device != IntPtr.Zero) return device;
        if (device != IntPtr.Zero) Marshal.Release(device);
        return IntPtr.Zero;
    }

    // ── the device encode ─────────────────────────────────────────────────────

    /// <summary>DirectXTex's own default alpha weighting.</summary>
    private const float AlphaWeight = 1f;

    private static byte[][] OnDevice(byte[] rgba, int width, int height, IntPtr device)
    {
        int levels = TextureCodec.MipChainLength(width, height);
        using var source = TexHelper.Instance.Initialize2D(
            DXGI_FORMAT.R8G8B8A8_UNORM, width, height, 1, 1, CP_FLAGS.NONE);
        var surface = source.GetImage(0);
        // row by row against the surface's own pitch, which need not be width*4
        for (int y = 0; y < height; y++)
            Marshal.Copy(rgba, y * width * 4, surface.Pixels + (nint)(y * surface.RowPitch), width * 4);

        // A 1×1 source has no chain to generate and DirectXTex refuses to be asked for one.
        using var chain = levels > 1 ? source.GenerateMipMaps(MipFilter(width, height), 0) : null;
        // UNORM whatever family the container will claim: the sRGB tag is written into the header, and
        // compressing in stored space lets one set of bytes serve either tag.
        using var compressed = (chain ?? source).Compress(
            device, DXGI_FORMAT.BC7_UNORM, TEX_COMPRESS_FLAGS.DEFAULT, AlphaWeight);

        int count = Math.Min(levels, compressed.GetImageCount());
        var outp = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var level = compressed.GetImage(i, 0, 0);
            var bytes = new byte[level.SlicePitch];
            Marshal.Copy(level.Pixels, bytes, 0, bytes.Length);
            outp[i] = bytes;
        }
        return outp;
    }

    /// <summary>The mip filter for a source of these dimensions. BOX is the plain 2×2 average a halving
    /// chain wants, but DirectXTex can only run it when every level of the chain has even dimensions, which
    /// only a power-of-two source guarantees; anything else takes TRIANGLE, which handles the uneven
    /// ratios an odd level forces. FORCE_NON_WIC keeps both off the imaging stack, so filtering stays over
    /// the stored bytes rather than a colour-managed reading of them.</summary>
    private static TEX_FILTER_FLAGS MipFilter(int width, int height) =>
        (IsPowerOfTwo(width) && IsPowerOfTwo(height) ? TEX_FILTER_FLAGS.BOX : TEX_FILTER_FLAGS.TRIANGLE)
        | TEX_FILTER_FLAGS.FORCE_NON_WIC;

    private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;
}
