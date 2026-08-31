using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Tests;

/// <summary>How far one committed change reaches into ②'s filed pictures.</summary>
public enum PreviewReach
{
    /// <summary>Nothing on the page is drawn from anything this change touched.</summary>
    None,

    /// <summary>Only the edit the change names. Every other edit's render, and the part's own, stay.</summary>
    Scoped,

    /// <summary>Everything. The change cannot say which pictures it moved, so none of them can be trusted.</summary>
    Global,
}

/// <summary>One row of the invalidation matrix: an authored mutation, and what the change it commits is
/// expected to cost each page.
///
/// <para>The list lives here rather than in either page's tests because both pages read the SAME committed
/// change and must not drift about what a case is. <see cref="BuildPageVmTests"/> walks it for whether the
/// planner ran; <see cref="EditPageVmTests"/> walks it for which renders survived.</para></summary>
public sealed record InvalidationCase(
    string Name,
    Action<AuthoredEditSession, string>? Arrange,
    Action<AuthoredEditSession, string> Act,
    bool Replans,
    PreviewReach Reach,
    string? TouchedEdit = null);

public static class InvalidationCases
{
    /// <summary>The one edit each scoped case names, and the one that must be left alone beside it. Both are
    /// content edits on the same part, so a scope that leaks would leak here first.</summary>
    public const string Touched = "edit-long";
    public const string Untouched = "edit-short";

    private const string Name = "Golden";
    private const string Version = "1.0";

    private static readonly IReadOnlyList<InvalidationCase> All = new[]
    {
        new InvalidationCase("description", null,
            (session, _) => session.SetIdentity(Name, Version, null, "What this mod does.", null, true,
                null, null),
            Replans: false, PreviewReach.None),

        new InvalidationCase("author", null,
            (session, _) => session.SetIdentity(Name, Version, "Somebody", null, null, true, null, null),
            Replans: false, PreviewReach.None),

        new InvalidationCase("version", null,
            (session, _) => session.SetIdentity(Name, "2.0", null, null, null, true, null, null),
            Replans: false, PreviewReach.None),

        new InvalidationCase("preview image", null,
            (session, _) => session.SetPreview("preview.png"),
            Replans: false, PreviewReach.None),

        // The plan resolves every authored file under the root to answer whether it is on disk, so where the
        // project lives is plan-affecting however much it reads like the rest of the identity form.
        new InvalidationCase("root dir", null,
            (session, root) => session.SetRootDir(Path.Combine(root, "moved")),
            Replans: true, PreviewReach.None),

        new InvalidationCase("edit label rename", null,
            (session, _) => session.RenameEdit(Touched, "Renamed"),
            Replans: true, PreviewReach.Scoped, Touched),

        new InvalidationCase("placement into a key-group state",
            (session, _) => { session.UnplaceEdit(Touched); session.CreateKeyGroup("F7", Untouched); },
            (session, _) => session.PlaceEdit(Touched, GroupId(session), "state-0002"),
            Replans: true, PreviewReach.None),

        new InvalidationCase("state reorder",
            (session, _) => session.CreateKeyGroup("F7", Untouched),
            (session, _) => session.ReorderState(GroupId(session), 0, 1),
            Replans: true, PreviewReach.None),

        new InvalidationCase("binding change", null,
            (session, _) => session.ChooseTargetGameValue(Touched, "slot-geometry"),
            Replans: true, PreviewReach.Scoped, Touched),

        new InvalidationCase("asset publish", (session, root) => WriteBoundFile(session, root),
            (session, _) => Publish(session),
            Replans: true, PreviewReach.Scoped, Touched),

        // Re-reading the install recaptures the whole inventory and names no edit or slot, so there is no
        // picture it can vouch for.
        new InvalidationCase("workspace recapture", null,
            (session, _) => session.SetWorkspaceIndex(new AuthoredWorkspaceIndex()),
            Replans: true, PreviewReach.Global),
    };

    public static IEnumerable<object[]> Names => All.Select(one => new object[] { one.Name });

    public static InvalidationCase Named(string name) =>
        All.SingleOrDefault(one => string.Equals(one.Name, name, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"no invalidation case called '{name}'");

    /// <summary>A project the whole matrix can be walked on: the golden two-edit fixture, rooted at a real
    /// folder so the plan's file reads and the publish case have somewhere to look.</summary>
    public static AuthoredEditSession Session(string root)
    {
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = root;
        return new AuthoredEditSession(project);
    }

    private static string GroupId(AuthoredEditSession session) => session.Outline().Groups[0].Id;

    /// <summary>Put the bytes the edit's geometry binding already names on disk, which is what a transport
    /// opens from.</summary>
    private static void WriteBoundFile(AuthoredEditSession session, string root)
    {
        var project = session.Snapshot();
        string assetId = project.EditDefinitions.Single(edit => edit.Id == Touched)
            .Bindings.Single(binding => binding.SlotId == "slot-geometry").ProjectAssetId!;
        string file = Path.Combine(root,
            project.ProjectAssets.Single(asset => asset.Id == assetId).File
                .Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[] { 1 });
    }

    private static void Publish(AuthoredEditSession session)
    {
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), Touched, "slot-geometry");
        File.WriteAllBytes(ingress.ReturnArtifact, new byte[] { 2 });
        session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry, "Published",
            ProjectAssetIngress.Binary);
    }
}
