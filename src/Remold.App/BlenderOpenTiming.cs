using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Remold.Core;

namespace Remold.App;

/// <summary>The Blender-open timing log (<see cref="LabPaths.BlenderOpenTimingLog"/>): one phase-timed
/// block per open that reached Blender, plus a line per deferred cache publication when it lands. The
/// file is truncated on the first write of each app launch, so it always reads as this run's opens.
/// Diagnostic-only — nothing reads it back, and every failure here is silent.
///
/// <para>TEMPORARY: this instrumentation — the class, the phase marks in the session open, the
/// timing sinks on the two restores and the LabPaths entry — comes out when the full-app
/// optimization pass closes.</para></summary>
internal static class BlenderOpenTiming
{
    private static readonly object Gate = new();
    private static bool _truncatedThisLaunch;

    internal static void WriteBlock(string header, IReadOnlyList<string> lines)
    {
        var block = new StringBuilder();
        block.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(header)
            .Append(Environment.NewLine);
        foreach (var line in lines) block.Append("  ").Append(line).Append(Environment.NewLine);
        Append(block.ToString());
    }

    internal static void WriteLine(string line) =>
        Append("[" + DateTime.Now.ToString("HH:mm:ss") + "]   " + line + Environment.NewLine);

    private static void Append(string text)
    {
        lock (Gate)
        {
            try
            {
                var path = LabPaths.BlenderOpenTimingLog;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!_truncatedThisLaunch)
                {
                    File.WriteAllText(path, "");
                    _truncatedThisLaunch = true;
                }
                File.AppendAllText(path, text);
            }
            catch { /* diagnostic-only — never disturb an open */ }
        }
    }
}
