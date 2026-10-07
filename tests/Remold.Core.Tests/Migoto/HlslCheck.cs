using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Emitted shaders are compiled by the runtime when the mod loads, and the loader prints every compile
/// warning over the game, so an emitted shader must compile with an empty log, not merely compile. The
/// check runs the same compiler the runtime ships with; d3dcompiler_47 ships with Windows.
/// </summary>
internal static class HlslCheck
{
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] src, nuint srcSize, string? name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr BlobPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint BlobSize(IntPtr self);

    private static string BlobText(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return "";
        IntPtr vtbl = Marshal.ReadIntPtr(blob);
        var ptr = Marshal.GetDelegateForFunctionPointer<BlobPointer>(Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size));
        var size = Marshal.GetDelegateForFunctionPointer<BlobSize>(Marshal.ReadIntPtr(vtbl, 4 * IntPtr.Size));
        string text = Marshal.PtrToStringAnsi(ptr(blob), (int)size(blob));
        Marshal.Release(blob);
        return text;
    }

    /// <summary>Compile <paramref name="hlsl"/>'s <c>main</c>: whether it compiled, and the compiler's log,
    /// warnings included.</summary>
    internal static (bool Ok, string Log) TryCompile(string hlsl, string name, string target = "cs_5_0")
    {
        if (!OperatingSystem.IsWindows()) return (true, "");
        var bytes = Encoding.ASCII.GetBytes(hlsl);
        int hr = D3DCompile(bytes, (nuint)bytes.Length, name, IntPtr.Zero, IntPtr.Zero, "main", target,
            flags1: 0, flags2: 0, out IntPtr code, out IntPtr errors);
        string log = BlobText(errors);
        if (code != IntPtr.Zero) Marshal.Release(code);
        return (hr == 0, $"0x{hr:x8}: {log}");
    }

    /// <summary>The shader compiles and the compiler has nothing to say about it.</summary>
    internal static void CompilesClean(string hlsl, string name, string target = "cs_5_0")
    {
        var (ok, log) = TryCompile(hlsl, name, target);
        Assert.True(ok, $"{name} failed to compile ({log})");
        Assert.True(log == "0x00000000: ", $"{name} compiled with warnings the loader would print over the game ({log})");
    }

    /// <summary>Every shader the ini names compiles clean, each at the stage the ini binds it to.</summary>
    internal static void EveryShaderCompilesClean(string ini, string outDir)
    {
        var shaders = ini.Split('\n').Select(l => l.Trim())
            .Where(l => (l.StartsWith("cs = ", StringComparison.Ordinal) || l.StartsWith("vs = ", StringComparison.Ordinal)
                         || l.StartsWith("ps = ", StringComparison.Ordinal))
                        && l.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        foreach (string line in shaders)
        {
            string file = line[5..];
            CompilesClean(File.ReadAllText(Path.Combine(outDir, file)), file, $"{line[..2]}_5_0");
        }
    }
}
