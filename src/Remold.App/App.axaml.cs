using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;

namespace Remold.App;

public partial class App : Application
{
    /// <summary>Another copy of the app already holds the single-instance mutex, so this one only shows the
    /// notice window and exits. Set by <see cref="Program"/> before the app builds.</summary>
    public static bool SecondInstance;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Ahead of the first-run gate: a second copy neither accepts terms nor loads a mod.
            if (SecondInstance)
                desktop.MainWindow = new AlreadyRunningWindow();
            else if (FirstRunGate.IsAccepted(LabAcceptableUse.Text))
                desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel() };
            else
                ShowFirstRunGate(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shown as the INITIAL window, so closing it without accepting exits the app: it is the only
    /// window open, and the default OnLastWindowClose shutdown fires. There is no skip path.</summary>
    private static void ShowFirstRunGate(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var gate = new FirstRunGateWindow(LabAcceptableUse.Title, LabAcceptableUse.Text);
        gate.Accepted += () =>
        {
            FirstRunGate.RecordAccepted(LabAcceptableUse.Text);
            var main = new MainWindow { DataContext = new MainWindowViewModel() };
            desktop.MainWindow = main;   // hand the main-window role over BEFORE the gate closes
            main.Show();
            gate.Close();
        };
        desktop.MainWindow = gate;
    }
}
