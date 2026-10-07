using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Remold.App.ViewModels;
using Remold.Core.Project;

namespace Remold.Core.Tests.Support;

/// <summary>Gives every window a test builds its own round-trip folder under a per-run temp folder. The
/// production default is one folder beside the app, which every window in a test run would share: each
/// would then hear the others' saves as news about another mod, and a run would leave round trips in the
/// build output.</summary>
internal static class RoundTripRedirect
{
    private static readonly string Base =
        Path.Combine(Path.GetTempPath(), "remold-tests", "round-trips-" + Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Redirect()
    {
        MainWindowViewModel.NewRoundTripStore =
            () => new RoundTripStore(Path.Combine(Base, Guid.NewGuid().ToString("N")));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Base, recursive: true); } catch { }
        };
    }

    /// <summary>The round-trip folder a test window bound for the mod in <paramref name="modRoot"/>.</summary>
    internal static string FolderOf(string modRoot)
    {
        string mod = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modRoot));
        var found = Directory.Exists(Base)
            ? Directory.EnumerateDirectories(Base).SelectMany(store =>
                Directory.EnumerateDirectories(store).Where(folder => string.Equals(
                    new RoundTripStore(store).OwnerOf(folder), mod, StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : new();
        return found.Count == 1 ? found[0]
            : throw new InvalidOperationException($"{found.Count} round-trip folders belong to {modRoot}");
    }
}
