using System;

namespace Remold.Core.Bundles;

/// <summary>
/// GFL2 bundle obfuscation: a standard UnityFS file XOR'd over only its first <see cref="EncLen"/>
/// bytes with a 16-byte key, recovered from the file's own header by known-plaintext (the fixed
/// "UnityFS\0..." magic) — NOT derived from the filename. XOR is symmetric, so one routine both
/// deobfuscates and re-obfuscates. The game also loads plain UnityFS bundles.
/// </summary>
public static class BundleObfuscation
{
    public const int EncLen = 0x8000;

    // "UnityFS\0\0\0\0\x07" + "5.x." — 16-byte template for key recovery
    private static readonly byte[] Tpl =
    {
        0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0x00,
        0x00, 0x00, 0x00, 0x07, 0x35, 0x2E, 0x78, 0x2E,
    };

    // Extended template (through byte 29) to validate the recovered key: "...x\0" + "2019.4.29f1\0".
    private static readonly byte[] Tpl30 = Concat(Tpl, new byte[]
    {
        0x78, 0x00, 0x32, 0x30, 0x31, 0x39, 0x2E, 0x34,
        0x2E, 0x32, 0x39, 0x66, 0x31, 0x00,
    });

    /// <summary>Recover the 16-byte key from an obfuscated header, validating to byte 29.</summary>
    public static byte[] RecoverKey(ReadOnlySpan<byte> head)
    {
        if (head.Length < Tpl30.Length)
            throw new ArgumentException("file too small to be a bundle");
        var key = new byte[16];
        for (int i = 0; i < 16; i++)
            key[i] = (byte)(head[i] ^ Tpl[i]);
        for (int i = 16; i < Tpl30.Length; i++)
            if ((head[i] ^ key[i % 16]) != Tpl30[i])
                throw new FormatException("not an obfuscated GF2 UnityFS bundle (header mismatch)");
        return key;
    }

    /// <summary>Returns true if the data starts with a plaintext UnityFS magic (key would be a no-op).</summary>
    public static bool IsPlain(ReadOnlySpan<byte> head) =>
        head.Length >= 8 && head[0] == 0x55 && head[1] == 0x6E && head[2] == 0x69 &&
        head[3] == 0x74 && head[4] == 0x79 && head[5] == 0x46 && head[6] == 0x53 && head[7] == 0x00;

    /// <summary>XOR the first <see cref="EncLen"/> bytes in place with the key. Symmetric.</summary>
    public static void XorPrefix(byte[] data, byte[] key)
    {
        int n = Math.Min(EncLen, data.Length);
        for (int i = 0; i < n; i++)
            data[i] ^= key[i % 16];
    }

    /// <summary>Deobfuscate a bundle's bytes in place; returns the recovered key. No-op if already plain.</summary>
    public static byte[] Deobfuscate(byte[] data)
    {
        if (IsPlain(data)) return new byte[16];
        var key = RecoverKey(data.AsSpan(0, Math.Min(32, data.Length)));
        XorPrefix(data, key);
        return key;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Array.Copy(a, r, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
