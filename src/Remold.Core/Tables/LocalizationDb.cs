using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Remold.Core.Tables;

/// <summary>
/// The game's localized-text resolver. Name-bearing rows carry a numeric <b>text-id</b>, not the string;
/// the per-language string lives in <c>LangPackageTable&lt;Locale&gt;Data.bytes</c> (flat map: #1 =
/// text-id, #2 = string, ~394k rows for en-US). The text-id is not a bare scalar — it sits in a one-field
/// wrapper sub-message <c>{ #1 = text-id }</c> at the name field, so resolving = read the wrapper's #1,
/// then look it up here.
/// </summary>
public sealed class LocalizationDb
{
    private readonly Dictionary<long, string> _text;

    /// <summary>The locale token this map was loaded for (e.g. <c>Enus</c>, <c>Jajp</c>).</summary>
    public string Locale { get; }
    public int Count => _text.Count;

    private LocalizationDb(string locale, Dictionary<long, string> text)
    {
        Locale = locale;
        _text = text;
    }

    private const int Lang_TextId = 1;   // LangPackageTable*: text-id
    private const int Lang_String = 2;   //                    localized string

    /// <summary>Load one locale's text map, resolved through the database's locale-folder probe like
    /// every other table read. <paramref name="locale"/> is the token in the filename:
    /// <c>Enus Cn Zhtc Jajp Kokr Dede Eses Frfr Ptpt Thth Vtvi</c>.</summary>
    public static LocalizationDb Load(GameDatabase db, string locale = "Enus")
    {
        var path = db.TablePath($"LangPackageTable{locale}Data");
        var map = new Dictionary<long, string>(400_000);
        foreach (var row in TableFile.ReadRows(path))
        {
            var id = row.Num(Lang_TextId);
            if (id is null) continue;
            var s = row.Str(Lang_String);
            if (s is not null) map[(long)id] = s;
        }
        return new LocalizationDb(locale, map);
    }

    // Every roster field that is actually resolved through this map. The snapshot keeps exactly the ids
    // these fields reference, rather than re-encoding the locale table's roughly 394,000 unrelated rows.
    private static readonly (string Table, int[] Fields)[] RosterTextFields =
    {
        ("ClothesData", new[] { 9 }),
        ("GunCharacterData", new[] { 2 }),
        ("BattleSummonedData", new[] { 16 }),
        ("EnemyData", new[] { 17, 40 }),
        ("GunWeaponData", new[] { 2 }),
        ("WeaponSkinData", new[] { 3 }),
        ("WeaponModSkinData", new[] { 3 }),
    };

    private const uint SnapshotMagic = 0x4E534E44; // "DNSN"
    private const byte SnapshotSchema = 1;
    private const int MaxSnapshotEntries = 100_000;
    private const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private readonly record struct TableIdentity(string Name, string Path, long Length, long MtimeTicks);

    /// <summary>Load the compact roster-only locale snapshot when all contributing table identities still
    /// match. Any miss or corruption runs the full locale decode and atomically replaces the snapshot.</summary>
    public static LocalizationDb LoadRosterCached(GameDatabase db, string snapshotPath,
        out bool snapshotHit, string locale = "Enus")
    {
        snapshotHit = false;
        IReadOnlyList<TableIdentity> identities;
        try { identities = RosterTableIdentities(db, locale); }
        catch
        {
            // Preserve the original launch behavior when a roster table is absent: localization itself can
            // still load, while each roster consumer keeps its own best-effort table guard.
            return Load(db, locale);
        }

        if (TryLoadRosterSnapshot(snapshotPath, locale, identities) is { } cached)
        {
            snapshotHit = true;
            return cached;
        }

        var full = Load(db, locale);
        HashSet<long> referenced;
        try { referenced = ReferencedRosterTextIds(db); }
        catch { return full; }
        var compact = new Dictionary<long, string>(referenced.Count);
        foreach (long id in referenced)
            if (full._text.TryGetValue(id, out var value)) compact[id] = value;
        var result = new LocalizationDb(locale, compact);
        try { SaveRosterSnapshot(snapshotPath, result, identities); }
        catch { /* cache-only; the compact in-memory result stands */ }
        return result;
    }

