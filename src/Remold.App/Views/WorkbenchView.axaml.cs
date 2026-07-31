using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Remold.App.ViewModels.Workbench;

namespace Remold.App.Views;

/// <summary>The Outfit Workbench pane, hosted in the Edit step. Its DataContext is the <c>WorkbenchVm</c>;
/// state and behaviour live there. Map cards are the only drop target — the pane takes the drop event but
/// refuses anything not landing on a card, with the rules in <see cref="WorkbenchVm.HandleDropAsync"/>.</summary>
public partial class WorkbenchView : UserControl
{
    public WorkbenchView()
    {
        AvaloniaXamlLoader.Load(this);
        // DragEnter as well as DragOver: crossing an element boundary mid-drag raises ENTER, not Over, and an
        // unhandled DragEnter leaves the platform's permissive effects standing — the cursor flickers, and a
        // release on that frame delivers a drop the pane meant to refuse.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Only a file drag over a map card THAT COULD TAKE IT gets the copy cursor — the pane
    /// background, every other row, and a card with nothing to replace all read as "no" while dragging. The
    /// cursor asks the VM's own gate, so a card the drop has nothing to do with never offers one. A card that
    /// PASSES the gate can still be refused once the drop lands, on state the hover doesn't read — the
    /// payload, and what the part's replacement carries — and that refusal writes the status line.</summary>
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files)
                        && CardUnder(e.Source) is { } card
                        && DataContext is WorkbenchVm vm && vm.CanAcceptDrop(card)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not WorkbenchVm vm) { e.DragEffects = DragDropEffects.None; return; }
        var card = CardUnder(e.Source);
        // A drag that got this far showed the copy cursor, so an unreadable payload can't just vanish: hand
        // the VM an empty list and let its refusal line answer for it.
        var files = e.Data.GetFiles();
        IReadOnlyList<string> paths = files is null
            ? Array.Empty<string>()
            : files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Select(p => p!).ToList();
        // Normalize what goes back to the drag source — unset, it returns the platform's Copy|Move|Link.
        e.DragEffects = DragDropEffects.Copy;
        // Fire-and-forget the top of the async chain — the VM's verb gate serializes the rest.
        _ = vm.HandleDropAsync(paths, card);
    }

    /// <summary>The map card under a drop, walking up the visual tree — the card Border and its children all
    /// carry the map row as DataContext. Null when the drop landed on the pane background.</summary>
    private static WorkbenchMapVm? CardUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors()
 .OfType<Control>()
 .Select(c => c.DataContext)
 .OfType<WorkbenchMapVm>()
 .FirstOrDefault();
}
