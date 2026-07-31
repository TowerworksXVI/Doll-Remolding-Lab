using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remold.App.Views;

/// <summary>
/// A small modal text prompt, code-built so it needs no XAML file and inherits the app theme. Returns the
/// trimmed text, or null if cancelled.
/// </summary>
public sealed class TextPromptWindow : Window
{
    private TextPromptWindow(string title, string prompt, string initial, string watermark, string confirmLabel)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        var box = new TextBox { Text = initial, Watermark = watermark };
        box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };

        var ok = new Button { Content = confirmLabel, IsDefault = true, Padding = new Thickness(16, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim());
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
    }

    /// <summary>Show modally over <paramref name="owner"/>; resolves to the entered text or null. Pass
    /// <paramref name="watermark"/> and <paramref name="confirmLabel"/> so a reused prompt doesn't mislabel
    /// its field or its accept button.</summary>
    public static Task<string?> Show(Window owner, string title, string prompt, string initial = "",
        string watermark = "mod name", string confirmLabel = "Create") =>
        new TextPromptWindow(title, prompt, initial, watermark, confirmLabel).ShowDialog<string?>(owner);
}
