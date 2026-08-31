using System;
using System.Collections.Generic;

namespace Remold.Core.Migoto;

/// <summary>Log-only detail attached to a build refusal whose user-facing message deliberately stays short.</summary>
public static class BuildLogDiagnostics
{
    private const string DataKey = "Remold.BuildLogDiagnostic";

    internal static T Attach<T>(T exception, string diagnostic) where T : Exception
    {
        exception.Data[DataKey] = diagnostic;
        return exception;
    }

    /// <summary>The diagnostic attached to <paramref name="exception"/>, if any.</summary>
    public static IReadOnlyList<string> From(Exception exception) =>
        exception.Data[DataKey] is string diagnostic
            ? new[] { diagnostic }
            : Array.Empty<string>();
}
