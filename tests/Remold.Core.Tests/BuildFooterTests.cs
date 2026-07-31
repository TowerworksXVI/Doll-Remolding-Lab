using System.Collections.Generic;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Build pane's footer state machine and the gate beside it: which line wins, what releases a holding
/// line, why the Build action is off, and which refresh may paint. All pure.
/// </summary>
public class BuildFooterTests
{
    private static BuildReadyCounts Counts(int rows, int replaced = 0, int retextured = 0, int hidden = 0) =>
        new(rows, replaced, retextured, hidden);

    [Fact]
    public void A_ready_line_counts_only_what_ships()
    {
        var f = BuildFooter.Idle.Derived(Counts(rows: 4, replaced: 2, hidden: 1));

        Assert.Equal(BuildFooterKind.Ready, f.Kind);
        Assert.Equal("Ready: 2 replaced · 1 hidden", f.Text);
        Assert.False(f.IsFailure);
        Assert.False(f.ShowPulse);
    }

    [Fact]
    public void Rows_that_are_all_unticked_read_as_nothing_shipping()
    {
        var f = BuildFooter.Idle.Derived(Counts(rows: 3));

        Assert.Equal("Nothing ships. Every change is left out of this build", f.Text);
    }

    [Fact]
    public void An_empty_change_list_leaves_the_footer_to_the_empty_state()
    {
        Assert.Equal(BuildFooterKind.Idle, BuildFooter.Idle.Derived(Counts(rows: 0)).Kind);
    }

    [Fact]
    public void A_failure_holds_the_line_through_every_recount()
    {
        var failed = BuildFooter.Idle.Failed("bundle 'x' isn't readable in this install");

        Assert.Equal(BuildFooterKind.Failed, failed.Kind);
        Assert.Equal("Build failed: bundle 'x' isn't readable in this install", failed.Text);
        Assert.True(failed.IsFailure);
        Assert.True(failed.ShowLogButton);
        // the refresh behind it, and a tick, both leave it alone
        Assert.Equal(failed, failed.Derived(Counts(rows: 2, replaced: 2)));
        Assert.Equal(failed, failed.Ticked(Counts(rows: 2, replaced: 1)));
        // only the next build (or a re-entry) releases it
        Assert.Equal(BuildFooterKind.Building, failed.Streaming("Building…").Kind);
        Assert.Equal(BuildFooterKind.Idle, failed.Cleared().Kind);
    }

    [Fact]
    public void A_built_line_holds_the_refresh_behind_it_but_a_tick_releases_it()
    {
        var built = BuildFooter.Idle.Built("vesna-newbody_anonymous_v1_0", 2);

        Assert.Equal("Built vesna-newbody_anonymous_v1_0 · 2 warning(s)", built.Text);
        Assert.Equal(built, built.Derived(Counts(rows: 1, replaced: 1)));

        var ticked = built.Ticked(Counts(rows: 1));
        Assert.Equal("Nothing ships. Every change is left out of this build", ticked.Text);
    }

    [Fact]
    public void A_clean_build_says_nothing_about_warnings()
    {
        Assert.Equal("Built karst-jacket_v2", BuildFooter.Idle.Built("karst-jacket_v2", 0).Text);
    }

    [Fact]
    public void Entering_the_step_pulses_while_the_change_list_is_derived()
    {
        var f = BuildFooter.Idle.Reading();

        Assert.Equal(BuildFooterKind.Reading, f.Kind);
        Assert.Equal("Reading changes…", f.Text);
        Assert.True(f.ShowPulse);
        Assert.False(f.IsFailure);
        // the counts it was waiting for release it
        Assert.Equal("Ready: 1 replaced", f.Derived(Counts(rows: 2, replaced: 1)).Text);
    }

    [Fact]
    public void A_finished_run_outranks_the_refresh_that_follows_it()
    {
        // the build's own finally re-derives; the ✓ line has to survive it
        var built = BuildFooter.Idle.Built("mine_v1_0", warnings: 0);

        Assert.Equal(built, built.Reading());
    }

    [Fact]
    public void A_failed_install_takes_the_stop_glyph_and_says_what_the_folder_holds()
    {
        var f = BuildFooter.Idle.InstallFailed("access denied",
            ModInstall.InstallFailedException.FolderUntouched);

        Assert.Equal("Install failed: access denied. The Mods folder is unchanged.", f.Text);
        Assert.True(f.IsFailure);
        Assert.True(f.Holds);
        Assert.False(f.ShowLogButton);   // an install writes no transcript to open
    }

