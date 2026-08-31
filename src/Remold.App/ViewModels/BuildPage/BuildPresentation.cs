using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;

namespace Remold.App.ViewModels.BuildPage;

public enum BuildFooterState
{
    Idle,
    Planning,
    Ready,
    Running,
    Built,
    Failed,
    Blocked,
    Notice,
}

/// <summary>The Build page's own footer channel. It is immutable so a plan refresh cannot partly overwrite
/// a completed-run line.</summary>
public sealed record BuildFooter(BuildFooterState State, string Glyph, string Lead, string Text)
{
    public bool IsBusy => State is BuildFooterState.Planning or BuildFooterState.Running;

    public bool IsAlarm => State is BuildFooterState.Failed or BuildFooterState.Blocked;

    public static BuildFooter Idle { get; } = new(BuildFooterState.Idle, "", "", "");
    public static BuildFooter Planning { get; } = new(BuildFooterState.Planning, "◌", "", "Checking…");
    public static BuildFooter Ready(int placements, int keys) => new(BuildFooterState.Ready, "✓", "Ready:",
        $"{placements} edit{(placements == 1 ? "" : "s")} in use · {keys} key{(keys == 1 ? "" : "s")}");
    public static BuildFooter Running(string line) => new(BuildFooterState.Running, "◌", "", line);
    public static BuildFooter Built(string package) => new(BuildFooterState.Built, "✓", "", $"Built {package}.");
    public static BuildFooter Failed(string reason) => new(BuildFooterState.Failed, "✗", "Build stopped:",
        End(reason));
    public static BuildFooter Blocked(string reason) => new(BuildFooterState.Blocked, "✗", "", End(reason));
    public static BuildFooter Notice(string line) => new(BuildFooterState.Notice, "✓", "", End(line));

    internal static string End(string line)
    {
        string value = line.TrimEnd();
        return value.Length == 0 || value.EndsWith('.') || value.EndsWith('!') || value.EndsWith('?')
            ? value : value + ".";
    }
}

/// <summary>The completed build and live plan warning lists share a box. An identical live sentence replaces
/// its run copy; only older facts retain the lead-in.</summary>
public static class BuildWarningSource
{
    public const string LastBuildLead = "From the last build:";

    public static IReadOnlyList<string> Merge(IReadOnlyList<string>? run, IReadOnlyList<string> live)
    {
        var current = live.Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal).ToList();
        var older = (run ?? Array.Empty<string>()).Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal).Where(line => !current.Contains(line, StringComparer.Ordinal))
            .ToList();
        if (older.Count == 0) return current;
        return new[] { LastBuildLead }.Concat(older).Concat(current).ToArray();
    }
}

/// <summary>ONE ordered Build gate. The page displays this same answer on the disabled button and footer.</summary>
public static class BuildGate
{
    /// <summary>The app's one sentence for an install that is not loaded.</summary>
    public const string GameUnavailable = Remold.Core.GameFilesGate.Unavailable;
    public const string NothingAuthored = "Nothing to build yet. Add an edit in ② Edit.";
    public const string NothingPlaced =
        "No edits are used yet. Add an edit to Always or to a state of a key group.";
    public const string Ready = "Build the mod folder and a zip for sharing.";
    public const string UnnamedPlanBlocker =
        "Cannot build this mod. Doll Remolding Lab did not say what stops it.";

    /// <summary>What a failed run says when it came back with no sentence of its own. The failure is still
    /// reported: silence would read as a build that simply did nothing.</summary>
    public const string UnnamedFailure = "Doll Remolding Lab did not say why.";

    public static string? Reason(BuildPlanningResult planning, AuthoredEditOutline? outline,
        string? firstPlanBlocker)
    {
        if (planning.GameUnavailable is not null) return planning.GameUnavailable;
        if (planning.Failure is not null) return planning.Failure;
        if (outline is null || outline.Edits.Count == 0) return NothingAuthored;
        int placements = outline.Always.Count + outline.Groups.Sum(group =>
            group.States.Sum(state => state.ActiveEditIds.Count));
        if (placements == 0) return NothingPlaced;
        if (planning.Plan is null) return BuildFooter.Planning.Text;
        if (!planning.Plan.CanBuild)
            return !string.IsNullOrWhiteSpace(firstPlanBlocker) ? firstPlanBlocker
                : UnnamedPlanBlocker;
        return null;
    }
}

