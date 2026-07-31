using System;
using System.Runtime.InteropServices;
using Remold.Core.Textures;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="Bc7Encoder"/>: the device ladder that picks an encoder, and the chain it hands back. The
/// ladder tests drive it with their own device factory so every rung is reachable without a machine that
/// happens to lack a GPU; the encode tests run the REAL ladder, so whatever this machine resolves to is
/// what they measure.
/// </summary>
public class Bc7EncoderTests
{
    /// <summary>A stand-in device handle. The ladder only ever compares it against zero.</summary>
    private static IntPtr Handle(int n) => new(n);

    private const int Hardware = 1, Warp = 5;

    [Fact]
    public void The_ladder_takes_the_hardware_device_and_never_asks_for_warp()
    {
        var asked = new System.Collections.Generic.List<int>();
        var (device, rung) = Bc7Encoder.Ladder(t => { asked.Add(t); return Handle(7); });

        Assert.Equal(Handle(7), device);
        Assert.Equal(Bc7Encoder.Rung.Hardware, rung);
        Assert.Equal(new[] { Hardware }, asked);
    }

    [Fact]
    public void A_hardware_device_that_fails_drops_to_warp()
    {
        var asked = new System.Collections.Generic.List<int>();
        var (device, rung) = Bc7Encoder.Ladder(t =>
        {
            asked.Add(t);
            return t == Hardware ? IntPtr.Zero : Handle(9);
        });

        Assert.Equal(Handle(9), device);
        Assert.Equal(Bc7Encoder.Rung.Warp, rung);
        Assert.Equal(new[] { Hardware, Warp }, asked);
    }

    [Fact]
    public void A_rung_that_throws_counts_as_a_rung_that_failed()
    {
        // A driver can fault rather than answer an HRESULT; the next rung still has to be tried.
        var (device, rung) = Bc7Encoder.Ladder(t =>
            t == Hardware ? throw new DllNotFoundException("d3d11.dll") : Handle(3));

        Assert.Equal(Handle(3), device);
        Assert.Equal(Bc7Encoder.Rung.Warp, rung);
    }

    [Fact]
    public void No_device_at_all_answers_none_rather_than_a_handle()
    {
        var (device, rung) = Bc7Encoder.Ladder(_ => IntPtr.Zero);

        Assert.Equal(IntPtr.Zero, device);
        Assert.Equal(Bc7Encoder.Rung.None, rung);
    }

    [Fact]
    public void Warp_creates_on_this_platform()
    {
        // The rung the ladder leans on: WARP ships with Windows, so a machine with no usable GPU still
        // encodes on a device. This runs the real interop, which is what pins the call's shape.
        var device = Bc7Encoder.CreateDevice(Warp);
        Assert.NotEqual(IntPtr.Zero, device);
        Marshal.Release(device);
    }

    [Fact]
    public void A_driver_type_the_platform_refuses_answers_no_device()
    {
        // Driver type 0 is "unknown", which is invalid without an adapter — a failing HRESULT must read as
        // no device rather than a handle nothing can compress on.
        Assert.Equal(IntPtr.Zero, Bc7Encoder.CreateDevice(0));
    }

    [Fact]
    public void The_real_ladder_resolves_a_device_on_a_supported_machine()
    {
        // WARP alone guarantees this on Windows. Without it every encode below still PASSES, on the managed
        // encoder and orders of magnitude slower — so the device path regressing to nothing would otherwise
        // be invisible to this suite.
        Assert.NotEqual(Bc7Encoder.Rung.None, Bc7Encoder.Resolved);
    }

