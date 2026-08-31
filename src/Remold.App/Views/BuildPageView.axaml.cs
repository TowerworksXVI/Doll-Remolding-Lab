using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remold.App.ViewModels.BuildPage;

namespace Remold.App.Views;

/// <summary>The ③ Build page. Its code-behind translates pointer gestures and file drops into page routes;
/// the board itself remains a fresh, read-only projection of the authored session outline.</summary>
public partial class BuildPageView : UserControl
{
    private const string PreviewDropZoneName = "PreviewDropZone";
    private const string BehaviorBoardName = "BehaviorBoard";
    private const string StateHandleName = "StateDragHandle";

    /// <summary>The one control on a token or a library row a drag may start from: its own name, which is
    /// also the button that opens the edit. Every other control there answers a click of its own — the
    /// token's ×, the row's ⋯ — and a hand that wobbles five pixels on one of those meant the click.</summary>
    private const string DragGrabName = "DragGrab";

    /// <summary>The page's content width: the viewport, floored at the layout minimum. Inside a horizontal
    /// ScrollViewer content is otherwise measured unbounded, and any over-long text would widen the whole
    /// page instead of truncating where it stands.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter PageWidth =
        new Avalonia.Data.Converters.FuncValueConverter<Rect, double>(bounds =>
            Math.Max(940, bounds.Width));

    /// <summary>The diagnostics overlay stays below sixty percent of the page and never grows beyond the
    /// owner-approved disclosure cap.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter FlyoutMaxHeight =
        new Avalonia.Data.Converters.FuncValueConverter<Rect, double>(bounds =>
            Math.Min(360, bounds.Height * 0.60));

    private Point _pressPoint;
    private string? _pressedDrag;
    private BuildPageVm? _observed;

