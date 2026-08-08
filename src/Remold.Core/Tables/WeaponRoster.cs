using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// Reads the weapon tables into the Weapons-tab rosters: weapons grouped by their owner character,
/// and standalone weapon skins grouped by weapon type. Weapons are separate game objects equippable
/// across characters, so every subject here is its own single-part world — the rosters never join
/// the sharing population, and their subjects' parts never pool with anything else.
/// </summary>
public static class WeaponRoster
{
    private const int GW_Id = 1;        // GunWeaponData: weapon id
    private const int GW_Name = 2;      //                display name (wrapped text-id)
    private const int GW_Type = 4;      //                weapon type, the 1–7 vocabulary of WeaponType
    private const int GW_ModelPath = 9; //                model path (Player/<stem>/<stem>_WL)
    private const int GW_Rarity = 11;   //                rarity (3=R, 4=SR, 5=SSR)

    private const int WS_Id = 1;        // WeaponSkinData (intl): skin id
    private const int WS_Name = 3;      //                display name (wrapped text-id)
    private const int WS_Rarity = 11;   //                rarity
    private const int WS_OwnWeapon = 13;//                set only on a weapon's own-appearance row
    private const int WS_ModelPath = 14;//                model path, set only on standalone skins

    private const int ST_Type = 1;      // WeaponSkinByTypeData (intl): weapon type
    private const int ST_SkinIds = 2;   //                packed id list: skins applicable to the type

    private const int CG_Id = 1;        // WeaponModCodGroupData: <partId><family><tier>1
    private const int CG_ModelPath = 2; //                model path (<Category>/WeaponPart_<slot>_<family>_<tier>)

    private const int MS_Id = 1;        // WeaponModSkinData (intl): attachment-skin id
    private const int MS_Name = 3;      //                display name (wrapped text-id)
    private const int MS_Rarity = 11;   //                rarity
    private const int MS_Ref = 13;      //                the base model this row appears as: <partId><family><marker>53
    private const int MS_ModelPath = 14;//                model path; absent = "use the base model", not a subject

    /// <summary>One weapon the roster shows: a <c>GunWeaponData</c> row with a model.</summary>
    public sealed record WeaponEntry(
        long WeaponId, int Type, string ModelPath, int Rarity, string? DisplayName);

    /// <summary>One standalone weapon skin: its own model, applicable to every weapon of
    /// <see cref="Type"/> (type-scoped by <c>WeaponSkinByTypeData</c>, never per-weapon). A weapon's
    /// own-appearance rows carry no model and are the weapons themselves, not skins.</summary>
    public sealed record WeaponSkinEntry(
        long SkinId, int Type, string ModelPath, int Rarity, string? DisplayName);

    /// <summary>One generic attachment model: a base family (one entry per distinct geometry — tier
    /// rows overwhelmingly share one mesh, so tiers collapse to the highest row and never list as
    /// separate subjects) or an attachment skin (its own model, named and rarity-badged).</summary>
    public sealed record AttachmentEntry(
        long Id, string ModelPath, int Rarity, string? DisplayName, bool IsSkin);

    /// <summary>Every <c>GunWeaponData</c> row that names a model.</summary>
    public static List<WeaponEntry> ReadWeapons(GameDatabase db, LocalizationDb? loc)
    {
        var list = new List<WeaponEntry>();
        foreach (var row in TableFile.ReadRows(db.TablePath("GunWeaponData")))
        {
            var id = row.Num(GW_Id);
            var path = row.Str(GW_ModelPath);
            if (id is null || string.IsNullOrEmpty(path)) continue;
            list.Add(new WeaponEntry(
                WeaponId: (long)id,
                Type: (int)(row.Num(GW_Type) ?? 0),
                ModelPath: path!,
                Rarity: (int)(row.Num(GW_Rarity) ?? 0),
                DisplayName: loc?.Resolve(row, GW_Name)));
        }
        return list;
    }

