using System;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a Settings row shows for the verdict it just read. The row's whole resting state is one glyph beside
/// its box, the words on its tooltip — so the glyph has to carry the difference the text used to spell out:
/// a row that can hold the form shut stops, the advisory loader row cautions, and a row that read clean says
/// so. The layout itself is live-only; this is the choice behind it.
/// </summary>
public class SettingsRowVerdictTests
{
    // ---- the glyph a verdict picks ----

    /// <summary>The three rows <see cref="SettingsValidation.SaveCommits"/> names — game folder, projects
    /// folder, CPU limit — refuse the Save, so their refusal reads as a stop.</summary>
    [Fact]
    public void ABadVerdictOnARowThatHoldsTheSave_Stops()
    {
        Assert.Equal(SettingsValidation.GlyphBlocking,
            SettingsValidation.RowGlyph(ok: false, blocking: true));
    }

    /// <summary>The loader row never holds the Save, so its refusal must not read like the ones that do.</summary>
    [Fact]
    public void ABadVerdictOnTheAdvisoryRow_Cautions()
    {
        Assert.Equal(SettingsValidation.GlyphAdvisory,
            SettingsValidation.RowGlyph(ok: false, blocking: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGoodVerdict_ReadsTheSameOnEveryRow(bool blocking)
    {
        Assert.Equal(SettingsValidation.GlyphOk, SettingsValidation.RowGlyph(ok: true, blocking));
    }

    /// <summary>Three glyphs, three meanings: a stop the modder can't save past, a caution they can, and a
    /// pass. Two that rendered the same would lose the distinction the status lines used to carry in words.</summary>
    [Fact]
    public void TheThreeVerdicts_AreThreeDifferentGlyphs()
    {
        Assert.NotEqual(SettingsValidation.GlyphOk, SettingsValidation.GlyphBlocking);
        Assert.NotEqual(SettingsValidation.GlyphOk, SettingsValidation.GlyphAdvisory);
        Assert.NotEqual(SettingsValidation.GlyphBlocking, SettingsValidation.GlyphAdvisory);
    }

    /// <summary>The app's severity vocabulary is one vocabulary: the glyph a Settings row shows for a pass or
    /// a caution is the glyph the status bar shows for the same reading.</summary>
    [Fact]
    public void TheGlyphs_AreTheOnesTheStatusBarPaints()
    {
        Assert.Equal(SettingsValidation.GlyphOk, StatusFacet.Good("x").Glyph);
        Assert.Equal(SettingsValidation.GlyphAdvisory, StatusFacet.Warn("x").Glyph);
    }

    // ---- what an empty box shows ----
    // The open form answers "what is this set to" for every validated row at once, so a blank box is a
    // reading of its own. Three name the fallback the app will actually use; the fourth is deliberately
    // silent.

    /// <summary>A blank game folder is unfinished, not refused: the form saves with it empty and the main
    /// screen asks for the folder. A stop glyph would mark a row Save doesn't stop on.</summary>
    [Fact]
    public void ABlankGameFolder_Cautions_AndSaysWhoAsksForIt()
    {
        var reading = SettingsValidation.BlankGameRow();

        Assert.Equal(SettingsValidation.GlyphAdvisory, reading.Glyph);
        Assert.Equal("Not set. The app asks for the game folder on the main screen.", reading.Tooltip);
    }

    /// <summary>A blank projects folder is the default library, which is a working answer — so it reads as
    /// one, and names the folder rather than leaving the modder to know where the default is.</summary>
    [Fact]
    public void ABlankProjectsFolder_PassesAndNamesTheLibraryStandingIn()
    {
        var reading = SettingsValidation.BlankProjectsRow(@"D:\Mods\Library");

        Assert.Equal(SettingsValidation.GlyphOk, reading.Glyph);
        Assert.Equal(@"Using the default library: D:\Mods\Library.", reading.Tooltip);
    }

    /// <summary>A blank CPU limit is every core, named with the number it comes to on this machine.</summary>
    [Fact]
    public void ABlankCpuLimit_PassesAndNamesTheCoreCount()
    {
        var reading = SettingsValidation.BlankCpuRow(12);

        Assert.Equal(SettingsValidation.GlyphOk, reading.Glyph);
        Assert.Equal("Using every core (12).", reading.Tooltip);
    }

    /// <summary>The one row that shows nothing when it is blank. The loader is optional — nothing on this
    /// form wants one — and the Build pane is where an unset loader is worth saying something about.</summary>
    [Fact]
    public void ABlankLoader_ShowsNothingAtAll()
    {
        var reading = SettingsValidation.BlankLoaderRow();

        Assert.Equal("", reading.Glyph);
        Assert.Null(reading.Tooltip);
    }

    /// <summary>Blank is a deliberate reading on every row, not an unanswered one: three show a glyph and the
    /// loader's silence is the exception, so a row left blank can't be a row nobody wired up.</summary>
    [Fact]
    public void EveryBlankRowExceptTheLoader_ShowsAGlyph()
    {
        Assert.NotEqual("", SettingsValidation.BlankGameRow().Glyph);
        Assert.NotEqual("", SettingsValidation.BlankProjectsRow("lib").Glyph);
        Assert.NotEqual("", SettingsValidation.BlankCpuRow(4).Glyph);
        Assert.Equal("", SettingsValidation.BlankLoaderRow().Glyph);
    }

    // ---- the CPU row's whole reading ----

    /// <summary>A cap that parses is shown, not left silent: on a form that answers for every row, a row
    /// saying nothing reads as a row nobody looked at.</summary>
    [Fact]
    public void ACpuLimitThatParses_ReadsAsAPass_NamingTheCap()
    {
        var reading = SettingsValidation.CpuRow("6", processorCount: 12);

        Assert.Equal(SettingsValidation.GlyphOk, reading.Glyph);
        Assert.Equal("Capped at 6 cores.", reading.Tooltip);
    }

    [Fact]
    public void ABlankCpuBox_ReadsAsTheAllCoresFallback()
    {
        Assert.Equal(SettingsValidation.BlankCpuRow(12), SettingsValidation.CpuRow("", processorCount: 12));
        Assert.Equal(SettingsValidation.BlankCpuRow(12), SettingsValidation.CpuRow(null, processorCount: 12));
        Assert.Equal(SettingsValidation.BlankCpuRow(12), SettingsValidation.CpuRow("  ", processorCount: 12));
    }

    /// <summary>The CPU row holds the Save shut, so what it refuses reads as a stop — and says the same thing
    /// the rule itself says.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("four")]
    [InlineData("2.5")]
    public void ACpuLimitThatIsNotACount_Stops(string typed)
    {
        var reading = SettingsValidation.CpuRow(typed, processorCount: 12);

        Assert.Equal(SettingsValidation.GlyphBlocking, reading.Glyph);
        Assert.Equal(SettingsValidation.CpuNotANumber, reading.Tooltip);
    }

    // ---- and the words on the tooltip ----

    /// <summary>The loader verdicts are read by a modder who has never heard of a texture hook: each says
    /// what happens to their mods, and every refusal names what to do about it.</summary>
    [Fact]
    public void TheLoaderVerdicts_SayWhatHappensToTheMods_NotHowTheHostWorks()
    {
        Assert.DoesNotContain("hook", SettingsValidation.LoaderReady);
        Assert.DoesNotContain("hook", SettingsValidation.LoaderNoHook);
        Assert.Contains("show up in game", SettingsValidation.LoaderReady);
        Assert.Contains("show up in game", SettingsValidation.LoaderNoHook);
        // the ini is a file the modder has no reason to have heard of, so its absence is reported as the same
        // outcome its siblings name rather than as a missing filename
        Assert.Contains("show up in game", SettingsValidation.LoaderNoIni);
        Assert.DoesNotContain(MigotoIni.FileName, SettingsValidation.LoaderNoIni);
    }

    /// <summary>Every loader refusal is two sentences: what is wrong, then what to pick. A verdict that only
    /// diagnoses leaves the modder on a row they can't act on.</summary>
    [Theory]
    [InlineData(SettingsValidation.LoaderNotThere)]
    [InlineData(SettingsValidation.LoaderNoIni)]
    [InlineData(SettingsValidation.LoaderNoHook)]
    public void EveryLoaderRefusal_EndsInSomethingToDo(string text)
    {
        var remedy = text[(text.IndexOf(". ", StringComparison.Ordinal) + 2)..];
        Assert.True(remedy.StartsWith("Select ", StringComparison.Ordinal)
            || remedy.StartsWith("Use ", StringComparison.Ordinal),
            $"no remedy sentence in: {text}");
    }

    /// <summary>ONE home for the diagnosis. The Settings row and the Build pane's Install gate refuse the
    /// same host in the same words — a modder who reads it on one surface and then the other must not think
    /// they are two different problems.
    /// <para>The Settings row adds what the reading means THERE: the form still commits. The suffix stays on
    /// this side, because on the Build pane it would be false — a hookless host is exactly what turns Install
    /// off.</para></summary>
    [Fact]
    public void TheSettingsRowAndTheInstallGate_RefuseAHooklessHostInTheSameWords()
    {
        Assert.StartsWith(InstallGate.NoTextureHook, SettingsValidation.LoaderNoHook);
        Assert.DoesNotContain(SettingsValidation.LoaderStillSaveable, InstallGate.NoTextureHook);
    }

    /// <summary>The one row whose refusal the form saves past says so, on the row itself: a caution the
    /// modder can't act on is a caution they go hunting for the Save button behind.</summary>
    [Fact]
    public void TheLoaderRefusal_SaysTheFormStillSaves()
    {
        Assert.EndsWith(SettingsValidation.LoaderStillSaveable, SettingsValidation.LoaderNoHook);
        Assert.Contains("still save", SettingsValidation.LoaderStillSaveable);
    }

    // ---- and the form-level line under them all ----

    /// <summary>A Save the rows let through closes the window, so the line has nothing to add. Anything it
    /// said on the attempt before is already gone — the line is wiped at the top of every attempt.</summary>
    [Fact]
    public void ASaveThatCommits_LeavesTheFormLineWithNothingToSay()
    {
        Assert.Null(SettingsValidation.SaveStatusLine(
            gamePathOk: true, projectsFolderOk: true, cpuLimitOk: true));
    }

    /// <summary>The one thing a refused Save changes on screen is the marked rows. Without this line the
    /// click is pixel-identical to a click that did nothing — the glyph the row was already wearing is the
    /// glyph it still wears.</summary>
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ARefusedSave_SaysSoOnTheFormLine(bool game, bool library, bool cpu)
    {
        Assert.Equal(SettingsValidation.SaveRefused,
            SettingsValidation.SaveStatusLine(game, library, cpu));
    }

    /// <summary>The line points at the rows by the glyph they are wearing, so the sentence and the rows
    /// can't come to name two different marks.</summary>
    [Fact]
    public void TheRefusalLine_NamesTheGlyphTheHeldRowsWear()
    {
        Assert.Contains(SettingsValidation.GlyphBlocking, SettingsValidation.SaveRefused);
        Assert.DoesNotContain(SettingsValidation.GlyphAdvisory, SettingsValidation.SaveRefused);
    }
}
