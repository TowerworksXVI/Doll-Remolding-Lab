using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// Reads the GFL2 design database (<c>Table/*.bytes</c>) into the character roster — the authoritative
/// source for which character has which outfits and model stems. Constants below are the game's schema
/// field numbers (names aren't shipped). Locating the actual mesh bytes is a separate bundle scan.
/// </summary>
public sealed class GameDatabase
{
    private readonly string _tableRoot;

    public GameDatabase(string tableRoot) => _tableRoot = tableRoot;

    /// <summary>Resolve the game's <c>Table</c> dir from the game root.</summary>
    public static GameDatabase FromGameDir(string gameRoot) => new(ResolveTableRoot(gameRoot));

    /// <summary>The <c>Table</c> dir this database reads from (root; <c>intl/</c> etc. hang off it).</summary>
    public string TableRoot => _tableRoot;

    /// <summary>The <c>Table</c> dir is a sibling of AssetBundles_Windows under LocalCache\Data.</summary>
    public static string ResolveTableRoot(string gameRoot)
    {
        var data = Path.GetDirectoryName(GameLocator.BundleDirOf(gameRoot))!;   // …/LocalCache/Data
        var table = Path.Combine(data, "Table");
        if (!Directory.Exists(table))
            throw new DirectoryNotFoundException($"no Table dir under {data}");
        return table;
    }

    private const int GC_Name = 3;     // GunCharacterData: character name
    private const int GC_Family = 4;   //                   family
    private const int GC_GunId = 9;    //                   GunId
    private const int GC_DormCfg = 33; //                   dorm ModelConfigId
    private const int GC_CharId = 1;   //                   internal charId

    private const int MC_Id = 1;       // ModelConfigData: ModelConfigId
    private const int MC_Stem = 2;     //                  model stem

    private const int BS_ModelCfgId = 5;   // BattleSummonedData: the summoned model's ModelConfigId

    /// <summary>The token that splits a summon's model stem into owner prefix and summon suffix.</summary>
    private const string SummonToken = "_Summon";

    /// <summary>Locale-folder probe order. The global client splits tables across <c>intl/</c> (the
    /// locale-gated 445: weapon skins, clothes…) and the Table root (the shared rest), with jp/kr/tw/us
    /// holding the other locales' variants; the CN client ships EVERYTHING in the Table root and leaves
    /// all five locale folders empty. <c>intl</c> first preserves the global client's exact reads, the
    /// root catches CN, and the remaining locales are a last resort for a client shipping only its
    /// own folder.</summary>
    private static readonly string[] LocaleProbe = { "intl", "", "jp", "kr", "tw", "us" };