    private static IReadOnlyList<TableIdentity> RosterTableIdentities(GameDatabase db, string locale)
    {
        var names = new[] { $"LangPackageTable{locale}Data" }
            .Concat(RosterTextFields.Select(spec => spec.Table));
        var identities = new List<TableIdentity>();
        foreach (string name in names)
        {
            string path = Path.GetFullPath(db.TablePath(name));
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("roster display-name table is absent", path);
            identities.Add(new TableIdentity(name, path, file.Length, file.LastWriteTimeUtc.Ticks));
        }
        return identities;
    }

    private static HashSet<long> ReferencedRosterTextIds(GameDatabase db)
    {
        var ids = new HashSet<long>();
        foreach (var spec in RosterTextFields)
            foreach (var row in TableFile.ReadRows(db.TablePath(spec.Table)))
                foreach (int field in spec.Fields)
                    if (WrappedTextId(row, field) is { } id) ids.Add(id);
        return ids;
    }

    private static LocalizationDb? TryLoadRosterSnapshot(string path, string locale,
        IReadOnlyList<TableIdentity> identities)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxSnapshotBytes) return null;
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != SnapshotMagic || reader.ReadByte() != SnapshotSchema
                || !string.Equals(reader.ReadString(), locale, StringComparison.Ordinal)) return null;
            int identityCount = reader.ReadInt32();
            if (identityCount != identities.Count) return null;
            for (int i = 0; i < identityCount; i++)
            {
                var expected = identities[i];
                if (!string.Equals(reader.ReadString(), expected.Name, StringComparison.Ordinal)
                    || !string.Equals(reader.ReadString(), expected.Path, StringComparison.OrdinalIgnoreCase)
                    || reader.ReadInt64() != expected.Length || reader.ReadInt64() != expected.MtimeTicks)
                    return null;
            }
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxSnapshotEntries) return null;
            var map = new Dictionary<long, string>(count);
            for (int i = 0; i < count; i++)
                if (!map.TryAdd(reader.ReadInt64(), reader.ReadString())) return null;
            if (stream.Position != stream.Length) return null;
            return new LocalizationDb(locale, map);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException
                                  or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static void SaveRosterSnapshot(string path, LocalizationDb db,
        IReadOnlyList<TableIdentity> identities)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(SnapshotMagic);
                writer.Write(SnapshotSchema);
                writer.Write(db.Locale);
                writer.Write(identities.Count);
                foreach (var identity in identities)
                {
                    writer.Write(identity.Name);
                    writer.Write(identity.Path);
                    writer.Write(identity.Length);
                    writer.Write(identity.MtimeTicks);
                }
                writer.Write(db._text.Count);
                foreach (var pair in db._text.OrderBy(pair => pair.Key))
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
        }
    }

    /// <summary>The localized string for a text-id, or null if absent.</summary>
    public string? Text(long textId) => _text.TryGetValue(textId, out var s) ? s : null;

    /// <summary>The text-id in the <c>{ #1 = id }</c> wrapper at <paramref name="field"/>, or null.
    /// Strict on the shape (exactly one field, #1, varint) so an otherwise-shaped field returns null
    /// instead of throwing or yielding a garbage id that resolves to unrelated text.</summary>
    public static long? WrappedTextId(PbMessage row, int field)
    {
        var raw = row.Raw(field);
        if (raw is null || raw.Length == 0) return null;
        PbMessage sub;
        try { sub = PbMessage.Parse(raw); }
        catch (FormatException) { return null; }          // bytes aren't a protobuf message
        catch (IndexOutOfRangeException) { return null; } // truncated mid-value
        if (!sub.FieldNumbers.SequenceEqual(new[] { Lang_TextId })) return null;
        var vals = sub.Field(Lang_TextId);
        return vals.Count == 1 && vals[0].Wire == WireType.Varint ? (long)vals[0].Num : null;
    }

    /// <summary>Resolve a wrapped-text-id name field on a row straight to its localized string, or null.</summary>
    public string? Resolve(PbMessage row, int field)
    {
        var id = WrappedTextId(row, field);
        return id is null ? null : Text((long)id);
    }
}
