namespace Remold.App.ViewModels;

/// <summary>Shared loader-path verdicts used by Settings, the status bar, and Launch.</summary>
public static class LoaderGate
{
    public const string NoLoader = "Set the 3DMigoto loader in Settings.";
    public const string NoTextureHook =
        "Texture mods will not show up in game with this 3DMigoto.";

    public static string LoaderNotFound(string loaderExe) => $"Couldn't find the 3DMigoto loader: {loaderExe}.";
    public static string NoModsFolder(string loaderExe) =>
        $"Couldn't find a Mods folder beside the 3DMigoto loader: {loaderExe}.";

    /// <summary>An exe with no 3DMigoto configuration beside it. Says what that MEANS rather than naming the
    /// file that is missing: the modder picking a loader has no reason to know the ini, and a filename here
    /// reads as something to go and create. Worded to match the Settings row's own verdict on this state.</summary>
    public static string NoLoaderIni(string loaderExe) =>
        $"This doesn't look like a 3DMigoto loader: {loaderExe}.";
}

/// <summary>ONE pure rule for the Launch action: the button's
/// enablement and the reason it shows on hover are one answer, so a disabled Launch always says what to do
/// about it. Both halves have to be known before the button can act — the game to start, and the 3DMigoto
/// loader that has to be up first — so the reason names the missing one. Launch runs the loader itself, so
    /// it does not care whether a Mods folder sits beside it.</summary>
public static class LaunchGate
{
    public const string NoGame = "Game not located. Use Tools · Locate game…";
    public const string Ready = "Start 3DMigoto, then the game.";

    /// <summary>The blocking reason, or null when Launch can run. ORDERED by what has to be settled first:
    /// the game, then the loader. <paramref name="loaderExists"/> is the caller's disk read, so this stays
    /// pure. The loader sentences are <see cref="LoaderGate"/>'s — one remedy, worded once.</summary>
    public static string? Reason(bool gameLocated, string? loaderExe, bool loaderExists)
    {
        if (!gameLocated) return NoGame;
        if (string.IsNullOrWhiteSpace(loaderExe)) return LoaderGate.NoLoader;
        if (!loaderExists) return LoaderGate.LoaderNotFound(loaderExe);
        return null;
    }
}
