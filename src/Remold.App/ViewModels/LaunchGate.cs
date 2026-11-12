namespace Remold.App.ViewModels;

/// <summary>ONE pure rule for the Launch action: the button's
/// enablement and the reason it shows on hover are one answer, so a disabled Launch always says what to do
/// about it. Both halves have to be known before the button can act — the game to start, and the 3DMigoto
/// loader that has to be up first — so the reason names the missing one. Launch runs the loader itself, so
    /// it does not care whether a Mods folder sits beside it.</summary>
public static class LaunchGate
{
    /// <summary>The blocking reason, or null when Launch can run. ORDERED by what has to be settled first:
    /// the game, then the loader. <paramref name="loaderExists"/> is the caller's disk read, so this stays
    /// pure. The loader sentences are the catalog's Loader entries, shared with Settings and Install — one remedy, worded once.</summary>
    public static string? Reason(bool gameLocated, string? loaderExe, bool loaderExists)
    {
        if (!gameLocated) return Strings.LaunchNoGame;
        if (string.IsNullOrWhiteSpace(loaderExe)) return Strings.LoaderNotSet;
        if (!loaderExists) return Strings.LoaderNotFound(loaderExe);
        return null;
    }
}
