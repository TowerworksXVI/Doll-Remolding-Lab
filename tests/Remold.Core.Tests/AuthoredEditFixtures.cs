using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The schema-2 projects the Edit-session command tests act on. Each one is asserted valid before a
/// session ever sees it, so a test that fails is failing on the command rather than on its starting point.
/// </summary>
internal static class AuthoredEditFixtures
{
    internal static TargetPart Body => Part("c_vesna_body_lod0");
    internal static TargetPart Hair => Part("c_vesna_hair_lod0");
    internal static TargetPart Cape => Part("c_vesna_cape_lod0");

    internal static TargetPart Part(string rendererSlot) => new()
    {
        Subject = "Vesna",
        Outfit = "VesnaSSR01",
        RendererSlot = rendererSlot,
    };

    /// <summary>The pinned one-part project: two content edits over one geometry and one ramp slot.</summary>
    internal static AuthoredProject Golden() => AuthoredProjectSerializer.Load(
        Path.Combine(AppContext.BaseDirectory, "Project", "golden", "authored_project_v2.json"));

    /// <summary>One content edit over the part's game-domain slots.</summary>
    internal static AuthoredProject Saved()
    {
        var project = Golden();
        project.EditDefinitions.RemoveAll(edit => edit.Id == "edit-short");
        project.ProjectAssets.RemoveAll(asset => asset.Id is "mesh-short" or "ramp-cool");
        return Valid(project);
    }

    /// <summary>A part the project knows the slots of and has never authored an answer for.</summary>
    internal static AuthoredProject SlotsOnly()
    {
        var project = Golden();
        project.EditDefinitions.Clear();
        project.Always.Clear();
        return Valid(project);
    }

