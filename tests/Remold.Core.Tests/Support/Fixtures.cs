using System;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core.Tests.Support;

/// <summary>
/// Synthetic on-disk fixtures — fake <c>Table/*.bytes</c> tables and game directory layouts, entirely from
/// spec, so no real game data is touched or shipped. Each instance owns a temp dir and deletes it.
/// </summary>
internal sealed class TempGame : IDisposable
{
    public string Root { get; }

    public TempGame()
    {
        // No Random/Date in tests — the per-test temp name is a Guid.
        Root = Path.Combine(Path.GetTempPath(), "remold-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>A row of GunCharacterData: #1 = charId, #3 = name, #4 = family, #9 = GunId,
    /// #33 = dorm ModelConfigId.</summary>
    public static byte[] GunCharRow(long charId, string name, string family, long gunId, long dormCfg) =>
        Pb.Msg()
          .Varint(1, charId)     // #1 = charId
          .Str(3, name)          // #3 = name
          .Str(4, family)        // #4 = family
          .Varint(9, gunId)      // #9 = GunId
          .Varint(33, dormCfg)   // #33 = dorm ModelConfigId
          .ToArray();

    /// <summary>A GunCharacterData row missing a field, to exercise the skip rules.</summary>
    public static byte[] GunCharRowRaw(long? charId, string? name, long? gunId)
    {
        var m = Pb.Msg();
        if (charId is not null) m.Varint(1, charId.Value);
        if (name is not null) m.Str(3, name);
        if (gunId is not null) m.Varint(9, gunId.Value);
        return m.ToArray();
    }

    /// <summary>A row of ModelConfigData: #1 = ModelConfigId, #2 = model stem.</summary>
    public static byte[] ModelConfigRow(long id, string stem) =>
        Pb.Msg().Varint(1, id).Str(2, stem).ToArray();

    /// <summary>A GunCharacterData row that ALSO carries the localized-name wrapper (<c>#2 = {#1=textId}</c>).
    /// <paramref name="nameTextId"/> null = no wrapper, the correctly-nameless case.</summary>
    public static byte[] GunCharRowLoc(long charId, string name, string family, long gunId, long dormCfg, long? nameTextId)
    {
        var m = Pb.Msg().Varint(1, charId).Str(3, name).Str(4, family).Varint(9, gunId).Varint(33, dormCfg);
        if (nameTextId is not null) m.Sub(2, Pb.Msg().Varint(1, nameTextId.Value));   // #2.1 = display-name text-id
        return m.ToArray();
    }

    /// <summary>A row of ClothesData (intl/): <c>#5</c> = GunId, <c>#8</c> = base stem, <c>#25</c> = exact
    /// modular stem, <c>#9 = {#1=textId}</c> = the name wrapper. Any may be null, for a partial row.</summary>
    public static byte[] ClothesRow(long gunId, string? baseStem, string? modelStem, long? nameTextId)
    {
        var m = Pb.Msg().Varint(5, gunId);
        if (baseStem is not null) m.Str(8, baseStem);
        if (modelStem is not null) m.Str(25, modelStem);
        if (nameTextId is not null) m.Sub(9, Pb.Msg().Varint(1, nameTextId.Value));   // #9.1 = outfit-name text-id
        return m.ToArray();
    }

    /// <summary>A row of a LangPackage table: <c>#1</c> = text-id, <c>#2</c> = the localized string.</summary>
    public static byte[] LangRow(long textId, string text) =>
        Pb.Msg().Varint(1, textId).Str(2, text).ToArray();

    /// <summary>A row of DormFormationData: <c>#1</c> = spot id, <c>#3</c> = GunId, <c>#4 = {#1=textId}</c>
    /// = the action-label wrapper, <c>#10</c> = the idle clip template, <c>#11</c> = a position tag.</summary>
    public static byte[] DormFormationRow(long spotId, long gunId, long labelTextId, string idleTemplate, string posTag = "Tag") =>
        Pb.Msg()
          .Varint(1, spotId)
          .Varint(3, gunId)
          .Sub(4, Pb.Msg().Varint(1, labelTextId))   // #4.1 = action-label text-id
          .Str(10, idleTemplate)
          .Str(11, posTag)
          .ToArray();

    /// <summary>A row of DromInteractData (the game's own "Drom" typo): <c>#2</c> = GunId, <c>#3</c> = spot
    /// id, <c>#8</c> = the reaction clip template.</summary>
    public static byte[] DromInteractRow(long gunId, long spotId, string reactTemplate) =>
        Pb.Msg().Varint(2, gunId).Varint(3, spotId).Str(8, reactTemplate).ToArray();

    /// <summary>A row of BattleSummonedData: <c>#5</c> = the summon's ModelConfigId,
    /// <c>#16 = {#1=textId}</c> = the summon-name wrapper (null = an unnamed battle row).</summary>
    public static byte[] BattleSummonedRow(long rowId, long modelConfigId, long? nameTextId)
    {
        var m = Pb.Msg().Varint(1, rowId).Varint(5, modelConfigId);
        if (nameTextId is not null) m.Sub(16, Pb.Msg().Varint(1, nameTextId.Value));   // #16.1 = summon-name text-id
        return m.ToArray();
    }

    /// <summary>A row of PartsTypeListData: <c>#1</c> = clothes id (1e6 + ModelConfigId), <c>#2</c> =
    /// packed slot ids.</summary>
    public static byte[] PartsTypeListRow(long clothesId, params long[] slotIds) =>
        Pb.Msg().Varint(1, clothesId).Packed(2, slotIds).ToArray();

    /// <summary>A row of PartsGroupData: <c>#1</c> = slot id, <c>#3</c> = packed variant ids.</summary>
    public static byte[] PartsGroupRow(long slotId, params long[] variantIds) =>
        Pb.Msg().Varint(1, slotId).Packed(3, variantIds).ToArray();

    /// <summary>A row of PartsListData: <c>#1</c> = variant id, <c>#4</c> = default flag.</summary>
    public static byte[] PartsListRow(long variantId, bool isDefault = false)
    {
        var m = Pb.Msg().Varint(1, variantId);
        if (isDefault) m.Varint(4, 1);
        return m.ToArray();
    }

    /// <summary>A row of PartsResourceData: <c>#1</c> = resource id (variantId·10+k), <c>#2</c> = token.</summary>
    public static byte[] PartsResourceRow(long resourceId, string token) =>
        Pb.Msg().Varint(1, resourceId).Str(2, token).ToArray();

    /// <summary>The parent of both <c>Table</c> and <c>AssetBundles_Windows</c>, derived from the game root
    /// exactly as the app does.</summary>
    private string DataDir => Path.Combine(Root, "GF2_Exilium_Data", "LocalCache", "Data");

    /// <summary>Write a <c>Table/intl/&lt;name&gt;.bytes</c> (where ClothesData/ItemData live).</summary>
    public string WriteIntlTable(string name, byte[] contents)
    {
        var intlDir = Path.Combine(DataDir, "Table", "intl");
        Directory.CreateDirectory(intlDir);
        File.WriteAllBytes(Path.Combine(intlDir, name + ".bytes"), contents);
        return Root;
    }

    /// <summary>
    /// 4-byte LE header word, then a top-level message carrying a leading field-#1 <i>varint</i> row-count
    /// (which the reader must IGNORE) followed by each row as a length-delimited field-#1 submessage.
    /// </summary>
    public static byte[] TableBytes(IReadOnlyList<byte[]> rows, int headerWord = 0, bool withCount = true)
    {
        var top = Pb.Msg();
        if (withCount) top.Varint(1, rows.Count);   // the count occurrence — reader keeps only Len entries
        foreach (var r in rows) top.Len(1, r);
        var body = top.ToArray();
        var outp = new byte[4 + body.Length];
        BitConverter.GetBytes(headerWord).CopyTo(outp, 0);
        body.CopyTo(outp, 4);
        return outp;
    }

    /// <summary>Write a <c>Table/&lt;name&gt;.bytes</c> and return the game root.</summary>
    public string WriteTable(string name, byte[] contents)
    {
        var tableDir = Path.Combine(DataDir, "Table");
        Directory.CreateDirectory(tableDir);
        File.WriteAllBytes(Path.Combine(tableDir, name + ".bytes"), contents);
        return Root;
    }

    /// <summary>Create <c>AssetBundles_Windows</c>, optionally with a catalog file.</summary>
    public string WriteGameDir(string? catalogVersion = "24535")
    {
        var abw = Path.Combine(DataDir, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        if (catalogVersion is not null)
            File.WriteAllText(Path.Combine(abw, $"catalog_main_{catalogVersion}.bin"), "x");
        return Root;
    }

    public string At(string rel) => System.IO.Path.Combine(Root, rel);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>The view-model saves settings through the app's durable path, which under the test host is the
/// test binaries' own folder. Snapshot it so a run leaves it as it found it.</summary>
internal sealed class SettingsSnapshot : IDisposable
{
    private readonly byte[]? _before =
        File.Exists(Remold.Core.LabSettings.DefaultPath) ? File.ReadAllBytes(Remold.Core.LabSettings.DefaultPath) : null;

    public void Dispose()
    {
        try
        {
            if (_before is null) File.Delete(Remold.Core.LabSettings.DefaultPath);
            else File.WriteAllBytes(Remold.Core.LabSettings.DefaultPath, _before);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
