using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Recording the submesh layout a replacement returns with: the places an edit's own pictures, ramp
/// and emissive-mask answer live. Every assertion here is about the SHAPE of those places — what they
/// address and who holds them — because the build, the projection and the planner all read them by shape.
/// </summary>
public sealed class AuthoredEditSessionOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-edit-outputs-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Every submesh answers only for the installed material's actual properties, and each place
    /// addresses the edit's own
    /// output rather than anything installed. The renderer and mesh come off the part's lod0 geometry slot:
    /// a replacement draws through the game object the part already addresses, which is what a build has to
    /// find it by.</summary>
    [Fact]
    public void A_recorded_layout_gives_every_submesh_one_place_per_input()
    {
        var session = SessionWithInventory();
        var anchor = session.Snapshot().TargetSlots.Single(slot => slot.Id == "slot-geometry");

        session.RecordReplacementOutputs("edit-long", 2);

        var outputs = Outputs(session, "edit-long");
        Assert.Equal(new[]
        {
            "0/BaseColor/_BaseMap", "0/Blend/_BlendTex", "0/Texture/_DetailAlbedo",
            "0/Ramp/_RampMap", "0/Rmo/_RMOTex", "0/RmoAlpha/",
            "1/BaseColor/_BaseMap", "1/Blend/_BlendTex", "1/Texture/_DetailAlbedo",
            "1/Ramp/_RampMap", "1/Rmo/_RMOTex", "1/RmoAlpha/",
        }, outputs.Select(slot => $"{slot.SubmeshIndex}/{slot.Input}/{slot.ShaderProperty}"));
        Assert.DoesNotContain(outputs, slot => slot.Input == TargetInputKind.Normal);
        foreach (var slot in outputs)
        {
            Assert.Equal(TargetSlotDomain.EditOutput, slot.Domain);
            Assert.Null(slot.Tier);
            Assert.Null(slot.Material);
            Assert.Equal(0, slot.MaterialSlotIndex);
            Assert.Equal(anchor.Renderer.PathId, slot.Renderer.PathId);
            Assert.Equal(anchor.Mesh!.PathId, slot.Mesh!.PathId);
        }
    }

    /// <summary>A place arrives asking the live carrier for its value, which is what an unauthored submesh
    /// has always done: it draws the part's own stock map. The layout is where answers go, never an answer
    /// itself.</summary>
    [Fact]
    public void A_recorded_place_asks_the_live_carrier_until_something_answers_it()
    {
        var session = SessionWithInventory();

        session.RecordReplacementOutputs("edit-long", 1);

        var slots = session.Slots("edit-long")
            .Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput).ToList();
        Assert.Equal(6, slots.Count);
        Assert.All(slots, state => Assert.Equal(BindingKind.InheritedLiveCarrier, state.Binding.Kind));
        Assert.All(slots, state => Assert.Null(state.ProjectAsset));
    }

    /// <summary>A re-send that returns the same submeshes keeps every answer authored against them: the
    /// layout is what changed, and it did not.</summary>
    [Fact]
    public void Recording_the_same_layout_twice_leaves_the_answers_alone()
    {
        var session = SessionWithInventory();
        session.SetRootDir(_root);
        session.RecordReplacementOutputs("edit-long", 2);
        string base_ = Outputs(session, "edit-long")
            .First(slot => slot.SubmeshIndex == 1 && slot.Input == TargetInputKind.BaseColor).Id;
        PublishPicture(session, base_, "Skin", 10);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        session.RecordReplacementOutputs("edit-long", 2);

        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>A layout that shrinks takes the places past its end with it, bindings included — a submesh
    /// the replacement no longer has is somewhere nothing draws. What the surviving submeshes answer is
    /// untouched, and the file the dropped place named is still the user's.</summary>
    [Fact]
    public void A_shrinking_layout_drops_the_submeshes_the_replacement_no_longer_has()
    {
        var session = SessionWithInventory();
        session.SetRootDir(_root);
        session.RecordReplacementOutputs("edit-long", 2);
        var outputs = Outputs(session, "edit-long");
        string kept = outputs.First(slot => slot.SubmeshIndex == 0 && slot.Input == TargetInputKind.Rmo).Id;
        string dropped = outputs.First(slot => slot.SubmeshIndex == 1
            && slot.Input == TargetInputKind.BaseColor).Id;
        PublishPicture(session, kept, "Rough", 20);
        string gone = PublishPicture(session, dropped, "Skin", 30);

        session.RecordReplacementOutputs("edit-long", 1);

        var after = session.Snapshot();
        Assert.Equal(6, Outputs(session, "edit-long").Count);
        Assert.DoesNotContain(after.TargetSlots, slot => slot.Id == dropped);
        Assert.DoesNotContain(after.EditDefinitions.SelectMany(edit => edit.Bindings),
            binding => binding.SlotId == dropped);
        Assert.Equal(BindingKind.ProjectAsset, session.Slots("edit-long")
            .Single(state => state.Slot.Id == kept).Binding.Kind);
        Assert.Contains(after.ProjectAssets, asset => asset.Id == gone);
    }

    /// <summary>Another edit taking a value from a place this one is about to drop refuses the whole
    /// command by name, the way deleting the edit that owns it does. Nothing of the shrink survives the
    /// refusal.</summary>
    [Fact]
    public void A_shrink_refuses_while_another_edit_takes_a_value_from_what_it_would_drop()
    {
        var session = SessionWithInventory();
        session.RecordReplacementOutputs("edit-long", 2);
        string borrowed = Outputs(session, "edit-long")
            .First(slot => slot.SubmeshIndex == 1 && slot.Input == TargetInputKind.BaseColor).Id;
        string second = session.CreateEdit(AuthoredEditFixtures.Body, "Short body");
        session.RecordReplacementOutputs(second, 2);
        string into = Outputs(session, second)
            .First(slot => slot.SubmeshIndex == 0 && slot.Input == TargetInputKind.BaseColor).Id;
        session.ChooseSourceSlot(second, into, borrowed, "edit-long");
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<InvalidOperationException>(
            () => session.RecordReplacementOutputs("edit-long", 1));

        Assert.Contains("Short body", error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>A part's second edit gets its own places, so two replacements of one part never write into
    /// each other's maps.</summary>
    [Fact]
    public void Each_edit_records_its_own_places()
    {
        var session = SessionWithInventory();
        string second = session.CreateEdit(AuthoredEditFixtures.Body, "Short body");

        session.RecordReplacementOutputs("edit-long", 1);
        session.RecordReplacementOutputs(second, 1);

        Assert.Empty(Outputs(session, "edit-long").Select(slot => slot.Id)
            .Intersect(Outputs(session, second).Select(slot => slot.Id), StringComparer.Ordinal));
    }

    /// <summary>A hide has no replacement of its own — it is the answer that takes the part off screen —
    /// and asking it for one is refused by name.</summary>
    [Fact]
    public void A_hide_has_no_replacement_to_record_outputs_for()
    {
        var session = SessionWithInventory();
        string hide = session.CreateHideEdit(AuthoredEditFixtures.Body);

        var error = Assert.Throws<InvalidOperationException>(
            () => session.RecordReplacementOutputs(hide, 1));

        Assert.Contains("hides the part, so it has no replacement", error.Message);
    }

    /// <summary>A layout of no submeshes is the empty one, not a refusal: it is what a replacement that
    /// returned nothing renderable leaves behind, and the places it used to hold go with it.</summary>
    [Fact]
    public void A_layout_of_no_submeshes_leaves_no_places_behind()
    {
        var session = SessionWithInventory();
        session.RecordReplacementOutputs("edit-long", 2);

        session.RecordReplacementOutputs("edit-long", 0);

        Assert.Empty(Outputs(session, "edit-long"));
    }

    /// <summary>The replacement's MATERIAL LIST, which four surfaces measure a submesh index against: the
    /// map-card drop's range, an adoption's landing, the extra material rows, and the repair record a built
    /// mod ships. Nothing writes it — the fold re-derives it from the layout recorded above, so recording
    /// two submeshes IS the list being two long, and a fixture never has to invent one.
    ///
    /// <para>The names are this app's own export names, which is what came back from a round trip that left
    /// Blender's slot list alone.</para></summary>
    [Fact]
    public void A_recorded_layout_is_what_the_replacements_material_list_is_derived_from()
    {
        var session = SessionWithInventory();
        session.RecordReplacementOutputs("edit-long", 2);

        Assert.Equal(new[] { "gf2_submesh0", "gf2_submesh1" },
            AuthoredDonorRows.MaterialNames(DonorRows(session, "edit-long"))!.ToArray());
    }

    /// <summary>A replacement with no layout recorded has no material list either: an adoption, a drop and
    /// the repair record all read that as "no submeshes to land on", which is the truth about it.</summary>
    [Fact]
    public void A_replacement_with_no_recorded_layout_has_no_material_list()
    {
        var session = SessionWithInventory();

        Assert.Null(AuthoredDonorRows.MaterialNames(DonorRows(session, "edit-long")));
    }

    /// <summary>One edit's bindings as the runtime compiler folds them.</summary>
    private static List<EditOutputRow> DonorRows(AuthoredEditSession session, string editId)
    {
        var project = session.Snapshot();
        var slots = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        return project.EditDefinitions.Single(edit => edit.Id == editId).Bindings
            .Select(binding => new EditOutputRow(binding, slots[binding.SlotId],
                binding.ProjectAssetId is null ? null
                    : project.ProjectAssets.SingleOrDefault(a => a.Id == binding.ProjectAssetId)))
            .ToList();
    }

    [Fact]
    public void A_negative_layout_is_refused()
    {
        var session = SessionWithInventory();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RecordReplacementOutputs("edit-long", -1));
    }

    /// <summary>A part the project has never opened has no game object to record a replacement against, and
    /// that is said out loud rather than minting places that address nothing.</summary>
    [Fact]
    public void A_part_with_no_game_geometry_slot_cannot_record_outputs()
    {
        var project = InventoryProject();
        project.TargetSlots.RemoveAll(slot => slot.Input == TargetInputKind.Geometry);
        foreach (var edit in project.EditDefinitions)
            edit.Bindings.RemoveAll(binding => !project.TargetSlots.Any(slot =>
                string.Equals(slot.Id, binding.SlotId, StringComparison.Ordinal)));
        var session = new AuthoredEditSession(project);

        var error = Assert.Throws<InvalidOperationException>(
            () => session.RecordReplacementOutputs("edit-long", 1));

        Assert.Contains("has no mesh in this mod to record a replacement against", error.Message);
    }

    /// <summary>Every edit-output place one edit holds, in the order the layout files them.</summary>
    private static List<TargetSlot> Outputs(AuthoredEditSession session, string editDefinitionId) =>
        session.Slots(editDefinitionId).Where(state =>
                state.Slot.Domain == TargetSlotDomain.EditOutput)
            .Select(state => state.Slot).ToList();

    /// <summary>A one-position installed material whose inventory deliberately omits Normal, includes Effect
    /// and one ordinary property, and binds a real RMO. Replacement outputs must mirror this list; RMO alpha
    /// is the only derived companion.</summary>
    private static AuthoredEditSession SessionWithInventory() => new(InventoryProject());

    private static AuthoredProject InventoryProject()
    {
        var project = AuthoredEditFixtures.Saved();
        var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        ramp.ShaderProperty = "_RampMap";
        var additions = new[]
        {
            Slot("slot-base", TargetInputKind.BaseColor, "_BaseMap"),
            Slot("slot-rmo", TargetInputKind.Rmo, "_RMOTex"),
            Slot("slot-blend", TargetInputKind.Blend, "_BlendTex"),
            Slot("slot-detail", TargetInputKind.Texture, "_DetailAlbedo"),
        };
        project.TargetSlots.AddRange(additions);
        var edit = project.EditDefinitions.Single(candidate => candidate.Id == "edit-long");
        foreach (var slot in additions)
            edit.Bindings.Add(new Binding { SlotId = slot.Id, Kind = BindingKind.TargetGameValue });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;

        TargetSlot Slot(string id, TargetInputKind input, string property) => new()
        {
            Id = id,
            Part = ramp.Part,
            Tier = ramp.Tier,
            SubmeshIndex = 0,
            MaterialSlotIndex = 0,
            Input = input,
            ShaderProperty = property,
            Renderer = ramp.Renderer,
            Material = ramp.Material,
        };
    }

    private string PublishPicture(AuthoredEditSession session, string slotId, string label, byte seed)
    {
        string source = Path.Combine(_root, $"source-{Guid.NewGuid():N}.png");
        Directory.CreateDirectory(_root);
        using (var image = new Image<Rgba32>(2, 2, new Rgba32(seed, seed, seed, 255)))
            image.SaveAsPng(source);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", slotId, source);
        return session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture, label,
            ProjectAssetIngress.Png).ProjectAssetId!;
    }
}
