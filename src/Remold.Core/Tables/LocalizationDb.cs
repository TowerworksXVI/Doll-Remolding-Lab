using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
