using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Remold.App.ViewModels.EditPage;

namespace Remold.App.Views;

/// <summary>The ② Edit page. Its DataContext is the <see cref="EditPageVm"/>; state and behaviour live there.
/// One thing takes a drop: a map card — an edit's, or the original maps a part with no edits shows.
/// Everything else refuses while dragging, and what an arriving drop actually does is decided in
/// <see cref="EditPageVm.HandleDropAsync"/>.</summary>
public partial class EditPageView : UserControl
{
    /// <summary>The width the tree pane was last dragged to. Null until the divider is first dragged: the pane
    /// then stands at the width the markup gives it. Whatever is set here, the columns' own minimums still
    /// hold, so a remembered width too wide for the window leaves the inspector its room.</summary>
    public static readonly StyledProperty<double?> TreeWidthProperty =
        AvaloniaProperty.Register<EditPageView, double?>(nameof(TreeWidth), defaultBindingMode: BindingMode.TwoWay);

    public double? TreeWidth
    {
        get => GetValue(TreeWidthProperty);
        set => SetValue(TreeWidthProperty, value);
    }

    private readonly ColumnDefinition _treeColumn;
    private readonly ColumnDefinition _dividerColumn;
    private readonly ColumnDefinition _inspectorColumn;
    private readonly GridLength _markupTreeWidth;

    public EditPageView()
    {
        AvaloniaXamlLoader.Load(this);
        var split = this.FindControl<Grid>("Split")!;
        _treeColumn = split.ColumnDefinitions[0];
        _dividerColumn = split.ColumnDefinitions[1];
        _inspectorColumn = split.ColumnDefinitions[2];
        _markupTreeWidth = _treeColumn.Width;
        split.SizeChanged += (_, e) => CapTreeWidth(e.NewSize.Width);
        this.FindControl<GridSplitter>("Divider")!.DragCompleted += (_, _) => RememberTreeWidth();
        // DragEnter as well as DragOver: crossing an element boundary mid-drag raises ENTER, not Over, and an
        // unhandled DragEnter leaves the platform's permissive effects standing — the cursor flickers, and a
        // release on that frame delivers a drop the pane meant to refuse.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TreeWidthProperty)
            _treeColumn.Width = TreeWidth is { } width ? new GridLength(width) : _markupTreeWidth;
    }

    /// <summary>The tree may take whatever the inspector's minimum leaves over, never more. A cap rather than a
    /// rewrite of the width, so the remembered width comes back when the window grows again.</summary>
    private void CapTreeWidth(double splitWidth) =>
        _treeColumn.MaxWidth = Math.Max(_treeColumn.MinWidth,
            splitWidth - _dividerColumn.Width.Value - _inspectorColumn.MinWidth);

    /// <summary>A finished drag is what gets remembered: the width the tree actually stands at, which the
    /// splitter has already held inside both minimums.</summary>
    private void RememberTreeWidth() => TreeWidth = Math.Round(_treeColumn.ActualWidth);

    /// <summary>Only a file drag over something that could take it gets the copy cursor. The cursor asks the
    /// VM's own gate, so a target the drop has nothing to do with never offers one; a target that passes can
    /// still be refused once the drop lands, on state the hover does not read.</summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && DataContext is EditPageVm vm
                        && vm.CanAcceptDrop(CardUnder(e.Source))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not EditPageVm vm) { e.DragEffects = DragDropEffects.None; return; }
        var card = CardUnder(e.Source);
        // A drag that got this far showed the copy cursor, so an unreadable payload cannot just vanish: hand
        // the VM an empty list and let its refusal line answer for it.
        var files = e.DataTransfer.TryGetFiles();
        IReadOnlyList<string> paths = files is null
            ? Array.Empty<string>()
            : files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        // Normalize what goes back to the drag source — unset, it returns the platform's Copy|Move|Link.
        e.DragEffects = DragDropEffects.Copy;
        // Fire-and-forget the top of the async chain — the VM's own busy gates serialize the rest.
        _ = vm.HandleDropAsync(paths, card);
    }

    /// <summary>Enter lands the name. The app has no type-then-button field, and the box is bound live, so
    /// the only two things that commit are this and leaving the box.</summary>
    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Return) return;
        e.Handled = true;
        Commit();
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (DataContext is EditPageVm vm && vm.SelectedNode is { } node
            && vm.CommitRenameCommand.CanExecute(node))
            vm.CommitRenameCommand.Execute(node);
    }

    /// <summary>The map card under a drop, walking up the visual tree — the card border and its children all
    /// carry the card as DataContext. Null when the drop landed anywhere else.</summary>
    private static EditMapCardVm? CardUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Control>()
            .Select(c => c.DataContext)
            .OfType<EditMapCardVm>()
            .FirstOrDefault();
}
