using System;
using System.Collections.Generic;
using System.Text.Json;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The two persisted shapes the ramp joins — the project's donor texture rows and a built mod's
/// <c>repair.json</c>. Both are read by things already in the world, so the bar is that a project and a mod
/// carrying no ramp are written exactly as they were before one existed.
/// </summary>
public class RampRecordShapeTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ---- the project's donor rows ------------------------------------------------------------------

    [Fact]
    public void A_row_with_no_ramp_serialises_exactly_as_it_did_before_the_slot_existed()
    {
        var row = new SubmeshTextures
        {
            Submesh = 2,
            Albedo = "textures/body_s2_base.png",
            AlbedoOrigin = SlotOrigin.Authored,
            NormalOrigin = SlotOrigin.VanillaOwn,
        };

        var json = JsonSerializer.Serialize(row, Json);

        Assert.DoesNotContain("ramp", json);
        Assert.Contains("\"albedo\": \"textures/body_s2_base.png\"", json);
    }

    [Fact]
    public void A_row_from_a_project_that_predates_ramps_reads_as_keeping_the_games_own()
    {
        var row = JsonSerializer.Deserialize<SubmeshTextures>(
            "{\"submesh\":0,\"albedo\":\"a.png\",\"rmo_origin\":\"ExplicitNeutral\"}")!;

        Assert.Null(row.Ramp);
        Assert.Equal(SlotOrigin.None, row.RampAsk);
        Assert.False(row.RampAsk.IsAsk());
    }

    [Fact]
    public void A_named_ramp_file_settles_the_ask_the_way_every_other_slot_does()
    {
        var row = new SubmeshTextures { Submesh = 0, Ramp = "textures/body_s0_ramp.dds" };
        Assert.Equal(SlotOrigin.Authored, row.RampAsk);

        var json = JsonSerializer.Serialize(row, Json);
        Assert.Contains("\"ramp\": \"textures/body_s0_ramp.dds\"", json);
        Assert.Equal("textures/body_s0_ramp.dds", JsonSerializer.Deserialize<SubmeshTextures>(json)!.Ramp);
    }

    /// <summary>The build refuses a file it can't read as a ramp, so a slot naming one must be reachable by
    /// the dangling-file check every other slot's file is.</summary>
    [Fact]
    public void A_ramp_file_is_one_of_the_edits_referenced_files()
    {
        var edit = new BuildWorkItem
        {
            Character = "Nadia", Outfit = "NadiaAA01", Mesh = "c_nadia01_body_lod0",
            Verb = EditVerbs.Replace,
            Gate = new EditGate(null, Array.Empty<KeyRef>(), null, BuildEmissionGate.Unconditional),
            Textures = new List<SubmeshTextures>
            {
                new() { Submesh = 0, Albedo = "a.png", Ramp = "r.dds" },
            },
        };

        Assert.Contains("r.dds", edit.ReferencedFiles());
    }

    // ---- the project's ramp picks on unreplaced materials -------------------------------------------

    /// <summary>Identity is the MATERIAL: subject, part, material name — never a texture hash, which reads
    /// too little of a ramp for two of them to differ on it.</summary>
    [Fact]
    public void A_stock_pick_is_keyed_on_the_material_and_holds_one_entry_per_one()
    {
        var p = new ModProject();
        p.SetStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_body", "textures/warm.dds");
        p.SetStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_face", "textures/cool.dds");
        // …and a second pick on one material replaces rather than doubling
        p.SetStockRamp("NADIA", "nadiaaa01", "C_NADIA01_BODY_LOD0", "M_BODY", "textures/warmer.dds");

        Assert.Equal(2, p.StockRamps!.Count);
        Assert.Equal("textures/warmer.dds",
            p.FindStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_body")!.Ramp);
        Assert.Equal("textures/cool.dds",
            p.FindStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_face")!.Ramp);
    }

    /// <summary>The slot's other state is having no entry at all — and a project back in that state is
    /// written exactly as one that never picked a ramp, so no existing manifest gains a key.</summary>
    [Fact]
    public void Clearing_the_last_pick_leaves_the_manifest_as_it_was()
    {
        var p = new ModProject();
        Assert.Null(p.StockRamps);
        Assert.DoesNotContain("stock_ramps", JsonSerializer.Serialize(p, Json));

        p.SetStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_body", "textures/warm.dds");
        var json = JsonSerializer.Serialize(p, Json);
        Assert.Contains("stock_ramps", json);
        var back = JsonSerializer.Deserialize<ModProject>(json)!;
        Assert.Equal("m_body", Assert.Single(back.StockRamps!).Material);

        back.SetStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_body", null);
        Assert.Null(back.StockRamps);
        Assert.DoesNotContain("stock_ramps", JsonSerializer.Serialize(back, Json));
    }

    /// <summary>Dropping a subject takes its picks with it, so a re-add doesn't resurface a ramp on a part
    /// the modder stopped touching.</summary>
    [Fact]
    public void A_removed_subject_takes_its_picks_with_it()
    {
        var p = new ModProject();
        p.SetStockRamp("Nadia", "NadiaAA01", "c_nadia01_body_lod0", "m_body", "textures/warm.dds");
        p.SetStockRamp("Nadia", "NadiaAA02", "c_nadia02_body_lod0", "m_body", "textures/cool.dds");

        Assert.Equal(1, p.RemoveSubjectStockRamps("nadia", "nadiaaa01"));
        Assert.Equal("NadiaAA02", Assert.Single(p.StockRamps!).Outfit);
        Assert.Equal(1, p.RemoveSubjectStockRamps("Nadia", "NadiaAA02"));
        Assert.Null(p.StockRamps);
    }

    // ---- repair.json --------------------------------------------------------------------------------

    [Fact]
    public void A_mod_shipping_no_ramp_writes_the_repair_record_it_always_did()
    {
        var rows = RepairData.Submeshes(
            new[] { new SubmeshTextures { Submesh = 0, Albedo = "a.png" } },
            (_, which) => which == DonorMapSlot.BaseColor ? "donor_s0_a.dds" : null);

        var record = Assert.Single(rows);
        Assert.Null(record.Ramp);
        Assert.DoesNotContain("ramp", JsonSerializer.Serialize(record, Json));
    }

    [Fact]
    public void A_shipped_ramp_is_recorded_under_its_own_slot_with_the_file_it_became()
    {
        var rows = RepairData.Submeshes(
            new[] { new SubmeshTextures { Submesh = 1, Ramp = "textures/body_s1_ramp.dds" } },
            (_, which) => which == DonorMapSlot.Ramp ? "donor_swap_s1_ramp.dds" : null);

        var record = Assert.Single(rows);
        Assert.Null(record.Albedo);
        Assert.Null(record.Normal);
        Assert.Null(record.Rmo);
        Assert.Equal(SlotOrigin.Authored.ToString(), record.Ramp!.Origin);
        Assert.Equal("donor_swap_s1_ramp.dds", record.Ramp.File);
    }

    [Fact]
    public void A_blend_only_submesh_survives_in_repair_data()
    {
        var rows = RepairData.Submeshes(
            new[] { new SubmeshTextures { Submesh = 2, Blend = "textures/effect.png" } },
            (_, which) => which == DonorMapSlot.Blend ? "donor_s2_b.dds" : null);

        var record = Assert.Single(rows);
        Assert.Null(record.Albedo);
        Assert.Null(record.Normal);
        Assert.Null(record.Rmo);
        Assert.Null(record.Ramp);
        Assert.Equal(SlotOrigin.Authored.ToString(), record.Blend!.Origin);
        Assert.Equal("donor_s2_b.dds", record.Blend.File);
        Assert.Contains("\"blend\"", JsonSerializer.Serialize(record, Json));
    }

    /// <summary>A stock ramp is named the way every other stock texture is — by the (bundle, name) pair the
    /// game carries it under, since same-named textures in different bundles are different assets.</summary>
    [Fact]
    public void A_ramp_slot_can_name_the_game_texture_it_stands_in_for()
    {
        var rows = RepairData.Submeshes(
            new[] { new SubmeshTextures { Submesh = 0, Ramp = "r.dds" } },
            (_, _) => "donor_swap_s0_ramp.dds",
            (_, which) => which == DonorMapSlot.Ramp
                ? new RepairData.StockTextureRef("bundle7", "ramp_warm") : null);

        var stock = Assert.Single(rows).Ramp!.Stock;
        Assert.Equal("bundle7", stock!.Bundle);
        Assert.Equal("ramp_warm", stock.Name);
    }

    /// <summary>A carried ramp and a picked one are both an ask on the row, and only the provenance
    /// separates them. It has to OUTRANK the slot's own origin, or a shading decision nobody made would
    /// read back as a file the modder chose — and a reader would offer to preserve it.</summary>
    [Fact]
    public void A_carried_ramp_records_the_carried_origin_over_the_rows_own()
    {
        var carried = new SubmeshTextures { Submesh = 0 };
        carried.SetRamp("textures/body_s0_ramp.dds", new CarriedRamp { Bundle = "b7", Name = "ramp_warm" });
        var picked = new SubmeshTextures { Submesh = 1 };
        picked.SetRamp("textures/chosen.dds");

        var rows = RepairData.Submeshes(new[] { carried, picked },
            (t, _) => $"donor_swap_s{t.Submesh}_ramp.dds",
            (t, _) => t.RampIsCarried
                ? new RepairData.StockTextureRef(t.RampCarried!.Bundle, t.RampCarried.Name) : null,
            (t, which) => which == DonorMapSlot.Ramp && t.RampIsCarried
                ? RepairData.CarriedFromDonor : null);

        Assert.Equal(RepairData.CarriedFromDonor, rows[0].Ramp!.Origin);
        Assert.Equal("ramp_warm", rows[0].Ramp!.Stock!.Name);
        // the modder's own pick keeps its ask, and names no game texture: nothing stood in for one
        Assert.Equal(SlotOrigin.Authored.ToString(), rows[1].Ramp!.Origin);
        Assert.Null(rows[1].Ramp!.Stock);
    }

    /// <summary>The ramp slot's two persisted states, and the opt-out that is neither. Written through the
    /// one route, so no flow can leave a file with no provenance beside it.</summary>
    [Fact]
    public void The_ramp_slots_write_route_keeps_file_origin_and_provenance_together()
    {
        var row = new SubmeshTextures { Submesh = 0 };
        Assert.Equal(SlotOrigin.None, row.RampAsk);

        row.SetRamp("textures/r.dds", new CarriedRamp { Bundle = "b", Name = "n" });
        Assert.Equal(SlotOrigin.Authored, row.RampAsk);
        Assert.True(row.RampIsCarried);

        // a hand pick over a carried one takes the provenance with it
        row.SetRamp("textures/chosen.dds");
        Assert.False(row.RampIsCarried);
        Assert.Null(row.RampCarried);

        row.KeepOwnRamp();
        Assert.Null(row.Ramp);
        Assert.Equal(SlotOrigin.VanillaOwn, row.RampOrigin);
        Assert.False(row.RampIsCarried);
    }

    /// <summary>The provenance is additive: a project written before it loads with the slot it always had,
    /// and a row that carries none writes none.</summary>
    [Fact]
    public void The_carried_provenance_is_additive_json()
    {
        var old = JsonSerializer.Deserialize<SubmeshTextures>(
            """{"submesh":0,"ramp":"textures/r.dds"}""")!;
        Assert.Equal("textures/r.dds", old.Ramp);
        Assert.Null(old.RampCarried);
        Assert.False(old.RampIsCarried);
        Assert.DoesNotContain("ramp_carried", JsonSerializer.Serialize(old, Json));

        old.SetRamp("textures/r.dds", new CarriedRamp { Bundle = "b7", Name = "ramp_warm" });
        var json = JsonSerializer.Serialize(old, Json);
        Assert.Contains("ramp_carried", json);
        Assert.Equal("ramp_warm", JsonSerializer.Deserialize<SubmeshTextures>(json)!.RampCarried!.Name);
    }

    /// <summary>A row every slot of which asks for nothing is still dropped whole — the ramp does not turn
    /// an empty row into a written one.</summary>
    [Fact]
    public void A_row_asking_for_nothing_at_all_is_still_dropped()
    {
        Assert.Empty(RepairData.Submeshes(
            new[] { new SubmeshTextures { Submesh = 0 } }, (_, _) => null));
    }
}
