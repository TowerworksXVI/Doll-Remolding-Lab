namespace Remold.App.ViewModels;

/// <summary>Short, shared outcomes for the session-native Edit page's Blender actions.</summary>
public static class BlenderGate
{
    public const string Busy = "Wait for the current edit action to finish.";
    public const string ReadyAll = "Open every part from the original in Blender.";
    public const string NotFound = "Couldn't find Blender. Set its location in Settings.";
    public const string StaticPart =
        "This part isn't rigged, so it cannot be opened with the item's other parts.";
    public const string StaticOnly = "This item has no rigged parts to open in Blender.";
}
