using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Remold.App.ViewModels.EditPage;

namespace Remold.App.Views;

/// <summary>
/// The toon-ramp pick list, code-built like every other dialog here so it needs no XAML file and inherits
/// the app theme. Rows come from <see cref="RampPickerVm"/>; this window owns only the two file dialogs and
/// the layout.
/// </summary>
public sealed class RampPickerWindow : Window
{
    private readonly RampPickerVm _vm;

    private RampPickerWindow(RampPickerVm vm)
    {
        _vm = vm;
        DataContext = vm;
        Title = "Choose a toon ramp";
        Width = 560;
        Height = 580;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brush("HudBgBrush");

        vm.Close = ok => Close(ok);
        vm.ChooseImportFile = ChooseImportFileAsync;
        vm.ChooseExportPath = ChooseExportPathAsync;

        // The material this pick lands on. It trims at the window's width, so the hover carries it whole.
        var header = new TextBlock
        {
            Text = vm.MaterialLabel,
            Foreground = Brush("HudTextBrush"),
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(header, vm.MaterialLabel);

        var filter = new TextBox
        {
            Watermark = "Filter by character, outfit, or part", Padding = new Thickness(8, 4),
        };
        filter.Bind(TextBox.TextProperty, new Binding(nameof(RampPickerVm.Filter), BindingMode.TwoWay));

        var list = new ListBox
        {
            ItemTemplate = new FuncDataTemplate<RampPickRowVm>((_, _) => Row(), supportsRecycling: true),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(RampPickerVm.Visible)));
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(RampPickerVm.Selected), BindingMode.TwoWay));

        // The app's pulsing dot beside the wait line. A code-built window has no XAML style scope to inherit
        // the Edit pane's from, so the animation is declared here — as the settings window's is.
        var dot = new TextBlock
        {
            Text = "●", Foreground = Brush("HudAccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Classes = { PulseClass },
        };
        Styles.Add(PulseStyle<TextBlock>());
        Styles.Add(PulseStyle<Border>());
        var reading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                dot,
                new TextBlock
                {
                    Text = "Reading toon ramps…",
                    Foreground = Brush("HudSubtextBrush"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        reading.Bind(IsVisibleProperty, new Binding(nameof(RampPickerVm.IsLoading)));

        var empty = new TextBlock
        {
            Text = RampPickerVm.NoRowsLine,
            Foreground = Brush("HudSubtextBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        empty.Bind(IsVisibleProperty, new Binding(nameof(RampPickerVm.HasNoRows)));

        // What the read dropped. Without it a list shortened by a locked install reads as the mod having
        // few ramps, and the shortfall is invisible exactly when it matters.
        var dropped = new TextBlock
        {
            Foreground = Brush("HudAmberBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        dropped.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickerVm.LoadNote)));
        dropped.Bind(IsVisibleProperty, new Binding(nameof(RampPickerVm.HasLoadNote)));

        var refusal = new TextBlock
        {
            Foreground = Brush("HudAmberBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        refusal.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickerVm.Refusal)));
        refusal.Bind(IsVisibleProperty, new Binding(nameof(RampPickerVm.HasRefusal)));

        var note = new TextBlock
        {
            Foreground = Brush("HudAccentBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        note.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickerVm.Note)));
        note.Bind(IsVisibleProperty, new Binding(nameof(RampPickerVm.HasNote)));

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        Grid.SetRow(header, 0);
        Grid.SetRow(filter, 1);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        var notes = new StackPanel { Spacing = 4, Children = { reading, empty, dropped, refusal, note } };
        Grid.SetRow(notes, 3);
        var footer = Footer(vm);
        Grid.SetRow(footer, 4);
        filter.Margin = new Thickness(0, 10, 0, 8);
        notes.Margin = new Thickness(0, 8, 0, 0);
        footer.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(header);
        body.Children.Add(filter);
        body.Children.Add(scroll);
        body.Children.Add(notes);
        body.Children.Add(footer);
        body.Margin = new Thickness(20);
        Content = body;

        Opened += async (_, _) => await vm.LoadAsync();
        Closed += (_, _) => vm.Dispose();
    }

    private Panel Footer(RampPickerVm vm)
    {
        var import = Small("Import DDS…", nameof(RampPickerVm.ImportCommand));
        import[ToolTip.TipProperty] = ImportTip;
        var export = Small("Export…", nameof(RampPickerVm.ExportCommand));
        export.Bind(IsEnabledProperty, new Binding(nameof(RampPickerVm.CanExport)));
        export[ToolTip.TipProperty] = ExportTip;

        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(RampPickerVm.CancelCommand)));
        var apply = new Button { Content = "Apply", IsDefault = true, Padding = new Thickness(16, 6) };
        apply.Bind(Button.CommandProperty, new Binding(nameof(RampPickerVm.ApplyCommand)));
        apply.Bind(IsEnabledProperty, new Binding(nameof(RampPickerVm.CanApply)));

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { import, export },
        };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, apply },
        };
        return new Panel { Children = { left, right } };
    }

    /// <summary>What Import states before the file dialog: the one shape a ramp file has, so a refusal
    /// afterwards is not the first time it is said.</summary>
    internal const string ImportTip = "Use a 256×16 RGBAHalf .dds as this material's toon ramp";

    /// <summary>Export writes what is selected, byte for byte.</summary>
    internal const string ExportTip = "Save the selected toon ramp as a .dds";

    private Button Small(string content, string command)
    {
        var b = new Button { Content = content, Padding = new Thickness(10, 4), FontSize = 12 };
        b.Bind(Button.CommandProperty, new Binding(command));
        ToolTip.SetShowOnDisabled(b, true);
        return b;
    }

    /// <summary>One row: the tone-mapped strip, then what it is and where it was read.</summary>
    private Control Row()
    {
        // A filled tile, not the Edit pane's `shimmer` class: that style is declared inside WorkbenchView's
        // own scope and never reaches this window, so a row waiting here drew nothing at all.
        var shimmer = new Border
        {
            Height = 32, CornerRadius = new CornerRadius(2),
            Background = Brush("HudPanelBrush"),
            BorderBrush = Brush("HudAccentBrush"),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Classes = { PulseClass },
        };
        shimmer.Bind(IsVisibleProperty, new Binding(nameof(RampPickRowVm.IsThumbLoading)));
        var image = new Image { Height = 32, Stretch = Stretch.Fill };
        image.Bind(Image.SourceProperty, new Binding(nameof(RampPickRowVm.Thumbnail)));
        image.Bind(IsVisibleProperty, new Binding(nameof(RampPickRowVm.HasThumb)));
        var strip = new Panel { Width = 160, Height = 32, Children = { shimmer, image } };

        var title = new TextBlock
        {
            Foreground = Brush("HudTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickRowVm.Title)));

        var source = new TextBlock
        {
            Foreground = Brush("HudSubtextBrush"), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        source.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickRowVm.Source)));
        source.Bind(IsVisibleProperty, new Binding(nameof(RampPickRowVm.HasSource)));

        // What the slot binds now, marked where the row says where it came from — the list is otherwise a
        // set of equals, and Apply is the default button.
        var boundNow = new TextBlock { Foreground = Brush("HudAccentBrush"), FontSize = 11 };
        boundNow.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickRowVm.BoundNote)));
        boundNow.Bind(IsVisibleProperty, new Binding(nameof(RampPickRowVm.HasBoundNote)));
        var sourceLine = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 5, Children = { source, boundNow },
        };

        var dims = new TextBlock { Foreground = Brush("HudSubtextBrush"), FontSize = 11 };
        dims.Bind(TextBlock.TextProperty, new Binding(nameof(RampPickRowVm.Dimensions)));

        var text = new StackPanel
        {
            Spacing = 1, VerticalAlignment = VerticalAlignment.Center,
            Children = { title, sourceLine, dims },
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 3),
            Children = { strip, text },
        };
        // The whole row on hover: a fold's every place, else the trimming title/source lines in full.
        row.Bind(ToolTip.TipProperty, new Binding(nameof(RampPickRowVm.RowTip)));
        return row;
    }

    /// <summary>The class the wait dot pulses under.</summary>
    private const string PulseClass = "pulse";

    /// <summary>The throbber's animation. A code-built window has no XAML style scope to inherit it from, so
    /// it carries its own copy of MainWindow's <c>TextBlock.pulse</c> — the same reason the settings window
    /// and the Edit pane do. The colour is set on the glyph itself; this is the motion alone.</summary>
    private static Style PulseStyle<T>() where T : Control
    {
        var anim = new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromSeconds(1.2),
            IterationCount = Avalonia.Animation.IterationCount.Infinite,
        };
        anim.Children.Add(Frame(0d, 0.25));
        anim.Children.Add(Frame(0.5, 1.0));
        anim.Children.Add(Frame(1d, 0.25));

        var style = new Style(x => x.OfType<T>().Class(PulseClass));
        style.Animations.Add(anim);
        return style;

        static Avalonia.Animation.KeyFrame Frame(double cue, double opacity) => new()
        {
            Cue = new Avalonia.Animation.Cue(cue),
            Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, opacity) },
        };
    }

    private static IBrush? Brush(string key) =>
        Application.Current?.TryFindResource(key, out var v) == true && v is IBrush b ? b : null;

    private async Task<string?> ChooseImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a toon ramp",
            AllowMultiple = false,
            FileTypeFilter = new[] { DdsFiles },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> ChooseExportPathAsync(string suggested)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export this toon ramp",
            SuggestedFileName = suggested,
            DefaultExtension = "dds",
            FileTypeChoices = new[] { DdsFiles },
        });
        return file?.TryGetLocalPath();
    }

    private static FilePickerFileType DdsFiles =>
        new("DDS texture") { Patterns = new[] { "*.dds" } };

    /// <summary>Show the pick list modally over <paramref name="owner"/>. Resolves to the chosen ramp, or
    /// null when the modder cancelled.</summary>
    public static async Task<RampChoice?> Show(Window owner, RampPickerVm vm)
    {
        var window = new RampPickerWindow(vm);
        bool applied = await window.ShowDialog<bool>(owner);
        return applied ? vm.Result : null;
    }
}
