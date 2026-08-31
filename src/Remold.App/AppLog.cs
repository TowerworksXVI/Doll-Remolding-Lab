using System;
using System.IO;
using System.Text;
using Remold.Core;

namespace Remold.App;

/// <summary>The general app log (<see cref="LabPaths.AppLogFile"/>): the technical detail behind what
/// the screens say in plain words — the full exception where a status line shows a steer, a failed
/// build's complete reason, the project-conversion report. One file per launch: the first write of a
/// run moves the previous log to <see cref="LabPaths.AppLogPrevFile"/> and opens with a version-stamped
/// header, so the pair always reads as this run and the one before. Diagnostic-only — nothing reads it
/// back, and every failure here is silent.</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    private static bool _rotatedThisLaunch;

    /// <summary>Test seam: paths derive from this root instead of <see cref="LabPaths"/> when set.
    /// Setting it re-arms the launch rotation so a test observes the first-write behavior.</summary>
    internal static string? RootOverride
    {
        get => _rootOverride;
        set { lock (Gate) { _rootOverride = value; _rotatedThisLaunch = false; } }
    }
    private static string? _rootOverride;

    private static string LogFile => _rootOverride is { } root ? Path.Combine(root, "app.log") : LabPaths.AppLogFile;
    private static string PrevFile => _rootOverride is { } root ? Path.Combine(root, "app.log.prev") : LabPaths.AppLogPrevFile;

    /// <summary>One fact with its technical detail. <paramref name="context"/> says what the app was
    /// doing in a few words; <paramref name="detail"/> is the full reason, multi-line welcome.</summary>
    internal static void Write(string context, string detail)
    {
        var block = new StringBuilder();
        block.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(context)
            .Append(Environment.NewLine);
        foreach (var line in detail.Replace("\r\n", "\n").Split('\n'))
            block.Append("  ").Append(line).Append(Environment.NewLine);
        Append(block.ToString());
    }

    /// <summary>The exception form: everything <see cref="Exception.ToString"/> knows, type and stack
    /// included — the half a plain-worded screen line deliberately leaves out.</summary>
    internal static void Write(string context, Exception e) => Write(context, e.ToString());

    /// <summary>Rotate and open this launch's log up front. Without it a session with nothing to report
    /// leaves the previous file standing, and stale content reads as this run's. The app calls it once at
    /// start; a second instance never does, so it cannot rotate the primary's log away.</summary>
    internal static void BeginLaunch() => Append(string.Empty);

    private static void Append(string text)
    {
        lock (Gate)
        {
            try
            {
                string path = LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!_rotatedThisLaunch)
                {
                    try { if (File.Exists(path)) File.Move(path, PrevFile, overwrite: true); }
                    catch { /* an unmovable old log must not block this run's */ }
                    string version = typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";
                    File.WriteAllText(path,
                        $"===== Doll Remolding Lab {version} · launched {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
                    _rotatedThisLaunch = true;
                }
                File.AppendAllText(path, text);
            }
            catch { /* diagnostic-only — never disturb the work being logged */ }
        }
    }
}
