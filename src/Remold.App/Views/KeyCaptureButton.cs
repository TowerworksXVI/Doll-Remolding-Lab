using System;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Remold.Core.Project;

namespace Remold.App.Views;

/// <summary>
/// A compact key-capture field: click it, press a key, and <see cref="BoundKey"/> carries that key in the
/// token form a 3DMigoto <c>key =</c> line takes (<c>F6</c>, <c>CTRL SHIFT H</c>). Delete or Backspace
/// clears the binding; Esc leaves capture with the binding as it was.
///
/// <para>It is a Button so it inherits the app's chrome and focus behaviour; capture is entered on click
/// and left on the FIRST real key, so the field can never sit armed while the rest of the pane takes
/// typing. A modifier pressed on its own does not end capture — it is half of the binding the next key
/// completes. Clicking away cancels: the field only writes on a key.</para>
/// </summary>
public sealed class KeyCaptureButton : Button
{
    /// <summary>The bound key, or null for none. Two-way by default: the field is an editor.</summary>
    public static readonly StyledProperty<string?> BoundKeyProperty =
        AvaloniaProperty.Register<KeyCaptureButton, string?>(nameof(BoundKey),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>What the field shows when no key is bound.</summary>
    public static readonly StyledProperty<string> EmptyLabelProperty =
        AvaloniaProperty.Register<KeyCaptureButton, string>(nameof(EmptyLabel), "＋ key");

    /// <summary>What the field shows while it is armed and waiting for a key.</summary>
    private const string CapturingLabel = "Press a key";

    /// <summary>What the field shows after a key with no name an ini line accepts. The binding is
    /// unchanged; the field says so instead of appearing to have ignored the press.</summary>
    internal const string UnnamedKeyLabel = "That key can't be bound.";

    /// <summary>How long <see cref="UnnamedKeyLabel"/> stands before the field returns to its binding.</summary>
    private static readonly TimeSpan FlashFor = TimeSpan.FromSeconds(2);

    private bool _capturing;
    /// <summary>The key whose down-stroke capture took, armed or completing. Its up-stroke is part of the
    /// same gesture and must not reach the button's own key activation.</summary>
    private Key? _consumedByCapture;
    private DispatcherTimer? _flashTimer;

    /// <summary>Styled as a plain Button. A derived control is styled by its own type by default, which
    /// would leave the field with none of the button chrome its neighbours in the row carry — a control
    /// that takes a click has to read as one.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    public string? BoundKey
    {
        get => GetValue(BoundKeyProperty);
        set => SetValue(BoundKeyProperty, value);
    }

    public string EmptyLabel
    {
        get => GetValue(EmptyLabelProperty);
        set => SetValue(EmptyLabelProperty, value);
    }

    public KeyCaptureButton()
    {
        Focusable = true;
        UpdateContent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundKeyProperty || change.Property == EmptyLabelProperty) UpdateContent();
    }

    protected override void OnClick()
    {
        // no base.OnClick(): the field's whole gesture is arm-then-capture, and a Command on it would fire
        // once for the arming click and never for the key
        _capturing = true;
        Focus();
        UpdateContent();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _consumedByCapture = null;   // the up-stroke of a gesture cut short lands somewhere else
        if (!_capturing) return;
        _capturing = false;   // clicking away cancels; the binding is unchanged
        UpdateContent();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_capturing)
        {
            // A down with the field un-armed opens a gesture of its own. Any slot still standing here was
            // left by an up-stroke that landed elsewhere, and it would swallow this gesture's up instead.
            _consumedByCapture = null;
            base.OnKeyDown(e);
            return;
        }
        _consumedByCapture = e.Key;
        // a modifier alone is half a binding — stay armed for the key that completes it
        if (IsModifierKey(e.Key)) { e.Handled = true; return; }

