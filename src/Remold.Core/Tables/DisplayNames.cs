using System;
using System.Collections.Generic;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// The ONE home for the localized-name text-id join. Each name-bearing row carries a
/// <c>{ #1 = text-id }</c> wrapper (see <see cref="LocalizationDb"/>) resolved through
/// <c>LangPackageTable&lt;Locale&gt;</c>. Field map:
/// <list type="bullet">
///   <item><b>Character</b> — <c>GunCharacterData #2.1</c> → name, joined to the roster by GunId (<c>#9</c>).</item>
///   <item><b>Outfit</b> — model stem → <c>ClothesData #25</c> (exact modular stem) or <c>#8</c> (base
///     stem) → <c>#9.1</c> → name.</item>
/// </list>
///
/// <para>A null lookup is the expected case, not a miss: only ~132 of 562 model stems carry a localized
/// name, and callers keep the token/stem label. A genuinely broken locale table instead surfaces as an
/// empty resolver (the loader throws), visible up front rather than masked per-row.</para>
/// </summary>
public sealed class DisplayNames
{
    // ClothesData (intl/) — outfit rows.
    private const int Cloth_GunId = 5;      // owning character's GunId (unused here; kept for the map note)
    private const int Cloth_BaseStem = 8;   // base model stem
    private const int Cloth_ModelStem = 25; // exact modular stem
    private const int Cloth_Name = 9;       // outfit name (wrapped text-id)

    // GunCharacterData (root) — character rows.
    private const int GC_GunId = 9;         // GunId, the roster join key
    private const int GC_Name = 2;          // character display name (wrapped text-id)

    // BattleSummonedData (root) — summons aren't wearable, so they have no ClothesData row and are named here.
    private const int BS_ModelCfgId = 5;
    private const int BS_Name = 16;   // wrapped text-id

    private readonly Dictionary<long, string> _characterByGunId;
    private readonly Dictionary<string, string> _outfitByStem;   // both exact (#25) and base (#8) stems
    private readonly Dictionary<long, string> _summonByModelCfgId;

    /// <summary>The locale these names were resolved for (e.g. <c>Enus</c>).</summary>
    public string Locale { get; }
    /// <summary>How many characters / outfit-stems resolved to a localized name (diagnostic; 0 means every
    /// lookup falls back to a token).</summary>
    public int CharacterCount => _characterByGunId.Count;
    public int OutfitStemCount => _outfitByStem.Count;

    private DisplayNames(string locale, Dictionary<long, string> byGunId, Dictionary<string, string> byStem,
                         Dictionary<long, string> summonByModelCfgId)
    {
        Locale = locale;
        _characterByGunId = byGunId;
        _outfitByStem = byStem;
        _summonByModelCfgId = summonByModelCfgId;
    }

