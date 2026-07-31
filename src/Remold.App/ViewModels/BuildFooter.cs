using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Migoto;

namespace Remold.App.ViewModels;

/// <summary>What the Build pane's footer is currently reporting.</summary>
public enum BuildFooterKind
{
    /// <summary>A fresh project, or a step not yet entered.</summary>
    Idle,
    /// <summary>The change list's ready summary — what the next build would ship.</summary>
    Ready,
    /// <summary>The change list is being derived; the pane can't say what would ship yet.</summary>
    Reading,
    /// <summary>A build is running; the line is its latest progress line.</summary>
    Building,
    Built,
    /// <summary>A build (or the save it needs) failed.</summary>
    Failed,
    /// <summary>Copying the built folder into the Mods folder failed.</summary>
    InstallFailed,
    /// <summary>Derivation threw, so there is no edit list to build from.</summary>
    Blocked,
    /// <summary>Something outside the build went wrong while the pane was in use.</summary>
    Notice,
}

/// <summary><see cref="Rows"/> counts every row, ticked or not, so "no changes at all" and "every change
/// left out" read as different states.</summary>
public readonly record struct BuildReadyCounts(int Rows, int Replaced, int Retextured, int Hidden)
{
    public int Shipping => Replaced + Retextured + Hidden;

    /// <summary><paramref name="verbs"/> is one entry per SHIPPING row's verb; <paramref name="rows"/> is
    /// the total.</summary>
    public static BuildReadyCounts From(int rows, IEnumerable<string> verbs)
    {
        var v = verbs as IList<string> ?? verbs.ToList();
        return new BuildReadyCounts(rows,
            v.Count(x => x == Core.Project.EditVerbs.Replace),
            v.Count(x => x == Core.Project.EditVerbs.Retexture),
            v.Count(x => x == Core.Project.EditVerbs.Hide));
    }
}

/// <summary>The Build pane's own footer line: an immutable state plus the pure transitions that produce
/// it. The pane drives this instead of the mod-level status channel, which every save/open/autosave also
/// writes — a build failure has to survive a Ctrl+S. A finished run, a blocked derivation and a Notice
/// all outrank a recount, so a routine refresh behind them can't report "Ready:" as if nothing happened;
/// <see cref="Ticked"/> releases the ones a tick makes stale, <see cref="Streaming"/> and
/// <see cref="Cleared"/> release everything.</summary>
public sealed record BuildFooter(BuildFooterKind Kind, string Text)
{
    public static readonly BuildFooter Idle = new(BuildFooterKind.Idle, "");

    /// <summary>The remedy on a blocked derivation. The filter that would drop a row runs LAST by design, so
    /// unticking cannot fix a throwing derivation — the edit itself has to change.</summary>
    public const string BlockedFix = "Fix or revert the edit in ② Edit.";

    /// <summary>⛔ states — all of them stop the flow.</summary>
    public bool IsFailure => Kind is BuildFooterKind.Failed or BuildFooterKind.Blocked
        or BuildFooterKind.InstallFailed;
    public bool ShowPulse => Kind is BuildFooterKind.Building or BuildFooterKind.Reading;
    /// <summary>A blocked derivation never ran and an install never wrote one, so neither has a log to
    /// offer; a successful run offers it from the result bar instead.</summary>
    public bool ShowLogButton => Kind == BuildFooterKind.Failed;

    /// <summary>This line outranks a recount.</summary>
    public bool Holds => Kind is BuildFooterKind.Building or BuildFooterKind.Built
        or BuildFooterKind.Failed or BuildFooterKind.Blocked or BuildFooterKind.Notice
        or BuildFooterKind.InstallFailed;

    /// <summary>A completed derivation's counts. Clears a stale Blocked line — that derivation now
    /// succeeds — while every other holding line stands.</summary>
    public BuildFooter Derived(BuildReadyCounts counts) =>
        Kind == BuildFooterKind.Blocked ? Ready(counts) : Recount(counts);

    /// <summary>A recount of the same list (a tick's re-count). Silent behind a holding line.</summary>
    public BuildFooter Recount(BuildReadyCounts counts) => Holds ? this : Ready(counts);

    /// <summary>The built line and the notice no longer describe the current state, so they let go; a
    /// failure and a blocked derivation stay until the next build or a re-entry.</summary>
    public BuildFooter Ticked(BuildReadyCounts counts) =>
        Kind is BuildFooterKind.Built or BuildFooterKind.Notice ? Ready(counts) : Recount(counts);

    /// <summary>The derivation is running: the pane pulses rather than reporting counts it doesn't have
    /// yet. Silent behind a holding line, and released by the counts the derivation lands.</summary>
    public BuildFooter Reading() => Holds ? this : new(BuildFooterKind.Reading, ReadingLine);

