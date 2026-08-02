using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Tables;

/// <summary>
/// The modular-wardrobe scheme (<c>Parts*Data</c> tables): which outfits offer swappable part slots,
/// the variants each slot offers, and the mesh tokens each variant is made of. An outfit appears here
/// exactly when the game's replaceable-parts machinery is active for it; every part whose slot name
/// carries a <c>_P&lt;n&gt;_</c> token is governed by this scheme, and exactly one variant per slot is
/// worn at a time.
///
/// <para>Schema (field numbers; names aren't shipped):
/// <c>PartsTypeListData</c> #1 = clothes id (1&#8239;000&#8239;000 + ModelConfigId), #2 = packed slot ids.
/// <c>PartsGroupData</c> #1 = slot id, #3 = packed variant ids.
/// <c>PartsListData</c> #1 = variant id, #4 = default flag.
/// <c>PartsResourceData</c> #1 = resource id, #2 = mesh token.
/// A variant's resources are keyed by id arithmetic — <c>resourceId / 10 == variantId</c> — not by any
/// list field: the row's own resource-list field is populated for only some variants, while the
/// arithmetic covers every resource. Token order within a variant follows resource id.</para>
/// </summary>
public sealed class PartScheme
{
    /// <summary>One wearable choice in a slot: the meshes it is made of are married — worn and loaded
    /// together, never separately.</summary>
    public sealed record Variant(long Id, bool IsDefault, IReadOnlyList<string> Tokens);

    /// <summary>One swappable slot of a modular outfit. The player wears exactly one of
    /// <see cref="Variants"/> at any time; which one is their choice, so nothing may assume the
    /// default.</summary>
    public sealed record Slot(long Id, IReadOnlyList<Variant> Variants);

    private readonly Dictionary<long, IReadOnlyList<Slot>> _byModelConfig;

    private PartScheme(Dictionary<long, IReadOnlyList<Slot>> byModelConfig) => _byModelConfig = byModelConfig;

    /// <summary>The clothes-table id space sits one million above ModelConfigId.</summary>
    private const long ClothesIdBase = 1_000_000;

    /// <summary>The scheme for an outfit, or null when the outfit is not modular. An empty slot list is
    /// a real state (a modular controller with nothing to control) and comes back as an empty list, not
    /// null.</summary>
    public IReadOnlyList<Slot>? For(long modelConfigId) =>
        _byModelConfig.TryGetValue(modelConfigId, out var slots) ? slots : null;

    /// <summary>Every modular outfit's ModelConfigId.</summary>
    public IReadOnlyCollection<long> ModularModelConfigIds => _byModelConfig.Keys;

    /// <summary>Every modular outfit's slots keyed by model stem, joined through
    /// <see cref="GameDatabase.ModelStemsById"/> from the scheme's own ids — several ModelConfig ids
    /// can share one stem, and the last id read wins, so a shared stem answers with one of its
    /// schemes rather than none. The one owner of that join, so every caller resolves a stem the
    /// same way.</summary>
    public Dictionary<string, IReadOnlyList<Slot>> ByStem(GameDatabase db)
    {
        var stems = db.ModelStemsById();
        var byStem = new Dictionary<string, IReadOnlyList<Slot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var cfg in ModularModelConfigIds)
            if (stems.TryGetValue(cfg, out var stem) && For(cfg) is { } slots)
                byStem[stem] = slots;
        return byStem;
    }

    /// <summary>
    /// Read the four tables into one scheme. Throws <see cref="InvalidDataException"/> on any dangling
    /// cross-reference — a slot, variant, or resource that points at a row that isn't there. The scheme
    /// decides which parts a build may lean on, so a half-read scheme must fail here rather than let a
    /// build proceed on a wrong one; the one tolerated gap is a variant with no resources, which
    /// constrains nothing.
    /// </summary>
    public static PartScheme Load(GameDatabase db)
    {
        var slotIdsByClothes = new Dictionary<long, List<long>>();
        foreach (var row in TableFile.ReadRows(db.TablePath("PartsTypeListData")))
            if (row.Num(1) is { } cid)
                slotIdsByClothes[(long)cid] = row.PackedVarints(2).Select(v => (long)v).ToList();

        var variantIdsBySlot = new Dictionary<long, List<long>>();
        foreach (var row in TableFile.ReadRows(db.TablePath("PartsGroupData")))
            if (row.Num(1) is { } sid)
                variantIdsBySlot[(long)sid] = row.PackedVarints(3).Select(v => (long)v).ToList();

        var defaultByVariant = new Dictionary<long, bool>();
        foreach (var row in TableFile.ReadRows(db.TablePath("PartsListData")))
            if (row.Num(1) is { } vid)
                defaultByVariant[(long)vid] = (row.Num(4) ?? 0) != 0;

        var tokensByVariant = new Dictionary<long, List<(long Rid, string Token)>>();
        foreach (var row in TableFile.ReadRows(db.TablePath("PartsResourceData")))
            if (row.Num(1) is { } rid && row.Str(2) is { } token)
            {
                long vid = (long)rid / 10;
                if (!defaultByVariant.ContainsKey(vid))
                    throw new InvalidDataException(
                        $"PartsResourceData {rid} ('{token}') belongs to no PartsListData variant");
                if (!tokensByVariant.TryGetValue(vid, out var list)) tokensByVariant[vid] = list = new();
                list.Add(((long)rid, token));
            }

        var byModelConfig = new Dictionary<long, IReadOnlyList<Slot>>();
        foreach (var (cid, slotIds) in slotIdsByClothes)
        {
            var slots = new List<Slot>(slotIds.Count);
            foreach (long sid in slotIds)
            {
                if (!variantIdsBySlot.TryGetValue(sid, out var variantIds))
                    throw new InvalidDataException($"PartsTypeListData {cid} names slot {sid}, which has no PartsGroupData row");
                var variants = new List<Variant>(variantIds.Count);
                foreach (long vid in variantIds)
                {
                    if (!defaultByVariant.TryGetValue(vid, out bool isDefault))
                        throw new InvalidDataException($"slot {sid} names variant {vid}, which has no PartsListData row");
                    var tokens = tokensByVariant.TryGetValue(vid, out var toks)
                        ? toks.OrderBy(t => t.Rid).Select(t => t.Token).ToList()
                        : new List<string>();
                    variants.Add(new Variant(vid, isDefault, tokens));
                }
                slots.Add(new Slot(sid, variants));
            }
            byModelConfig[cid - ClothesIdBase] = slots;
        }
        return new PartScheme(byModelConfig);
    }
}
