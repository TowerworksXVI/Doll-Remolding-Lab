using System;
using System.ComponentModel;
using System.IO;

namespace Remold.Core.Project;

/// <summary>A refusal the authored model raises whose sentence is written for the person reading it: it
/// names parts, edits and materials by what the modder calls them and never by an identity the model keeps
/// for itself. A surface shows it as it is.
///
/// <para>It derives from <see cref="InvalidOperationException"/> so nothing that already catches the
/// commands' refusals has to learn a second type. Everything the model throws that is NOT one of these is a
/// defect — a slot id that does not exist, a binding a caller invented — and its message names the model's
/// own identifiers, which mean nothing on a status line.</para>
///
/// <para>The build path raises it for the same reason: one route carries both the refusals a modder is owed
/// by name and the internal-consistency guards behind them, and the type is what tells a surface which of
/// the two it is holding.</para></summary>
public class AuthoredRefusalException : InvalidOperationException
{
    public AuthoredRefusalException(string message) : base(message) { }
}

/// <summary>The one place a surface turns a failed authored command into a sentence. Three answers, and
/// what decides between them is who wrote the message: the model's own refusal, the operating system's
/// account of a file it would not give up, or a defect that has no wording for anyone and gets the action's
/// own.</summary>
public static class AuthoredRefusal
{
    /// <summary>What one failed action says. <paramref name="action"/> completes "Couldn't …" — an
    /// imperative phrase in the modder's own words, such as <c>"delete this edit"</c>.</summary>
    public static string ForScreen(Exception failure, string action)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure is AuthoredRefusalException) return failure.Message;
        return Cause(failure) is { } cause ? $"Couldn't {action}: {cause}" : $"Couldn't {action}.";
    }

    /// <summary>The outside world's own account of what went wrong, ready to follow a colon, or null where
    /// the failure has no wording anyone outside the code can use.
    ///
    /// <para>The three families here are the ones whose messages the operating system writes for a person to
    /// read — a file another program is holding, a folder the account may not touch, a program that would
    /// not start. Every other failure is the app's own defect, and its message names identities the model
    /// keeps for itself.</para></summary>
    private static string? Cause(Exception failure)
    {
        if (failure is not (IOException or UnauthorizedAccessException or Win32Exception)) return null;
        string message = failure.Message.Trim();
        return message.Length == 0 ? null
            : message.EndsWith('.') ? message : message + ".";
    }
}