    /// <summary>One edit that owns its own material-input slots, one of which takes its value from the
    /// other — the shape a duplicate has to re-point and a delete has to take with it.</summary>
    internal static AuthoredProject WithOwnedSlots()
    {
        var project = Golden();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "skin-base", Kind = ProjectAssetKind.Picture, Label = "Skin", File = "textures/skin.png",
        });
        project.TargetSlots.Add(OwnedSlot(project, "slot-owned", 0));
        project.TargetSlots.Add(OwnedSlot(project, "slot-owned-2", 1));
        var edit = project.EditDefinitions.Single(e => e.Id == "edit-long");
        edit.Bindings.Add(new Binding
        {
            SlotId = "slot-owned", Kind = BindingKind.ProjectAsset, ProjectAssetId = "skin-base",
        });
        edit.Bindings.Add(new Binding
        {
            SlotId = "slot-owned-2",
            Kind = BindingKind.SourceSlot,
            SourceSlot = new BindingSourceSlot { SlotId = "slot-owned", EditDefinitionId = "edit-long" },
        });
        return Valid(project);
    }

    /// <summary>A second edit that takes its base colour from a slot the first edit owns.</summary>
    internal static AuthoredProject WithBorrowedSlot()
    {
        var project = WithOwnedSlots();
        var geometry = project.TargetSlots.Single(s => s.Id == "slot-geometry");
        var ramp = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-base",
            Part = Body,
            Tier = "lod0",
            SubmeshIndex = 0,
            MaterialSlotIndex = 0,
            Input = TargetInputKind.BaseColor,
            Renderer = geometry.Renderer,
            Material = ramp.Material,
        });
        project.EditDefinitions.Single(e => e.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = "slot-base", Kind = BindingKind.ProjectAsset, ProjectAssetId = "skin-base",
        });
        project.EditDefinitions.Single(e => e.Id == "edit-short").Bindings.Add(new Binding
        {
            SlotId = "slot-base",
            Kind = BindingKind.SourceSlot,
            SourceSlot = new BindingSourceSlot { SlotId = "slot-owned", EditDefinitionId = "edit-long" },
        });
        return Valid(project);
    }

    /// <summary>A second edit with an output slot of its OWN, taking its value from a slot the first edit
    /// owns. The borrowing slot belongs to nobody but the borrower — the shape a mesh-edited edit borrows in
    /// — so naming the source's edit and the slots that edit binds says nothing at all about the borrower.
    /// </summary>
    internal static AuthoredProject WithCrossEditBorrow()
    {
        var project = WithOwnedSlots();
        project.TargetSlots.Add(OwnedSlot(project, "slot-short-base", 0));
        project.EditDefinitions.Single(e => e.Id == "edit-short").Bindings.Add(new Binding
        {
            SlotId = "slot-short-base",
            Kind = BindingKind.SourceSlot,
            SourceSlot = new BindingSourceSlot { SlotId = "slot-owned", EditDefinitionId = "edit-long" },
        });
        return Valid(project);
    }

    /// <summary>A LINE of borrowings, written far link first: edit-short binds a picture on a slot of its
    /// own, and three of edit-long's slots take their value in a chain from it — chain-1 from that root,
    /// chain-2 from chain-1, chain-3 from chain-2.
    ///
    /// <para>Two things about it are deliberate. The chain sits on the edit that does NOT hold the root, so
    /// naming the borrowing edit is something only the reverse pass can do. And the three bindings are
    /// written in reverse — chain-3, then chain-2, then chain-1 — because the order they are read in is what
    /// decides how far one pass down the list gets: a pass that meets a link before its source has nothing
    /// to match yet.</para>
    ///
    /// <para>Three hops rather than two, and that is the point. The expansion reads both the before and the
    /// after project, so an untouched chain is listed twice over and one pass down that list is worth two
    /// hops for free. Only the third hop tells a single pass apart from a fixed point.</para></summary>
    internal static AuthoredProject WithBorrowChain()
    {
        var project = WithOwnedSlots();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "skin-root", Kind = ProjectAssetKind.Picture, Label = "Root", File = "textures/root.png",
        });
        project.TargetSlots.Add(OwnedSlot(project, "chain-root", 2));
        project.TargetSlots.Add(OwnedSlot(project, "chain-1", 3));
        project.TargetSlots.Add(OwnedSlot(project, "chain-2", 4));
        project.TargetSlots.Add(OwnedSlot(project, "chain-3", 5));
        project.EditDefinitions.Single(e => e.Id == "edit-short").Bindings.Add(new Binding
        {
            SlotId = "chain-root", Kind = BindingKind.ProjectAsset, ProjectAssetId = "skin-root",
        });
        var borrower = project.EditDefinitions.Single(e => e.Id == "edit-long");
        borrower.Bindings.Add(Borrow("chain-3", "chain-2", "edit-long"));
        borrower.Bindings.Add(Borrow("chain-2", "chain-1", "edit-long"));
        borrower.Bindings.Add(Borrow("chain-1", "chain-root", "edit-short"));
        return Valid(project);
    }

    private static Binding Borrow(string slotId, string sourceSlotId, string sourceEditDefinitionId) => new()
    {
        SlotId = slotId,
        Kind = BindingKind.SourceSlot,
        SourceSlot = new BindingSourceSlot
        {
            SlotId = sourceSlotId, EditDefinitionId = sourceEditDefinitionId,
        },
    };

    /// <summary>Three parts in the three states the key-group commands care about: the body with two edits
    /// and an always-on answer, the hair with one, and a cape the project knows only the slot of.</summary>
    internal static AuthoredProject MultiPart()
    {
        var project = Golden();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "mesh-hair", Kind = ProjectAssetKind.Geometry, Label = "Hair", File = "meshes/hair.glb",
        });
        project.TargetSlots.Add(GeometrySlot("slot-hair", Hair, 70002, 72002));
        project.TargetSlots.Add(GeometrySlot("slot-cape", Cape, 70003, 72003));
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-hair",
            Target = Hair,
            Label = "Braided",
            Bindings =
            {
                new Binding { SlotId = "slot-hair", Kind = BindingKind.ProjectAsset, ProjectAssetId = "mesh-hair" },
            },
        });
        project.Always.Add("edit-hair");
        return Valid(project);
    }

    private static TargetSlot OwnedSlot(AuthoredProject project, string id, int submeshIndex) => new()
    {
        Id = id,
        Part = Body,
        Tier = "lod0",
        SubmeshIndex = submeshIndex,
        MaterialSlotIndex = 0,
        Input = TargetInputKind.BaseColor,
        Domain = TargetSlotDomain.EditOutput,
        Renderer = project.TargetSlots.Single(s => s.Id == "slot-geometry").Renderer,
    };

    private static TargetSlot GeometrySlot(string id, TargetPart part, long renderer, long mesh) => new()
    {
        Id = id,
        Part = part,
        Tier = "lod0",
        Input = TargetInputKind.Geometry,
        Renderer = Reference(renderer, part.RendererSlot),
        Mesh = Reference(mesh, part.RendererSlot + "_mesh"),
    };

    private static GameAssetRef Reference(long pathId, string name) => new()
    {
        GameBuild = "26109",
        LogicalBundle = "characters/vesna_ssr01",
        PathId = pathId,
        Name = name,
    };

    private static AuthoredProject Valid(AuthoredProject project)
    {
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }
}
