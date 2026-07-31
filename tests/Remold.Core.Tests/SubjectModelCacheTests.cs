using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The session's subject-model memo. Building a model reads the subject's whole scope out of the game's
/// bundles, and both the Edit tree and the Build list rebuild on every entry — so what this memo answers, and
/// what it refuses to answer, is the difference between a step opening at once and re-reading the game.
/// </summary>
[Collection("Dispatcher")]
public class SubjectModelCacheTests
{
    private static SubjectModel Model(string character = "Vesna", string stem = "VesnaSSR01",
        params string[] problems) =>
        new(character, stem, SubjectSource.Prefab, Array.Empty<SubjectPart>(), Skeleton: null, problems);

    [Fact]
    public void TheSecondAskForOneSubject_IsAnsweredWithoutBuildingAgain()
    {
        var cache = new SubjectModelCache();
        int builds = 0;
        SubjectModel Build() { builds++; return Model(); }

        var first = cache.GetOrBuild("Vesna", "VesnaSSR01", Build);
        var second = cache.GetOrBuild("Vesna", "VesnaSSR01", Build);

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    /// <summary>The same case-insensitive identity every other subject comparison in the ledger uses, or the
    /// Build list and the Edit tree miss each other's entries over a capital letter.</summary>
    [Fact]
    public void TheKeyIsCaseInsensitive_LikeEveryOtherSubjectComparison()
    {
        var cache = new SubjectModelCache();
        var built = cache.GetOrBuild("Vesna", "VesnaSSR01", () => Model());

        Assert.Same(built, cache.GetOrBuild("vesna", "vesnassr01", () => Model()));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void TwoSubjects_GetTwoEntries()
    {
        var cache = new SubjectModelCache();
        var a = cache.GetOrBuild("Vesna", "VesnaSSR01", () => Model("Vesna", "VesnaSSR01"));
        var b = cache.GetOrBuild("Vesna", "VesnaSSR02", () => Model("Vesna", "VesnaSSR02"));

        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
    }

    /// <summary>A build that threw is not an answer. Memoizing it would leave the subject unreadable for the
    /// rest of the session over a bundle the game happened to hold open for a moment.</summary>
    [Fact]
    public void ABuildThatThrows_IsNotMemoized_AndTheNextAskRetries()
    {
        var cache = new SubjectModelCache();
        int attempts = 0;
        SubjectModel Flaky()
        {
            attempts++;
            if (attempts == 1) throw new InvalidOperationException("bundle held open");
            return Model();
        }

        Assert.Throws<InvalidOperationException>(() => cache.GetOrBuild("Vesna", "VesnaSSR01", Flaky));
        Assert.Equal(0, cache.Count);

        Assert.NotNull(cache.GetOrBuild("Vesna", "VesnaSSR01", Flaky));
        Assert.Equal(2, attempts);
        Assert.Equal(1, cache.Count);
    }

    /// <summary>Only a re-read of the game can change what a build answers, so the drop is what a rescan
    /// leaves behind: after it the next ask builds again.</summary>
    [Fact]
    public void Clear_MakesTheNextAskBuildAgain()
    {
        var cache = new SubjectModelCache();
        int builds = 0;
        SubjectModel Build() { builds++; return Model(); }
        cache.GetOrBuild("Vesna", "VesnaSSR01", Build);

        cache.Clear();
        Assert.Equal(0, cache.Count);

        cache.GetOrBuild("Vesna", "VesnaSSR01", Build);
        Assert.Equal(2, builds);
    }

    /// <summary>Both panes build off the UI thread, so two of them can reach the same key at once. Whichever
    /// result is stored, every caller from then on has to see THAT one — the models are equivalent, and two
    /// instances behind one key would show as a tree and a change list disagreeing about the same subject.
    /// </summary>
    [Fact]
    public async Task ARaceOnOneKey_SettlesOnOneModelForEveryLaterAsk()
    {
        var cache = new SubjectModelCache();
        var models = Enumerable.Range(0, 8).Select(_ => Model()).ToList();

        await Task.WhenAll(models.Select((_, i) =>
            Task.Run(() => cache.GetOrBuild("Vesna", "VesnaSSR01", () => models[i]))));

        Assert.Equal(1, cache.Count);
        // one of the racers' models won, and it is the only one anyone gets from here on
        var settled = cache.GetOrBuild("Vesna", "VesnaSSR01", () => Model());
        Assert.Contains(settled, models);
        Assert.Same(settled, cache.GetOrBuild("Vesna", "VesnaSSR01", () => Model()));
    }

    /// <summary>The app hands the Edit pane the same memo the Build step uses, so an Edit-to-Build hop reads
    /// no subject twice. One instance is the whole mechanism.</summary>
    [Fact]
    public void TheViewModelExposesOneMemo()
    {
        var vm = new MainWindowViewModel(startLoad: false);

        Assert.NotNull(vm.SubjectModels);
        Assert.Same(vm.SubjectModels, vm.SubjectModels);
        Assert.Equal(0, vm.SubjectModels.Count);
    }
}
