using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The persistent side of the tier material map: a finished map filed under a caller's own key and read
/// back instead of measured again. A build files none and reads none — its tier routing is decided by
/// material identity alone — so these drive the cache directly.
/// </summary>
public class TierMapCacheTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "gf2-tiermap-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>A map with all three shapes an entry can take: a carrier, no carrier, and each of the
    /// rules that decide one.</summary>
    private static readonly TierMapEntry[] Map =
    {
        new(0, 2, TierMapRule.Geometry),
        new(1, null, TierMapRule.Unresolved),
        new(2, 0, TierMapRule.Identity),
    };

    private static string Key => $"{TierMaterialMap.RulesVersion}|part|lod0mesh|tiermesh";

    /// <summary>What was filed is what comes back: every position, its carrier — a carrier of "nowhere"
    /// included — and the rule that decided it. The entries ARE the map, so a caller can go straight from
    /// them to the answers it needs.</summary>
    [Fact]
    public void A_filed_map_reads_back_entry_for_entry()
    {
        TierMapCache.Write(_dir, Key, Map);

        var kept = TierMapCache.Read(_dir, Key);

        Assert.NotNull(kept);
        Assert.Equal(Map, kept!.ToArray());
        Assert.Equal("0→2 geometry, 1→none unresolved, 2→0 identity",
            TierMaterialMap.FromEntries(kept).Diagnostic);
    }

    /// <summary>A key nothing was filed under is a miss, which costs the caller its own measurement and
    /// never somebody else's answer.</summary>
    [Fact]
    public void A_key_nothing_was_filed_under_reads_as_a_miss()
    {
        TierMapCache.Write(_dir, Key, Map);

        Assert.Null(TierMapCache.Read(_dir, "a key nothing was filed under"));
    }

    /// <summary>The key is repeated inside the payload, so an entry whose file name hashed the same as
    /// the key being asked about reads as a miss rather than as that other map.</summary>
    [Fact]
    public void An_entry_filed_under_another_key_reads_as_a_miss()
    {
        TierMapCache.Write(_dir, Key, Map);
        string file = Assert.Single(Directory.GetFiles(_dir, "*.map"));
        var lines = File.ReadAllLines(file);
        File.WriteAllLines(file, new[] { lines[0], "another key entirely" }.Concat(lines.Skip(2)));

        Assert.Null(TierMapCache.Read(_dir, Key));
    }

    /// <summary>A payload in a layout this version does not read — an entry left by an older one, or a
    /// write that did not finish — is a miss as well.</summary>
    [Fact]
    public void A_payload_in_another_layout_reads_as_a_miss()
    {
        TierMapCache.Write(_dir, Key, Map);
        string file = Assert.Single(Directory.GetFiles(_dir, "*.map"));
        File.WriteAllLines(file, new[] { "remold-tiermap-0", Key, "0 2 Geometry" });

        Assert.Null(TierMapCache.Read(_dir, Key));
    }
}
