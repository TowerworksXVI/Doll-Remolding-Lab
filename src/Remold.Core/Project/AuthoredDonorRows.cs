using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>One binding of one edit as the donor-row fold reads it: what the modder asked for, the slot the
/// ask lands on, and the project asset the ask resolves to.</summary>
public sealed record EditOutputRow(Binding Binding, TargetSlot Slot, ProjectAsset? Asset);

/// <summary>The edit-output half of an edit, folded into the per-submesh shape the runtime compiler binds
/// from. It reads an edit's OWN bindings and nothing else — no workspace, no composition, no projection —
/// so a part answered differently in two states states each state's own pictures rather than the head's.
/// </summary>
public static class AuthoredDonorRows
{
    /// <summary>The per-submesh donor texture rows one edit ships.</summary>
    public static List<SubmeshTextures>? Rows(IReadOnlyList<EditOutputRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var result = new List<SubmeshTextures>();
        foreach (var group in rows.Where(r => r.Slot.Domain == TargetSlotDomain.EditOutput
                && r.Slot.SubmeshIndex is not null && r.Slot.Input is
                TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                    or TargetInputKind.Blend or TargetInputKind.Texture
                    or TargetInputKind.RmoAlpha or TargetInputKind.Ramp)
            .GroupBy(r => r.Slot.SubmeshIndex!.Value).OrderBy(g => g.Key))
        {
            var row = new SubmeshTextures { Submesh = group.Key };
            var fixedKinds = new HashSet<TargetInputKind>();
            foreach (var value in group)
            {
                if (value.Slot.Input == TargetInputKind.RmoAlpha)
                {
                    row.RmoAlpha = value.Asset?.Value?.Value switch
                    {
                        "rebuild-from-stock" => RmoAlphaAnswer.Rebuild,
                        "ship-as-authored" => RmoAlphaAnswer.ShipAsAuthored,
                        _ => null,
                    };
                    continue;
                }
                if (value.Slot.Input == TargetInputKind.Ramp && KeepsOwnRamp(value.Binding))
                {
                    // The decision is about the replacement's OWN slot: the submesh keeps whatever its
                    // installed material binds there, whether that is the game's own ramp or one this
                    // project already replaced it with, and it ships nothing of its own either way. Written
                    // through the row's own route so the file and its provenance cannot disagree.
                    row.KeepOwnRamp();
                    continue;
                }
                var origin = Origin(value.Binding, value.Slot, value.Asset);
                bool additionalExactFixed = value.Slot.Input is TargetInputKind.BaseColor
                        or TargetInputKind.Normal or TargetInputKind.Rmo or TargetInputKind.Blend
                    && !string.IsNullOrWhiteSpace(value.Slot.ShaderProperty)
                    && !fixedKinds.Add(value.Slot.Input);
                if (additionalExactFixed)
                    AddProperty(row, value.Slot.ShaderProperty!, value.Asset?.File, origin);
                else
                    SetMap(row, value.Slot.Input, value.Asset?.File, origin, value.Slot.ShaderProperty);
                if (value.Slot.Input == TargetInputKind.Ramp && value.Asset?.Source?.GameAsset is { } game)
                    row.RampCarried = new CarriedRamp
                    {
                        Bundle = game.LogicalBundle,
                        Name = game.Name ?? "",
                        PathId = game.PathId,
                    };
            }
            result.Add(row);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>How many submeshes one replacement has, and what each is called, read off the SAME
    /// edit-output layout the donor rows come from. The count is the load-bearing half: the map-card drop,
    /// the adoption, the derivation's warning and the send-back's restatement all measure a submesh index
    /// against it, and a replacement whose list is absent is one they all read as having no submeshes at all.
    ///
    /// <para>The names are this app's own export names — a round trip that leaves Blender's slot list alone
    /// brings exactly these back, which is what the released record held. The model keeps a replacement's
    /// LAYOUT and not the returned material's label, so a slot list the modder renamed in Blender is no
    /// longer carried; nothing binds on the name, and the count it stood beside is exact.</para>
    ///
    /// <para>Dense to the highest submesh the layout holds: a donor row's index IS a position in this list,
    /// so a layout with a gap in it still has to be measurable at its far end.</para></summary>
    public static List<string>? MaterialNames(IReadOnlyList<EditOutputRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int count = 0;
        foreach (var row in rows)
            if (row.Slot.Domain == TargetSlotDomain.EditOutput && row.Slot.SubmeshIndex is { } submesh
                && row.Slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal
                    or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Texture
                    or TargetInputKind.RmoAlpha or TargetInputKind.Ramp)
                count = Math.Max(count, submesh + 1);
        return count == 0 ? null
            : Enumerable.Range(0, count).Select(Mesh.MeshGltf.SubmeshMaterialName).ToList();
    }

    /// <summary>The keep-the-game's-ramp decision: a source slot naming a game slot by address, with no edit
    /// definition of its own. It is the one binding a replacement's ramp slot can hold that says "keep
    /// whatever the carrier binds here" out loud, which is what tells the decision apart from a ramp nobody
    /// has answered yet.</summary>
    private static bool KeepsOwnRamp(Binding binding) =>
        binding is { Kind: BindingKind.SourceSlot, SourceSlot.EditDefinitionId: null };

    /// <summary>What one file-less donor slot asks the released shape for.
    ///
    /// <para>The ramp is the one input whose two file-less answers are different things. Every other input
    /// reads "the part's own value keeps drawing" off both, so they collapse onto
    /// <see cref="SlotOrigin.VanillaOwn"/> — which is also what the build reads a normal or RMO slot's
    /// silence as under a Replace. A ramp slot's silence is a QUESTION the conversion offers to fill
    /// (<see cref="Migoto.RampConversion"/>), and its <see cref="SlotOrigin.VanillaOwn"/> is the recorded
    /// answer that pass leaves alone; collapsing them would turn every unanswered ramp into a decision the
    /// modder never gave. The decision is said by <see cref="KeepsOwnRamp"/> and written above, so what
    /// reaches here is the question.</para></summary>
    private static SlotOrigin Origin(Binding binding, TargetSlot slot, ProjectAsset? asset)
    {
        if (asset is not null) return SlotOrigin.Authored;
        if (binding.Kind == BindingKind.Neutral) return SlotOrigin.ExplicitNeutral;
        if (slot.Input == TargetInputKind.Ramp) return SlotOrigin.None;
        return binding.Kind is BindingKind.TargetGameValue or BindingKind.InheritedLiveCarrier
            ? SlotOrigin.VanillaOwn : SlotOrigin.None;
    }

    private static void SetMap(SubmeshTextures row, TargetInputKind input, string? file, SlotOrigin origin,
        string? shaderProperty)
    {
        switch (input)
        {
            case TargetInputKind.BaseColor: row.Albedo = file; row.AlbedoOrigin = origin; break;
            case TargetInputKind.Normal: row.Normal = file; row.NormalOrigin = origin; break;
            case TargetInputKind.Rmo: row.Rmo = file; row.RmoOrigin = origin; break;
            case TargetInputKind.Blend: row.Blend = file; row.BlendOrigin = origin; break;
            case TargetInputKind.Ramp: row.Ramp = file; row.RampOrigin = origin; break;
            case TargetInputKind.Texture when !string.IsNullOrWhiteSpace(shaderProperty):
                row.Textures ??= new List<PropertyTextureBinding>();
                row.Textures.Add(new PropertyTextureBinding
                {
                    ShaderProperty = shaderProperty,
                    File = file,
                    Origin = origin,
                });
                break;
        }
    }

    private static void AddProperty(SubmeshTextures row, string shaderProperty, string? file,
        SlotOrigin origin)
    {
        row.Textures ??= new List<PropertyTextureBinding>();
        row.Textures.Add(new PropertyTextureBinding
        {
            ShaderProperty = shaderProperty,
            File = file,
            Origin = origin,
        });
    }
}
