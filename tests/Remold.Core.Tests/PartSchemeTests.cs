using System.IO;
using System.Linq;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The modular-wardrobe scheme read (<see cref="PartScheme"/>), driven by synthetic tables. Worked
/// example mirrors the shipped shape: outfit VesnaSSR0101 (ModelConfig 107101, clothes 1107101) with a
/// single-token slot, a married-pair slot, and variant membership keyed by resource-id arithmetic.
/// </summary>
public class PartSchemeTests
{
    private static GameDatabase Db(TempGame g,
        byte[][]? typeList = null, byte[][]? groups = null, byte[][]? lists = null, byte[][]? resources = null)
    {
        g.WriteTable("PartsTypeListData", TempGame.TableBytes(typeList ?? System.Array.Empty<byte[]>()));
        g.WriteTable("PartsGroupData", TempGame.TableBytes(groups ?? System.Array.Empty<byte[]>()));
        g.WriteTable("PartsListData", TempGame.TableBytes(lists ?? System.Array.Empty<byte[]>()));
        var root = g.WriteTable("PartsResourceData", TempGame.TableBytes(resources ?? System.Array.Empty<byte[]>()));
        return GameDatabase.FromGameDir(root);
    }

    private static (byte[][] TypeList, byte[][] Groups, byte[][] Lists, byte[][] Resources) VesnaFixture() => (
        new[] { TempGame.PartsTypeListRow(1107101, 710101, 710102) },
        new[]
        {
            TempGame.PartsGroupRow(710101, 71010101, 71010102),
            TempGame.PartsGroupRow(710102, 71010201, 71010202),
        },
        new[]
        {
            TempGame.PartsListRow(71010101, isDefault: true),
            TempGame.PartsListRow(71010102),
            TempGame.PartsListRow(71010201, isDefault: true),
            TempGame.PartsListRow(71010202),
        },
        new[]
        {
            TempGame.PartsResourceRow(710101011, "P1_head"),
            TempGame.PartsResourceRow(710101021, "P2_head"),
            // the married pair, deliberately written out of id order to prove ordering is by resource id
            TempGame.PartsResourceRow(710102012, "P1_cloth2"),
            TempGame.PartsResourceRow(710102011, "P1_cloth1"),
            TempGame.PartsResourceRow(710102021, "P2_cloth1"),
            TempGame.PartsResourceRow(710102022, "P2_cloth2"),
        });

    [Fact]
    public void Load_ResolvesSlotsVariantsAndMarriedTokens_ByResourceIdArithmetic()
    {
        using var g = new TempGame();
        var (t, gr, l, r) = VesnaFixture();
        var scheme = PartScheme.Load(Db(g, t, gr, l, r));

        var slots = scheme.For(107101);
        Assert.NotNull(slots);
        Assert.Equal(2, slots!.Count);

        var head = slots[0];
        Assert.Equal(new[] { "P1_head" }, head.Variants[0].Tokens);
        Assert.True(head.Variants[0].IsDefault);
        Assert.Equal(new[] { "P2_head" }, head.Variants[1].Tokens);
        Assert.False(head.Variants[1].IsDefault);

        // married pair, in resource-id order regardless of table row order
        var cloth = slots[1];
        Assert.Equal(new[] { "P1_cloth1", "P1_cloth2" }, cloth.Variants[0].Tokens);
        Assert.Equal(new[] { "P2_cloth1", "P2_cloth2" }, cloth.Variants[1].Tokens);
    }

