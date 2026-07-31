using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Remold.Core;

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
            // Two copies over one settings file and one mod library would overwrite each other's writes.
            if (!owned) { App.SecondInstance = true; }
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
    }

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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