    /// <summary>The line the derivation pulses under, matching the Edit pane's own wait.</summary>
    public const string ReadingLine = "Reading changes…";

    public BuildFooter Streaming(string line) => new(BuildFooterKind.Building, line);

    public BuildFooter Built(string packageName, int warnings) => new(BuildFooterKind.Built,
        $"Built {packageName}" + (warnings > 0 ? $" · {warnings} warning(s)" : ""));

    /// <summary>Holds the footer until the next build or a re-entry into the step.</summary>
    public BuildFooter Failed(string message) => new(BuildFooterKind.Failed, $"Build failed: {message}");

    /// <summary>An install that didn't land. <paramref name="folderState"/> is the sentence naming what the
    /// Mods folder holds now, which only the install itself can say.</summary>
    public BuildFooter InstallFailed(string message, string folderState) =>
        new(BuildFooterKind.InstallFailed, $"Install failed: {Sentence(message)} {folderState}");

    /// <summary>Derivation threw: what's wrong, then what to do about it.</summary>
    public BuildFooter Blocked(string message) => new(BuildFooterKind.Blocked, BlockedLine(message));

    /// <summary>The blocked line as text — the same value the Build gate reports as its reason.</summary>
    public static string BlockedLine(string message) => Sentence(message) + " " + BlockedFix;

    /// <summary>A non-build failure the pane has to own (a failed autosave on a tick). A ⛔ line outranks
    /// it — the build's own failure is the more actionable, and the autosave failure still reports on the
    /// mod-level channel.</summary>
    public BuildFooter Notice(string message) =>
        IsFailure ? this : new(BuildFooterKind.Notice, message);

    /// <summary>Entering the step, and switching project: no run, no hold, nothing to report.</summary>
    public BuildFooter Cleared() => Idle;

    private static BuildFooter Ready(BuildReadyCounts c)
    {
        if (c.Rows == 0) return Idle;   // the empty state carries the sentence; the footer stays quiet
        if (c.Shipping == 0)
            return new(BuildFooterKind.Ready, "Nothing ships. Every change is left out of this build");
        return new(BuildFooterKind.Ready, "Ready: " + string.Join(" · ", new[]
        {
            c.Replaced > 0 ? $"{c.Replaced} replaced" : null,
            c.Retextured > 0 ? $"{c.Retextured} retextured" : null,
            c.Hidden > 0 ? $"{c.Hidden} hidden" : null,
        }.Where(s => s is not null)));
    }

    private static string Sentence(string message)
    {
        var s = message.TrimEnd();
        return s.Length == 0 || s.EndsWith('.') || s.EndsWith('!') || s.EndsWith('?') ? s : s + ".";
    }
}

/// <summary>A refresh runs off-thread, so the one completing may no longer be the one the pane awaits.</summary>
public static class BuildRefresh
{
    /// <summary>No newer refresh has started, and the project it derived from is still the open one — a
    /// switch mid-refresh would otherwise land another mod's list, and a tick on it would write that mod's
    /// exclusions into this one.</summary>
    public static bool IsCurrent(int stamp, int latest, object? derivedFor, object? current) =>
        stamp == latest && ReferenceEquals(derivedFor, current);
}

/// <summary>Which warning set the pane shows.</summary>
public static class BuildWarningSource
{
    /// <summary>A completed run's warnings replace the derivation's wholesale; with no run owning the
    /// surface the derivation's own stand, so its "not in this build" lines are readable before a build.
    ///
    /// <para>Identical sentences collapse to one. A warning is written about a FACT — a map that won't bind,
    /// a texture two changes claim — and the same fact is reachable more than once in a run (once per
    /// material map, once per claimant), so the list would otherwise repeat itself.</para></summary>
    public static IReadOnlyList<string> Current(
        IReadOnlyList<string>? runWarnings, IReadOnlyList<string> derivationWarnings) =>
        (runWarnings ?? derivationWarnings).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>ONE pure rule for the Edit pane's Open-in-Blender verbs, the same shape as
/// <see cref="InstallGate"/>: enablement and the reason shown on hover are one answer, so a disabled Open
/// always says what to do about it. Blender is optional to the app, so "not found" is a normal state with a
/// normal remedy, not an error.</summary>
public static class BlenderGate
{
    public const string NotFound = "Blender not found. Set the Blender path in Settings.";
    public const string Busy = "Working on this already.";
    public const string AlreadyOpen = "Already open in Blender.";
    public const string ReadyPart = "The rest of the outfit comes along as context. Send to Lab returns this part.";
    public const string ReadyPartAlone = "No outfit around it. Opens without building the combined file.";
    public const string ReadyAll = "Open every part in one Blender session. Send to Lab returns the edits";
    public const string BlendShapes = "This mesh uses expressions and cannot be replaced.";
    public const string SkinLayout = "This mesh's skin is reduced, and replacement needs a full poseable one. Hide and retexture still work.";
    public const string StaticOnly = "Static parts open one at a time. Select a part and use Open in Blender.";
    public const string StaticPart = "Static parts open on their own. Use Open in Blender.";

