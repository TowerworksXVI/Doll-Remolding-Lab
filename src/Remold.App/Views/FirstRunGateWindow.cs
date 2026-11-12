using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Remold.Core.Text;

namespace Remold.App.Views;

/// <summary>The first-run window's language list, kept off the window so it can be exercised without a UI
/// runtime. The window is the first one the app opens, before any view model or background work exists, so
/// a pick applies to the whole process at once.</summary>
internal sealed class FirstRunLanguage
{
    /// <summary>The offered languages, as the Settings form's Language row lists them.</summary>
    internal IReadOnlyList<LanguageChoice> Choices { get; }

    /// <summary>The language in force; Accept stores its tag, null for English.</summary>
    internal LanguageChoice Selected { get; private set; }

    /// <summary>Opens on <see cref="Initial"/> and applies it, so the window renders in the language it
    /// shows as selected.</summary>
    internal FirstRunLanguage(string? stored, CultureInfo installed)
    {
        (Choices, Selected) = LanguageRow.Choices(Initial(stored, installed));
        Catalog.Apply(Selected.Tag);
    }

    /// <summary>The language the list opens on: the settings file's, which startup already applied, or with
    /// none stored, Simplified Chinese when the Windows display language selects it, else English.</summary>
    internal static string? Initial(string? stored, CultureInfo installed) =>
        !string.IsNullOrWhiteSpace(stored) ? stored
        : Catalog.Resolve(installed.Name) == Catalog.SimplifiedChinese ? Catalog.SimplifiedChinese
        : null;

    internal void Pick(LanguageChoice choice)
    {
        Selected = choice;
        Catalog.Apply(choice.Tag);
    }
}

/// <summary>
/// The unskippable first-run acceptable-use gate. There is NO skip affordance: closing the window exits
/// the app, since it is the only one open. Code-built (no XAML), so it inherits the app theme. A language
/// pick rewrites the texts of the controls already shown, so the language box keeps focus while the arrow
/// keys step through it.
/// </summary>
public sealed class FirstRunGateWindow : Window
{
    /// <summary>Raised once on accept; the caller persists it and opens the main window.</summary>
    public event Action? Accepted;

    private readonly FirstRunLanguage _language;
    private readonly ComboBox _picker;
    private readonly TextBlock _label;
    private readonly TextBlock _headline;
    private readonly ScrollViewer _body;
    private readonly Button _accept;

    internal FirstRunGateWindow(FirstRunLanguage language)
    {
        _language = language;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        _picker = new ComboBox
        {
            ItemsSource = _language.Choices,
            SelectedItem = _language.Selected,
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _picker.SelectionChanged += (_, _) => OnPicked();

        _accept = new Button { IsDefault = true, Padding = new Thickness(20, 6) };
        _accept.Click += (_, _) => Accepted?.Invoke();

        _label = new TextBlock
        {
            // Wide enough for the label in every offered language, so a pick does not move the box.
            MinWidth = 130,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("HudTextBrush") ?? Brushes.White,
        };
        _headline = new TextBlock
        {
            FontSize = 18, FontWeight = FontWeight.Bold,
            Foreground = Res("HudAccentBrush") ?? Brushes.Peru,
        };
        _body = new ScrollViewer { MaxHeight = 420 };

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _label, _picker },
                },
                _headline,
                _body,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _accept },
                },
            },
        };
        ShowTexts();
    }

    /// <summary>The language box's selection changed: a new language applies to the process and every text
    /// on the window is rewritten in it.</summary>
    private void OnPicked()
    {
        if (_picker.SelectedItem is not LanguageChoice choice || ReferenceEquals(choice, _language.Selected)) return;
        _language.Pick(choice);
        ShowTexts();
    }

    /// <summary>Every text on the window, in the language in force.</summary>
    private void ShowTexts()
    {
        Title = Strings.AppName;   // title bar = the app name; the headline renders in-window
        _label.Text = Strings.FirstRunLanguageLabel;
        _headline.Text = Strings.FirstRunHeadline;
        _accept.Content = Strings.FirstRunAccept;
        _body.Content = BuildBody(Strings.AcceptableUseBody);
    }

    /// <summary>A line starting "** " is a section header, a blank line is spacing, anything else is body.
    /// The markers live in the terms entry itself, in every language. The acceptance stamp hashes the
    /// English entry, not the text shown here, so editing the English terms re-prompts.</summary>
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
