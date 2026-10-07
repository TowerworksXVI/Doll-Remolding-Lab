using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;

namespace Remold.Core.Tests.Support;

/// <summary>
/// Runs one test body inside an isolated headless Avalonia application that draws for real, for the tests
/// whose subject is a picture — a decoded ramp, a rendered preview, an encoded thumbnail — rather than a
/// window.
///
/// <para>Isolated is the point. The suite's window-free view-model tests rely on the process having NO
/// Avalonia platform: with none registered, <c>Dispatcher.UIThread</c> is the null dispatcher, every thread
/// passes its access check, and the window's own <c>OnUi</c> runs inline — which is how those tests drive
/// marshalled work synchronously. A platform stood up in the global locator (the desktop platform via
/// <c>SetupWithoutStarting</c>, or a per-assembly headless session) binds that dispatcher to one thread
/// for the rest of the process, and every test that touches it afterwards posts to a loop nobody runs.
/// That was the suite's ordering-dependent failure: whether a class saw the null dispatcher or a foreign
/// one depended on which class ran first. A per-test session enters its own locator scope, builds the
/// application on its own thread, runs the body there, and tears the scope down again, so nothing it
/// registers outlives the body.</para>
/// </summary>
internal static class HeadlessPictures
{
    /// <summary>The session's entry point: a bare application over Skia, with the headless platform's own
    /// drawing turned off so bitmaps hold real pixels and encode to real PNGs.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static async Task RunAsync(Action body)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessPictures));
        await session.Dispatch(body, CancellationToken.None);
    }
}
