namespace Remold.App.ViewModels;

/// <summary>ONE pure rule for the Launch action, the same shape as <see cref="InstallGate"/>: the button's
/// enablement and the reason it shows on hover are one answer, so a disabled Launch always says what to do
/// about it. Both halves have to be known before the button can act — the game to start, and the 3DMigoto
/// loader that has to be up first — so the reason names the missing one. Launch runs the loader itself, so
/// unlike Install it does not care whether a Mods folder sits beside it.</summary>
public static class LaunchGate
{
    public const string NoGame = "Game not located. Use Tools · Locate game…";
    public const string Ready = "Start 3DMigoto, then the game.";

    /// <summary>The blocking reason, or null when Launch can run. ORDERED by what has to be settled first:
    /// the game, then the loader. <paramref name="loaderExists"/> is the caller's disk read, so this stays
    /// pure. The loader sentences are <see cref="InstallGate"/>'s — one remedy, worded once.</summary>
    public static string? Reason(bool gameLocated, string? loaderExe, bool loaderExists)
    {
        if (!gameLocated) return NoGame;
        if (string.IsNullOrWhiteSpace(loaderExe)) return InstallGate.NoLoader;
        if (!loaderExists) return InstallGate.LoaderNotFound(loaderExe);
        return null;
    }
}