    /// <summary>The blocking reason, or null when Open can run. ORDERED by what has to be settled first:
    /// the permanent properties of the subject and its mesh, then no Blender (a setting to fix), then a
    /// live session on this part, and last a running verb — the shortest wait. Only the MESH verbs pass
    /// <paramref name="blocked"/> and <paramref name="openInBlender"/>: a subject opens its whole outfit,
    /// which an unreplaceable part rides as context. Only the SUBJECT verb passes
    /// <paramref name="staticOnly"/>: the combined session carries the skinned parts, so a subject with
    /// none has no session to open. Only the part's REFERENCES open passes <paramref name="staticPart"/> —
    /// the session carrying the outfit around a static part would not carry the part itself.</summary>
    public static string? Reason(bool blenderFound, bool busy,
        StreamDump.SkinRefusal? blocked = null, bool staticOnly = false, bool openInBlender = false,
        bool staticPart = false)
    {
        if (blocked is { } why) return Blocked(why);
        if (staticOnly) return StaticOnly;
        if (staticPart) return StaticPart;
        if (!blenderFound) return NotFound;
        if (openInBlender) return AlreadyOpen;
        if (busy) return Busy;
        return null;
    }

    /// <summary>The line a refused mesh shows, per branch of the recoverable-skin rule.</summary>
    public static string Blocked(StreamDump.SkinRefusal refusal) =>
        refusal == StreamDump.SkinRefusal.BlendShapes ? BlendShapes : SkinLayout;
}

/// <summary>ONE pure rule for the Install action, the same shape as <see cref="BuildGate"/>: the button's
/// enablement and the reason it shows on hover are one answer, so a disabled Install always says what to do
/// about it. The loader is USER-SET ONLY, so "not set" is a normal state with a normal remedy, not an
/// error.</summary>
public static class InstallGate
{
    public const string NoBuild = "Build the mod first.";
    public const string NoLoader = "Set the 3DMigoto loader in Settings first.";
    public const string Ready = "Copy the built folder into the loader's Mods folder.";

    /// <summary>A set path that isn't there — a renamed or moved 3DMigoto install. Naming the path is the
    /// whole remedy: the modder recognises it and re-points Settings at the exe that moved.</summary>
    public static string LoaderNotFound(string? path) => $"3DMigoto loader not found: {path}";

    /// <summary>A loader with no <c>Mods\</c> folder beside it — an exe that isn't a 3DMigoto loader, or
    /// an install stripped of its folder.</summary>
    public static string NoModsFolder(string? loaderExe) => $"No Mods folder beside {loaderExe}";

    /// <summary>The blocking reason, or null when Install can run. ORDERED by what the modder has to do
    /// first: there is nothing to install before there is a build. <paramref name="loaderExists"/> and
    /// <paramref name="modsFolder"/> are the caller's disk reads, so this stays pure.</summary>
    public static string? Reason(bool hasBuild, string? loaderExe, bool loaderExists, string? modsFolder)
    {
        if (!hasBuild) return NoBuild;
        if (string.IsNullOrWhiteSpace(loaderExe)) return NoLoader;
        if (!loaderExists) return LoaderNotFound(loaderExe);
        if (modsFolder is null) return NoModsFolder(loaderExe);
        return null;
    }
}

/// <summary>ONE pure rule: the button's enablement and the reason it shows on hover are the same answer, so
/// a disabled button can always say why.</summary>
public static class BuildGate
{
    public const string GameUnavailable =
        "Game files unavailable. Use Tools · Locate game…, then Tools · Rescan game files.";
    public const string NothingDerived = "Nothing to build yet. Edit or hide a part in ② Edit.";
    public const string NothingTicked = "Every change is left out of this build";
    public const string Ready = "Build the mod folder and a zip for sharing.";

    /// <summary>The blocking reason, or null when a build can run. ORDERED by what the modder has to fix
    /// first: the install, a throwing derivation, an empty list, an all-unticked one.</summary>
    public static string? Reason(bool gameLoaded, string? derivationFailure, BuildReadyCounts counts)
    {
        if (!gameLoaded) return GameUnavailable;
        if (derivationFailure is not null) return derivationFailure;
        if (counts.Rows == 0) return NothingDerived;
        if (counts.Shipping == 0) return NothingTicked;
        return null;
    }
}
