using System;
using System.IO;
using Remold.App;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The general app log's launch behavior: the first write of a run rotates the previous log to
/// app.log.prev and opens with a version-stamped header; later writes append.</summary>
public class AppLogTests
{
    [Fact]
    public void The_first_write_of_a_launch_rotates_the_previous_log_and_opens_with_a_header()
    {
        string root = Path.Combine(Path.GetTempPath(), "drl-applog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? sharedRedirect = AppLog.RootOverride;   // the module-wide test redirect, restored below
        try
        {
            File.WriteAllText(Path.Combine(root, "app.log"), "last run's content\n");
            AppLog.RootOverride = root;

            AppLog.Write("Couldn't save the mod", new IOException("disk full"));
            AppLog.Write("Settings couldn't be saved", "The write failed.");

            Assert.Equal("last run's content\n", File.ReadAllText(Path.Combine(root, "app.log.prev")));
            string log = File.ReadAllText(Path.Combine(root, "app.log"));
            Assert.StartsWith("===== Doll Remolding Lab ", log);
            Assert.Contains("Couldn't save the mod", log);
            Assert.Contains("IOException", log);      // the technical half the screen line leaves out
            Assert.Contains("disk full", log);
            Assert.Contains("Settings couldn't be saved", log);
            Assert.Contains("  The write failed.", log);
        }
        finally
        {
            AppLog.RootOverride = sharedRedirect;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_quiet_launch_still_rotates_and_leaves_only_this_runs_header()
    {
        string root = Path.Combine(Path.GetTempPath(), "drl-applog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? sharedRedirect = AppLog.RootOverride;
        try
        {
            File.WriteAllText(Path.Combine(root, "app.log"), "last run's content\n");
            AppLog.RootOverride = root;

            AppLog.BeginLaunch();

            Assert.Equal("last run's content\n", File.ReadAllText(Path.Combine(root, "app.log.prev")));
            string log = File.ReadAllText(Path.Combine(root, "app.log"));
            Assert.StartsWith("===== Doll Remolding Lab ", log);
            Assert.Single(log.Split('\n', System.StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            AppLog.RootOverride = sharedRedirect;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