    [Theory]
    [InlineData(64, 64, 7)]
    [InlineData(64, 32, 7)]
    [InlineData(30, 30, 5)]     // no power of two, so the chain hits odd levels
    [InlineData(1, 1, 1)]       // nothing to generate at all
    public void The_chain_is_every_level_the_dimensions_admit_at_the_formats_size(int w, int h, int levels)
    {
        var chain = Bc7Encoder.EncodeMipChain(Noise(w, h), w, h);

        Assert.Equal(levels, chain.Length);
        Assert.Equal(TextureCodec.MipChainLength(w, h), chain.Length);
        int lw = w, lh = h;
        for (int i = 0; i < chain.Length; i++)
        {
            Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, lw, lh, 1), chain[i].Length);
            lw = Math.Max(1, lw >> 1);
            lh = Math.Max(1, lh >> 1);
        }
    }

    [Fact]
    public void Every_level_decodes_back_to_the_colour_it_was_encoded_from()
    {
        // A flat source survives both the filtering and the compression at every level, so a chain whose
        // levels were sliced or ordered wrongly reads as the wrong colour rather than passing on size alone.
        const int w = 64, h = 64;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4 + 0] = 200; rgba[i * 4 + 1] = 100; rgba[i * 4 + 2] = 50; rgba[i * 4 + 3] = 255;
        }

        var chain = Bc7Encoder.EncodeMipChain(rgba, w, h);

        int lw = w, lh = h;
        for (int i = 0; i < chain.Length; i++)
        {
            var back = TextureCodec.DecodeToRgba(chain[i], lw, lh, ATTextureFormat.BC7);
            Assert.InRange(back[0], (byte)195, (byte)205);
            Assert.InRange(back[1], (byte)95, (byte)105);
            Assert.InRange(back[2], (byte)45, (byte)55);
            Assert.Equal(255, back[3]);
            lw = Math.Max(1, lw >> 1);
            lh = Math.Max(1, lh >> 1);
        }
    }

    [Fact]
    public void The_mip_chain_averages_rather_than_dropping_texels()
    {
        // Half the source black, half white: the smallest level has to read mid-grey. A point-sampled
        // chain would answer one of the two extremes, and a distant sample would flicker between them.
        const int w = 64, h = 64;
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = y < h / 2 ? (byte)0 : (byte)255;
                int i = (y * w + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = v;
                rgba[i + 3] = 255;
            }

        var chain = Bc7Encoder.EncodeMipChain(rgba, w, h);
        var last = TextureCodec.DecodeToRgba(chain[^1], 1, 1, ATTextureFormat.BC7);

        Assert.InRange(last[0], (byte)118, (byte)138);
    }

    [Fact]
    public void Releasing_the_device_leaves_the_next_encode_working()
    {
        // The app releases on exit; a release that left the resolution latched would strand every later
        // encode on a handle that no longer exists.
        var before = Bc7Encoder.EncodeMipChain(Noise(16, 16), 16, 16);
        Bc7Encoder.Release();
        var after = Bc7Encoder.EncodeMipChain(Noise(16, 16), 16, 16);

        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[0], after[0]);
    }

    [Fact]
    public void A_compress_that_fails_on_a_live_device_drops_the_resolution()
    {
        // A device can go under the app mid-build (a driver reset, an adapter removed). The resolution is
        // latched, so without this every later encode would fail on the same dead handle until a restart.
        Assert.NotEqual(Bc7Encoder.Rung.None, Bc7Encoder.Resolved);   // a device is up to lose
        int walks = Bc7Encoder.Resolutions;

        Assert.Throws<InvalidOperationException>(() => Bc7Encoder.TryOnDevice(new byte[4 * 4 * 4], 4, 4,
            (_, _, _, _) => throw new InvalidOperationException("device removed")));

        Assert.NotEqual(Bc7Encoder.Rung.None, Bc7Encoder.Resolved);   // the next encode still has a device…
        Assert.True(Bc7Encoder.Resolutions > walks, "the failure dropped the resolution and the ladder was walked again");
        Assert.Equal(TextureCodec.MipChainLength(16, 16), Bc7Encoder.EncodeMipChain(Noise(16, 16), 16, 16).Length);
    }

    [Fact]
    public void A_buffer_short_of_its_dimensions_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => Bc7Encoder.EncodeMipChain(new byte[16], 8, 8));
        Assert.Contains("need 256", ex.Message);
    }

    private static byte[] Noise(int w, int h)
    {
        var b = new byte[w * h * 4];
        new Random(5).NextBytes(b);
        return b;
    }
}
