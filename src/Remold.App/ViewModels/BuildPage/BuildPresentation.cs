using Remold.Core.Text;
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
    public static BuildFooter Planning { get; } = new(BuildFooterState.Planning, "◌", "", Strings.BuildChecking);
    public static BuildFooter Ready(int placements, int keys) => new(BuildFooterState.Ready, "✓", Strings.BuildReadyLead,
        Catalog.Plural(placements, Strings.BuildReadyEdits_One(placements), Strings.BuildReadyEdits_Other(placements))
        + " · " + Catalog.Plural(keys, Strings.BuildReadyKeys_One(keys), Strings.BuildReadyKeys_Other(keys)));
    public static BuildFooter Running(string line) => new(BuildFooterState.Running, "◌", "", line);
    public static BuildFooter Built(string package) => new(BuildFooterState.Built, "✓", "", Strings.BuildBuilt(package));
    public static BuildFooter Failed(string reason) => new(BuildFooterState.Failed, "✗", Strings.BuildStoppedLead,
        End(reason));
    public static BuildFooter Blocked(string reason) => new(BuildFooterState.Blocked, "✗", "", End(reason));
    public static BuildFooter Notice(string line) => new(BuildFooterState.Notice, "✓", "", End(line));

    /// <summary>The line closed as a sentence in the UI language (<see cref="Catalog.Sentence"/>): a line that
    /// already ends one in either script, an ellipsis included, is left as it is.</summary>
    internal static string End(string line) => Catalog.Sentence(line);
}

/// <summary>The completed build and live plan warning lists share a box. An identical live sentence replaces
/// its run copy; only older facts retain the lead-in, which is the one line marked as a heading.</summary>
public static class BuildWarningSource
{
    public static IReadOnlyList<(string Line, bool IsHeading)> Merge(IReadOnlyList<string>? run,
        IReadOnlyList<string> live)
    {
        var current = live.Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal).ToList();
        var older = (run ?? Array.Empty<string>()).Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal).Where(line => !current.Contains(line, StringComparer.Ordinal))
            .ToList();
        if (older.Count == 0) return current.Select(line => (line, false)).ToArray();
        return new[] { (Strings.BuildLastBuildLead, true) }.Concat(older.Concat(current).Select(line => (line, false)))
            .ToArray();
    }
}

/// <summary>ONE ordered Build gate. The page displays this same answer on the disabled button and footer.</summary>
public static class BuildGate
{
    public static string? Reason(BuildPlanningResult planning, AuthoredEditOutline? outline,
        string? firstPlanBlocker)
    {
        if (planning.GameUnavailable is not null) return planning.GameUnavailable;
        if (planning.Failure is not null) return planning.Failure;
        if (outline is null || outline.Edits.Count == 0) return Strings.BuildNothingAuthored;
        int placements = outline.Always.Count + outline.Groups.Sum(group =>
            group.States.Sum(state => state.ActiveEditIds.Count));
        if (placements == 0) return Strings.BuildNothingPlaced;
        if (planning.Plan is null) return BuildFooter.Planning.Text;
        if (!planning.Plan.CanBuild)
            return !string.IsNullOrWhiteSpace(firstPlanBlocker) ? firstPlanBlocker
                : Strings.BuildUnnamedPlanBlocker;
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
            return BuildFooter.End(Strings.BuildCannotBuildOnPart(rows[0].Edit, rows[0].Part, reason));
        if (rows.Select(row => row.Part).Distinct(StringComparer.Ordinal).Count() == 1)
            return BuildFooter.End(Strings.BuildCannotBuildOnPart(
                Catalog.List(rows.Select(row => row.Edit).ToList()), rows[0].Part, reason));
        return BuildFooter.End(Strings.BuildCannotBuildList(
            Catalog.List(rows.Select(row => Strings.BuildEditOnPart(row.Edit, row.Part)).ToList()), reason));
    }

    /// <summary>Row 3's width-bounded form. The flyout and other diagnostic surfaces keep
    /// <see cref="Blocking"/>'s complete owner list.</summary>
    public static string BlockingSummary(string reason, IReadOnlyList<BuildIssueOwner> owners)
    {
        var rows = Owners(owners);
        return rows.Count <= 1 ? Blocking(reason, rows)
            : BuildFooter.End(Catalog.Plural(rows.Count - 1,
                Strings.BuildCannotBuildAndMore_One(rows[0].Edit, rows.Count - 1, reason),
                Strings.BuildCannotBuildAndMore_Other(rows[0].Edit, rows.Count - 1, reason)));
    }

    private static IReadOnlyList<BuildIssueOwner> Owners(IReadOnlyList<BuildIssueOwner> owners) =>
        owners.DistinctBy(owner => owner.EditDefinitionId, StringComparer.Ordinal).ToList();
}

/// <summary>ONE ordered Install gate. Loader sentences are the catalog's Loader entries, including the
/// hookless-host block shared with Settings and the status bar.</summary>
public static class InstallGate
{
    public static string? Reason(bool hasBuild, BuildLoaderState loader)
    {
        if (!hasBuild) return Strings.InstallNoBuild;
        if (string.IsNullOrWhiteSpace(loader.LoaderExe)) return Strings.LoaderNotSet;
        if (!loader.LoaderExists) return Strings.LoaderNotFound(loader.LoaderExe);
        if (loader.ModsFolder is null) return Strings.LoaderNoModsFolder(loader.LoaderExe);
        if (!loader.Ini.Found) return Strings.LoaderNoIni(loader.LoaderExe);
        if (!loader.Ini.HasTextureHook) return Strings.LoaderNoTextureHook;
        return null;
    }
}

/// <summary>Warnings for keys that 3DMigoto will treat as one input. A collision is legal: it says that the
/// named controls switch together. State shortcuts shared between groups are the feature working, so two
/// shortcuts never warn about each other; a shortcut on a group key or the whole-mod key does.</summary>
public static class KeyCollisions
{
    public sealed record Entry(string Identity, string Label, string? Key, bool Shortcut = false);

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
                var clashes = rows.Where(other => !ReferenceEquals(other, row)
                    && !(row.Shortcut && other.Shortcut)).ToList();
                if (clashes.Count == 0) continue;
                var others = clashes.Select(other => other.Label).Distinct(StringComparer.Ordinal).ToList();
                bool jumps = row.Shortcut || clashes.Any(other => other.Shortcut);
                tips[row.Identity] = Strings.KeySameAs(NameList(others)) + " "
                    + (!jumps ? Strings.KeysSwitchTogether
                        : others.Count == 1 ? Strings.KeysOnePressBoth : Strings.KeysOnePressAll);
            }
        }
        return tips;
    }

    internal static string NameList(IReadOnlyList<string> names) =>
        names.Count == 0 ? Strings.KeyAnotherControl : Catalog.List(names);
}
