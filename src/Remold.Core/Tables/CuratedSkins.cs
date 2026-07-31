using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// One curated skin: a shipped model the design DB does not surface, described well enough for the roster
/// to list it and the resolver to reach it.
/// </summary>
/// <param name="Character">The internal character KEY — the grouping key the selection ledger, the mod
/// manifest and the export subfolders persist. Never shown; <see cref="CharacterDisplay"/> is.</param>
/// <param name="CharacterDisplay">The player-facing character label. A CURATED string, not a
/// localization lookup: these subjects have no <c>GunCharacterData</c> row to name them.</param>
/// <param name="Stem">The outfit's model stem, and the container ROOT its prefab ships under — the two
/// are deliberately the same string, because the workbench's slot-ownership rule treats every slot of a
/// root-name-matching prefab as this subject's, which is what lets a skin claim the shared face mesh it
/// does not carry its own prefix on.</param>
/// <param name="OutfitDisplay">The player-facing outfit label. Curated, like
/// <see cref="CharacterDisplay"/>.</param>
/// <param name="MeshPrefix">The prefix every one of this skin's meshes carries, ending at the part token
/// (so <c>c_X_dorm_face_lod0</c> under <c>c_X_dorm_</c> yields the part <c>face</c>).</param>
/// <param name="Route">How the resolver reaches the assembly prefab, or null when the stem-address
/// formula already reaches it (<see cref="Bundles.GameVfs.PrefabsFor(string)"/>) and only the roster row
/// itself is curated.</param>
/// <param name="ModelConfigId">The outfit's id. NEGATIVE by construction — see
/// <see cref="CuratedSkins"/> on why the sign is the whole collision guarantee.</param>
public sealed record CuratedSkin(
    string Character,
    string CharacterDisplay,
    string Stem,
    string OutfitDisplay,
    string MeshPrefix,
    SubjectRoute? Route,
    long ModelConfigId)
{
    /// <summary>The player-facing name for this skin: character and outfit label together, as a warning
    /// or a picker names a subject.</summary>
    public string Display => $"{CharacterDisplay} {OutfitDisplay}";

    /// <summary>This entry as a roster outfit. <see cref="OutfitKind.Other"/> is not a fallback: it is
    /// the kind whose label renders BARE (see <see cref="FriendlyNames.KindAndLabel"/>), which is what
    /// makes the curated string the whole label instead of "Base · Base". It is also literally true —
    /// these ids follow no <c>GunId</c> scheme.</summary>
    public Outfit ToOutfit() =>
        new(ModelConfigId, Stem, OutfitKind.Other)
        {
            MeshPrefixOverride = MeshPrefix,
            Route = Route,
            DisplayName = OutfitDisplay,
        };
}

/// <summary>
/// Curated extra skins the roster shows alongside the design DB's own. Each is a model the game ships and
/// loads but no <c>ModelConfigData</c> row names, so the DB read cannot enumerate it and the stem-address
/// formula cannot locate it — the entry carries its own <see cref="SubjectRoute"/> instead.
///
/// <para><b>Only VERIFIED entries belong here</b>, on the same rule as <see cref="SummonModels"/>: an
/// entry makes a subject pickable and exportable, and a wrong bundle or root name would export a
/// different model under this name. Verification = the shipped asset ties the route to this skin.</para>
///
/// <para><b>Ids are negative.</b> <c>ModelConfigId</c> is the key the roster snapshot persists and the
/// fill re-reads, and every id the design DB issues is positive. Taking the negative half of the range
/// makes a collision with a real row impossible by construction rather than by hoping a chosen number
/// stays free — a collision would hand a DB outfit a curated one's confirmed part list.</para>
///
/// <para><b>Merging, not shadowing.</b> A curated character whose internal key the DB already carries is
/// MERGED into that row (see <see cref="MergeInto"/>) rather than listed beside it, so a character the
/// game half-describes appears exactly once.</para>
/// </summary>
public static class CuratedSkins
{
    // Bundle ids are LOGICAL bundle ids — the catalog's own identity for a bundle, which always ends
    // ".bundle" (see CatalogIndex.Parse). The bare 32-hex hash is not a logical id and resolves nothing.

