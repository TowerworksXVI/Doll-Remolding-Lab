using System.Collections.Generic;
using System.Text;

namespace Remold.Core.Project;

/// <summary>Filesystem-safe naming helpers for mod folders and build output.</summary>
public static class ModNaming
{
    /// <summary>A filesystem-safe slug, e.g. "Karst Jacket" → "karst-jacket". Letters and digits are judged
    /// by Unicode, so a non-ASCII name keeps its own characters rather than being transliterated.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); dash = false; }
            else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "mod" : s;
    }

    /// <summary>The one subject slug every subject-scoped filesystem name derives from
    /// (<c>{slug(character)}_{stem}</c>): the export subfolder, and the subject segment of a workspace
    /// texture's filename.</summary>
    public static string SubjectSlug(string character, string stem) => $"{Slug(character)}_{stem}";

    /// <summary>The published package's folder name, and the zip stem: <c>{name}_{author}_v{version}</c>,
    /// e.g. <c>vesna-newbody_anonymous_v1_0</c>. Name and author are slugs; the version keeps only
    /// letters, digits and dots, with dots written as underscores so the whole name stays one filesystem
    /// token. A blank author or an empty-sanitizing version drops its segment.</summary>
    public static string PackageFolderName(string name, string? author, string? version)
    {
        var parts = new List<string> { Slug(name) };
        if (!string.IsNullOrWhiteSpace(author)) parts.Add(Slug(author!));
        var v = VersionToken(version);
        if (v.Length > 0) parts.Add("v" + v);
        return string.Join("_", parts);
    }

    /// <summary>The folder name for a project's own identity metadata.</summary>
    public static string PackageFolderName(ProjectInfo info) =>
        PackageFolderName(info.Name, info.Author, info.Version);

    private static string VersionToken(string? version)
    {
        if (version is null) return "";
        var trimmed = version.Trim();
        // the segment carries its own "v" prefix, so a typed "v1.0" would read "vv1_0"
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1]))
            trimmed = trimmed[1..];
        var sb = new StringBuilder();
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == '.') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }
}
