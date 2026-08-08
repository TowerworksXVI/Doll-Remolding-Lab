using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="TimelineShoes"/>'s address pass: which of the probed addresses are opened at all, and the
/// count that separates "this character plays nothing that hides a node" from "the install would not give
/// up its timelines". Both answer with an empty override list, so only the count tells them apart.
/// </summary>
public class TimelineShoesTests
{
    private const string Dorm = "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline/TestyDorm/";

    private static CatalogIndex Catalog(params (string Address, string Bundle)[] rows) =>
        CatalogIndex.ForTest(rows);

    [Fact]
    public void An_address_the_catalog_does_not_carry_is_no_failure_at_all()
    {
        // The templates describe the whole roster's clip vocabulary, so most formatted names belong to no
        // character. Those are skipped in silence and must never read as an install that won't open.
        var shoes = TimelineShoes.Read(Catalog(), _ => null,
            new[] { Dorm + "c_TestyDorm_Cloth_Idle.prefab" }, out int unreadable);

        Assert.Empty(shoes);
        Assert.Equal(0, unreadable);
    }

    [Fact]
    public void A_resolved_bundle_that_will_not_open_is_counted()
    {
        // THE LOCKED-INSTALL SHAPE, which is what the game running looks like from here: the catalog knows
        // the address, and the bytes never arrive. Same empty list as the case above, different count.
        var catalog = Catalog((Dorm + "c_TestyDorm_Cloth_Idle.prefab", "cloth.bundle"));

        var shoes = TimelineShoes.Read(catalog, _ => null,
            new[] { Dorm + "c_TestyDorm_Cloth_Idle.prefab" }, out int unreadable);

        Assert.Empty(shoes);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void A_reader_that_throws_counts_the_same_as_one_that_answers_nothing()
    {
        var catalog = Catalog((Dorm + "c_TestyDorm_Cloth_Idle.prefab", "cloth.bundle"));

        var shoes = TimelineShoes.Read(catalog, _ => throw new InvalidOperationException("locked"),
            new[] { Dorm + "c_TestyDorm_Cloth_Idle.prefab" }, out int unreadable);

        Assert.Empty(shoes);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void Bytes_that_are_not_a_bundle_are_counted_too()
    {
        // Deobfuscation answering with something unparseable is still an install that gave up nothing.
        var catalog = Catalog((Dorm + "c_TestyDorm_Cloth_Idle.prefab", "cloth.bundle"));

        var shoes = TimelineShoes.Read(catalog, _ => new byte[] { 1, 2, 3, 4 },
            new[] { Dorm + "c_TestyDorm_Cloth_Idle.prefab" }, out int unreadable);

        Assert.Empty(shoes);
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void Several_clips_sharing_one_bundle_are_read_and_counted_once()
    {
        // A character's clips commonly share a bundle. The read is deduped by bundle, so the count is of
        // BUNDLES that would not open — not of addresses, which would multiply one locked file into many.
        var addresses = new[]
        {
            Dorm + "c_TestyDorm_Cloth_Before.prefab",
            Dorm + "c_TestyDorm_Cloth_Idle.prefab",
            Dorm + "c_TestyDorm_Cloth_After.prefab",
        };
        var catalog = Catalog(addresses.Select(a => (a, "cloth.bundle")).ToArray());
        var asked = new List<string>();

        var shoes = TimelineShoes.Read(catalog, b => { asked.Add(b); return null; }, addresses,
            out int unreadable);

        Assert.Empty(shoes);
        Assert.Equal(1, unreadable);
        Assert.Equal(new[] { "cloth.bundle" }, asked);
    }

    [Fact]
    public void Two_separate_bundles_that_will_not_open_are_counted_separately()
    {
        var a1 = Dorm + "c_TestyDorm_Cloth_Idle.prefab";
        var a2 = Dorm + "c_Testy_Bedroom_01_Idle.prefab";
        var catalog = Catalog((a1, "cloth.bundle"), (a2, "bedroom.bundle"));

        TimelineShoes.Read(catalog, _ => null, new[] { a1, a2 }, out int unreadable);

        Assert.Equal(2, unreadable);
    }
}