    /// <summary>Every standalone skin (a <c>WeaponSkinData</c> row with its own model path), typed by
    /// <c>WeaponSkinByTypeData</c> membership. A skin no type list names keeps type 0 and still
    /// shows — the roster shows what ships, it doesn't referee the tables.</summary>
    public static List<WeaponSkinEntry> ReadSkins(GameDatabase db, LocalizationDb? loc)
    {
        var typeOf = new Dictionary<long, int>();
        foreach (var row in TableFile.ReadRows(db.TablePath("WeaponSkinByTypeData", intl: true)))
        {
            var type = row.Num(ST_Type);
            if (type is null) continue;
            foreach (var skinId in row.PackedVarints(ST_SkinIds))
                typeOf[(long)skinId] = (int)type;
        }

        var list = new List<WeaponSkinEntry>();
        foreach (var row in TableFile.ReadRows(db.TablePath("WeaponSkinData", intl: true)))
        {
            var id = row.Num(WS_Id);
            var path = row.Str(WS_ModelPath);
            if (id is null || string.IsNullOrEmpty(path)) continue;
            if (row.Num(WS_OwnWeapon) is not null) continue;   // a weapon's own appearance, not a skin
            list.Add(new WeaponSkinEntry(
                SkinId: (long)id,
                Type: typeOf.GetValueOrDefault((long)id),
                ModelPath: path!,
                Rarity: (int)(row.Num(WS_Rarity) ?? 0),
                DisplayName: loc?.Resolve(row, WS_Name)));
        }
        return list;
    }

    /// <summary>The generic attachment models: base families from <c>WeaponModCodGroupData</c> —
    /// one entry per tier-stripped model path, carried by its highest-tier row (tier variants
    /// overwhelmingly share one mesh, and the low spring-family tiers ship no prefab at all) — and
    /// every <c>WeaponModSkinData</c> row with a model path. A path-less skin row means "use the
    /// base model" and produces nothing.</summary>
    public static List<AttachmentEntry> ReadAttachments(GameDatabase db, LocalizationDb? loc)
    {
        // every CodGroup row, id → path (for the naming join), and per tier-stripped path the
        // max-tier row that carries the family
        var pathById = new Dictionary<long, string>();
        var families = new Dictionary<string, (int Tier, long Id, string Path)>(StringComparer.Ordinal);
        foreach (var row in TableFile.ReadRows(db.TablePath("WeaponModCodGroupData")))
        {
            var id = row.Num(CG_Id);
            var path = row.Str(CG_ModelPath);
            if (id is null || string.IsNullOrEmpty(path)) continue;
            pathById[(long)id] = path!;
            var (stripped, tier) = SplitTier(path!);
            if (!families.TryGetValue(stripped, out var have) || tier > have.Tier)
                families[stripped] = (tier, (long)id, path!);
        }

        // A path-less skin row is a base model's own appearance entry, so its localized name IS the
        // base family's name. Its ref decomposes as <partId><family><marker>53 — the marker digit
        // varies by slot and is NOT the tier — so the join reads the family digit and lands on that
        // family's base row at whichever tier ships, whose path keys the name.
        var familyNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var skinRows = TableFile.ReadRows(db.TablePath("WeaponModSkinData", intl: true));
        foreach (var row in skinRows)
        {
            if (row.Str(MS_ModelPath) is not null) continue;
            if (row.Num(MS_Ref) is not { } refId) continue;
            if (loc?.Resolve(row, MS_Name) is not { } name) continue;
            long body = (long)refId / 100;          // <partId><family><marker>
            long family = body / 10 % 10;
            long partId = body / 100;
            for (int tier = 3; tier >= 1; tier--)
            {
                if (!pathById.TryGetValue(partId * 1000 + family * 100 + tier * 10 + 1, out var path))
                    continue;
                // the row is an appearance entry, so the game prefixes it as one; the family shows bare
                const string skinPrefix = "Skin - ";
                var bare = name.StartsWith(skinPrefix, StringComparison.Ordinal)
                    ? name[skinPrefix.Length..] : name;
                familyNames.TryAdd(SplitTier(path).Stripped, bare);
                break;
            }
        }

        var list = families
            .OrderBy(f => f.Value.Path, StringComparer.Ordinal)
            .Select(f => new AttachmentEntry(f.Value.Id, f.Value.Path, Rarity: 0,
                DisplayName: familyNames.GetValueOrDefault(f.Key), IsSkin: false))
            .ToList();

        foreach (var row in skinRows)
        {
            var id = row.Num(MS_Id);
            var path = row.Str(MS_ModelPath);
            if (id is null || string.IsNullOrEmpty(path)) continue;
            list.Add(new AttachmentEntry((long)id, path!,
                Rarity: (int)(row.Num(MS_Rarity) ?? 0),
                DisplayName: loc?.Resolve(row, MS_Name),
                IsSkin: true));
        }
        return list;
    }