    // The material and texture bundles the commander prefabs reference across skins. Named once because
    // a bundle serves several skins and the scope is a set: listing them per skin would drift.
    private const string MaleMats = "c43a6a755b04d911e924f288de3cebdd.bundle";
    private const string FemaleMats = "2eb60fd82a96b0a81b6f7ccc0622c537.bundle";
    private const string Skin02Mats = "faeae77d7f5a840098fdeaed9649bb25.bundle";
    private const string MaleTex = "d8888cf812fb2ffe91364edff4e3edf7.bundle";
    private const string FemaleTex = "bb3c10ec3d203a0480d5b22437e71677.bundle";
    private const string Male02Tex = "e93e53820a0ea8a445a40430b626db68.bundle";
    private const string FaceBlendTex = "d78eede2ffb86be3a3cd21f2729c1bdf.bundle";
    private const string SkinBlendTex = "0eb2fd22fb88ce4e4ea4695613d5a267.bundle";

    private static readonly CuratedSkin[] Entries =
    {
        // TWO LISTED BUILDS of this character — different assets, not two names for one. Barracks is the
        // six-part build; Crew Deck is the one the lobby scene instantiates, reached through its own
        // address under a different asset root and carrying genuinely different mesh bytes. The root name
        // is NOT decoration: a bundle can ship sibling roots, and a first-root-that-parses read lands on
        // the wrong one. A third sibling root, `Mayling_dorm_nobag`, is deliberately NOT listed: its four
        // parts are byte-identical to the Barracks build's (measured; only the two bag prop parts are
        // missing), so its draw hashes are the same and a Barracks mod already covers it in game — a row
        // for it would offer the same mod twice.
        new("Mayling", "Mayling", "Mayling_dorm", "Barracks", "c_Mayling_dorm_",
            SubjectRoute.Addressable("Assets/ConfigPrefab/BarrackModel/Character/Mayling/Mayling_dorm.prefab",
                "Mayling_dorm"),
            -1),
        new("Mayling", "Mayling", "c_Mayling_dorm_nobag_skin_model", "Crew Deck", "c_Mayling_dorm_",
            SubjectRoute.Addressable(
                "Assets/ArtsResource/Lobby_NPC/Mayling/Models/c_Mayling_dorm_nobag_skin_model.prefab",
                "c_Mayling_dorm_nobag_skin_model"),
            -9),

        // The male commander: three numbered skins plus the NPC "Neutral" body. Neutral is a direct-SMR
        // prefab (no RoleMeshRes recipe; its renderer slots carry serialized meshes), which the prefab
        // reader and the workbench already accept as the enemy/NPC shape. Its prefab sits under a context
        // root of the stem-address formula, so it carries NO route — only the roster row is curated. It
        // wears the SAME mesh family as skin 001, plus a hand part 001 has no slot for.
        new("CommanderMale", "Commander (M)", "CommanderMale", "001", "c_CommanderMale_dorm_",
            SubjectRoute.DirectBundle("fbca8e4d50ae538388763de438d4c903.bundle", "CommanderMale",
                MaleMats, MaleTex, SkinBlendTex),
            -2),
        new("CommanderMale", "Commander (M)", "CommanderMale02", "002", "c_CommanderMale02_dorm_",
            SubjectRoute.DirectBundle("fedfcb7bd945fc4b300574062376286b.bundle", "CommanderMale02",
                MaleMats, Skin02Mats, MaleTex, Male02Tex, SkinBlendTex),
            -3),
        new("CommanderMale", "Commander (M)", "CommanderMale03", "003", "c_CommanderMale03_dorm_",
            SubjectRoute.DirectBundle("ced2697753ae7a2f598ca1ea7c185be7.bundle", "CommanderMale03",
                MaleMats, "615fd3e9ef994e37ab2f96e91f0d308b.bundle",
                MaleTex, "952c5d63aa99c91c2074266dda7c8185.bundle", SkinBlendTex),
            -4),
        new("CommanderMale", "Commander (M)", "CommanderNeutral", "Neutral", "c_CommanderMale_dorm_",
            null, -5),

        new("CommanderFemale", "Commander (F)", "CommanderFemale", "001", "c_CommanderFemale_dorm_",
            SubjectRoute.DirectBundle("c2882f637be0d4d52d6b3182e1af1b5e.bundle", "CommanderFemale",
                FemaleMats, MaleMats, FemaleTex, MaleTex, FaceBlendTex, SkinBlendTex),
            -6),
        new("CommanderFemale", "Commander (F)", "CommanderFemale02", "002", "c_CommanderFemale02_dorm_",
            SubjectRoute.DirectBundle("10f82b41089bbd6bdbcacbf40b93b052.bundle", "CommanderFemale02",
                FemaleMats, Skin02Mats, "ff8e4b32f6bcca4ffd6a3e88e03e2c7b.bundle",
                FemaleTex, Male02Tex, "2bda47d868fa119688d3ccb0e9d7f248.bundle", FaceBlendTex, SkinBlendTex),
            -7),
        new("CommanderFemale", "Commander (F)", "CommanderFemale03", "003", "c_CommanderFemale03_dorm_",
            SubjectRoute.DirectBundle("79873b5e22b3cae800dcdc5c084d4e74.bundle", "CommanderFemale03",
                FemaleMats, "1762af754e7916708a3a6436c3247734.bundle",
                FemaleTex, "4604d7d02a7e07a30029317b611bb01c.bundle", FaceBlendTex, SkinBlendTex),
            -8),
    };

