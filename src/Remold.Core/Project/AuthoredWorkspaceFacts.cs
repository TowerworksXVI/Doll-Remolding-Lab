using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>The materialization facts a build reads off <see cref="AuthoredWorkspaceIndex.Records"/>: what
/// the target mesh measured when it was exported, and which renderer slots a materialized game texture
/// serves. Nothing authored lives here — every decision the build acts on comes from the plan.
///
/// <para>Texture records are grouped exactly as one materialized file is: a bundle, a path id and the
/// project file it was written to. Two records of one texture differing only in the renderer slot that
/// reached it are ONE file with two users, which is what a repair record has to say.</para></summary>
public sealed class AuthoredWorkspaceFacts
{
    private readonly List<AuthoredWorkspaceRecord> _records;
    private readonly List<TextureUse> _textures = new();

    public AuthoredWorkspaceFacts(AuthoredProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _records = project.WorkspaceIndex?.Records ?? new List<AuthoredWorkspaceRecord>();
        foreach (var record in _records)
        {
            if (record.Kind == ProjectAssetKind.Geometry) continue;
            var use = _textures.FirstOrDefault(candidate =>
                string.Equals(candidate.Bundle, record.GameAsset.LogicalBundle,
                    StringComparison.OrdinalIgnoreCase)
                && candidate.PathId == record.GameAsset.PathId
                && string.Equals(candidate.ProjectFile, record.ProjectFile,
                    StringComparison.OrdinalIgnoreCase));
            if (use is null)
            {
                use = new TextureUse(record.GameAsset.LogicalBundle, record.GameAsset.PathId,
                    record.ProjectFile,
                    record.GameAsset.Name ?? $"Texture2D_{record.GameAsset.PathId}");
                _textures.Add(use);
            }
            if (!use.Users.Contains(record.Part.RendererSlot, StringComparer.OrdinalIgnoreCase))
                use.Users.Add(record.Part.RendererSlot);
        }
    }

    /// <summary>The target mesh's recorded scene-rest uprighting, or null where nothing was baked.</summary>
    public IReadOnlyList<float>? BakedRestOf(TargetPart part) => Geometry(part)?.BakedRest;

    /// <summary>The target mesh's recorded vertex count, or null where none was measured.</summary>
    public int? OriginalVerticesOf(TargetPart part) => Geometry(part)?.OriginalVertices;

    /// <summary>The renderer slots a materialized game texture serves, by the identity the game holds the
    /// asset under. Null for a texture no record names.</summary>
    public IReadOnlyList<string>? TextureUsersOf(string bundleId, string textureName) =>
        _textures.FirstOrDefault(use =>
            string.Equals(use.Name, textureName, StringComparison.Ordinal)
            && string.Equals(use.Bundle, bundleId, StringComparison.Ordinal))?.Users;

    /// <summary>The part a materialized picture was exported from, or null when the file is authored bytes
    /// or nothing this project materialized. The build asks it of a donor row so a replacement's maps
    /// cannot reach a subject the content policy blocks.</summary>
    public TargetPart? PictureSourceOf(string? projectRelativeFile)
    {
        if (projectRelativeFile is null) return null;
        return _records.FirstOrDefault(record => record.Kind != ProjectAssetKind.Geometry
            && SameFile(record.ProjectFile, projectRelativeFile))?.Part;
    }

    private AuthoredWorkspaceRecord? Geometry(TargetPart part) =>
        _records.FirstOrDefault(record => record.Kind == ProjectAssetKind.Geometry
            && record.Part.SameAs(part));

    private static bool SameFile(string? left, string? right) =>
        string.Equals(left?.Replace('\\', '/'), right?.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private sealed record TextureUse(string Bundle, long PathId, string ProjectFile, string Name)
    {
        public List<string> Users { get; } = new();
    }
}
