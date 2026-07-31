using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remold.App.Views;

/// <summary>
/// A small modal yes/no confirmation, code-built so it needs no XAML file and inherits the app theme.
/// Esc = cancel; the confirm button can be tinted danger-red. Enter confirms, EXCEPT on a danger dialog,
/// where it cancels: a destructive answer is only ever given by clicking it.
/// </summary>
public sealed class ConfirmWindow : Window
{
    private ConfirmWindow(string title, string message, string confirmLabel, string? cancelLabel, bool danger)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        // OK-only notice (cancelLabel null): the single button is BOTH default and cancel, so Enter and Esc
        // both dismiss it. A danger confirm with a Cancel beside it gives the default to Cancel instead, so
        // a stray Enter can never be the destructive answer.
        bool defaultIsCancel = danger && cancelLabel is not null;
        var ok = new Button { Content = confirmLabel, IsDefault = !defaultIsCancel, IsCancel = cancelLabel is null, Padding = new Thickness(16, 6) };
        if (danger && Application.Current?.TryFindResource("HudErrorBrush", out var err) == true && err is IBrush e)
            ok.Foreground = e;
        ok.Click += (_, _) => Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (cancelLabel is not null)
        {
            var cancel = new Button
            {
                Content = cancelLabel, IsCancel = true, IsDefault = defaultIsCancel,
                Padding = new Thickness(16, 6),
            };
            cancel.Click += (_, _) => Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        // the theme's body text, as the first-run gate paints its copy — not Fluent's near-white default
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        if (Application.Current?.TryFindResource("HudTextBrush", out var fg) == true && fg is IBrush t)
            body.Foreground = t;

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children = { body, buttons },
        };
    }

    /// <summary>Show the confirmation modally over <paramref name="owner"/>; resolves to true if confirmed.</summary>
    public static Task<bool> Show(Window owner, string title, string message,
        string confirmLabel = "OK", string cancelLabel = "Cancel", bool danger = false) =>
        new ConfirmWindow(title, message, confirmLabel, cancelLabel, danger).ShowDialog<bool>(owner);

    /// <summary>An OK-only NOTICE: a single-button dialog for a failure the user only has to acknowledge.</summary>
    public static Task<bool> Notice(Window owner, string title, string message, string okLabel = "OK") =>
        new ConfirmWindow(title, message, okLabel, cancelLabel: null, danger: false).ShowDialog<bool>(owner);
}
