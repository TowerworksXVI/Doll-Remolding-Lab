namespace Remold.Core.Project;

/// <summary>What a place on the board is called, in one home. A placement is named by the key group it
/// belongs to and the state inside it, and both halves prefer what the modder typed: the group's key, else
/// its name; the state's name, else its position. Always is a place with a name of its own.
///
/// <para>Every surface that names a place reads it here — the session's refusals, the plan's lines, the
/// library chips, the checklist rows and the status line — so a state a modder named cannot be called
/// "State 2" on one surface and by its name on another.</para></summary>
public static class PlacementNames
{
    /// <summary>The place an edit is in when no key can switch it off.</summary>
    public const string Always = "Always";

    /// <summary>What a key group with neither a key nor a name is called.</summary>
    public const string UnnamedGroup = "Unnamed key group";

    /// <summary>The key group, by its key where it has one and by its name otherwise.</summary>
    public static string Group(string? key, string? label) =>
        !string.IsNullOrWhiteSpace(key) ? key.Trim()
        : !string.IsNullOrWhiteSpace(label) ? label.Trim()
        : UnnamedGroup;

    /// <summary>The state, by its name where it has one and by its position otherwise.</summary>
    public static string State(string? label, int stateIndex) =>
        !string.IsNullOrWhiteSpace(label) ? label.Trim() : $"State {stateIndex + 1}";

    /// <summary>One place, both halves together.</summary>
    public static string Place(string? key, string? groupLabel, string? stateLabel, int stateIndex) =>
        $"{Group(key, groupLabel)} · {State(stateLabel, stateIndex)}";

    public static string Group(KeyGroup group) => Group(group.Key, group.Label);

    public static string Group(KeyGroupOutline group) => Group(group.Key, group.Label);

    public static string State(KeyGroup group, KeyGroupState state) =>
        State(state.Label, group.States.IndexOf(state));

    public static string Place(KeyGroup group, KeyGroupState state) =>
        Place(group.Key, group.Label, state.Label, group.States.IndexOf(state));

    public static string Place(KeyGroup group, KeyGroupState state, int stateIndex) =>
        Place(group.Key, group.Label, state.Label, stateIndex);

    public static string Place(KeyGroupOutline group, KeyGroupStateOutline state, int stateIndex) =>
        Place(group.Key, group.Label, state.Label, stateIndex);
}
