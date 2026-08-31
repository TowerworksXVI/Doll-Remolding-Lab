using System;

namespace Remold.App.ViewModels.BuildPage;

/// <summary>What a board drag can be about.</summary>
public enum BuildDragKind
{
    /// <summary>A library row. Dropping it USES the edit somewhere; the row stays where it is.</summary>
    Edit,

    /// <summary>A state header, dragged to reorder its own key group's states.</summary>
    State,

    /// <summary>An edit already placed on the board. Dropping it MOVES the use it was dragged from.</summary>
    Token,
}

/// <summary>What a board drag carries, and how it reads back. The drag is plain text because that is what
/// the platform hands across a drop, so this is the one place that writes the text and the one place that
/// parses it — a reader and a writer that disagree is a drag that quietly does nothing.
///
/// <para>A null <see cref="GroupId"/> and <see cref="StateId"/> name Always, which is the same way every
/// placement verb underneath reads them.</para></summary>
public sealed record BuildDragPayload(BuildDragKind Kind, string EditDefinitionId, string? GroupId,
    string? StateId)
{
    private const string EditPrefix = "drl-build-edit:";
    private const string StatePrefix = "drl-build-state:";
    private const string TokenPrefix = "drl-build-token:";
    private const char Separator = '\u001f';

    public static string Edit(string editDefinitionId) => EditPrefix + editDefinitionId;

    public static string State(string groupId, string stateId) =>
        StatePrefix + groupId + Separator + stateId;

    public static string Token(string editDefinitionId, string? groupId, string? stateId) =>
        TokenPrefix + editDefinitionId + Separator + (groupId ?? "") + Separator + (stateId ?? "");

    /// <summary>Read a drag's text, or null for anything this board did not write.</summary>
    public static BuildDragPayload? Read(string? text)
    {
        if (text is null) return null;
        if (text.StartsWith(EditPrefix, StringComparison.Ordinal))
        {
            string id = text[EditPrefix.Length..];
            return id.Length == 0 ? null : new BuildDragPayload(BuildDragKind.Edit, id, null, null);
        }
        if (text.StartsWith(StatePrefix, StringComparison.Ordinal))
        {
            string[] address = text[StatePrefix.Length..].Split(Separator);
            return address.Length != 2 || address[0].Length == 0 || address[1].Length == 0 ? null
                : new BuildDragPayload(BuildDragKind.State, "", address[0], address[1]);
        }
        if (text.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            string[] address = text[TokenPrefix.Length..].Split(Separator);
            if (address.Length != 3 || address[0].Length == 0) return null;
            // Always is one place, so half an address is no address: a group without its state, or a state
            // without its group, is text this board could not have written.
            if (address[1].Length == 0 != (address[2].Length == 0)) return null;
            return new BuildDragPayload(BuildDragKind.Token, address[0],
                address[1].Length == 0 ? null : address[1],
                address[2].Length == 0 ? null : address[2]);
        }
        return null;
    }
}
