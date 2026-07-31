using System.Linq;
using System.Threading.Tasks;
using Remold.App.Views;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Which of a Settings row's readings is allowed to land on its glyph. The rules behind three of the four
/// rows walk the disk, and the form reads them again on every pause in the typing — so several are in flight
/// at once and they finish in whatever order the disk gives them. Only the newest may be shown: a tree-walk
/// over a half-typed path that comes back late must not overwrite the verdict for the text in the box now.
/// <para>The timer that spaces the requests out, and the four that run when the form opens, are live-only.
/// This is the decision underneath them.</para>
/// </summary>
public class SettingsRowGenerationTests
{
    [Fact]
    public void TheOnlyRequestOutstanding_Applies()
    {
        var gen = new RowGeneration();

        Assert.True(gen.Applies(gen.Next()));
    }

    /// <summary>The race the counter exists for: two readings in flight, the older finishing last. What it
    /// read answered for text that has since changed, so it is dropped.</summary>
    [Fact]
    public void AReadingOvertakenByANewerOne_IsDropped()
    {
        var gen = new RowGeneration();

        int first = gen.Next();
        int second = gen.Next();

        Assert.False(gen.Applies(first));
        Assert.True(gen.Applies(second));
    }

    /// <summary>Typing fast: a request per keystroke, and only the last one gets the glyph.</summary>
    [Fact]
    public void OnlyTheLastOfManyRequests_Applies()
    {
        var gen = new RowGeneration();

        var ids = Enumerable.Range(0, 20).Select(_ => gen.Next()).ToArray();

        Assert.All(ids[..^1], id => Assert.False(gen.Applies(id)));
        Assert.True(gen.Applies(ids[^1]));
    }

    /// <summary>A landed reading doesn't consume the request: the row stays answered for that value until
    /// something asks it again, and asking twice about one result gives one answer.</summary>
    [Fact]
    public void AReadingThatApplied_StillAppliesUntilSomethingElseIsAsked()
    {
        var gen = new RowGeneration();
        int id = gen.Next();

        Assert.True(gen.Applies(id));
        Assert.True(gen.Applies(id));

        gen.Next();

        Assert.False(gen.Applies(id));
    }

    /// <summary>No two requests share an id, however many are taken — an id reused would let a stale reading
    /// pass as the current one.</summary>
    [Fact]
    public void EveryRequest_TakesAnIdOfItsOwn()
    {
        var gen = new RowGeneration();

        var ids = Enumerable.Range(0, 500).Select(_ => gen.Next()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>Requests are issued as the form takes them, but results come back off the thread pool. The
    /// count still holds: every id is unique and the last one issued is the one that applies.</summary>
    [Fact]
    public async Task IdsTakenFromManyThreads_AreStillUnique()
    {
        var gen = new RowGeneration();

        var batches = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => Enumerable.Range(0, 250).Select(_ => gen.Next()).ToArray())));

        var ids = batches.SelectMany(b => b).ToArray();
        Assert.Equal(2000, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.True(gen.Applies(ids.Max()));
    }
}
