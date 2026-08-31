using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;

namespace Remold.App.Views;

/// <summary>
/// The shading-values dialog: every value the material's shader reads, one row each — plain-language
/// label, the field's own name, a box holding the value the edit sets, and the material's original
/// beside it. An empty box means the original; typing a number sets it. Applying returns only the rows
/// that changed, validated against each field's shape before the dialog closes.
/// </summary>
public sealed class ShadingValuesWindow : Window
{
    internal const string CopiedValueUnreadable = "Couldn't read the copied value.";

    internal sealed record DialogRow(EditShadingField Field, string Initial, bool Copied,
        string? Problem);
    internal sealed record DialogInput(EditShadingField Field, string Initial, bool Copied,
        string Text);
    internal sealed record DialogApply(IReadOnlyList<EditShadingValueEdit> Edits,
        IReadOnlyDictionary<string, string> Problems, bool MatchesOriginal)
    {
        public bool Refused => Problems.Count > 0;
    }

    private sealed class Row
    {
        public required EditShadingField Field { get; init; }
        public required TextBox Box { get; init; }
        public required TextBlock Problem { get; init; }
        public required string Initial { get; init; }
        public required bool Copied { get; init; }
    }

    private readonly List<Row> _rows = new();

    private ShadingValuesWindow(string materialLabel, IReadOnlyList<EditShadingField> fields,
        IReadOnlyDictionary<string, string> authored, IReadOnlySet<string> copied,
        IReadOnlySet<string> unreadableCopies, bool addsFirstEdit)
    {
        Title = "Shading values";
        Width = 560;
        MaxHeight = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;
        IBrush? text = Brush("HudTextBrush");
        IBrush? dim = Brush("HudSubtextBrush");
        IBrush? amber = Brush("HudAmberBrush");

        var list = new StackPanel { Spacing = 8 };
        foreach (var state in DialogRows(fields, authored, copied, unreadableCopies))
        {
            var field = state.Field;
            var box = new TextBox
            {
                Width = 170,
                FontSize = 12,
                Text = state.Initial,
                Watermark = field.OriginalValue is null ? "original (not stated)" : "original",
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(box, field.Kind == MaterialValueKind.Color
                ? "Four numbers: red, green, blue, alpha."
                : $"One number. The game's own materials use {Trim(field.ObservedMin)} to {Trim(field.ObservedMax)}.");
            var problem = new TextBlock
            {
                FontSize = 11, Foreground = amber, IsVisible = state.Problem is not null,
                Text = state.Problem ?? "", TextWrapping = TextWrapping.Wrap,
            };
            var name = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = field.Label, FontSize = 12, Foreground = text,
                        TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = field.Semantic, FontSize = 10, Foreground = dim },
                },
            };
            var original = new TextBlock
            {
                Text = field.OriginalValue is null ? "original: not stated" : $"original: {field.OriginalValue}",
                FontSize = 11, Foreground = dim, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 130,
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,140"),
                Children = { name, box, original },
            };
            Grid.SetColumn(name, 0);
            Grid.SetColumn(box, 1);
            Grid.SetColumn(original, 2);
            original.Margin = new Thickness(10, 0, 0, 0);
            list.Children.Add(new StackPanel { Children = { grid, problem } });
            _rows.Add(new Row
            {
                Field = field, Box = box, Problem = problem,
                Initial = state.Initial, Copied = state.Copied,
            });
        }

        var apply = new Button { Content = "Apply", IsDefault = true, Padding = new Thickness(16, 6) };
        apply.Click += (_, _) => Apply();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = materialLabel, FontSize = 12, Foreground = dim,
                    TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "An empty box keeps the original value.",
                    FontSize = 11, Foreground = dim,
                },
                new TextBlock
                {
                    Text = EditPageVm.AddsFirstEdit, IsVisible = addsFirstEdit,
                    FontSize = 11, Foreground = dim,
                },
                new ScrollViewer { Content = list, MaxHeight = 460 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, apply },
                },
            },
        };
    }

    private void Apply()
    {
        var result = ApplyRows(_rows.Select(row => new DialogInput(row.Field, row.Initial,
            row.Copied, row.Box.Text ?? "")));
        foreach (var row in _rows)
        {
            row.Problem.IsVisible = false;
            if (result.Problems.TryGetValue(row.Field.Semantic, out string? problem))
            {
                row.Problem.Text = problem;
                row.Problem.IsVisible = true;
            }
        }
        if (result.Refused) return;
        Close(new EditShadingValuesResult(result.Edits, result.MatchesOriginal));
    }

    internal static IReadOnlyList<DialogRow> DialogRows(IReadOnlyList<EditShadingField> fields,
        IReadOnlyDictionary<string, string> authored, IReadOnlySet<string> copied,
        IReadOnlySet<string> unreadableCopies) =>
        fields.Select(field =>
        {
            bool has = authored.TryGetValue(field.Semantic, out string? current);
            return new DialogRow(field, has ? current ?? "" : "", copied.Contains(field.Semantic),
                unreadableCopies.Contains(field.Semantic) ? CopiedValueUnreadable : null);
        }).ToList();

    internal static DialogApply ApplyRows(IEnumerable<DialogInput> rows)
    {
        var edits = new List<EditShadingValueEdit>();
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        bool matchesOriginal = true;
        foreach (var row in rows)
        {
            string typed = row.Text.Trim();
            if (typed.Length == 0)
            {
                if (typed == row.Initial) continue;
                edits.Add(new EditShadingValueEdit(row.Field.Semantic, null));
                continue;
            }
            if (!MaterialValueBuildSupport.TryValues(row.Field.Semantic, typed, out _,
                    out string canonical))
            {
                problems[row.Field.Semantic] = row.Field.Kind == MaterialValueKind.Color
                    ? "Not four numbers."
                    : row.Field.Semantic == MaterialValueSemantics.UseGiFlatten
                        ? "Not 0 or 1." : "Not a number.";
                matchesOriginal = false;
                continue;
            }
            bool isOriginal = string.Equals(canonical, row.Field.OriginalValue, StringComparison.Ordinal);
            matchesOriginal &= isOriginal;
            if (typed == row.Initial) continue;
            if (canonical == row.Initial) continue;
            if (isOriginal && row.Initial.Length == 0 && !row.Copied)
                continue;
            edits.Add(new EditShadingValueEdit(row.Field.Semantic, canonical));
        }
        return new DialogApply(edits, problems, matchesOriginal && problems.Count == 0);
    }

    private static string Trim(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : null;

    /// <summary>Show the dialog modally; resolves to the changed rows, or null on a cancel.</summary>
    public static Task<EditShadingValuesResult?> Show(Window owner, string materialLabel,
        IReadOnlyList<EditShadingField> fields, IReadOnlyDictionary<string, string> authored,
        IReadOnlySet<string> copied, IReadOnlySet<string> unreadableCopies, bool addsFirstEdit) =>
        new ShadingValuesWindow(materialLabel, fields, authored, copied, unreadableCopies, addsFirstEdit)
            .ShowDialog<EditShadingValuesResult?>(owner);
}