    [Fact]
    public void ByStem_JoinsThroughModelStems_AndSkipsAnIdWithoutAStemRow()
    {
        using var g = new TempGame();
        g.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(107101, "VesnaSSR0101"),
        }));
        var (t, gr, l, r) = VesnaFixture();
        // a second modular outfit whose ModelConfig has no stem row: left out, not thrown on
        t = t.Append(TempGame.PartsTypeListRow(1107999, 710101)).ToArray();
        var db = Db(g, t, gr, l, r);

        var byStem = PartScheme.Load(db).ByStem(db);

        Assert.True(byStem.TryGetValue("vesnassr0101", out var slots));   // case-insensitive
        Assert.Equal(2, slots!.Count);
        Assert.Single(byStem);
    }

    [Fact]
    public void For_ReturnsNullForANonModularOutfit()
    {
        using var g = new TempGame();
        var (t, gr, l, r) = VesnaFixture();
        var scheme = PartScheme.Load(Db(g, t, gr, l, r));

        Assert.Null(scheme.For(107102));
        Assert.Equal(new[] { 107101L }, scheme.ModularModelConfigIds.ToArray());
    }

    [Fact]
    public void Load_ToleratesAVariantWithNoResources()
    {
        // a modular controller with an empty variant is a shipped state; it constrains nothing
        using var g = new TempGame();
        var scheme = PartScheme.Load(Db(g,
            new[] { TempGame.PartsTypeListRow(1105204, 520401) },
            new[] { TempGame.PartsGroupRow(520401, 52040101) },
            new[] { TempGame.PartsListRow(52040101, isDefault: true) }));

        var slots = scheme.For(105204);
        Assert.NotNull(slots);
        Assert.Empty(Assert.Single(Assert.Single(slots!).Variants).Tokens);
    }

    [Fact]
    public void Load_FailsLoudly_OnASlotWithNoGroupRow()
    {
        using var g = new TempGame();
        var db = Db(g, new[] { TempGame.PartsTypeListRow(1107101, 710109) });
        var ex = Assert.Throws<InvalidDataException>(() => PartScheme.Load(db));
        Assert.Contains("710109", ex.Message);
    }

    [Fact]
    public void Load_FailsLoudly_OnAVariantWithNoListRow()
    {
        using var g = new TempGame();
        var db = Db(g,
            new[] { TempGame.PartsTypeListRow(1107101, 710101) },
            new[] { TempGame.PartsGroupRow(710101, 71010107) });
        var ex = Assert.Throws<InvalidDataException>(() => PartScheme.Load(db));
        Assert.Contains("71010107", ex.Message);
    }

    [Fact]
    public void Load_SkipsAndReports_ResourceRowsBelongingToNoListedVariant()
    {
        // Releases pre-seed an unshipped outfit's resource rows ahead of its list/group/clothes rows
        // (measured on catalog 26932); no clothes row can reach them, so they cost nothing — the load
        // succeeds, names the skip, and the healthy outfits read whole.
        using var g = new TempGame();
        var (t, gr, l, r) = VesnaFixture();
        r = r.Concat(new[]
        {
            TempGame.PartsResourceRow(999999991, "P1_stray"),
            TempGame.PartsResourceRow(999999992, "P1_stray2"),
            TempGame.PartsResourceRow(888888881, "P2_stray"),
        }).ToArray();
        var notes = new System.Collections.Generic.List<string>();

        var scheme = PartScheme.Load(Db(g, t, gr, l, r), notes.Add);

        var slots = scheme.For(107101);
        Assert.NotNull(slots);
        Assert.Equal(new[] { "P1_cloth1", "P1_cloth2" }, slots![1].Variants[0].Tokens);
        var report = Assert.Single(notes);
        Assert.Contains("3 row(s)", report);
        Assert.DoesNotContain("variant id", report);
        Assert.Contains("skips", report);
    }

    [Fact]
    public void Load_ReportsNothing_WhenEveryResourceRowIsListed()
    {
        using var g = new TempGame();
        var (t, gr, l, r) = VesnaFixture();
        var notes = new System.Collections.Generic.List<string>();
        PartScheme.Load(Db(g, t, gr, l, r), notes.Add);
        Assert.Empty(notes);
    }

    [Fact]
    public void Load_FailsLoudly_WhenATableFileIsMissing()
    {
        using var g = new TempGame();
        // only the first table exists — the scheme decides build eligibility, so no silent empty scheme
        var root = g.WriteTable("PartsTypeListData", TempGame.TableBytes(System.Array.Empty<byte[]>()));
        Assert.Throws<FileNotFoundException>(() => PartScheme.Load(GameDatabase.FromGameDir(root)));
    }
}