    /// <summary>Every curated skin, in listing order (which is the order they appear under their
    /// character).</summary>
    public static IReadOnlyList<CuratedSkin> All => Entries;

    /// <summary>
    /// <paramref name="roster"/> with the curated skins folded in, re-sorted the way
    /// <see cref="GameDatabase.ReadRoster"/> sorts. A curated character whose internal key the roster
    /// already carries MERGES into that character — its curated outfits are appended to the ones the DB
    /// resolved — so a character the DB names but has no model rows for appears once, not twice. Only
    /// then, for a key the roster does not carry, is a new character added.
    ///
    /// <para>The DB's own localized name wins on a merge, because the game naming a character is a better
    /// label source than this table; the curated label stands in only when the DB left it nameless. A
    /// curated stem the character already lists is skipped, so the DB's version of a model this table
    /// also describes is never displaced.</para>
    ///
    /// <para>Call AFTER <see cref="DisplayNames.Enrich"/>: these labels are curated strings, and enriching
    /// them would look up a GunId they do not have.</para>
    /// </summary>
    public static List<Character> MergeInto(IReadOnlyList<Character> roster)
    {
        var result = new List<Character>(roster);
        foreach (var group in Entries.GroupBy(e => e.Character, StringComparer.OrdinalIgnoreCase))
        {
            int at = result.FindIndex(c => string.Equals(c.Name, group.Key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                var existing = result[at];
                var stems = existing.Outfits.Select(o => o.Stem).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var outfits = new List<Outfit>(existing.Outfits);
                foreach (var e in group)
                    if (stems.Add(e.Stem)) outfits.Add(e.ToOutfit());
                result[at] = existing with
                {
                    Outfits = outfits,
                    DisplayName = string.IsNullOrEmpty(existing.DisplayName)
                        ? group.First().CharacterDisplay : existing.DisplayName,
                };
                continue;
            }
            var first = group.First();
            result.Add(new Character(
                CharId: first.ModelConfigId,
                Name: first.Character,
                Family: "",
                GunId: 0,
                DormModelConfigId: 0,
                Outfits: group.Select(e => e.ToOutfit()).ToList())
            { DisplayName = first.CharacterDisplay });
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
