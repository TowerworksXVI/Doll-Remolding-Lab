using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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

    /// <summary>A row of LobbyActionListData: <c>#1</c> = action id, and the three clip-name carriers
    /// <c>#6</c>, <c>#7</c> and <c>#19</c>. Any may be null, for a row carrying only some of them.</summary>
    public static byte[] LobbyActionListRow(long actionId, string? clip6 = null, string? clip7 = null,
        string? clip19 = null)
    {
        var m = Pb.Msg().Varint(1, actionId);
        if (clip6 is not null) m.Str(6, clip6);
        if (clip7 is not null) m.Str(7, clip7);
        if (clip19 is not null) m.Str(19, clip19);
        return m.ToArray();
    }

    /// <summary>A row of LobbyActionData: <c>#1</c> = action id, <c>#5</c> = the clip name.</summary>
    public static byte[] LobbyActionRow(long actionId, string clip) =>
        Pb.Msg().Varint(1, actionId).Str(5, clip).ToArray();

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

    /// <summary>A row of GunWeaponData: <c>#1</c> = weapon id, <c>#2 = {#1=textId}</c> = the name
    /// wrapper, <c>#4</c> = weapon type, <c>#9</c> = model path, <c>#11</c> = rarity, <c>#30</c> =
    /// packed default part ids. Null path = a row naming no model, which the reader skips.</summary>
    public static byte[] GunWeaponRow(long weaponId, int type, string? modelPath, int rarity,
        long? nameTextId = null, params long[] defaultPartIds)
    {
        var m = Pb.Msg().Varint(1, weaponId).Varint(4, type).Varint(11, rarity);
        if (nameTextId is not null) m.Sub(2, Pb.Msg().Varint(1, nameTextId.Value));   // #2.1 = name text-id
        if (modelPath is not null) m.Str(9, modelPath);
        if (defaultPartIds.Length > 0) m.Packed(30, defaultPartIds);
        return m.ToArray();
    }

    /// <summary>A row of WeaponSkinData (intl/): <c>#1</c> = skin id, <c>#3 = {#1=textId}</c> = the
    /// name wrapper, <c>#11</c> = rarity, <c>#13</c> = the own-appearance weapon link, <c>#14</c> =
    /// the standalone skin's model path. A real row carries #13 or #14, never both.</summary>
    public static byte[] WeaponSkinRow(long skinId, int rarity, long? ownWeaponId = null,
        string? modelPath = null, long? nameTextId = null)
    {
        var m = Pb.Msg().Varint(1, skinId).Varint(11, rarity);
        if (nameTextId is not null) m.Sub(3, Pb.Msg().Varint(1, nameTextId.Value));   // #3.1 = name text-id
        if (ownWeaponId is not null) m.Varint(13, ownWeaponId.Value);
        if (modelPath is not null) m.Str(14, modelPath);
        return m.ToArray();
    }

    /// <summary>A row of WeaponSkinByTypeData (intl/): <c>#1</c> = weapon type, <c>#2</c> = packed
    /// applicable skin ids.</summary>
    public static byte[] WeaponSkinByTypeRow(int type, params long[] skinIds) =>
        Pb.Msg().Varint(1, type).Packed(2, skinIds).ToArray();

    /// <summary>A row of WeaponModCodGroupData: <c>#1</c> = the packed part/family/tier id, <c>#2</c> =
    /// the base attachment model path.</summary>
    public static byte[] WeaponModCodRow(long id, string modelPath) =>
        Pb.Msg().Varint(1, id).Str(2, modelPath).ToArray();

    /// <summary>A row of WeaponModSkinData (intl/): <c>#1</c> = skin id, <c>#3 = {#1=textId}</c> = the
    /// name wrapper, <c>#11</c> = rarity, <c>#13</c> = the base-model ref
    /// (<c>&lt;partId&gt;&lt;family&gt;&lt;tier&gt;53</c>), <c>#14</c> = model path (null = "use the
    /// base model").</summary>
    public static byte[] WeaponModSkinRow(long skinId, int rarity, string? modelPath = null,
        long? nameTextId = null, long? refId = null)
    {
        var m = Pb.Msg().Varint(1, skinId).Varint(11, rarity);
        if (nameTextId is not null) m.Sub(3, Pb.Msg().Varint(1, nameTextId.Value));   // #3.1 = name text-id
        if (refId is not null) m.Varint(13, refId.Value);
        if (modelPath is not null) m.Str(14, modelPath);
        return m.ToArray();
    }

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
    private readonly byte[]? _before = ReadOrNull(Remold.Core.LabSettings.DefaultPath);

    /// <summary>The file's bytes, or null when it isn't there. RETRIED: the settings file is published by
    /// an atomic replace and the test host rewrites it constantly, so a reader can arrive during the
    /// instant the OS (or a scanner watching the new file) still has the handle — a sharing violation that
    /// says nothing about the test taking the snapshot. Failing on it turns any settings-driven test into a
    /// coin flip; the retry is short and the last attempt is allowed to throw, so a REAL unreadable file
    /// still fails loudly.</summary>
    private static byte[]? ReadOrNull(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(25); }
        }
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (_before is null) File.Delete(Remold.Core.LabSettings.DefaultPath);
                else File.WriteAllBytes(Remold.Core.LabSettings.DefaultPath, _before);
                return;
            }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
    }
}