    /// <summary>Index the tables once so lookups are pure dictionary hits. <paramref name="db"/> locates
    /// the tables, <paramref name="loc"/> supplies one locale's strings. A per-table read failure leaves
    /// that half empty rather than throwing, so an install that ships one table names what it can.</summary>
    public static DisplayNames Build(GameDatabase db, LocalizationDb loc)
    {
        var byGunId = new Dictionary<long, string>();
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // a ClothesData row can carry both an exact modular stem (#25) and a base stem (#8); index BOTH so
        // a hit works whichever the roster was built from, first-writer-wins so an exact stem isn't
        // clobbered by another row's base-stem alias
        try
        {
            foreach (var r in TableFile.ReadRows(db.TablePath("ClothesData", intl: true)))
            {
                var name = loc.Resolve(r, Cloth_Name);
                if (name is null) continue;
                var exact = r.Str(Cloth_ModelStem);
                var basic = r.Str(Cloth_BaseStem);
                if (!string.IsNullOrEmpty(exact)) byStem.TryAdd(exact!, name);
                if (!string.IsNullOrEmpty(basic)) byStem.TryAdd(basic!, name);
            }
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            // unreadable — outfits stay token-labelled
        }

        try
        {
            foreach (var r in TableFile.ReadRows(db.TablePath("GunCharacterData")))
            {
                var gunId = r.Num(GC_GunId);
                if (gunId is null) continue;
                var name = loc.Resolve(r, GC_Name);
                if (name is not null) byGunId.TryAdd((long)gunId, name);
            }
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            // unreadable — characters stay token-labelled
        }

        // one model id recurs across many BattleSummonedData rows (per-difficulty stat blocks) whose names
        // occasionally differ (dev variants like "… (Controllable Test)"), so the most frequent name wins
        var summonVotes = new Dictionary<long, Dictionary<string, int>>();
        try
        {
            foreach (var r in TableFile.ReadRows(db.TablePath("BattleSummonedData")))
            {
                var cfgId = r.Num(BS_ModelCfgId);
                if (cfgId is null) continue;
                var name = loc.Resolve(r, BS_Name);
                if (name is null) continue;
                if (!summonVotes.TryGetValue((long)cfgId, out var votes))
                    summonVotes[(long)cfgId] = votes = new Dictionary<string, int>();
                votes[name] = votes.GetValueOrDefault(name) + 1;
            }
        }
        catch (Exception e) when (e is System.IO.IOException or System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException)
        {
            // unreadable — summons stay stem-labelled
        }
        var summonByCfgId = new Dictionary<long, string>();
        foreach (var (cfgId, votes) in summonVotes)
        {
            string? best = null; int bestCount = 0;
            foreach (var (name, count) in votes)
                if (count > bestCount) { best = name; bestCount = count; }
            if (best is not null) summonByCfgId[cfgId] = best;
        }

        return new DisplayNames(loc.Locale, byGunId, byStem, summonByCfgId);
    }

    /// <summary>The localized name for a character by GunId, or null when the game has none (the caller
    /// keeps the roster's internal <see cref="Character.Name"/>).</summary>
    public string? Character(long gunId) => _characterByGunId.GetValueOrDefault(gunId);

    /// <summary>The localized name for an outfit by model stem, or null when the stem has no ClothesData
    /// row (the caller keeps the stem label).</summary>
    public string? Outfit(string stem) =>
        string.IsNullOrEmpty(stem) ? null : _outfitByStem.GetValueOrDefault(stem);

    /// <summary>The localized name for a battle summon by its ModelConfigId, or null when no
    /// BattleSummonedData row names that model (the caller keeps the stem label).</summary>
    public string? Summon(long modelConfigId) => _summonByModelCfgId.GetValueOrDefault(modelConfigId);

    /// <summary>A copy of <paramref name="roster"/> with
    /// <see cref="Model.Character.DisplayName"/>/<see cref="Model.Outfit.DisplayName"/> filled in where the
    /// game has a localized name. <see cref="Model.Character.Name"/> (the mesh-stem grouping key) is
    /// untouched.
    ///
    /// <para>Fills, never CLEARS: a row that already carries a display name and resolves no localized one
    /// keeps what it had. Curated labels (<see cref="CuratedSkins"/>) have no localization row by
    /// definition, and a lookup miss silently blanking them is exactly the failure this rules out.</para>
    /// </summary>
    public List<Character> Enrich(IReadOnlyList<Character> roster)
    {
        var result = new List<Character>(roster.Count);
        foreach (var c in roster)
        {
            var outfits = new List<Outfit>(c.Outfits.Count);
            foreach (var o in c.Outfits)
                // keyed on Kind, not merely a fallback: BattleSummonedData also has rows for a few
                // NON-summon model ids, whose names must never leak onto a real outfit
                outfits.Add(o with
                {
                    DisplayName = (o.Kind == OutfitKind.Summon
                        ? Outfit(o.Stem) ?? Summon(o.ModelConfigId)
                        : Outfit(o.Stem)) ?? o.DisplayName,
                });
            result.Add(c with { DisplayName = Character(c.GunId) ?? c.DisplayName, Outfits = outfits });
        }
        return result;
    }
}
