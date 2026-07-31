using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Remold.App.Views;

/// <summary>Only what the app can truthfully report: its own version, the on-disk schema numbers it
/// reads/writes, and where its data lives. NO fabricated URLs: a link the app cannot stand behind is worse
/// than no link at all.</summary>
public sealed record AboutInfo(
    string AppVersion,
    int ProjectSchema,
    string SettingsPath,
    string LibraryRoot,
    string? GamePath);

/// <summary>
/// The About dialog, code-built so it needs no XAML and inherits the app theme. Paths are selectable so they
/// can be copied.
/// </summary>
public sealed class AboutWindow : Window
{
    private AboutWindow(AboutInfo info)
    {
        Title = "About Doll Remolding Lab";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 4, 0, 0) };
        int row = 0;
        Fact(grid, ref row, "Version", info.AppVersion);
        Fact(grid, ref row, "Project schema", info.ProjectSchema.ToString());
        Fact(grid, ref row, "Settings", info.SettingsPath);
        Fact(grid, ref row, "Projects folder", info.LibraryRoot);
        Fact(grid, ref row, "Game folder", string.IsNullOrEmpty(info.GamePath) ? "not located" : info.GamePath!);

        var ok = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 6) };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Doll Remolding Lab", FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock
                {
                    Text = "Author mods for GIRLS' FRONTLINE 2: EXILIUM.",
                    TextWrapping = TextWrapping.Wrap, Foreground = Subtext(),
                },
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok },
                },
            },
        };
    }

    /// <summary>Show the About dialog modally over <paramref name="owner"/>.</summary>
    public static Task Show(Window owner, AboutInfo info) => new AboutWindow(info).ShowDialog(owner);

    /// <summary>One label + selectable value row — the values are worth copying for support.</summary>
    private static void Fact(Grid grid, ref int row, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var lbl = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 16, 4), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var val = new SelectableTextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(val, row);
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);
        row++;
    }

    private static IBrush Subtext() =>
        Application.Current?.TryFindResource("HudSubtextBrush", out var r) == true && r is IBrush b ? b : Brushes.Gray;
}
