using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>With the pseudolocale selected, every word the main window shows comes back bracketed: text that
/// reaches the screen without passing through a string catalog is the text that stays plain. The walk covers
/// the home screen and the three pages of a window with no game and no mod loaded; menus, popups and dialogs
/// are not opened.</summary>
[Collection("UiCulture")]
public sealed class PseudolocaleWalkTests
{
    /// <summary>Text the walk finds plain by design, as "where: text". Each one is a reviewed exception;
    /// glyph-only and number-only text carries no letter and needs no entry.</summary>
    private static readonly HashSet<string> Allowlist = new(StringComparer.Ordinal)
    {
        // the Build footer's checking line is read from the catalog once per process and kept; the app selects
        // its language before that first read, but an earlier test in this process may have read it in English
        "Build: Checking…",
    };

    /// <summary>A recent-mods row shows the mod's own name and folder, which are the modder's data, not the
    /// app's text.</summary>
    private static bool IsModdersData(object? dataContext) => dataContext is RecentModVm;

    private static readonly Regex Letter = new(@"\p{L}", RegexOptions.CultureInvariant);

    [Fact]
    public async Task Every_word_on_the_home_screen_and_the_three_pages_is_read_from_a_catalog()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Remold.App.App));
        var plain = new List<string>();
        var seen = new List<(string Where, string Text)>();
        await session.Dispatch(() =>
        {
            Catalog.Apply(Catalog.Pseudo);
            MainWindow? window = null;
            try
            {
                var vm = new MainWindowViewModel(startLoad: false);
                window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Walk(window, "home", plain, seen);

                vm.ShowHome = false;
                foreach (var step in new[] { WorkflowStep.Pick, WorkflowStep.Edit, WorkflowStep.Build })
                {
                    vm.SelectedStep = step;
                    Dispatcher.UIThread.RunJobs();
                    Walk(window, step.ToString(), plain, seen);
                }
            }
            finally
            {
                window?.Close();
                Catalog.Apply(Catalog.English);
            }
        }, CancellationToken.None);

        Assert.True(plain.Count == 0, "Plain text with the pseudolocale on:\n" + string.Join("\n", plain.Distinct()));
        // each page was reached: it shows text the home screen does not
        var home = seen.Where(s => s.Where == "home").Select(s => s.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var page in new[] { WorkflowStep.Pick, WorkflowStep.Edit, WorkflowStep.Build })
            Assert.Contains(seen, s => s.Where == page.ToString() && !home.Contains(s.Text));
    }

    private static void Walk(Window window, string where, List<string> plain, List<(string Where, string Text)> seen)
    {
        void Check(string kind, object? value)
        {
            if (value is not string text || !Letter.IsMatch(text)) return;
            seen.Add((where, text));
            if (IsBracketed(text) || Allowlist.Contains($"{where}: {text}")) return;
            plain.Add($"{where} {kind}: {text}");
        }

        Check("Window.Title", window.Title);
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is Control control && (!control.IsEffectivelyVisible || IsModdersData(control.DataContext)))
                continue;
            switch (visual)
            {
                case TextBlock block: Check("TextBlock", block.Text); break;
                case TextBox box: Check("TextBox.Watermark", box.Watermark); break;
            }
            if (visual is Button button) Check("Button.Content", button.Content);
            if (visual is HeaderedContentControl headered) Check("Header", headered.Header);
            if (visual is Control tipped) Check("ToolTip.Tip", ToolTip.GetTip(tipped));
        }
    }

    [Theory]
    [InlineData("[Save]", true)]
    [InlineData("[Edits] · 3", true)]
    [InlineData("[Game files unavailable.].", true)]
    [InlineData("[Remove] [cloth]?", true)]
    [InlineData("Vesna [mod]", false)]
    [InlineData("Untitled", false)]
    public void Text_is_bracketed_when_every_letter_sits_inside_a_bracket(string text, bool bracketed)
    {
        Assert.Equal(bracketed, IsBracketed(text));
    }

    /// <summary>Whether every letter in <paramref name="text"/> sits inside a bracketed span.</summary>
    internal static bool IsBracketed(string text)
    {
        var outside = new StringBuilder();
        int depth = 0;
        foreach (char c in text)
        {
            if (c == '[') { depth++; continue; }
            if (c == ']' && depth > 0) { depth--; continue; }
            if (depth == 0) outside.Append(c);
        }
        return !Letter.IsMatch(outside.ToString());
    }
}
