using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Migoto;
using Remold.Core.Project;

namespace Remold.App.ViewModels.BuildPage;

/// <summary>The result of one pure planning read. The two refusal fields are mutually exclusive and
/// ordered by <see cref="BuildGate.Reason"/>; a successful read carries <see cref="Plan"/>.</summary>
public sealed record BuildPlanningResult(AuthoredBuildPlan? Plan = null, string? GameUnavailable = null,
    string? Failure = null);

/// <summary>The loader disk facts Install reads. The page asks again when it is entered and after a loader
/// pick, so a folder changed outside the app does not leave a stale gate.</summary>
public sealed record BuildLoaderState(string? LoaderExe, bool LoaderExists, string? ModsFolder,
    MigotoIniFacts Ini);

/// <summary>The preview named by the project, read against the disk. <see cref="Stamp"/> includes the file
/// contents, so replacing the bytes under the same <c>preview.png</c> name still makes a result stale.</summary>
public sealed record BuildPreviewState(string? RelativeFile, string? FullPath, bool Missing, string Stamp,
    int? PixelWidth = null, int? PixelHeight = null)
{
    public bool HasPreview => FullPath is not null && !Missing;
    public bool HasNoPreview => RelativeFile is null;
}

/// <summary>One finished build attempt. A failed attempt still carries its log path; a successful one also
/// carries the published artifacts and the exact session/preview baseline it consumed.</summary>
public sealed record BuildRunResult(bool Succeeded, string? Failure, string OutDir, string? ZipPath,
    string Package, string LogPath, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Infos, long Revision, string PreviewStamp);

/// <summary>One install attempt. A cancel is not a failure and leaves the page's standing footer alone.</summary>
public sealed record BuildInstallResult(bool Completed, bool Failed, string Line, string? InstalledDir = null);

public enum BuildArtifactKind
{
    Folder,
    Zip,
    Log,
    InstalledFolder,
}

/// <summary>The imperative half of ③ Build. Authored behavior remains in the session supplied to
/// <see cref="BuildPageVm.Load"/>; this seam owns current-install reads, disk publication, dialogs and step
/// navigation.</summary>
public interface IBuildPageShell
{
    BuildPlanningResult PlanBuild(AuthoredProject? project, CancellationToken cancellationToken);
    BuildLoaderState LoaderState();
    string SubjectLabel(string subject, string outfit);
    string PartToken(TargetPart part);
    string? WholeModKey { get; }

    Task<BuildRunResult> RunBuildAsync(IProgress<string> progress);
    Task<BuildInstallResult> InstallBuildAsync(string builtDir, string package);
    Task ChooseLoaderAsync();
    void OpenArtifact(BuildArtifactKind kind, string path);

    BuildPreviewState ReadPreview(AuthoredProject? project);
    Task<Bitmap?> LoadPreviewAsync(string path, int decodeWidth);
    Task<string?> PickPreviewAsync();
    void SetPreviewFrom(AuthoredEditSession session, string sourceFile);
    void RemovePreviewFile(AuthoredEditSession session, BuildPreviewState preview);

    Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false);
    void GoToEdit(EditRef edit);
    void ProjectChanged(long revision);
}