    /// <summary>Path to a table, resolved through the locale probe order — the first folder that holds
    /// the file wins. A table found nowhere resolves to the Table-root path, so the caller's read fails
    /// naming the common-denominator location.</summary>
    public string TablePath(string name)
    {
        string file = name + ".bytes";
        foreach (var loc in LocaleProbe)
        {
            string p = loc.Length == 0 ? Path.Combine(_tableRoot, file) : Path.Combine(_tableRoot, loc, file);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(_tableRoot, file);
    }

    /// <summary>Read the full character roster with each character's outfits resolved.</summary>
    public List<Character> ReadRoster()
    {
        var modelStems = ReadModelStems();
        var summons = ReadSummonsByOwnerStem(modelStems);
        var chars = new List<Character>();
        foreach (var row in TableFile.ReadRows(TablePath("GunCharacterData")))
            if (Materialize(row, modelStems, summons) is { } c)
                chars.Add(c);
        return chars.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Find one character by display name (case-insensitive).</summary>
    public Character? FindCharacter(string name)
    {
        var modelStems = ReadModelStems();
        var summons = ReadSummonsByOwnerStem(modelStems);
        foreach (var row in TableFile.ReadRows(TablePath("GunCharacterData")))
            if (string.Equals(row.Str(GC_Name), name, StringComparison.OrdinalIgnoreCase))
                return Materialize(row, modelStems, summons);
        return null;
    }

    /// <summary>One GunCharacterData row → a <see cref="Character"/> with outfits resolved, or null when
    /// the row carries no name or no GunId. Shared by the roster read and the by-name lookup so they
    /// can't drift. A row that resolves no outfits is kept here and dropped by the launch confirm-fill,
    /// which is the one place that knows what actually has a model.</summary>
    private Character? Materialize(PbMessage row, Dictionary<long, string> modelStems,
                                   IReadOnlyDictionary<string, List<(long Id, string Stem)>> summonsByOwnerStem)
    {
        var name = row.Str(GC_Name);
        var gunId = row.Num(GC_GunId);
        if (string.IsNullOrEmpty(name) || gunId is null) return null;
        return new Character(
            CharId: (long)(row.Num(GC_CharId) ?? 0),
            Name: name!,
            Family: row.Str(GC_Family) ?? "",
            GunId: (long)gunId,
            DormModelConfigId: (long)(row.Num(GC_DormCfg) ?? 0),
            Outfits: ResolveOutfits((long)gunId, modelStems, summonsByOwnerStem));
    }

    private const int ED_Id = 1;        // EnemyData: enemy id
    private const int ED_Name = 17;     //            full display name (wrapped text-id)
    private const int ED_ShortName = 40;//            short display name (wrapped text-id)
    private const int ED_ModelCfgId = 23;   //        ModelConfigId — the direct model join (on every row)

    /// <summary>
    /// The enemy roster for Pick's Enemies tab: one <see cref="Character"/> per model STEM (variants and
    /// even two ModelConfig rows can share one), display-named by the most frequent localized enemy name.
    /// <see cref="Character.Name"/> is the stem — the grouping key the selection ledger persists. Each
    /// carries ONE <see cref="OutfitKind.Other"/> outfit whose <see cref="Outfit.MeshPrefixOverride"/> is
    /// the SHORT <c>c_&lt;stem&gt;_</c> prefix (the summon convention), which the slot-token rules resolve
    /// part-less enemy slots against. <paramref name="excludeStems"/> drops stems the playable roster
    /// already shows, so a summon both tables reference doesn't appear twice.
    /// </summary>
    public List<Character> ReadEnemyRoster(LocalizationDb? loc = null, ISet<string>? excludeStems = null)
    {
        var stems = ReadModelStems();
        var rows = new List<(long EnemyId, long ModelCfgId, string? Name)>();
        foreach (var row in TableFile.ReadRows(TablePath("EnemyData")))
        {
            var id = row.Num(ED_Id);
            var cfg = row.Num(ED_ModelCfgId);
            if (id is null || cfg is null) continue;
            var name = loc?.Resolve(row, ED_Name) ?? loc?.Resolve(row, ED_ShortName);
            rows.Add(((long)id, (long)cfg, name));
        }
        return BuildEnemyRoster(rows, stems, excludeStems);
    }

    /// <summary>The pure grouping/naming half of <see cref="ReadEnemyRoster"/>: join each row's
    /// ModelConfigId to its stem, group by stem (lowest id wins as the outfit's ModelConfigId), vote the
    /// display name by frequency (first-seen wins ties), sort by display label. A row whose id has no
    /// stem, an excluded stem, and a blacklisted stem/name drop silently — not a resolution failure.</summary>
    internal static List<Character> BuildEnemyRoster(
        IReadOnlyList<(long EnemyId, long ModelCfgId, string? Name)> rows,
        IReadOnlyDictionary<long, string> stems, ISet<string>? excludeStems = null)
    {
        var byStem = new Dictionary<string, (long ModelCfgId, Dictionary<string, int> Votes)>(StringComparer.OrdinalIgnoreCase);
        var voteOrder = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, cfgId, name) in rows)
        {
            if (!stems.TryGetValue(cfgId, out var stem)) continue;
            if (excludeStems is not null && excludeStems.Contains(stem)) continue;
            if (!byStem.TryGetValue(stem, out var e))
            {
                byStem[stem] = e = (cfgId, new Dictionary<string, int>(StringComparer.Ordinal));
                voteOrder[stem] = new List<string>();
            }
            if (cfgId < e.ModelCfgId) byStem[stem] = e = (cfgId, e.Votes);
            if (!string.IsNullOrEmpty(name))
            {
                if (!e.Votes.ContainsKey(name!)) voteOrder[stem].Add(name!);
                e.Votes[name!] = e.Votes.GetValueOrDefault(name!) + 1;
            }
        }

        var result = new List<Character>(byStem.Count);
        foreach (var (stem, (cfgId, votes)) in byStem)
        {
            if (RosterBlacklist.IsBlacklisted(stem)
                || voteOrder[stem].Any(RosterBlacklist.IsBlacklisted))
                continue;
            string? display = null;
            int best = 0;
            foreach (var name in voteOrder[stem])   // first-seen order breaks ties deterministically
                if (votes[name] > best) { best = votes[name]; display = name; }
            result.Add(new Character(
                CharId: cfgId, Name: stem, Family: "", GunId: 0, DormModelConfigId: 0,
                Outfits: new List<Outfit>
                {
                    new(cfgId, stem, OutfitKind.Other) { MeshPrefixOverride = $"c_{stem}_" },
                })
            { DisplayName = display });
        }
        result.Sort((a, b) => string.Compare(
            a.DisplayName ?? a.Name, b.DisplayName ?? b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>ModelConfigId → model stem, the whole table. Several ids can share one stem, so a
    /// caller joining FROM ids reads this rather than inverting <see cref="StemToModelConfigId"/>,
    /// whose last-wins collapse would shadow all but one of them.</summary>
    public Dictionary<long, string> ModelStemsById() => ReadModelStems();

    /// <summary>Model stem → ModelConfigId, for callers that hold a stem and want its DB id.</summary>
    public Dictionary<string, long> StemToModelConfigId()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, stem) in ReadModelStems()) map[stem] = id;
        return map;
    }

