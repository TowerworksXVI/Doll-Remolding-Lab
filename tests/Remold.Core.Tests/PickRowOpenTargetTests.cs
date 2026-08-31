using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core.Model;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Where a double-click on a Pick row opens it in ② Edit: the row's name label — the
/// CheckBox's own content, and the natural double-click target — must count as the row, the
/// checkbox's box chrome must stay a toggle, and a row with nothing to open is no target.</summary>
[Collection("Dispatcher")]
public sealed class PickRowOpenTargetTests
{
    private static OutfitVm Row(bool isLoading = false) =>
        new(new Outfit(-1, "VesnaSSR01", OutfitKind.Base), new[] { "body" }, _ => { }, isLoading);

    [Fact]
    public async Task The_rows_name_label_opens_and_the_box_chrome_does_not()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Remold.App.App));
        await session.Dispatch(() =>
        {
            var row = Row();
            var label = new TextBlock { Text = "Vesna" };
            var box = new CheckBox { Content = label, DataContext = row };
            var window = new Window { Content = box };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                // the label reaches the CheckBox through the template's ContentPresenter: it is the row
                Assert.True(MainWindow.PickRowOpenTarget(label, out object? opened));
                Assert.Same(row, opened);
                // a press the CheckBox reports on itself is its chrome: a toggle, never an open
                Assert.False(MainWindow.PickRowOpenTarget(box, out _));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void A_row_with_nothing_to_open_is_no_target()
    {
        Assert.False(MainWindow.PickRowOpenTarget(new TextBlock { DataContext = Row(isLoading: true) }, out _));
        Assert.False(MainWindow.PickRowOpenTarget(new TextBlock { DataContext = new object() }, out _));
        Assert.False(MainWindow.PickRowOpenTarget(null, out _));
    }
}