    public BuildPageView()
    {
        AvaloniaXamlLoader.Load(this);
        // DragEnter as well as DragOver: crossing an element boundary mid-drag raises ENTER, not Over, and
        // an unhandled DragEnter leaves the platform's permissive effects standing.
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        // handledEventsToo: a row's own buttons mark the press handled, and every draggable thing on this
        // board carries one. Without this the drag could only start from the gaps between the controls,
        // which is what the library row and the state header were reduced to. A press that never moves is
        // still the button's own click: the drag only begins past the movement threshold.
        AddHandler(PointerPressedEvent, OnBoardPointerPressed, RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnBoardPointerMoved, RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(KeyDownEvent, OnBoardKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) => ObservePage();
    }

    private void ObservePage()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnPagePropertyChanged;
        _observed = DataContext as BuildPageVm;
        if (_observed is not null) _observed.PropertyChanged += OnPagePropertyChanged;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BuildPageVm.FocusTarget) || _observed?.FocusTarget.Length == 0) return;
        string target = _observed!.FocusTarget;
        BuildMarkedTarget? markedTarget = _observed.MarkedTarget;
        Dispatcher.UIThread.Post(() =>
        {
            var control = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(candidate =>
                target == "always" ? candidate.DataContext is BuildAlwaysVm
                    : target.StartsWith("group:", StringComparison.Ordinal)
                        ? candidate.DataContext is BuildGroupVm group
                            && group.Id == target["group:".Length..]
                    : target.StartsWith("edit:", StringComparison.Ordinal)
                        ? candidate.DataContext is BuildEditRowVm edit
                            && edit.EditDefinitionId == target["edit:".Length..]
                        : candidate.DataContext is BuildStateVm state && state.Id == target
                            && (markedTarget?.Kind != BuildMarkedTargetKind.State
                                || markedTarget.Matches(state)));
            control?.BringIntoView();
        });
    }

    private void OnBoardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedDrag = null;
        ClearMarkForBoardInput(e.Source);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressedDrag = DragUnder(e.Source);
        _pressPoint = e.GetPosition(this);
    }

    private async void OnBoardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedDrag is not { } payload || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || !PastDragThreshold(e.GetPosition(this))) return;
        _pressedDrag = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(payload));
        e.Handled = true;
        // A library row is COPIED into a placement and stays in the library; anything already on the board
        // is MOVED. The cursor says which before the drop lands.
        await DragDrop.DoDragDropAsync(e, transfer,
            BuildDragPayload.Read(payload)?.Kind == BuildDragKind.Edit
                ? DragDropEffects.Copy : DragDropEffects.Move);
    }

    /// <summary>What a press here would drag, or null. Read outward from what was pressed, so the innermost
    /// draggable thing wins: a token sitting inside a state is the token, not the state.</summary>
    private string? DragUnder(object? source)
    {
        if (source is not Visual visual) return null;
        var walk = visual.GetSelfAndVisualAncestors().OfType<Control>().ToList();
        // A press inside an open flyout is not a press on the board. The flyout stands in the window's own
        // overlay layer, and its presses still route through here — starting a drag from a "Use in…" tick
        // would take the pointer capture the tick needs to land.
        if (!walk.Contains(this)) return null;
        foreach (var control in walk)
        {
            // A text box drags its own selection, which is a real interaction and outranks the board's.
            if (control is TextBox) return null;
            // A control that answers a click keeps it. The exception is the row's own name, which is what
            // the row is grabbed by — the same discipline the state header states with its ⠿ handle.
            if (control is Button or ComboBox or Slider or MenuItem && control.Name != DragGrabName)
                return null;
            switch (control.DataContext)
            {
                case BuildTokenVm token:
                    return BuildDragPayload.Token(token.EditDefinitionId, token.GroupId, token.StateId);
                case BuildEditRowVm edit:
                    return BuildDragPayload.Edit(edit.EditDefinitionId);
                // Only the state's own handle. The rest of the header is a name box and four buttons, and a
                // drag started on one of those would eat its click.
                case BuildStateVm state when control.Name == StateHandleName:
                    return BuildDragPayload.State(state.GroupId, state.Id);
            }
        }
        return null;
    }

    private bool PastDragThreshold(Point point) =>
        Math.Abs(point.X - _pressPoint.X) >= 5 || Math.Abs(point.Y - _pressPoint.Y) >= 5;

    private void OnDragOver(object? sender, DragEventArgs e) => e.DragEffects = Accepts(e);

    /// <summary>Whether this drag can land where it is hovering, and as what. The DROP asks the same
    /// question, so the cursor cannot promise something the release then swallows.</summary>
    private DragDropEffects Accepts(DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
            return OverNamedZone(e.Source, PreviewDropZoneName)
                ? DragDropEffects.Copy : DragDropEffects.None;
        if (BuildDragPayload.Read(e.DataTransfer.TryGetText()) is not { } payload)
            return DragDropEffects.None;
        var state = ContextUnder<BuildStateVm>(e.Source);
        bool always = state is null && ContextUnder<BuildAlwaysVm>(e.Source) is not null;
        // An edit already used where it is hovering has nowhere to land: the drop would only refuse, so
        // the cursor says so first. The page holds the placements; the release asks it again.
        bool used = (state is not null || always)
            && DataContext is BuildPageVm page
            && page.IsUsedAt(payload.EditDefinitionId, state?.GroupId, state?.Id);
        switch (payload.Kind)
        {
            case BuildDragKind.Edit:
                return (state is not null || always) && !used
                    ? DragDropEffects.Copy : DragDropEffects.None;
            case BuildDragKind.State:
                // A state belongs to the key group it is in. There is no move that takes it to another
                // group's card, so the cursor refuses rather than the drop going nowhere.
                return state is not null
                    && string.Equals(state.GroupId, payload.GroupId, StringComparison.Ordinal)
                    && !string.Equals(state.Id, payload.StateId, StringComparison.Ordinal)
                    ? DragDropEffects.Move : DragDropEffects.None;
            case BuildDragKind.Token:
                if (used) return DragDropEffects.None;
                if (state is not null)
                    return string.Equals(state.GroupId, payload.GroupId, StringComparison.Ordinal)
                        && string.Equals(state.Id, payload.StateId, StringComparison.Ordinal)
                        ? DragDropEffects.None : DragDropEffects.Move;
                return always && payload.GroupId is not null
                    ? DragDropEffects.Move : DragDropEffects.None;
            default:
                return DragDropEffects.None;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not BuildPageVm page) { e.DragEffects = DragDropEffects.None; return; }
        if (OverNamedZone(e.Source, BehaviorBoardName)) page.ClearMarkedTarget();
        var effects = Accepts(e);
        e.DragEffects = effects;
        if (effects == DragDropEffects.None) return;
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            var files = e.DataTransfer.TryGetFiles();
            IReadOnlyList<string> paths = files is null ? Array.Empty<string>()
                : files.Select(file => file.TryGetLocalPath()).Where(path => path is not null)
                    .Select(path => path!).ToList();
            page.DropPreview(paths);
            return;
        }

        var payload = BuildDragPayload.Read(e.DataTransfer.TryGetText())!;
        var state = ContextUnder<BuildStateVm>(e.Source);
        switch (payload.Kind)
        {
            case BuildDragKind.Edit:
                page.DropEdit(payload.EditDefinitionId, state?.GroupId, state?.Id);
                break;
            case BuildDragKind.State:
                page.DropState(payload.GroupId!, payload.StateId!, state!.Id);
                break;
            case BuildDragKind.Token:
                page.DropToken(payload.EditDefinitionId, payload.GroupId, payload.StateId,
                    state?.GroupId, state?.Id);
                break;
        }
    }

    private void OnBoardLabelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter and not Key.Return) return;
        e.Handled = true;
        CommitBoardLabel(sender);
    }

    private void OnBoardKeyDown(object? sender, KeyEventArgs e) => ClearMarkForBoardInput(e.Source);

    private void ClearMarkForBoardInput(object? source)
    {
        if (OverNamedZone(source, BehaviorBoardName) && DataContext is BuildPageVm page)
            page.ClearMarkedTarget();
    }

    private void OnBoardLabelLostFocus(object? sender, RoutedEventArgs e) => CommitBoardLabel(sender);

    private void OnChoiceFlyoutClick(object? sender, RoutedEventArgs e) =>
        (sender as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.Popup>()?.Close();

    private void OnDiagnosticFlyoutChipClick(object? sender, RoutedEventArgs e) =>
        (sender as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.Popup>()?.Close();

    private static void CommitBoardLabel(object? sender)
    {
        if (sender is not TextBox box) return;
        if (box.DataContext is BuildGroupVm group) group.Label = box.Text ?? "";
        else if (box.DataContext is BuildStateVm state) state.Label = box.Text ?? "";
    }

    private static T? ContextUnder<T>(object? source) where T : class =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<T>().FirstOrDefault();

    private static bool OverNamedZone(object? source, string name) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .Any(control => control.Name == name) ?? false;
}