        e.Handled = true;
        _capturing = false;
        // Esc leaves capture and nothing else — the two gestures are separate, so a modder backing out of a
        // rebind can't lose the key that was already there. Delete/Backspace are the clear.
        if (e.Key is Key.Escape) { UpdateContent(); return; }
        if (e.Key is Key.Delete or Key.Back) { BoundKey = null; UpdateContent(); return; }
        // a key with no token an ini line accepts leaves the binding ALONE — writing null here would read
        // as "cleared" and quietly drop a key the modder had already set
        if (Token(e.Key, e.KeyModifiers) is { } token) BoundKey = token;
        else { Flash(UnnamedKeyLabel); return; }
        UpdateContent();
    }

    /// <summary>A Button activates on the Space key-UP stroke, so the up that completes a Space capture
    /// would re-arm the field and make the capture read as failed. An up-stroke whose down-stroke capture
    /// consumed belongs to the capture gesture and ends here; every other up-stroke goes to the base, which
    /// is what still lets Space activate an un-armed field to arm it.</summary>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_consumedByCapture == e.Key)
        {
            // No pressed state to clear: the capture handled the down itself, so the base never set one.
            // (A hold that reaches auto-repeat clears the slot on its repeat downs, so its up lands on the
            // base, which clears the state it set. IsPressed is read-only from here either way.)
            _consumedByCapture = null;
            e.Handled = true;
            return;
        }
        base.OnKeyUp(e);
    }

    /// <summary>Show a line on the field itself for a moment, then return to the binding. The field is the
    /// surface the press happened on, so it is where the refusal belongs.</summary>
    private void Flash(string line)
    {
        Content = line;
        _flashTimer ??= new DispatcherTimer { Interval = FlashFor };
        _flashTimer.Stop();
        _flashTimer.Tick -= OnFlashElapsed;
        _flashTimer.Tick += OnFlashElapsed;
        _flashTimer.Start();
    }

    private void OnFlashElapsed(object? sender, EventArgs e)
    {
        _flashTimer!.Stop();
        UpdateContent();
    }

    private void UpdateContent()
    {
        _flashTimer?.Stop();
        // the binding holds the ini token; the field shows the keycap's own reading of it
        Content = _capturing ? CapturingLabel : ModKeys.Display(BoundKey, EmptyLabel);
    }

    private static bool IsModifierKey(Key k) => k
        is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>The key + modifiers as the tokens a <c>key =</c> line takes, or null when this key has no
    /// name that line accepts. Returning null rather than a guess keeps an unnameable key out of the
    /// emitted ini, where it would silently fail to bind.</summary>
    internal static string? Token(Key key, KeyModifiers modifiers)
    {
        var name = KeyName(key);
        if (name is null) return null;
        var sb = new StringBuilder();
        if (modifiers.HasFlag(KeyModifiers.Control)) sb.Append("CTRL ");
        if (modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("ALT ");
        if (modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("SHIFT ");
        return sb.Append(name).ToString();
    }

    /// <summary>The key's own token, spelled exactly as <see cref="Remold.Core.Project.ModKeys"/> keeps it:
    /// every name here is a canonical token of the same key table the emitted <c>key =</c> line is parsed
    /// against, so a captured key and a loaded spelling of it are one binding. A key outside the table is
    /// unnamed on purpose — an unnameable key stays out of the emitted ini, where it would silently fail to
    /// bind. Escape, Delete and Backspace never reach here: they are the field's own leave/clear
    /// gestures, though their spelled names still bind when a saved project carries one.</summary>
    private static string? KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NUMPAD" + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "SPACE",
            Key.Tab => "TAB",
            Key.Enter => "ENTER",
            Key.Home => "HOME",
            Key.End => "END",
            Key.Insert => "INSERT",
            Key.PageUp => "PRIOR",
            Key.PageDown => "NEXT",
            Key.Left => "LEFT",
            Key.Right => "RIGHT",
            Key.Up => "UP",
            Key.Down => "DOWN",
            Key.Pause => "PAUSE",
            Key.CapsLock => "CAPS_LOCK",
            Key.NumLock => "NUMLOCK",
            Key.Scroll => "SCROLL",
            Key.PrintScreen => "PRINT_SCREEN",
            Key.Apps => "APPS",
            Key.Help => "HELP",
            Key.Cancel => "CANCEL",
            Key.Clear => "CLEAR",
            Key.Select => "SELECT",
            Key.Print => "PRINT",
            Key.Execute => "EXECUTE",
            Key.Sleep => "SLEEP",
            Key.Multiply => "MULTIPLY",
            Key.Add => "ADD",
            Key.Separator => "SEPARATOR",
            Key.Subtract => "SUBTRACT",
            Key.Decimal => "DECIMAL",
            Key.Divide => "DIVIDE",
            Key.OemSemicolon => "SEMICOLON",
            Key.OemPlus => "EQUALS",
            Key.OemComma => "COMMA",
            Key.OemMinus => "MINUS",
            Key.OemPeriod => "PERIOD",
            Key.OemQuestion => "SLASH",
            Key.OemTilde => "VK_OEM_3",
            Key.OemOpenBrackets => "VK_OEM_4",
            Key.OemPipe => "BACKSLASH",
            Key.OemCloseBrackets => "VK_OEM_6",
            Key.OemQuotes => "QUOTE",
            Key.Oem8 => "VK_OEM_8",
            Key.OemBackslash => "VK_OEM_102",
            Key.BrowserBack => "BROWSER_BACK",
            Key.BrowserForward => "BROWSER_FORWARD",
            Key.BrowserRefresh => "BROWSER_REFRESH",
            Key.BrowserStop => "BROWSER_STOP",
            Key.BrowserSearch => "BROWSER_SEARCH",
            Key.BrowserFavorites => "BROWSER_FAVORITES",
            Key.BrowserHome => "BROWSER_HOME",
            Key.VolumeMute => "VOLUME_MUTE",
            Key.VolumeDown => "VOLUME_DOWN",
            Key.VolumeUp => "VOLUME_UP",
            Key.MediaNextTrack => "MEDIA_NEXT_TRACK",
            Key.MediaPreviousTrack => "MEDIA_PREV_TRACK",
            Key.MediaStop => "MEDIA_STOP",
            Key.MediaPlayPause => "MEDIA_PLAY_PAUSE",
            Key.LaunchMail => "LAUNCH_MAIL",
            Key.SelectMedia => "LAUNCH_MEDIA_SELECT",
            Key.LaunchApplication1 => "LAUNCH_APP1",
            Key.LaunchApplication2 => "LAUNCH_APP2",
            _ => null,
        };
    }
}
