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

/// <summary>Edits material values and enabled effects together. Disabled effects keep their values.</summary>
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

    internal sealed class EffectSelection
    {
        private readonly Dictionary<string, EditShadingEffect> _effects;
        private readonly Dictionary<string, bool> _enabled;

        internal EffectSelection(IReadOnlyList<EditShadingEffect> effects)
        {
            _effects = effects.ToDictionary(effect => effect.Id, StringComparer.Ordinal);
            _enabled = effects.ToDictionary(effect => effect.Id, effect => effect.IsEnabled,
                StringComparer.Ordinal);
            foreach (var effect in effects)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { effect.Id };
                for (string? parent = effect.ParentId; parent is not null; parent = _effects[parent].ParentId)
                    if (!_effects.ContainsKey(parent) || !seen.Add(parent))
                        throw new ArgumentException("The effect hierarchy is incomplete or contains a cycle.",
                            nameof(effects));
            }
        }

        internal bool Contains(string? id) => id is not null && _effects.ContainsKey(id);
        internal bool IsEnabled(string id) => _enabled[id];
        internal bool IsAvailable(string id)
        {
            for (string? parent = _effects[id].ParentId; parent is not null; parent = _effects[parent].ParentId)
                if (!_enabled[parent]) return false;
            return true;
        }
        internal bool IsActive(string? id) => !Contains(id) || IsEnabled(id!) && IsAvailable(id!);
        internal void SetEnabled(string id, bool enabled) => _enabled[id] = enabled;
        internal bool MatchesOriginal => _enabled.Values.All(enabled => enabled);
        internal IReadOnlyList<EditShadingEffectEdit> Edits => _effects.Values
            .Where(effect => effect.IsEnabled != _enabled[effect.Id])
            .Select(effect => new EditShadingEffectEdit(effect.Id, _enabled[effect.Id])).ToArray();
    }

    private sealed class Row
    {
        public required EditShadingField Field { get; init; }
        public required TextBox Box { get; init; }
        public required TextBlock Problem { get; init; }
        public required string Initial { get; init; }
        public required bool Copied { get; init; }
        public required Control Container { get; init; }
    }

    private readonly List<Row> _rows = new();
    private readonly EffectSelection _effectSelection;
    private readonly Dictionary<string, (CheckBox Box, TextBlock Edited)> _effectControls =
        new(StringComparer.Ordinal);

    private ShadingValuesWindow(string materialLabel, IReadOnlyList<EditShadingField> fields,
        IReadOnlyDictionary<string, string> authored, IReadOnlySet<string> copied,
        IReadOnlySet<string> unreadableCopies, bool addsFirstEdit,
        IReadOnlyList<EditShadingEffect> effects)
    {
        _effectSelection = new EffectSelection(effects);
        Title = "Advanced shading";
        Width = 620;
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

        var list = new StackPanel { Spacing = 12 };
        var states = DialogRows(fields, authored, copied, unreadableCopies);

        void AddField(StackPanel panel, DialogRow state)
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
                ? field.Semantic.EndsWith("_ST", StringComparison.Ordinal)
                    ? "Four numbers: horizontal scale, vertical scale, horizontal offset, vertical offset."
                    : "Four numbers: red, green, blue, alpha."
                : $"One number. Original materials use {Trim(field.ObservedMin)} to {Trim(field.ObservedMax)}.");
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
            var container = new StackPanel
            {
                Margin = new Thickness(_effectSelection.Contains(field.EffectId) ? 20 : 0, 0, 0, 0),
                Children = { grid, problem },
            };
            panel.Children.Add(container);
            _rows.Add(new Row
            {
                Field = field, Box = box, Problem = problem,
                Initial = state.Initial, Copied = state.Copied, Container = container,
            });
            box.TextChanged += (_, _) => RefreshEffectControls();
        }

        void AddEffect(StackPanel panel, EditShadingEffect effect)
        {
            var body = new StackPanel { Spacing = 8 };
            var enabled = new CheckBox { Content = effect.Label, FontSize = 12,
                IsChecked = effect.IsEnabled, MinHeight = 28 };
            var edited = new TextBlock { Text = "✎", Foreground = Brush("HudAccentBrush"),
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            ToolTip.SetTip(edited, "Edited values");
            _effectControls.Add(effect.Id, (enabled, edited));
            enabled.IsCheckedChanged += (_, _) =>
            {
                _effectSelection.SetEnabled(effect.Id, enabled.IsChecked == true);
                RefreshEffectControls();
            };
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { enabled, edited } });
            foreach (var state in states.Where(state => state.Field.EffectId == effect.Id))
                AddField(body, state);
            foreach (var child in effects.Where(child => child.ParentId == effect.Id))
            {
                var nested = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
                AddEffect(nested, child);
                body.Children.Add(nested);
            }
            panel.Children.Add(body);
        }

        foreach (var effect in effects.Where(effect => effect.ParentId is null)) AddEffect(list, effect);
        var ordinary = states.Where(state => !_effectSelection.Contains(state.Field.EffectId)).ToList();
        if (ordinary.Count > 0)
        {
            var values = new StackPanel { Spacing = 8 };
            if (effects.Count > 0)
                values.Children.Add(new TextBlock { Text = "Other values", FontSize = 12, Foreground = text });
            foreach (var state in ordinary) AddField(values, state);
            list.Children.Add(values);
        }
        RefreshEffectControls();

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
                    Text = "An empty box keeps the original value. Disabled effects keep their saved values.",
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
        Close(new EditShadingValuesResult(result.Edits,
            result.MatchesOriginal && _effectSelection.MatchesOriginal, _effectSelection.Edits));
    }

    private void RefreshEffectControls()
    {
        foreach (var (id, controls) in _effectControls)
        {
            controls.Box.IsEnabled = _effectSelection.IsAvailable(id);
            controls.Edited.IsVisible = _effectSelection.IsActive(id) && _rows
                .Where(row => row.Field.EffectId == id)
                .Any(row => row.Copied || row.Box.Text?.Trim() is { Length: > 0 } typed &&
                    !string.Equals(typed, row.Field.OriginalValue, StringComparison.Ordinal));
        }
        foreach (var row in _rows)
            row.Container.IsEnabled = _effectSelection.IsActive(row.Field.EffectId);
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
        IReadOnlySet<string> copied, IReadOnlySet<string> unreadableCopies, bool addsFirstEdit,
        IReadOnlyList<EditShadingEffect>? effects = null) =>
        new ShadingValuesWindow(materialLabel, fields, authored, copied, unreadableCopies, addsFirstEdit,
            effects ?? Array.Empty<EditShadingEffect>())
            .ShowDialog<EditShadingValuesResult?>(owner);
}