    [Fact]
    public void A_blocked_derivation_carries_the_remedy_and_offers_no_log()
    {
        var blocked = BuildFooter.Idle.Blocked(
            "edited mesh 'c_vesna01_ghost_lod0' is not in Vesna · VesnaSSR01's roster");

        Assert.True(blocked.IsFailure);
        Assert.False(blocked.ShowLogButton);   // nothing ran, so there is no transcript to open
        Assert.EndsWith("roster. Fix or revert the edit in ② Edit.", blocked.Text);
        // unticking cannot fix it, so a tick doesn't release the line
        Assert.Equal(blocked, blocked.Ticked(Counts(rows: 1, replaced: 1)));
        // a derivation that now succeeds does
        Assert.Equal("Ready: 1 replaced", blocked.Derived(Counts(rows: 1, replaced: 1)).Text);
    }

    [Fact]
    public void A_blocked_message_that_already_ends_in_a_stop_is_not_double_punctuated()
    {
        Assert.Equal("It broke. " + BuildFooter.BlockedFix, BuildFooter.Idle.Blocked("It broke.").Text);
    }

    [Fact]
    public void An_autosave_failure_survives_the_recount_the_tick_triggers()
    {
        const string line = "Autosave failed: access denied. Changes are still in memory. Use File · Save Mod to retry.";
        var f = BuildFooter.Idle.Derived(Counts(rows: 2, replaced: 2)).Notice(line);

        Assert.Equal(BuildFooterKind.Notice, f.Kind);
        Assert.Equal(line, f.Recount(Counts(rows: 2, replaced: 1)).Text);
        // the next tick, which saved cleanly, releases it
        Assert.Equal("Ready: 2 replaced", f.Ticked(Counts(rows: 2, replaced: 2)).Text);
    }

    [Fact]
    public void A_build_failure_outranks_an_autosave_notice()
    {
        var failed = BuildFooter.Idle.Failed("disk full");
        Assert.Equal(failed, failed.Notice("Autosave failed: disk full."));
    }

    [Fact]
    public void A_running_build_streams_under_the_pulse()
    {
        var f = BuildFooter.Idle.Failed("earlier run").Streaming("Dumping vanilla streams…");

        Assert.True(f.ShowPulse);
        Assert.False(f.IsFailure);
        Assert.Equal("Dumping vanilla streams…", f.Text);
        Assert.Equal(f, f.Derived(Counts(rows: 1, replaced: 1)));   // the refresh can't talk over a run
    }

    // ---- the gate ---------------------------------------------------------------------------------

    [Fact]
    public void The_build_action_is_off_with_a_reason_for_each_way_it_cannot_run()
    {
        Assert.Equal(BuildGate.GameUnavailable,
            BuildGate.Reason(gameLoaded: false, derivationFailure: null, Counts(rows: 2, replaced: 2)));
        Assert.Equal("boom. " + BuildFooter.BlockedFix,
            BuildGate.Reason(true, "boom. " + BuildFooter.BlockedFix, Counts(rows: 2, replaced: 2)));
        Assert.Equal(BuildGate.NothingDerived, BuildGate.Reason(true, null, Counts(rows: 0)));
        Assert.Equal(BuildGate.NothingTicked, BuildGate.Reason(true, null, Counts(rows: 3)));
        Assert.Null(BuildGate.Reason(true, null, Counts(rows: 3, retextured: 1)));
    }

    [Fact]
    public void The_ready_counts_read_the_verbs_of_the_shipping_rows()
    {
        var c = BuildReadyCounts.From(4, new[]
        {
            EditVerbs.Replace, EditVerbs.Retexture, EditVerbs.Hide,
        });

        Assert.Equal(4, c.Rows);
        Assert.Equal(3, c.Shipping);
        Assert.Equal(1, c.Replaced);
        Assert.Equal(1, c.Retextured);
        Assert.Equal(1, c.Hidden);
    }

    // ---- the refresh stamp ------------------------------------------------------------------------

    [Fact]
    public void A_refresh_overtaken_by_a_newer_one_drops_its_result()
    {
        var project = new ModProject();

        Assert.True(BuildRefresh.IsCurrent(stamp: 3, latest: 3, project, project));
        Assert.False(BuildRefresh.IsCurrent(stamp: 2, latest: 3, project, project));
    }

    [Fact]
    public void A_refresh_of_the_project_that_was_closed_under_it_drops_its_result()
    {
        Assert.False(BuildRefresh.IsCurrent(stamp: 1, latest: 1, new ModProject(), new ModProject()));
    }

    // ---- which warnings show ----------------------------------------------------------------------

    [Fact]
    public void A_completed_run_owns_the_warning_list_until_its_result_clears()
    {
        var derivation = new List<string> { "'body' is replaced. Its texture edit is not in this build" };
        var run = new List<string>
            { "tier 'c_vesna01_body_lod1' can't serve the swap (no bone table). Its vanilla draw is left running" };

        Assert.Equal(derivation, BuildWarningSource.Current(null, derivation));
        Assert.Equal(run, BuildWarningSource.Current(run, derivation));
        Assert.Equal(derivation, BuildWarningSource.Current(null, derivation));
    }
}