public sealed record BuildIssueOwner(string EditDefinitionId, string Edit, string Part);

/// <summary>The attribution added around a planner reason. The reason itself remains byte-for-byte intact;
/// each approved sentence shape has one assembly point here so a wording ruling changes one line.</summary>
public static class BuildIssueAttribution
{
    public static string Blocking(string reason, IReadOnlyList<BuildIssueOwner> owners)
    {
        var rows = Owners(owners);
        if (rows.Count == 0) return reason;
        if (rows.Count == 1)
            return BuildFooter.End($"Cannot build {rows[0].Edit} on {rows[0].Part}: {reason}");
        if (rows.Select(row => row.Part).Distinct(StringComparer.Ordinal).Count() == 1)
            return BuildFooter.End($"Cannot build {KeyCollisions.NameList(rows.Select(row => row.Edit).ToList())} "
                + $"on {rows[0].Part}: {reason}");
        return BuildFooter.End($"Cannot build {KeyCollisions.NameList(rows.Select(row => $"{row.Edit} on {row.Part}").ToList())}: "
            + reason);
    }

    /// <summary>Row 3's width-bounded form. The flyout and other diagnostic surfaces keep
    /// <see cref="Blocking"/>'s complete owner list.</summary>
    public static string BlockingSummary(string reason, IReadOnlyList<BuildIssueOwner> owners)
    {
        var rows = Owners(owners);
        return rows.Count <= 1 ? Blocking(reason, rows)
            : BuildFooter.End($"Cannot build {rows[0].Edit} and {rows.Count - 1} more: {reason}");
    }

    private static IReadOnlyList<BuildIssueOwner> Owners(IReadOnlyList<BuildIssueOwner> owners) =>
        owners.DistinctBy(owner => owner.EditDefinitionId, StringComparer.Ordinal).ToList();
}

/// <summary>ONE ordered Install gate. Loader sentences come from <see cref="LoaderGate"/>, including the
/// hookless-host block shared with Settings and the status bar.</summary>
public static class InstallGate
{
    public const string NoBuild = "Build the mod first.";
    public const string Ready = "Copy the built folder into the loader's Mods folder.";
    public const string SetLoader =
        "Select the loader exe: 3DMigoto Loader.exe, or SSMT's per-game Run.exe.";

    public static string? Reason(bool hasBuild, BuildLoaderState loader)
    {
        if (!hasBuild) return NoBuild;
        if (string.IsNullOrWhiteSpace(loader.LoaderExe)) return LoaderGate.NoLoader;
        if (!loader.LoaderExists) return LoaderGate.LoaderNotFound(loader.LoaderExe);
        if (loader.ModsFolder is null) return LoaderGate.NoModsFolder(loader.LoaderExe);
        if (!loader.Ini.Found) return LoaderGate.NoLoaderIni(loader.LoaderExe);
        if (!loader.Ini.HasTextureHook) return LoaderGate.NoTextureHook;
        return null;
    }
}

/// <summary>Warnings for keys that 3DMigoto will treat as one input. A collision is legal: it says that the
/// named controls switch together.</summary>
public static class KeyCollisions
{
    public const string WholeModLabel = "the whole mod";

    public sealed record Entry(string Identity, string Label, string? Key);

    public static IReadOnlyDictionary<string, string> Tips(IEnumerable<Entry> entries)
    {
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var collision in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => ModKeys.Normalize(entry.Key), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var rows = collision.ToList();
            foreach (var row in rows)
            {
                var others = rows.Where(other => !ReferenceEquals(other, row)).Select(other => other.Label)
                    .Distinct(StringComparer.Ordinal).ToList();
                tips[row.Identity] = $"Same key as {NameList(others)}. They switch together.";
            }
        }
        return tips;
    }

    internal static string NameList(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "another control",
        1 => names[0],
        2 => names[0] + " and " + names[1],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
