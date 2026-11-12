using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Remold.Core;
using Remold.Core.Text;

namespace Remold.App;

internal static class Program
{
    /// <summary>The single-instance handle. <c>Local\</c> scopes it to the logon session, so one user's app
    /// never blocks another's on a shared machine.</summary>
    private const string InstanceMutexName = @"Local\DollRemoldingLab-instance";

    [STAThread]
    public static void Main(string[] args)
    {
        // Capture fatal exceptions to crash.log — an async-void UI handler otherwise vanishes as a silent
        // process exit.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("UnobservedTask", e.Exception); e.SetObserved(); };
        using var instance = new Mutex(initiallyOwned: false, InstanceMutexName);
        bool owned;
        try { owned = instance.WaitOne(TimeSpan.Zero, exitContext: false); }
        // The holder died without releasing. The wait still succeeded and this process now owns it, so a
        // crashed session must not lock the app out of every later launch.
        catch (AbandonedMutexException) { owned = true; }
        try
        {
            // Before the application builds, so every window and thread reads it. The second instance reads
            // the same file, read only, so its one window is in the chosen language too.
            StartupLanguage.Apply();
            // Two copies over one settings file and one mod library would overwrite each other's writes.
            if (!owned) { App.SecondInstance = true; }
            else AppLog.BeginLaunch();   // the log reads as THIS run even when nothing gets reported
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main", ex);
            throw;
        }
        finally
        {
            Core.Textures.Bc7Encoder.Release();
            if (owned) try { instance.ReleaseMutex(); } catch (ApplicationException) { /* not held — nothing to give back */ }
        }
        // After the release, so the new copy takes the single-instance handle instead of finding it held.
        if (RelaunchStart(App.RestartRequested, Environment.ProcessPath, App.RestartReopen) is { } relaunch)
            try { Process.Start(relaunch)?.Dispose(); }
            catch (Exception ex) { AppLog.Write("Couldn't start the app again after a language change", ex); }
    }

    /// <summary>How to start the app again after it shut down for a restart, or null when no restart was
    /// asked for or the process cannot start itself again (<see cref="CanRelaunch"/>). A
    /// <paramref name="reopen"/> target goes along as launch arguments, so the new process opens that mod.
    /// The new process is not waited on: it outlives this one.</summary>
    internal static ProcessStartInfo? RelaunchStart(bool restartRequested, string? processPath,
        ReopenTarget? reopen = null)
    {
        if (!restartRequested || !CanRelaunch(processPath)) return null;
        var start = new ProcessStartInfo(processPath!) { UseShellExecute = false };
        if (reopen is not null)
            foreach (var argument in reopen.ToArguments()) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>Whether <paramref name="processPath"/> starts this app on its own: the app's executable. A
    /// missing path cannot, and neither can the shared <c>dotnet</c> host that runs <c>dotnet Remold.App.dll</c>,
    /// which started bare would not open the app.</summary>
    internal static bool CanRelaunch(string? processPath) =>
        !string.IsNullOrEmpty(processPath)
        && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    private static void LogCrash(string source, Exception? ex)
    {
        // Overwrite — keep only the latest crash.
        var text = $"===== CRASH ({source}) {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n{ex}\n";
        foreach (var path in CrashLogPaths())
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                Console.Error.WriteLine($"[crash logged to {path}]");
                return;
            }
            catch { /* try the next candidate */ }
        Console.Error.WriteLine(text);   // last resort: at least hit stderr
    }

    // Fallback order, because a crash is exactly the case where the preferred folder may be unwritable.
    // The two app roots come from LabPaths, which owns every user-profile derivation.
    private static string[] CrashLogPaths() => new[]
    {
        Path.Combine(LabPaths.DurableRoot, "crash.log"),
        Path.Combine(LabPaths.CacheRoot, "crash.log"),
        Path.Combine(Path.GetTempPath(), "dollremoldinglab_crash.log"),
    };

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
        bool termsGateOpens = !App.SecondInstance && !FirstRunGate.IsAccepted(App.AcceptedTerms);
        return ChineseFallback(Catalog.CurrentTag, termsGateOpens) is { } options ? builder.With(options) : builder;
    }

    /// <summary>The font fallback for this session, or null for none. Microsoft YaHei UI is named in two cases:
    /// the startup language is Simplified Chinese, or the first-run terms window will open
    /// (<paramref name="termsGateOpens"/>: no acceptance recorded yet), because a pick on that window can switch
    /// the session to Chinese after the fallback is fixed. CJK glyphs come from the system, and naming the
    /// Simplified face keeps English Windows from falling back to a Traditional one. Otherwise the system's own
    /// fallback stays, which draws the symbols Inter lacks (＋, ◌) as the English UI always has.</summary>
    internal static FontManagerOptions? ChineseFallback(string uiTag, bool termsGateOpens) =>
        uiTag == Catalog.SimplifiedChinese || termsGateOpens
            ? new FontManagerOptions { FontFallbacks = [new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") }] }
            : null;
}
