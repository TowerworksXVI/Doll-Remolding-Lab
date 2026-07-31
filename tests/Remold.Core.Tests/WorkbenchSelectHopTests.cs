using System;
using System.Collections.Generic;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Build pane's row → Edit hop as the workbench serves it: which node a change lands on, what happens
/// when the tree can't serve the request yet, and that a request never outlives the mod it named. Plus the
/// same machinery's other user — the selection a step hop remembers and puts back.
/// </summary>
public class WorkbenchSelectHopTests
{
    private static WorkbenchVm NewVm() => new(
        project: () => new ModProject(),
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null);

    private static readonly WorkbenchSubjectRef Subject =
        new("Vesna", "VesnaSSR01", "c_vesna01_", new Outfit(0, "VesnaSSR01", OutfitKind.Base));

    /// <summary>One subject root carrying one part with <paramref name="materials"/> material children —
    /// the shape <c>BuildSubjectNode</c> produces, built by hand so the tree needs no game install.</summary>
    private static WorkbenchNodeVm SubjectNode(string slotName, int materials,
        Remold.Core.Workbench.SubjectMap?[]? rmoMaps = null)
    {
        var part = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Part,
            Title = "body",
            Subject = Subject,
            PartToken = "body",
            Recipe = new RecipePart("body", slotName, "addr", Array.Empty<RecipeTierSlot>()),
            // one entry per renderer slot — the game's own material count, which is what an edited mesh's
            // submesh list is measured against
            SubmeshBaseMaps = new Remold.Core.Workbench.SubjectMap?[materials],
            SubmeshRmoMaps = rmoMaps ?? new Remold.Core.Workbench.SubjectMap?[materials],
        };
        for (int i = 0; i < materials; i++)
            part.Children.Add(new WorkbenchNodeVm
            {
                Kind = WorkbenchNodeKind.Material,
                Title = $"mat{i}",
                Subject = Subject,
                PartToken = "body",
                MaterialIndex = i,
            });
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "Vesna", Subject = Subject };
        subject.Children.Add(part);
        return subject;
    }

    private static WorkbenchVm VmWithTree(int materials = 2)
    {
        var vm = NewVm();
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials));
        return vm;
    }

    [Fact]
    public void A_change_with_no_submesh_lands_on_the_part()
    {
        var vm = VmWithTree();

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0");

        Assert.Equal(WorkbenchNodeKind.Part, vm.SelectedNode?.Kind);
        Assert.False(vm.HasPendingSelect);
    }

    [Fact]
    public void A_retexture_lands_on_the_material_its_submesh_binds()
    {
        var vm = VmWithTree(materials: 3);

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", submesh: 1);

        Assert.Equal(WorkbenchNodeKind.Material, vm.SelectedNode?.Kind);
        Assert.Equal(1, vm.SelectedNode?.MaterialIndex);
    }

    [Fact]
    public void A_submesh_past_the_last_material_lands_on_the_last_one()
    {
        // renderer material order IS the submesh binding, and a shortfall repeats the last slot — the same
        // rule the build's retexture pass assigns maps by
        var vm = VmWithTree(materials: 2);

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", submesh: 7);

        Assert.Equal(1, vm.SelectedNode?.MaterialIndex);
    }

    [Fact]
    public void A_part_with_no_materials_at_all_still_takes_the_hop()
    {
        var vm = VmWithTree(materials: 0);

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", submesh: 0);

        Assert.Equal(WorkbenchNodeKind.Part, vm.SelectedNode?.Kind);
    }

    [Fact]
    public void A_part_the_tree_does_not_carry_leaves_the_selection_alone_and_says_so()
    {
        var vm = VmWithTree();

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", partLabel: "ghost");

        Assert.Null(vm.SelectedNode);
        Assert.False(vm.HasPendingSelect);   // asked and answered, even though nothing matched
        // the hop otherwise lands on the pane with no selection change and nothing said
        Assert.Equal("ghost isn't in this tree.", vm.Status);
    }

    [Fact]
    public void A_miss_with_no_label_names_the_mesh_slot()
    {
        var vm = VmWithTree();

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0");

        Assert.Equal("c_vesna01_ghost_lod0 isn't in this tree.", vm.Status);
    }

    // ---- the selection a step hop remembers ----

    [Fact]
    public void A_step_hop_puts_the_selected_part_back()
    {
        var vm = VmWithTree();
        vm.SelectedNode = vm.Nodes[0].Children[0];

        vm.Activate();                 // ② Edit re-entered: the tree is rebuilt whole, no node survives
        Assert.Null(vm.SelectedNode);
        Assert.True(vm.HasPendingSelect);

        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 2));
        vm.ApplyPendingSelect();

        Assert.Equal(WorkbenchNodeKind.Part, vm.SelectedNode?.Kind);
    }

    [Fact]
    public void A_step_hop_puts_the_selected_map_set_back()
    {
        var vm = VmWithTree(materials: 3);
        vm.SelectedNode = vm.Nodes[0].Children[0].Children[2];

        vm.Activate();
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 3));
        vm.ApplyPendingSelect();

        Assert.Equal(WorkbenchNodeKind.Material, vm.SelectedNode?.Kind);
        Assert.Equal(2, vm.SelectedNode?.MaterialIndex);
    }

    [Fact]
    public void A_step_hop_puts_the_selected_subject_back()
    {
        var vm = VmWithTree();
        vm.SelectedNode = vm.Nodes[0];

        vm.Activate();
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 2));
        vm.ApplyPendingSelect();

        Assert.Equal(WorkbenchNodeKind.Subject, vm.SelectedNode?.Kind);
    }

    [Fact]
    public void A_remembered_selection_whose_part_is_gone_says_nothing()
    {
        // nobody asked for it; only a hop the modder DID ask for reports a miss
        var vm = VmWithTree();
        vm.SelectedNode = vm.Nodes[0].Children[0];

        vm.Activate();
        vm.Nodes.Add(SubjectNode("c_vesna01_ghost_lod0", materials: 1));
        vm.ApplyPendingSelect();

        Assert.Null(vm.SelectedNode);
        Assert.DoesNotContain("isn't in this tree", vm.Status);
    }

    [Fact]
    public void A_row_hop_wins_over_the_selection_the_same_step_entry_remembered()
    {
        // the Build row sets the step (which remembers), then asks for its own part: the ask is what the
        // modder wanted, so it takes the held slot
        var vm = VmWithTree(materials: 3);
        vm.SelectedNode = vm.Nodes[0].Children[0];

        vm.Activate();
        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", submesh: 2);
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 3));
        vm.ApplyPendingSelect();

        Assert.Equal(2, vm.SelectedNode?.MaterialIndex);
    }

    // ---- the game's own submesh count ----

    [Fact]
    public void The_game_submesh_count_is_the_parts_renderer_slot_count()
    {
        var vm = VmWithTree(materials: 3);

        Assert.Equal(3, vm.GameSubmeshCount("Vesna", "VesnaSSR01", "c_vesna01_body_lod0"));
        // no part, no baseline — a caller that can only report a difference says nothing
        Assert.Null(vm.GameSubmeshCount("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0"));
    }

    // ---- the game's RMO per submesh ----

    /// <summary>An authored RMO's emissive mask is rebuilt off the part's stock RMO, and where the send-back
    /// carries no record of one this is the only place left that names it. "No RMO on that slot" and "the
    /// tree cannot speak for that slot" have to stay apart: the first has no mask to lose, the second is a
    /// mask that may have gone missing, and only the second is worth telling the modder about.</summary>
    [Fact]
    public void The_game_rmo_map_separates_a_slot_with_no_rmo_from_a_slot_it_cannot_answer_for()
    {
        var vm = NewVm();
        var rmo = new Remold.Core.Workbench.SubjectMap("_RMOTex", "c_vesna01_body_r", "bundle1");
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 2,
            rmoMaps: new[] { rmo, null }));

        Assert.Equal((true, rmo), vm.GameRmoMap("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", 0));
        Assert.Equal((true, null), vm.GameRmoMap("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", 1));
        // past the renderer's slots — where a send-back's added submesh lands
        Assert.Equal((false, null), vm.GameRmoMap("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", 2));
        Assert.Equal((false, null), vm.GameRmoMap("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", 0));
    }

    [Fact]
    public void A_hop_that_arrives_before_the_tree_is_held_until_it_lands()
    {
        var vm = NewVm();

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", submesh: 0);
        Assert.True(vm.HasPendingSelect);
        Assert.Null(vm.SelectedNode);

        // the rebuild lands its tree and applies what was held
        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 2));
        vm.ApplyPendingSelect();

        Assert.Equal(0, vm.SelectedNode?.MaterialIndex);
        Assert.False(vm.HasPendingSelect);
    }

    [Fact]
    public void Closing_the_mod_drops_a_hop_the_tree_never_served()
    {
        // otherwise the request named a part of mod A and applies to whatever mod B's tree happens to
        // carry under the same name
        var vm = NewVm();
        vm.RequestSelectPart("Vesna", "VesnaSSR01", "c_vesna01_body_lod0");
        Assert.True(vm.HasPendingSelect);

        vm.Reset();
        Assert.False(vm.HasPendingSelect);

        vm.Nodes.Add(SubjectNode("c_vesna01_body_lod0", materials: 1));
        vm.ApplyPendingSelect();

        Assert.Null(vm.SelectedNode);
    }
}
