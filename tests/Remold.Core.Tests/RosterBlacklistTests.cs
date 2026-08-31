using System;
using System.Collections.Generic;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The silent blacklist predicate (<c>Bundles/RosterBlacklist</c>) — the child-NPC content policy.
/// Load-bearing: a failure here means an enforcement point was removed, and that removal is the bug.
/// The resolution surfaces have their own tests (<see cref="SubjectScopeTests"/>: a blacklisted stem
/// builds an EMPTY scope).
/// </summary>
public class RosterBlacklistTests
{
    [Theory]
    [InlineData("c_Helena_body_lod0", true)]
    [InlineData("C_HELENA_BODY", true)]      // case-insensitive
    [InlineData("Helena", true)]
    [InlineData("Melanie", true)]
    [InlineData("c_Melanie_dress_lod0", true)]
    [InlineData("c_Helen_body", false)]      // a different character, not a prefix of this one
    [InlineData("c_Wren_body", false)]
    public void IsBlacklisted_DropsTheBlockedNameSilently(string name, bool expected) =>
        Assert.Equal(expected, RosterBlacklist.IsBlacklisted(name));

    [Fact]
    public void Phase_one_rows_drop_a_blacklisted_db_character_before_game_files_load()
    {
        using var game = new TempGame();
        game.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(1, "Helena", "HelenaNPC", 100, 0),
            TempGame.GunCharRow(2, "Vesna", "VesnaSSR", 200, 0),
        }));
        game.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(100, "HelenaNPC01"),
            TempGame.ModelConfigRow(200, "VesnaSSR01"),
        }));
        game.WriteTable("BattleSummonedData", TempGame.TableBytes(Array.Empty<byte[]>()));

        // This is the exact Phase-1 source the CharacterVm rows are built from. No GameVfs is created:
        // the later game-file/index load can fail without ever having exposed the blocked row.
        var roster = MainWindowViewModel.PhaseOnePlayableRoster(GameDatabase.FromGameDir(game.Root), null);
        var rows = roster.Select(character => new CharacterVm(character, (_, _) => { }, (_, _) => { }))
            .ToList();
        for (int i = 0; i < roster.Count; i++)
            rows[i].Populate(roster[i].Outfits.Select(outfit =>
                (outfit, (System.Collections.Generic.IEnumerable<string>)Array.Empty<string>())),
                lightUp: false);

        Assert.Contains(rows, row => row.Name == "Vesna");
        Assert.DoesNotContain(rows, row => RosterBlacklist.IsBlacklisted(row.Name)
            || row.Outfits.Any(outfit => RosterBlacklist.IsBlacklisted(outfit.Stem)));
    }

    [Fact]
    public void Phase_one_enemy_rows_drop_blacklisted_stems_and_names_before_game_files_load()
    {
        var stems = new Dictionary<long, string>
        {
            [1] = "HelenaNpc",
            [2] = "MaskedChild",
            [3] = "FB_Commander",
        };
        var rows = new (long, long, string?)[]
        {
            (1, 1, "Alias"),
            (2, 2, "Melanie"),
            (3, 3, "Commander"),
        };

        var roster = GameDatabase.BuildEnemyRoster(rows, stems);

        var allowed = Assert.Single(roster);
        Assert.Equal("FB_Commander", allowed.Name);
        Assert.Equal("Commander", allowed.DisplayName);
    }

    [Fact]
    public void Phase_one_weapon_rows_drop_blacklisted_group_keys_and_labels_before_game_files_load()
    {
        var weapons = new[]
        {
            new WeaponRoster.WeaponEntry(1, 2, "Player/HelenaSSR01/HelenaSSR01_WL", 5, "Blocked key"),
            new WeaponRoster.WeaponEntry(2, 2, "Player/MaskedSSR01/MaskedSSR01_WL", 5, "Blocked label"),
            new WeaponRoster.WeaponEntry(3, 2, "Player/VesnaSSR01/VesnaSSR01_WL", 5, "Allowed"),
        };
        var playable = new[]
        {
            Character("Masked", "Melanie"),
            Character("Vesna", "Mirel"),
        };

        var roster = WeaponRoster.BuildWeaponsByCharacter(weapons, playable);

        var allowed = Assert.Single(roster);
        Assert.Equal("Vesna_Weapons", allowed.Name);
        Assert.Equal("Mirel", allowed.DisplayName);
    }

    [Fact]
    public void Phase_one_enemy_dedup_keeps_blacklisted_playable_stems_in_exclusions()
    {
        using var game = new TempGame();
        game.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(1, "Helena", "HelenaNPC", 100, 0),
        }));
        game.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(100, "HelenaNPC01"),
        }));
        game.WriteTable("BattleSummonedData", TempGame.TableBytes(Array.Empty<byte[]>()));

        var visible = MainWindowViewModel.PhaseOnePlayableRoster(
            GameDatabase.FromGameDir(game.Root), null, out var enemyExclusions);

        Assert.DoesNotContain(visible, character => character.Name == "Helena");
        Assert.Contains("HelenaNPC01", enemyExclusions);
    }

    [Fact]
    public void Filtered_phase_one_rosters_add_no_blacklisted_friendly_lookup()
    {
        var enemies = GameDatabase.BuildEnemyRoster(
            new[] { (1L, 1L, (string?)"Helena") },
            new Dictionary<long, string> { [1] = "MaskedChild" });
        var weapons = WeaponRoster.BuildWeaponsByCharacter(
            new[] { new WeaponRoster.WeaponEntry(1, 2, "Player/HelenaSSR01/HelenaSSR01_WL", 5, "Blocked") },
            Array.Empty<Character>());

        var friendly = FriendlyNames.FromRoster(enemies.Concat(weapons).ToList());

        Assert.Equal("MaskedChild", friendly.Character("MaskedChild"));
    }

    private static Character Character(string name, string display) =>
        new(CharId: 1, Name: name, Family: "", GunId: 0, DormModelConfigId: 0,
            Outfits: new List<Outfit>())
        { DisplayName = display };
}
