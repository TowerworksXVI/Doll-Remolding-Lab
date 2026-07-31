using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remold.App.Views;

/// <summary>
/// All a second copy of the app shows. It is the only window open, so closing it ends the process through
/// the default OnLastWindowClose shutdown. Code-built (no XAML), so it inherits the app theme.
/// </summary>
public sealed class AlreadyRunningWindow : Window
{
    public AlreadyRunningWindow()
    {
        Title = "Doll Remolding Lab";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        var close = new Button { Content = "Close", IsDefault = true, Padding = new Thickness(20, 6) };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = "Doll Remolding Lab is already running.",
                    Foreground = Res("HudTextBrush") ?? Brushes.White,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { close },
                },
            },
        };
    }

    private static ISolidColorBrush? Res(string key) =>
        Application.Current?.TryFindResource(key, out var r) == true && r is ISolidColorBrush b ? b : null;
}
