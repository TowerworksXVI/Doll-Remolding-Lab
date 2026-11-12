using System;
using System.Collections.Generic;
using Remold.App.ViewModels;

namespace Remold.App;

/// <summary>The mod a language-change restart opens again, and the step it was on. It reaches the new process
/// as launch arguments; nothing else the app does reads them.</summary>
internal sealed record ReopenTarget(string Folder, WorkflowStep Step)
{
    internal const string Flag = "--reopen";

    internal IEnumerable<string> ToArguments() => [Flag, Folder, Step.ToString()];

    /// <summary>The target in <paramref name="args"/>, or null for any other launch. Only the exact shape
    /// <see cref="ToArguments"/> writes is read, so a launch with anything else opens on Home as before.</summary>
    internal static ReopenTarget? Parse(IReadOnlyList<string>? args) =>
        args is [Flag, var folder, var step]
        && folder.Length > 0
        && Enum.TryParse<WorkflowStep>(step, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
        && parsed.ToString() == step
            ? new ReopenTarget(folder, parsed)
            : null;
}
