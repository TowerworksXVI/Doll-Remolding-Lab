using Remold.App;
using Remold.Core.Tests.Support;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The first-run terms: one acceptance holds in every UI language, and an edit to the English terms
/// asks again. These change the process's UI language, so they run with the other tests that do.</summary>
[Collection("UiCulture")]
public class AcceptedTermsTests
{
    /// <summary>The stamp of the terms every released build has shown. An acceptance recorded by one of them
    /// stays valid only while the English entry hashes to this.</summary>
    private const string ReleasedTermsStamp = "6E1CBCA9EBDFE297EB1814864C5A7554F21FFE6C64074B11528C59301B9E94ED";

    [Fact]
    public void The_stamped_terms_are_the_english_terms_released_builds_showed()
    {
        Assert.Equal(ReleasedTermsStamp, FirstRunGate.Stamp(App.App.AcceptedTerms));
        Assert.StartsWith("** ", App.App.AcceptedTerms, System.StringComparison.Ordinal);
        Assert.Contains("\n\n** ", App.App.AcceptedTerms, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Catalog.SimplifiedChinese)]
    [InlineData(Catalog.Pseudo)]
    public void An_acceptance_recorded_in_english_holds_in_another_language(string tag)
    {
        using var g = new TempGame();
        var path = g.At("accepted.json");
        try
        {
            FirstRunGate.RecordAccepted(App.App.AcceptedTerms, path);
            Catalog.Apply(tag);

            Assert.True(FirstRunGate.IsAccepted(App.App.AcceptedTerms, path));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void The_stamp_ignores_the_shown_text_and_follows_the_english()
    {
        try
        {
            Catalog.Apply(Catalog.Pseudo);

            Assert.NotEqual(App.App.AcceptedTerms, Strings.AcceptableUseBody);
            Assert.Equal(ReleasedTermsStamp, FirstRunGate.Stamp(App.App.AcceptedTerms));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void An_acceptance_of_different_english_terms_does_not_hold()
    {
        using var g = new TempGame();
        var path = g.At("accepted.json");
        FirstRunGate.RecordAccepted(App.App.AcceptedTerms + " An added sentence.", path);

        Assert.False(FirstRunGate.IsAccepted(App.App.AcceptedTerms, path));
    }
}
