using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remold.App.ViewModels.BuildPage;
using Remold.App.ViewModels.EditPage;
using Remold.App.Views;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public sealed class BuildDiagnosticsFlyoutTests
{
    private const string Sentence =
        "This diagnostic sentence is intentionally long enough to expose the presenter's narrow viewport while still fitting on one line at the intended reading width.";

    [Fact]
    public async Task Open_diagnostics_flyout_has_no_horizontal_scroll_range()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Remold.App.App));
        await session.Dispatch(MeasureOpenFlyout, CancellationToken.None);
    }

    private static void MeasureOpenFlyout()
    {
        var vm = new BuildPageVm(new Shell());
        var placements = new[]
        {
            new BuildPlacementChipVm(vm, "Body options · State 1", "body", "state-1"),
            new BuildPlacementChipVm(vm, "Hair color · State 2", "hair", "state-2"),
            new BuildPlacementChipVm(vm, "Accessory toggle · State 3", "accessory", "state-3"),
            new BuildPlacementChipVm(vm, "Expression set · State 4", "expression", "state-4"),
        };
        vm.Warnings.Add(Sentence);
        vm.WarningRows.Add(new BuildIssueVm(Sentence, Sentence, Sentence, false,
            Array.Empty<string>(), Array.Empty<string>(), placements));

        var page = new BuildPageView { DataContext = vm };
        var window = new Window { Width = 920, Height = 600, Content = page };
        Flyout? flyout = null;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = Assert.Single(page.GetVisualDescendants().OfType<Button>(), candidate =>
                candidate.Classes.Contains("diagnosticCounts") && candidate.Flyout is Flyout);
            flyout = Assert.IsType<Flyout>(button.Flyout);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            var presenter = Assert.Single(content.GetVisualAncestors().OfType<FlyoutPresenter>());
            var viewers = presenter.GetVisualDescendants().OfType<ScrollViewer>().ToArray();
            Assert.NotEmpty(viewers);
            var presenterViewer = viewers.MinBy(viewer => viewer.GetVisualAncestors().Count())!;
            var viewport = Assert.Single(presenterViewer.GetVisualDescendants()
                .OfType<ScrollContentPresenter>(), candidate =>
                    ReferenceEquals(candidate.TemplatedParent, presenterViewer));
            var sentence = Assert.Single(presenter.GetVisualDescendants().OfType<TextBlock>(), candidate =>
                candidate.Text == Sentence);
            var sentenceOrigin = sentence.TranslatePoint(default, viewport);
            Assert.NotNull(sentenceOrigin);
            double sentenceRight = sentenceOrigin.Value.X + sentence.Bounds.Width;
            string measurements = $"window={window.Bounds.Width:0.##}x{window.Bounds.Height:0.##}; "
                + $"presenter width={presenter.Bounds.Width:0.##}, max-width={presenter.MaxWidth:0.##}, "
                + $"max-height={presenter.MaxHeight:0.##}; content max-height={content.MaxHeight:0.##}; "
                + string.Join("; ", viewers.Select((viewer, index) =>
                    $"viewer[{index}] extent={viewer.Extent.Width:0.##}, viewport={viewer.Viewport.Width:0.##}, "
                    + $"horizontal={ScrollViewer.GetHorizontalScrollBarVisibility(viewer)}"))
                + $"; sentence right={sentenceRight:0.##}, presenter viewport={viewport.Bounds.Width:0.##}";

            Assert.True(presenter.Bounds.Width <= presenter.MaxWidth + 0.01, measurements);
            Assert.All(viewers, viewer => Assert.True(
                Math.Abs(viewer.Extent.Width - viewer.Viewport.Width) <= 0.01, measurements));
            Assert.True(sentenceOrigin.Value.X >= -0.01
                && sentenceRight <= viewport.Bounds.Width + 0.01, measurements);
        }
        finally
        {
            flyout?.Hide();
            window.Close();
        }
    }

    private sealed class Shell : IBuildPageShell
    {
        public string? WholeModKey => null;
        public BuildPlanningResult PlanBuild(AuthoredProject? project, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public BuildLoaderState LoaderState() => throw new NotSupportedException();
        public string SubjectLabel(string subject, string outfit) => throw new NotSupportedException();
        public string PartToken(TargetPart part) => throw new NotSupportedException();
        public Task<BuildRunResult> RunBuildAsync(IProgress<string> progress) => throw new NotSupportedException();
        public Task<BuildInstallResult> InstallBuildAsync(string builtDir, string package) =>
            throw new NotSupportedException();
        public Task ChooseLoaderAsync() => throw new NotSupportedException();
        public void OpenArtifact(BuildArtifactKind kind, string path) => throw new NotSupportedException();
        public BuildPreviewState ReadPreview(AuthoredProject? project) => throw new NotSupportedException();
        public Task<Bitmap?> LoadPreviewAsync(string path, int decodeWidth) => throw new NotSupportedException();
        public Task<string?> PickPreviewAsync() => throw new NotSupportedException();
        public void SetPreviewFrom(AuthoredEditSession session, string sourceFile) => throw new NotSupportedException();
        public void RemovePreviewFile(AuthoredEditSession session, BuildPreviewState preview) =>
            throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false) =>
            throw new NotSupportedException();
        public void GoToEdit(EditRef edit) => throw new NotSupportedException();
        public void ProjectChanged(long revision) => throw new NotSupportedException();
    }
}
