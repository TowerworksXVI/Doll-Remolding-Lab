using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// The support-team roster: one <see cref="Character"/> per team from the design DB's <c>SupportData</c>
/// table, with the team's three member models and its team weapon as the outfits. Support teams sit
/// outside <c>ModelConfigData</c> entirely — no GunId arithmetic and no stem formula reaches them — so
/// every outfit here carries its own <see cref="SubjectRoute"/> to a literal prefab address under the
/// <c>Character/SupportTeam</c> and <c>Weapon/SupportTeam</c> roots.
///
/// <para><b>Members are a naming convention, not table rows.</b> The table names the team and its model
/// stem; the members exist only as the three prefabs <c>&lt;stem&gt;_MemberA|B|C</c> beside each other in
/// one bundle. Nothing in the DB names a member, so the rows read Member A, Member B and Member C. A member
/// prefab that ships without geometry on an install is not this reader's concern: the roster's own
/// existence and confirm phases drop an outfit no geometry backs, and a team with no confirmed member
/// drops with it.</para>
///
/// <para><b>Ids are synthetic and negative, in a band of their own.</b> The outfit id is what the roster
/// snapshot persists and the fill re-reads. The design DB issues positive ids, the curated skins take the
/// small negatives, and the weapon roster negates three weapon tables' own ids; the team band sits far
/// below all of those so a collision is impossible by construction (see <see cref="OutfitId"/>).</para>
/// </summary>
public static class SupportTeamRoster
{
    // SupportData field numbers.
    private const int SD_Id = 1;         // team id (1001…: the released set; 9001…, 11xx… are variants of the same stems)
    private const int SD_Name = 2;       // wrapper {#1 = team display-name text-id}
    private const int SD_Stem = 13;      // model stem — the prefab folder and the member/weapon prefab names
    private const int SD_Primary = 19;   // 1 on the row that names a stem's primary entry; absent on the rest

    /// <summary>The member suffix letters, in listing order.</summary>
    public static readonly IReadOnlyList<string> MemberLetters = new[] { "A", "B", "C" };

    /// <summary>The team weapon row's label.</summary>
    public const string WeaponLabel = "Weapon";

    /// <summary>Base of the synthetic id band. Every team outfit id is <c>-(IdBand + teamId·10 + slot)</c>,
    /// slot 0–2 for the members and 3 for the weapon.</summary>
    internal const long IdBand = 9_000_000_000L;

    /// <summary>One decoded <c>SupportData</c> row: the team id, its model stem, the localized team name
    /// (null when unresolved), and whether the row is the stem's primary entry.</summary>
    public sealed record TeamRow(long Id, string Stem, string? DisplayName, bool Primary);

    /// <summary>The synthetic outfit id for one slot of a team — see the class note on the band.</summary>
    internal static long OutfitId(long teamId, int slot) => -(IdBand + teamId * 10 + slot);

    /// <summary>The member prefab's container root, which is also the outfit stem: the two are the same
    /// string so the workbench's root-name ownership rule claims every slot of the prefab, whatever prefix
    /// its slots carry.</summary>
    public static string MemberStem(string teamStem, string letter) => $"{teamStem}_Member{letter}";

    /// <summary>Whether an outfit stem names a team member (rather than the team weapon).</summary>
    public static bool IsMemberStem(string stem) =>
        MemberLetters.Any(l => stem.EndsWith("_Member" + l, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read the table and build the roster; <paramref name="loc"/> null leaves every team
    /// labelled by its stem.</summary>
    public static List<Character> ReadTeams(GameDatabase db, LocalizationDb? loc = null) =>
        BuildTeams(ReadRows(db, loc));

    /// <summary>Decode <c>SupportData</c> into rows. A row missing its id or stem is skipped.</summary>
    public static List<TeamRow> ReadRows(GameDatabase db, LocalizationDb? loc = null)
    {
        var rows = new List<TeamRow>();
        foreach (var row in TableFile.ReadRows(db.TablePath("SupportData")))
        {
            var id = row.Num(SD_Id);
            var stem = row.Str(SD_Stem);
            if (id is null || string.IsNullOrWhiteSpace(stem)) continue;
            rows.Add(new TeamRow((long)id, stem!.Trim(), loc?.Resolve(row, SD_Name), row.Num(SD_Primary) == 1));
        }
        return rows;
    }

    /// <summary>The pure half of <see cref="ReadTeams"/>: one character per distinct stem. Several rows
    /// share a stem (mode variants); the primary row wins, else the lowest id, and that row's id and name
    /// are the team's. Sorted by the label the tree shows.</summary>
    public static List<Character> BuildTeams(IReadOnlyList<TeamRow> rows)
    {
        var byStem = new Dictionary<string, TeamRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!byStem.TryGetValue(row.Stem, out var have)
                || (row.Primary && !have.Primary)
                || (row.Primary == have.Primary && row.Id < have.Id))
                byStem[row.Stem] = row;
        }

        var result = new List<Character>(byStem.Count);
        foreach (var team in byStem.Values)
        {
            var outfits = new List<Outfit>(MemberLetters.Count + 1);
            for (int i = 0; i < MemberLetters.Count; i++)
            {
                string letter = MemberLetters[i];
                string stem = MemberStem(team.Stem, letter);
                outfits.Add(new Outfit(OutfitId(team.Id, i), stem, OutfitKind.Other)
                {
                    MeshPrefixOverride = $"c_{stem}_slg_",
                    Route = SubjectRoute.Addressable(
                        $"Assets/ConfigPrefab/Character/SupportTeam/{team.Stem}/{stem}.prefab", stem),
                    DisplayName = $"Member {letter}",
                });
            }
            // The team weapon, in the weapon roster's own shape (prefix, pooling) so it builds like every
            // other weapon subject. Its bundle also ships a shared-ammunition root under a sibling name;
            // the route pins the weapon root, and the sibling's foreign-prefixed slots are nobody's here.
            string weaponRoot = $"{team.Stem}_WL";
            outfits.Add(new Outfit(OutfitId(team.Id, MemberLetters.Count), weaponRoot, OutfitKind.Other)
            {
                MeshPrefixOverride = $"cw_{team.Stem}_",
                Route = SubjectRoute.Addressable(
                    $"Assets/ConfigPrefab/Weapon/SupportTeam/{team.Stem}/{weaponRoot}.prefab", weaponRoot),
                DisplayName = WeaponLabel,
                PartsPoolAlone = true,
            });
            result.Add(new Character(
                CharId: team.Id, Name: team.Stem, Family: "", GunId: 0, DormModelConfigId: 0, Outfits: outfits)
            { DisplayName = team.DisplayName, OutfitsAppearTogether = true });
        }
        result.Sort((a, b) => string.Compare(
            a.DisplayName ?? a.Name, b.DisplayName ?? b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}
