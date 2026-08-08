using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remold.App.Views;

/// <summary>
/// The unskippable first-run acceptable-use gate. There is NO skip affordance: closing the window exits
/// the app, since it is the only one open. Code-built (no XAML), so it inherits the app theme.
/// </summary>
public sealed class FirstRunGateWindow : Window
{
    /// <summary>Raised once on accept; the caller persists it and opens the main window.</summary>
    public event Action? Accepted;

    public FirstRunGateWindow(string title, string body)
    {
        Title = "Doll Remolding Lab";   // title bar = the app name; the headline renders in-window
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        var button = new Button
        {
            Content = "Accept",
            IsDefault = true,
            Padding = new Thickness(20, 6),
        };
        button.Click += (_, _) => Accepted?.Invoke();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = title, FontSize = 18, FontWeight = FontWeight.Bold,
                    Foreground = Res("HudAccentBrush") ?? Brushes.Peru,
                },
                new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = BuildBody(body),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { button },
                },
            },
        };
    }

    /// <summary>A line starting "** " is a section header, a blank line is spacing, anything else is body.
    /// The markers live in the copy string itself, so the acceptance stamp hashes exactly what is shown.</summary>
    private static StackPanel BuildBody(string body)
    {
        var panel = new StackPanel { Spacing = 4 };
        foreach (string line in body.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                panel.Children.Add(new Control { Height = 6 });
            }
            else if (line.StartsWith("** ", StringComparison.Ordinal))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line[3..],
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Res("HudAccentBrush") ?? Brushes.Peru,
                    Margin = new Thickness(0, 6, 0, 0),
                });
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("HudTextBrush") ?? Brushes.White,
                });
            }
        }
        return panel;
    }

    private static ISolidColorBrush? Res(string key) =>
        Application.Current?.TryFindResource(key, out var r) == true && r is ISolidColorBrush b ? b : null;
}
