using Remold.Core;
using Remold.Core.Text;

namespace Remold.App;

/// <summary>Selects the UI language from the settings file. <see cref="Program"/> calls it before the
/// application builds, so every window and every thread the app starts reads the chosen language.</summary>
internal static class StartupLanguage
{
    /// <summary>Reads the settings file (read only; a missing or unreadable file is English) and applies its
    /// language. Returns the language applied.</summary>
    internal static string Apply(string? settingsPath = null)
    {
        Catalog.Apply(LabSettings.Load(settingsPath).Language);
        return Catalog.CurrentTag;
    }
}
