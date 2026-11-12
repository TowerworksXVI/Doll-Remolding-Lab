using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Text;

namespace Remold.App;

public partial class App : Application
{
    /// <summary>Another copy of the app already holds the single-instance mutex, so this one only shows the
    /// notice window and exits. Set by <see cref="Program"/> before the app builds.</summary>
    public static bool SecondInstance;

    /// <summary>The modder chose to restart into a new language and the close flow went ahead. Read by
    /// <see cref="Program"/> once the app has shut down and released the single-instance mutex.</summary>
    public static bool RestartRequested;

    /// <summary>The mod the restart opens again, or null to start on Home. Set with
    /// <see cref="RestartRequested"/>.</summary>
    internal static ReopenTarget? RestartReopen;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The terms text the acceptance stamp hashes: the English entry, whatever the UI language, so
    /// one acceptance holds across languages and an edit to the English terms asks again.</summary>
    internal static string AcceptedTerms => Catalog.Neutral(Strings.Rm, nameof(Strings.AcceptableUseBody));

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ahead of the first-run gate: a second copy neither accepts terms nor loads a mod.
            if (SecondInstance)
                desktop.MainWindow = new AlreadyRunningWindow();
            else if (FirstRunGate.IsAccepted(AcceptedTerms))
                desktop.MainWindow = new MainWindow
                    { DataContext = MainWindowViewModel.Reopening(ReopenTarget.Parse(desktop.Args)) };
            else
                ShowFirstRunGate(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shown as the INITIAL window, so closing it without accepting exits the app: it is the only
    /// window open, and the default OnLastWindowClose shutdown fires. There is no skip path.</summary>
    private static void ShowFirstRunGate(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Nothing but this window exists yet, so a language picked on it applies to the whole process at
        // once, and the main window opens in it.
        var language = new FirstRunLanguage(LabSettings.Load().Language, CultureInfo.InstalledUICulture);
        var gate = new FirstRunGateWindow(language);
        gate.Accepted += () =>
        {
            FirstRunGate.RecordAccepted(AcceptedTerms);
            var main = new MainWindow { DataContext = MainWindowViewModel.AfterFirstRun(language.Selected) };
            desktop.MainWindow = main;   // hand the main-window role over BEFORE the gate closes
            main.Show();
            gate.Close();
        };
        desktop.MainWindow = gate;
    }
}
