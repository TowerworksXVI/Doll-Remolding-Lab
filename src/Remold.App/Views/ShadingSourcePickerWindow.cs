using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Remold.App.ViewModels.EditPage;

namespace Remold.App.Views;

/// <summary>
/// The shading-source pick list: the materials of every part in the mod, loaded in the background while
/// the dialog is already on screen. Picking one resolves to its row; Esc resolves to null.
/// </summary>
public sealed class ShadingSourcePickerWindow : Window
{
    private readonly ShadingSourcePickerVm _vm = new();
    private readonly ListBox _list;
    private readonly Button _copy;

    private ShadingSourcePickerWindow(string targetLabel,
        Func<CancellationToken, Task<ShadingSourceLoad>> load,
        Func<long, CancellationToken, Task> waitForWarm)
    {
        DataContext = _vm;
        Title = "Copy from material";
        Width = 520;
        Height = 520;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        if (Application.Current?.TryFindResource("HudBgBrush", out var bg) == true && bg is IBrush b)
            Background = b;
        IBrush? dim = Brush("HudSubtextBrush");

        var filter = new TextBox
        {
            Watermark = "Filter by part or material", Padding = new Thickness(8, 4),
        };
        filter.Bind(TextBox.TextProperty,
            new Binding(nameof(ShadingSourcePickerVm.Filter), BindingMode.TwoWay));

        _list = new ListBox { FontSize = 12 };
        _list.Bind(ItemsControl.ItemsSourceProperty,
            new Binding(nameof(ShadingSourcePickerVm.Visible)));
        _list.Bind(ListBox.SelectedItemProperty,
            new Binding(nameof(ShadingSourcePickerVm.Selected), BindingMode.TwoWay));
        _list.DoubleTapped += (_, _) => CloseWithSelection();
        _list.SelectionChanged += (_, _) => _copy!.IsEnabled = _list.SelectedItem is not null;
        var throbber = new TextBlock
        {
            Text = "◌", Foreground = Brush("HudAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Classes = { PulseClass },
        };
        throbber.Bind(IsVisibleProperty, new Binding(nameof(ShadingSourcePickerVm.IsLoading)));
        Styles.Add(PulseStyle<TextBlock>());
        var state = new TextBlock
        {
            FontSize = 11, Foreground = dim, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        state.Bind(TextBlock.TextProperty, new Binding(nameof(ShadingSourcePickerVm.StateLine)));
        var stateSlot = new Grid
        {
            Height = 38,
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 6,
            Children = { throbber, state },
        };
        Grid.SetColumn(state, 1);

        _copy = new Button
        {
            Content = "Copy", IsDefault = true, IsEnabled = false, Padding = new Thickness(16, 6),
        };
        ToolTip.SetShowOnDisabled(_copy, true);
        ToolTip.SetTip(_copy, CopyDisabledTip);
        _copy.Click += (_, _) => CloseWithSelection();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Click += (_, _) => Close(null);

        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            Children =
            {
                Put(new TextBlock { Text = $"Onto {targetLabel}", FontSize = 12, Foreground = dim,
                    TextWrapping = TextWrapping.Wrap }, 0),
                Put(filter, 1),
                Put(new ScrollViewer { Content = _list }, 2),
                Put(stateSlot, 3),
                Put(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, _copy },
                }, 4),
            },
        };
        filter.Margin = new Thickness(0, 10, 0, 8);
        stateSlot.Margin = new Thickness(0, 8, 0, 0);

        var gone = new CancellationTokenSource();
        Closed += (_, _) => gone.Cancel();
        _ = LoadAsync(load, waitForWarm, gone.Token);
    }

    private async Task LoadAsync(Func<CancellationToken, Task<ShadingSourceLoad>> load,
        Func<long, CancellationToken, Task> waitForWarm, CancellationToken gone)
    {
        try
        {
            await _vm.LoadAsync(load, gone, waitForWarm);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (gone.IsCancellationRequested) return;
            _vm.Read = EditSubjectRead.Answered;
            _vm.Failure = "Couldn't read materials: " + e.Message;
        }
    }

    private void CloseWithSelection()
    {
        if (_list.SelectedItem is ShadingSourceRow row) Close(row);
    }

    private static Control Put(Control control, int row)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush : null;

    internal const string CopyDisabledTip = "Select a material to copy.";

    private const string PulseClass = "pulse";

    private static Avalonia.Styling.Style PulseStyle<T>() where T : Control
    {
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromSeconds(1.2),
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
        };
        anim.Children.Add(Frame(0d, 0.25));
        anim.Children.Add(Frame(0.5, 1.0));
        anim.Children.Add(Frame(1d, 0.25));

        var style = new Avalonia.Styling.Style(x => x.OfType<T>().Class(PulseClass));
        style.Animations.Add(anim);
        return style;

        static Avalonia.Animation.KeyFrame Frame(double cue, double opacity) => new()
        {
            Cue = new Avalonia.Animation.Cue(cue),
            Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, opacity) },
        };
    }

    /// <summary>Show the pick list modally; resolves to the picked row, or null on a cancel.</summary>
    public static Task<ShadingSourceRow?> Show(Window owner, string targetLabel,
        Func<CancellationToken, Task<ShadingSourceLoad>> load,
        Func<long, CancellationToken, Task> waitForWarm) =>
        new ShadingSourcePickerWindow(targetLabel, load, waitForWarm).ShowDialog<ShadingSourceRow?>(owner);
}
