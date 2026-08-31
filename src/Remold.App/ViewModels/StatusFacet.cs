using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Remold.App.ViewModels;

/// <summary>One status-bar cell: a severity glyph plus a short label, the full detail in a tooltip so a
/// long remedy can't overflow the single-line bar. The glyph carries the state, so the label is detail.</summary>
public sealed record StatusFacet(string Glyph, IBrush Color, string Text, string Detail = "")
{
    public bool HasGlyph => Glyph.Length > 0;
    /// <summary>Anything to show at all — a cell that also carries glyphless progress lines hangs on this
    /// rather than on the glyph.</summary>
    public bool HasText => Text.Length > 0;

    /// <summary>What a width-capped cell's tooltip shows: the whole label first, then the detail on its own
    /// line.
    ///
    /// <para>A capped label ellipsizes from the END, so what it drops is the tail — and on a line that
    /// reports live work the tail is the counts, the only part of it that is not a constant. A tooltip bound
    /// to <see cref="Detail"/> alone therefore leaves the live half of the cell nowhere to be read. Cells
    /// whose label is not capped bind <see cref="Detail"/>: their label is already whole on screen, and the
    /// tooltip's job there is to say what the label does not.</para></summary>
    public string Tip =>
        Text.Length == 0 ? Detail
        : Detail.Length == 0 ? Text
        : Text + "\n" + Detail;

    // HUD severity palette, read from the app's theme resources per access rather than cached: the view
    // models are also built headless in tests, where there is no Application to resolve against, and a
    // cached miss would outlive it. The fallbacks are singletons so a facet still compares equal to itself.
    public static IBrush Ok => Res("HudAccentBrush", OkFallback);
    public static IBrush Caution => Res("HudAmberBrush", CautionFallback);
    public static IBrush Danger => Res("HudErrorBrush", DangerFallback);
    private static IBrush Neutral => Res("HudSubtextBrush", NeutralFallback);

    private static readonly IBrush OkFallback = Brushes.Peru;
    private static readonly IBrush CautionFallback = Brushes.Goldenrod;
    private static readonly IBrush DangerFallback = Brushes.IndianRed;
    private static readonly IBrush NeutralFallback = Brushes.Gray;

    private static IBrush Res(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, out object? r) == true && r is IBrush b ? b : fallback;

    public static StatusFacet None => new("", Neutral, "");

    public static StatusFacet Loading(string text, string detail = "") => new("", Neutral, text, detail);
    public static StatusFacet Good(string text) => new("✓", Ok, text);
    public static StatusFacet Info(string text, string detail = "") => new("✓", Ok, text, detail);
    public static StatusFacet Warn(string text, string detail = "") => new("⚠", Caution, text, detail);
    public static StatusFacet Bad(string text, string detail = "") => new("✗", Danger, text, detail);
}
