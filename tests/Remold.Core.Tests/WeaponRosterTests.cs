using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

public class WeaponRosterTests
{
    private static Character Char(string name, long gunId, string? display = null) =>
        new(CharId: 1, Name: name, Family: name + "SSR", GunId: gunId, DormModelConfigId: 0,
            Outfits: new List<Outfit>())
        { DisplayName = display };

    [Fact]
    public void Weapons_read_skips_pathless_rows_and_resolves_names()
    {
        using var g = new TempGame();
        g.WriteTable("GunWeaponData", TempGame.TableBytes(new[]
        {
            TempGame.GunWeaponRow(10131, 2, "Player/VesnaR01/VesnaR01_WL", 3, nameTextId: 501, 2012, 1012),
            TempGame.GunWeaponRow(10133, 2, null, 5),                       // no model — skipped
            TempGame.GunWeaponRow(10004, 4, "Player/BpARssr001/BpARssr001_WL", 5),
        }));
        g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(501, "Quiet Argument"),
        }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));
        var loc = LocalizationDb.Load(db);

        var weapons = WeaponRoster.ReadWeapons(db, loc);

        Assert.Equal(2, weapons.Count);
        var vesna = weapons.Single(w => w.WeaponId == 10131);
        Assert.Equal(2, vesna.Type);
        Assert.Equal(3, vesna.Rarity);
        Assert.Equal("Quiet Argument", vesna.DisplayName);
        Assert.Null(weapons.Single(w => w.WeaponId == 10004).DisplayName);
    }

    [Fact]
    public void Skins_read_keeps_only_standalone_rows_and_types_them_by_membership()
    {
        using var g = new TempGame();
        g.WriteIntlTable("WeaponSkinData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponSkinRow(3111045, 5, ownWeaponId: 10643),             // own appearance — not a skin
            TempGame.WeaponSkinRow(4131074, 4, modelPath: "Player/HR_SR_Cla_A001/HR_SR_Cla_A001_WL", nameTextId: 601),
            TempGame.WeaponSkinRow(4131099, 3, modelPath: "Player/AR_R_Cla_B001/AR_R_Cla_B001_WL"),  // in no type list
        }));
        g.WriteIntlTable("WeaponSkinByTypeData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponSkinByTypeRow(1, 3111045, 4131074),
        }));
        g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(601, "Skin - Dry Season"),
        }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));
        var loc = LocalizationDb.Load(db);

        var skins = WeaponRoster.ReadSkins(db, loc);

        Assert.Equal(2, skins.Count);
        var hr = skins.Single(s => s.SkinId == 4131074);
        Assert.Equal(1, hr.Type);
        Assert.Equal(4, hr.Rarity);
        Assert.Equal("Skin - Dry Season", hr.DisplayName);
        Assert.Equal(0, skins.Single(s => s.SkinId == 4131099).Type);   // unlisted skin still shows
    }

    [Fact]
    public void Weapons_group_by_stem_join_not_id_arithmetic()
    {
        var roster = new[] { Char("Vesna", 1071, display: "Mirel"), Char("Neris", 2) };
        var weapons = new[]
        {
            // launch-block shape: sequential ids unrelated to any GunId, stems still prefix the owner
            new WeaponRoster.WeaponEntry(10131, 2, "Player/VesnaR01/VesnaR01_WL", 3, "W1"),
            new WeaponRoster.WeaponEntry(10132, 2, "Player/VesnaSR01/VesnaSR01_WL", 4, "W2"),
            new WeaponRoster.WeaponEntry(10133, 2, "Player/VesnaSSR01/VesnaSSR01_WL", 5, "W3"),
            new WeaponRoster.WeaponEntry(10004, 4, "Player/BpARssr001/BpARssr001_WL", 5, "Bp"),
            new WeaponRoster.WeaponEntry(10500, 6, "Player/StraySSR01/StraySSR01_WL", 5, "S"),
        };

        var groups = WeaponRoster.BuildWeaponsByCharacter(weapons, roster);

        var vesna = Assert.Single(groups, c => c.Name == "Vesna_Weapons");
        Assert.Equal("Mirel", vesna.DisplayName);
        Assert.Equal(new[] { "VesnaR01_WL", "VesnaSR01_WL", "VesnaSSR01_WL" },
            vesna.Outfits.Select(o => o.Stem));   // rarity order: R, SR, SSR
        Assert.Equal(new int?[] { 3, 4, 5 }, vesna.Outfits.Select(o => o.WeaponRarity));

        var bp = Assert.Single(groups, c => c.Name == WeaponRoster.BattlePassGroup);
        Assert.Equal("Battle Pass", bp.DisplayName);
        Assert.Equal("BpARssr001_WL", Assert.Single(bp.Outfits).Stem);

        // an ownerless stem still shows, grouped under its bare core
        var stray = Assert.Single(groups, c => c.Name == "Stray_Weapons");
        Assert.Equal("Stray", stray.DisplayName);
    }

    [Fact]
    public void A_weapon_outfit_routes_to_its_prefab_and_owns_the_cw_prefix()
    {
        var weapons = new[]
        {
            new WeaponRoster.WeaponEntry(10133, 2, "Player/VesnaSSR01/VesnaSSR01_WL", 5, "Loud Reply"),
        };

        var o = Assert.Single(Assert.Single(
            WeaponRoster.BuildWeaponsByCharacter(weapons, new[] { Char("Vesna", 1071) })).Outfits);

        Assert.Equal("VesnaSSR01_WL", o.Stem);
        Assert.Equal(OutfitKind.Other, o.Kind);
        Assert.Equal("Loud Reply", o.DisplayName);
        Assert.Equal("cw_VesnaSSR01_", o.MeshPrefix);
        Assert.True(o.PartsPoolAlone);
        Assert.NotNull(o.Route);
        Assert.Equal("Assets/ConfigPrefab/Weapon/Player/VesnaSSR01/VesnaSSR01_WL.prefab", o.Route!.Address);
        Assert.Equal("VesnaSSR01_WL", o.Route!.RootName);
    }

    [Fact]
    public void Skins_group_by_type_token()
    {
        var skins = new[]
        {
            new WeaponRoster.WeaponSkinEntry(4131074, 1, "Player/HR_SR_Cla_A001/HR_SR_Cla_A001_WL", 4, "A"),
            new WeaponRoster.WeaponSkinEntry(4131075, 1, "Player/HR_SSR_Cla_S002/HR_SSR_Cla_S002_WL", 5, "B"),
            new WeaponRoster.WeaponSkinEntry(5131074, 7, "Player/Knife_SR_Cla_K001/Knife_SR_Cla_K001_WL", 4, "K"),
        };

        var groups = WeaponRoster.BuildSkinsByType(skins);

        var hg = Assert.Single(groups, c => c.Name == "WeaponSkins_HG");
        Assert.Equal("Skins · HG", hg.DisplayName);
        Assert.Equal(2, hg.Outfits.Count);
        Assert.Equal("HR_SR_Cla_A001_WL", hg.Outfits[0].Stem);          // SR before SSR
        Assert.Equal("cw_HR_SR_Cla_A001_", hg.Outfits[0].MeshPrefix);
        Assert.Equal(-4131074, hg.Outfits[0].ModelConfigId);            // negated out of the config id space
        var melee = Assert.Single(groups, c => c.Name == "WeaponSkins_Melee");
        Assert.Equal("Skins · Melee", melee.DisplayName);
    }

    [Fact]
    public void Attachments_collapse_tier_rows_to_one_family_and_skip_pathless_skins()
    {
        using var g = new TempGame();
        g.WriteTable("WeaponModCodGroupData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponModCodRow(1011111, "Sights/WeaponPart_1_gungb_1"),
            TempGame.WeaponModCodRow(1011121, "Sights/WeaponPart_1_gungb_2"),
            TempGame.WeaponModCodRow(1011131, "Sights/WeaponPart_1_gungb_3"),
            TempGame.WeaponModCodRow(2021621, "Silencers/WeaponPart_2_brake_1"),
        }));
        g.WriteIntlTable("WeaponModSkinData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponModSkinRow(7231035, 5, "Sights/Scope_SSR_Set005_3", nameTextId: 701),
            // path-less: names the base family it references (partId 1011, family 1, tier 3)
            TempGame.WeaponModSkinRow(6311054, 4, nameTextId: 702, refId: 10111353),
        }));
        g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(701, "Skin - Glittering Tide Sight"),
            TempGame.LangRow(702, "Skin - Holo Sight Mk.I"),
        }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));
        var loc = LocalizationDb.Load(db);

        var entries = WeaponRoster.ReadAttachments(db, loc);

        // three gungb tier rows collapse to ONE family carried by the highest tier; the path-less
        // skin row lists no subject of its own — it NAMES the family instead, bare of the prefix
        Assert.Equal(3, entries.Count);
        var gungb = Assert.Single(entries, e => e.ModelPath.Contains("gungb"));
        Assert.Equal("Sights/WeaponPart_1_gungb_3", gungb.ModelPath);
        Assert.Equal(1011131, gungb.Id);
        Assert.False(gungb.IsSkin);
        Assert.Equal("Holo Sight Mk.I", gungb.DisplayName);
        Assert.Null(Assert.Single(entries, e => e.ModelPath.Contains("brake")).DisplayName);
        var skin = Assert.Single(entries, e => e.IsSkin);
        Assert.Equal("Skin - Glittering Tide Sight", skin.DisplayName);
        Assert.Equal(5, skin.Rarity);
    }

    [Fact]
    public void Attachments_group_by_category_and_route_to_the_attachments_address()
    {
        var entries = new[]
        {
            new WeaponRoster.AttachmentEntry(1011131, "Sights/WeaponPart_1_gungb_3", 0, null, IsSkin: false),
            new WeaponRoster.AttachmentEntry(7231035, "Sights/Scope_SSR_Set005_3", 5, "Glitter", IsSkin: true),
            new WeaponRoster.AttachmentEntry(2021621, "Silencers/WeaponPart_2_brake_1", 0, null, IsSkin: false),
        };

        var groups = WeaponRoster.BuildAttachmentsBySlot(entries);

        Assert.Equal(2, groups.Count);
        var sights = Assert.Single(groups, c => c.Name == "WeaponParts_Sights");
        Assert.Equal("Parts · Sights", sights.DisplayName);
        // base family first, then the skin
        Assert.Equal(new[] { "WeaponPart_1_gungb_3", "Scope_SSR_Set005_3" },
            sights.Outfits.Select(o => o.Stem));
        var family = sights.Outfits[0];
        Assert.Equal("cw_WeaponPart_1_gungb_", family.MeshPrefix);   // meshes drop the tier suffix
        Assert.Equal(-1011131, family.ModelConfigId);
        Assert.Null(family.WeaponRarity);
        Assert.True(family.PartsPoolAlone);
        Assert.Equal(
            "Assets/ConfigPrefab/Weapon/Attachments/Sights/WeaponPart_1_gungb_3/WeaponPart_1_gungb_3.prefab",
            family.Route!.Address);
        Assert.Equal("WeaponPart_1_gungb_3", family.Route!.RootName);
        Assert.Equal(5, sights.Outfits[1].WeaponRarity);
    }

    [Theory]
    [InlineData("WeaponPart_1_gungb_3", "WeaponPart_1_gungb", 3)]
    [InlineData("Scope_SSR_Set005_3", "Scope_SSR_Set005", 3)]
    [InlineData("WeaponPart_3_hangsp1_3", "WeaponPart_3_hangsp1", 3)]
    [InlineData("foregrip_all_zenit", "foregrip_all_zenit", 0)]
    public void Tier_split_strips_only_a_trailing_underscore_number(string name, string stripped, int tier) =>
        Assert.Equal((stripped, tier), WeaponRoster.SplitTier(name));

    [Theory]
    [InlineData(1, "HG")]
    [InlineData(4, "AR")]
    [InlineData(7, "Melee")]
    [InlineData(9, "Type 9")]
    public void Type_tokens_follow_the_measured_vocabulary(int code, string token) =>
        Assert.Equal(token, WeaponType.Token(code));

    [Theory]
    [InlineData("Player/VesnaSSR01/VesnaSSR01_WL", "VesnaSSR01")]
    [InlineData("Player/None/BpKnifessr001_WL", "BpKnifessr001")]
    public void Folder_stem_is_the_leaf_minus_WL(string path, string stem) =>
        Assert.Equal(stem, WeaponRoster.FolderStem(path));

    [Theory]
    [InlineData("VesnaSSR01", "Vesna")]
    [InlineData("VesnaSR01", "Vesna")]
    [InlineData("VesnaR01", "Vesna")]
    [InlineData("Wren1", "Wren")]     // no tier token: bare core after digit strip
    public void Owner_core_strips_digits_then_one_tier_token(string stem, string core) =>
        Assert.Equal(core, WeaponRoster.OwnerCore(stem));
}