    /// <summary>ModelConfigId → model stem, the whole table indexed once.</summary>
    private Dictionary<long, string> ReadModelStems()
    {
        var map = new Dictionary<long, string>();
        foreach (var row in TableFile.ReadRows(TablePath("ModelConfigData")))
        {
            var id = row.Num(MC_Id);
            var stem = row.Str(MC_Stem);
            if (id is not null && !string.IsNullOrEmpty(stem))
                map[(long)id] = stem!;
        }
        return map;
    }

    /// <summary>
    /// Owner base stem → the summon models that stem owns, ordered by ModelConfigId. Membership comes from
    /// <c>BattleSummonedData</c>: a <c>ModelConfigData</c> row is a summon exactly when a battle row
    /// summons it, which is what keeps unused config rows out of the roster. Ownership comes from the
    /// summon's own stem — the text before its <c>_Summon</c> token is the owner's base stem, and no id
    /// arithmetic relates the two. A stem with no <c>_Summon</c> token carries no owner prefix and is
    /// keyed to nobody, so the enemy-side summons the same table lists reach no character.
    /// </summary>
    private Dictionary<string, List<(long Id, string Stem)>> ReadSummonsByOwnerStem(Dictionary<long, string> stems)
    {
        var byOwner = new Dictionary<string, List<(long Id, string Stem)>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<long>();   // one model recurs across many battle rows
        foreach (var row in TableFile.ReadRows(TablePath("BattleSummonedData")))
        {
            var id = row.Num(BS_ModelCfgId);
            if (id is null || !seen.Add((long)id)) continue;
            if (!stems.TryGetValue((long)id, out var stem)) continue;
            int cut = stem.IndexOf(SummonToken, StringComparison.OrdinalIgnoreCase);
            if (cut <= 0) continue;
            var owner = stem[..cut];
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<(long, string)>();
            list.Add(((long)id, stem));
        }
        foreach (var list in byOwner.Values) list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return byOwner;
    }

    /// <summary>
    /// Enumerate a character's outfits: the ModelConfig id schemes around the GunId (Base = GunId,
    /// Alt = GunId*100+NN, Dorm = GunId*100+99) plus the summons whose stem prefix is this character's
    /// base stem. A summon's id belongs to no scheme — some sit inside the Alt range — so the summon set
    /// is resolved first and its ids are barred from the scheme probes rather than classified out of them.
    /// A character with no <c>ModelConfigData</c> row at its GunId has no base stem to own by, and so no
    /// summons.
    /// </summary>
    private static List<Outfit> ResolveOutfits(long gunId, Dictionary<long, string> stems,
        IReadOnlyDictionary<string, List<(long Id, string Stem)>> summonsByOwnerStem)
    {
        List<(long Id, string Stem)>? summons = null;
        if (stems.TryGetValue(gunId, out var baseStem))
            summonsByOwnerStem.TryGetValue(baseStem, out summons);

        var outfits = new List<Outfit>();
        void TryAdd(long id, OutfitKind kind)
        {
            if (summons is not null && summons.Exists(s => s.Id == id)) return;
            if (!stems.TryGetValue(id, out var stem)) return;
            outfits.Add(new Outfit(id, stem, kind));
        }

        TryAdd(gunId, OutfitKind.Base);
        for (int nn = 1; nn <= 98; nn++)
            TryAdd(gunId * 100 + nn, OutfitKind.Alt);
        TryAdd(gunId * 100 + 99, OutfitKind.Dorm);
        if (summons is not null)
            foreach (var (id, stem) in summons)
                outfits.Add(new Outfit(id, stem, OutfitKind.Summon)
                {
                    MeshPrefixOverride = SummonModels.MeshPrefixFor(id),
                });

        return outfits;
    }
}
