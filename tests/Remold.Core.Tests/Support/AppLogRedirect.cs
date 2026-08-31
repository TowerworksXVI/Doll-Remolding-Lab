using System;
using System.IO;
using System.Runtime.CompilerServices;
using Remold.App;

namespace Remold.Core.Tests.Support;

/// <summary>Points <see cref="AppLog"/> at a per-run temp folder before any test executes. The
/// view-model tests exercise the exact seams that log — without this, every suite run writes its
/// refusals into the developer's real app.log and rotates the real previous launch away.</summary>
internal static class AppLogRedirect
{
    [ModuleInitializer]
    internal static void RedirectAppLog() => AppLog.RootOverride =
        Path.Combine(Path.GetTempPath(), "remold-tests", "applog-" + Guid.NewGuid().ToString("N"));
}
