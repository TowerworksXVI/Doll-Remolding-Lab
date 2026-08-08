using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Remold.Core.Tables;

/// <summary>
/// The addresses of the dorm and lobby timeline prefabs a character plays, derived from the game's own
/// tables. Nothing here is a shipped string: the clip NAMES are plaintext templates carried in the tables
/// (<c>{0}</c> = the clothes-model name), and the FOLDER they sit in is the character token the game's own
/// naming rule cuts out of the model stem.
///
/// <para>Schema (field numbers; names aren't shipped): <c>DormFormationData</c> #10 = bedroom idle pose,
/// <c>DromInteractData</c> #8 = bedroom interaction (the game's own misspelling of the table name),
/// <c>LobbyActionListData</c> #6/#7/#19 and <c>LobbyActionData</c> #5 = lobby clips. Those fields also carry
/// already-resolved names and unrelated scenery ids, so only values containing <c>{0}</c> are templates.
/// A row's optional outfit field is not read: the formatted name embeds the stem, so an address that
/// resolves belongs to that stem, and the catalog is the only gate needed.</para>
///
/// <para>Most formatted names resolve to nothing, which is normal — the tables describe the whole roster's
/// clip vocabulary, not one character's. Resolution is a catalog dictionary lookup, so probing all of them
/// costs nothing worth measuring.</para>
/// </summary>
public sealed class TimelineTemplates
{
    /// <summary>The clip-name templates that land in each character's dorm folder.</summary>
    public IReadOnlyList<string> Dorm { get; }

    /// <summary>The clip-name templates that land in each character's lobby folder.</summary>
    public IReadOnlyList<string> Lobby { get; }

    internal TimelineTemplates(IReadOnlyList<string> dorm, IReadOnlyList<string> lobby)
    { Dorm = dorm; Lobby = lobby; }

    private const string DormFolder = "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline";
    private const string LobbyFolder = "Assets/ConfigPrefab/Nonbattle_Timeline/Lobby_Timeline";

    /// <summary>The game's own folder-token rule: the model stem up to its rarity or dorm marker, which is
    /// what names the per-character timeline folder. A stem matching no marker answers with itself.</summary>
    public static string CharacterToken(string stem)
    {
        var m = Regex.Match(stem, @"(^.*?)(SSR|SR|R|Dorm)\d*$");
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : stem;
    }

    /// <summary>Every timeline address worth probing for one outfit stem, deduped, in a stable order.
    /// Formatting is invariant-culture: these are asset paths, never display text.</summary>
    public IReadOnlyList<string> AddressesFor(string stem)
    {
        string token = CharacterToken(stem);
        // Ordinal, because the catalog key these become is case-EXACT: two addresses differing only in
        // case are two different lookups, and folding them here would drop one that resolves.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new List<string>();
        void Add(string folder, string sub, string name)
        {
            var addr = $"{folder}/{token}{sub}/{name}.prefab";
            if (seen.Add(addr)) addresses.Add(addr);
        }
        foreach (var t in Dorm) Add(DormFolder, "Dorm", Format(t, stem));
        foreach (var t in Lobby) Add(LobbyFolder, "Lobby", Format(t, stem));
        // The wardrobe-change clips are the one list carrier no table template reaches, so their names are
        // built structurally from the same folder token.
        foreach (var tail in new[] { "_Before", "_Idle", "_After" })
            Add(DormFolder, "Dorm", $"c_{token}Dorm_Cloth{tail}");
        return addresses;
    }

    private static string Format(string template, string stem)
    {
        try { return string.Format(CultureInfo.InvariantCulture, template, stem); }
        catch (FormatException) { return template; }   // a template the game authored with stray braces
    }

    /// <summary>Read the four tables' clip-name templates. A table that cannot be READ throws, the way
    /// <see cref="PartScheme.Load"/> does: the caller's "timeline tables unreadable" fallback is the one
    /// place that decides what an unreadable table costs, and swallowing the failure here would leave it
    /// unreachable and the load silently answering with fewer templates than the game has. A single
    /// MALFORMED ROW is tolerated — it costs only the timelines that row would have found.</summary>
    public static TimelineTemplates Load(GameDatabase db)
    {
        var dorm = new List<string>();
        var lobby = new List<string>();
        Collect(db, "DormFormationData", dorm, 10);
        Collect(db, "DromInteractData", dorm, 8);
        Collect(db, "LobbyActionListData", lobby, 6, 7, 19);
        Collect(db, "LobbyActionData", lobby, 5);
        return new TimelineTemplates(Dedupe(dorm), Dedupe(lobby));
    }

    private static void Collect(GameDatabase db, string table, List<string> into, params int[] fields)
    {
        // The table READ is not caught: a missing or corrupt table is the caller's call, not this one's.
        // Per-ROW tolerance needs nothing here — a field that is absent, holds a non-string, or holds
        // bytes that are not decodable UTF-8 all answer null from Str, so one bad row costs only the
        // templates it would have carried and the rest of the table still lands.
        var rows = TableFile.ReadRows(db.TablePath(table));
        foreach (var row in rows)
            foreach (int f in fields)
                if (row.Str(f) is { } s && s.Contains("{0}", StringComparison.Ordinal))
                    into.Add(s);
    }

    private static IReadOnlyList<string> Dedupe(List<string> raw) =>
        raw.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
}