    /// <summary>The attachments branch: one group per category folder (the slot label the game's own
    /// paths carry — Sights, Silencers, Lights, Grip, Stool, Blades), base families first, then that
    /// slot's skins by rarity.</summary>
    public static List<Character> BuildAttachmentsBySlot(IReadOnlyList<AttachmentEntry> attachments)
    {
        var groups = new Dictionary<string, (string Display, List<Outfit> Outfits)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in attachments
                     .OrderBy(a => a.IsSkin ? 1 : 0)
                     .ThenBy(a => a.IsSkin ? a.Rarity : 0)
                     .ThenBy(a => a.Id))
        {
            var slash = a.ModelPath.IndexOf('/');
            var category = slash > 0 ? a.ModelPath[..slash] : "Other";
            var key = PartGroupPrefix + category;
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = ($"Parts · {category}", new List<Outfit>());

            var basename = a.ModelPath[(a.ModelPath.LastIndexOf('/') + 1)..];
            g.Outfits.Add(new Outfit(-a.Id, basename, OutfitKind.Other)
            {
                DisplayName = a.DisplayName,
                // the shipped meshes drop the tier suffix (cw_WeaponPart_1_gungb_WL_lod0 under
                // WeaponPart_1_gungb_3), so ownership keys on the tier-stripped family
                MeshPrefixOverride = $"cw_{SplitTier(basename).Stripped}_",
                WeaponRarity = a.IsSkin && a.Rarity > 0 ? a.Rarity : null,
                PartsPoolAlone = true,
                Route = SubjectRoute.Addressable(
                    $"Assets/ConfigPrefab/Weapon/Attachments/{a.ModelPath}/{basename}.prefab", basename),
            });
        }
        return SortedGroups(groups);
    }

    /// <summary>Grouping-key prefix for the per-slot attachment groups.</summary>
    public const string PartGroupPrefix = "WeaponParts_";

    /// <summary>A name minus its trailing <c>_&lt;digits&gt;</c> tier suffix, plus the tier it
    /// carried (0 with none).</summary>
    internal static (string Stripped, int Tier) SplitTier(string name)
    {
        int cut = name.Length;
        while (cut > 0 && char.IsAsciiDigit(name[cut - 1])) cut--;
        if (cut == name.Length || cut == 0 || name[cut - 1] != '_') return (name, 0);
        return (name[..(cut - 1)], int.Parse(name[cut..]));
    }

    /// <summary>
    /// The weapons branch: one group per owner character (three tier-badged weapons each), plus one
    /// loose group for the <c>Bp</c> Battle Pass weapons. The owner join is the MODEL STEM — the text
    /// before the tier token prefixes a roster character's internal name — because id arithmetic
    /// breaks on a launch-era block of sequentially numbered rows. A non-Bp weapon whose stem matches
    /// no character groups under its bare stem core, so it still shows.
    /// </summary>
    public static List<Character> BuildWeaponsByCharacter(
        IReadOnlyList<WeaponEntry> weapons, IReadOnlyList<Character> playableRoster)
    {
        var charsByName = new Dictionary<string, Character>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in playableRoster) charsByName[c.Name] = c;

        var groups = new Dictionary<string, (string Display, List<Outfit> Outfits)>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in weapons.OrderBy(w => w.Rarity).ThenBy(w => w.WeaponId))
        {
            var folderStem = FolderStem(w.ModelPath);
            string key, display;
            if (folderStem.StartsWith("Bp", StringComparison.Ordinal))
            {
                key = BattlePassGroup;
                display = "Battle Pass";
            }
            else
            {
                var owner = OwnerCore(folderStem);
                key = owner + GroupSuffix;
                display = charsByName.TryGetValue(owner, out var c) ? c.DisplayName ?? owner : owner;
            }
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = (display, new List<Outfit>());
            g.Outfits.Add(WeaponOutfit(w.WeaponId, folderStem, w.Rarity, w.ModelPath, w.DisplayName));
        }

        return SortedGroups(groups);
    }

    /// <summary>The skins branch: one group per weapon type, each skin a first-class subject — it
    /// applies to any weapon of the type, so it belongs to the type, not to a weapon.</summary>
    public static List<Character> BuildSkinsByType(IReadOnlyList<WeaponSkinEntry> skins)
    {
        var groups = new Dictionary<string, (string Display, List<Outfit> Outfits)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in skins.OrderBy(s => s.Rarity).ThenBy(s => s.SkinId))
        {
            var token = WeaponType.Token(s.Type);
            // Out-of-vocabulary types key on the bare code: the key persists in the selection ledger,
            // where the token's display spacing has no business.
            var key = SkinGroupPrefix + (WeaponType.IsKnown(s.Type) ? token : s.Type.ToString());
            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = ($"Skins · {token}", new List<Outfit>());
            g.Outfits.Add(WeaponOutfit(s.SkinId, FolderStem(s.ModelPath), s.Rarity, s.ModelPath, s.DisplayName));
        }
        return SortedGroups(groups);
    }

    /// <summary>The grouping-key suffix for a character's weapon group — distinct from every internal
    /// character name and enemy stem, because the selection ledger persists these keys beside
    /// theirs.</summary>
    public const string GroupSuffix = "_Weapons";

    /// <summary>The loose group holding the Battle Pass (<c>Bp*</c>) weapons, which prefix no
    /// character's name.</summary>
    public const string BattlePassGroup = "BattlePass" + GroupSuffix;

    /// <summary>Grouping-key prefix for the per-type skin groups.</summary>
    public const string SkinGroupPrefix = "WeaponSkins_";

    private static Outfit WeaponOutfit(long id, string folderStem, int rarity, string modelPath, string? name)
    {
        var root = folderStem + "_WL";
        // NEGATED id: the field is ModelConfigData's id space everywhere else, weapon/skin ids live in
        // their own tables, and the two overlap (a weapon id can equal a summon's config id). Negative
        // ids are the curated-subject convention and collide with nothing id-keyed (roster snapshot).
        return new Outfit(-id, root, OutfitKind.Other)
        {
            DisplayName = name,
            MeshPrefixOverride = $"cw_{folderStem}_",
            WeaponRarity = rarity,
            PartsPoolAlone = true,
            Route = SubjectRoute.Addressable($"Assets/ConfigPrefab/Weapon/{modelPath}.prefab", root),
        };
    }

    /// <summary>The stem folder of a weapon model path (<c>Player/&lt;stem&gt;/&lt;stem&gt;_WL</c> →
    /// <c>&lt;stem&gt;</c>): the last segment minus its <c>_WL</c> tail.</summary>
    internal static string FolderStem(string modelPath)
    {
        var leaf = modelPath[(modelPath.LastIndexOf('/') + 1)..];
        return leaf.EndsWith("_WL", StringComparison.Ordinal) ? leaf[..^3] : leaf;
    }

    /// <summary>The owner-character core of a weapon stem: trailing digits stripped, then one tier
    /// token (<c>SSR</c>/<c>SR</c>/<c>R</c>). <c>LennaSSR01</c> → <c>Lenna</c>.</summary>
    internal static string OwnerCore(string folderStem)
    {
        var core = folderStem.TrimEnd("0123456789".ToCharArray());
        foreach (var tier in new[] { "SSR", "SR", "R" })
            if (core.EndsWith(tier, StringComparison.Ordinal))
                return core[..^tier.Length];
        return core;
    }

    private static List<Character> SortedGroups(
        Dictionary<string, (string Display, List<Outfit> Outfits)> groups)
    {
        var result = new List<Character>(groups.Count);
        foreach (var (key, (display, outfits)) in groups)
            result.Add(new Character(CharId: 0, Name: key, Family: "", GunId: 0, DormModelConfigId: 0,
                Outfits: outfits)
            { DisplayName = display });
        // The grouping key breaks a display-name tie, so two same-named groups keep one order per run.
        result.Sort((a, b) => string.Compare(
                a.DisplayName ?? a.Name, b.DisplayName ?? b.Name, StringComparison.OrdinalIgnoreCase)
            is var byLabel and not 0 ? byLabel : string.CompareOrdinal(a.Name, b.Name));
        return result;
    }
}

/// <summary>The weapon-type vocabulary shared by <c>GunWeaponData #4</c> and
/// <c>WeaponSkinByTypeData #1</c>, with the game's own type tokens.</summary>
public static class WeaponType
{
    /// <summary>Type code → the token the game's UI uses for it. Codes outside the vocabulary label
    /// as themselves rather than vanishing.</summary>
    public static string Token(int type) => type switch
    {
        1 => "HG",
        2 => "SMG",
        3 => "RF",
        4 => "AR",
        5 => "MG",
        6 => "SG",
        7 => "Melee",
        _ => $"Type {type}",
    };

    /// <summary>Whether the code is in the measured vocabulary (its token is a real name, not a
    /// numbered fallback).</summary>
    public static bool IsKnown(int type) => type is >= 1 and <= 7;
}
