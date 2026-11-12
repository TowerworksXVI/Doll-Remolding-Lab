using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remold.App.ViewModels.BuildPage;
using Remold.App.Views;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>A part's name or a name the modder typed shows on a ③ Build chip exactly as written. A button's
/// own text reads its first underscore as an access-key marker and draws the name without it, so a chip
/// that names a part such as <c>cloth1_Fight</c> carries its text in a plain text block instead.</summary>
[Collection("Dispatcher")]
public sealed class LiteralNameChipTests
{
    [Theory]
    [InlineData("cloth1_Fight", "Edit 1")]
    [InlineData("衣物_trans", "my_edit")]
    public async Task A_chip_shows_a_part_and_edit_name_with_every_underscore(string part, string edit)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Remold.App.App));
        await session.Dispatch(() => RenderChips(part, edit), CancellationToken.None);
    }

    private static void RenderChips(string part, string edit)
    {
        var vm = new BuildPageVm(new BuildPageShellStub());
        var target = new TargetPart { Subject = "S", Outfit = "S01", RendererSlot = "c_S01_slg_" + part + "_lod0" };
        vm.Always.Tokens.Add(new BuildTokenVm(vm, "edit-1", target, EditDefinitionKind.Content, edit, part,
            null, null));
        string placement = $"{part} · {edit}";
        vm.Warnings.Add("warning");
        vm.WarningRows.Add(new BuildIssueVm("warning", "warning", "warning", false, Array.Empty<string>(),
            Array.Empty<string>(), new[] { new BuildPlacementChipVm(vm, placement, "group", "state") }));

        var page = new BuildPageView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = page };
        Flyout? flyout = null;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var chip = Assert.Single(page.GetVisualDescendants().OfType<Button>(),
                button => button.Name == "DragGrab" && button.DataContext is BuildTokenVm);
            AssertLiteral(chip, $"{part} · {edit}");

            var counts = Assert.Single(page.GetVisualDescendants().OfType<Button>(), candidate =>
                candidate.Classes.Contains("diagnosticCounts") && candidate.Flyout is Flyout);
            flyout = Assert.IsType<Flyout>(counts.Flyout);
            flyout.ShowAt(counts);
            Dispatcher.UIThread.RunJobs();
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            var presenter = Assert.Single(content.GetVisualAncestors().OfType<FlyoutPresenter>());
            var placementChip = Assert.Single(presenter.GetVisualDescendants().OfType<Button>(),
                button => button.DataContext is BuildPlacementChipVm);
            AssertLiteral(placementChip, placement);
        }
        finally
        {
            flyout?.Hide();
            window.Close();
        }
    }

    /// <summary>The button draws its name through a plain text block, never through access-key text, which
    /// would hide the name's first underscore.</summary>
    private static void AssertLiteral(Button button, string expected)
    {
        var texts = button.GetVisualDescendants().OfType<TextBlock>().ToArray();
        Assert.DoesNotContain(texts, text => text is AccessText);
        var shown = Assert.Single(texts, text => text.Text == expected);
        Assert.Equal(typeof(TextBlock), shown.GetType());
    }

    private sealed class BuildPageShellStub : IBuildPageShell
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
        public Task<Avalonia.Media.Imaging.Bitmap?> LoadPreviewAsync(string path, int decodeWidth) =>
            throw new NotSupportedException();
        public Task<string?> PickPreviewAsync() => throw new NotSupportedException();
        public void SetPreviewFrom(AuthoredEditSession session, string sourceFile) => throw new NotSupportedException();
        public void RemovePreviewFile(AuthoredEditSession session, BuildPreviewState preview) =>
            throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false) =>
            throw new NotSupportedException();
        public void GoToEdit(Remold.App.ViewModels.EditPage.EditRef edit) => throw new NotSupportedException();
        public void ProjectChanged(long revision) => throw new NotSupportedException();
    }
}
