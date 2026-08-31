using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>What an ordinary open-and-save does to intent the released workbench shape has no field for.
/// Since the 5b cutover the answer is "nothing": a save writes what the edit session holds, and the released
/// shape is a projection derived from it that no save reads back. The four cases below were each pinned as a
/// measured loss while the save was still derived from that shape; each one is now pinned as survival.</summary>
public sealed class AuthoredSaveRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-save-roundtrip-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A part with two content edits, one in each state of one key, saved and reopened. Both edits,
    /// the key and every slot under them are what the save writes, so they all come back.</summary>
    [Fact]
    public void Two_content_edits_for_one_part_survive_a_save_and_still_build()
    {
        string manifest = Saved();
        var authored = TwoEdits(manifest, secondEditLeads: false);

        var reopened = AuthoredProjectDocument.Load(manifest);
        reopened.Save(manifest);
        var after = AuthoredProjectSerializer.Load(manifest);

        Assert.Equal(authored.EditDefinitions.Select(edit => $"{edit.Id}/{edit.Kind}/{edit.Label}"),
            after.EditDefinitions.Select(edit => $"{edit.Id}/{edit.Kind}/{edit.Label}"));
        Assert.Equal(authored.TargetSlots.Select(Describe), after.TargetSlots.Select(Describe));
        Assert.Equal(Bindings(authored), Bindings(after));
        Assert.Equal(Groups(authored), Groups(after));

        var plan = AuthoredBuildPlanner.Plan(after, new AuthoredBuildPlannerTests.Backend());
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        var body = plan.Parts.Single(part => part.Target.SameAs(Body()));
        Assert.Equal(new[] { PlannedPartDisposition.Edit, PlannedPartDisposition.Edit },
            body.Operations.Select(operation => operation.Disposition).ToArray());
    }

    /// <summary>The same project with the NEWER edit leading the group — the case the released shape had no
    /// way to tell from the older one leading it, which is why the pre-cutover save lost the key, re-minted an
    /// edit onto an id a prior edit already held, and took the inputs only that edit answered for with it.
    ///
    /// <para>Schema 2 is what a save reads from now, so the whole distinction is simply kept: the group still
    /// leads with the newer edit, both edits still bind their own mesh, and every slot is still there.</para>
    /// </summary>
    [Fact]
    public void Two_content_edits_survive_a_save_with_the_newer_one_leading_the_group()
    {
        string manifest = Saved();
        var authored = TwoEdits(manifest, secondEditLeads: true);
        string leading = Assert.Single(authored.KeyGroups.Single().States[0].ActiveEditIds);

        var reopened = AuthoredProjectDocument.Load(manifest);
        reopened.Save(manifest);
        var after = AuthoredProjectSerializer.Load(manifest);

        Assert.Equal(Groups(authored), Groups(after));
        Assert.Equal(leading, Assert.Single(after.KeyGroups.Single().States[0].ActiveEditIds));
        Assert.DoesNotContain(leading, after.Always);
        Assert.Equal(authored.EditDefinitions.Select(edit => $"{edit.Id}/{edit.Kind}/{edit.Label}"),
            after.EditDefinitions.Select(edit => $"{edit.Id}/{edit.Kind}/{edit.Label}"));
        Assert.Equal(authored.TargetSlots.Select(Describe), after.TargetSlots.Select(Describe));
        Assert.Equal(Bindings(authored), Bindings(after));
        // The leading edit still binds its OWN mesh: nothing re-mints an edit onto an id another one holds.
        string mesh = after.EditDefinitions.Single(edit => edit.Id == leading)
            .Bindings.Select(binding => binding.ProjectAssetId).First(id => id is not null)!;
        var meshFile = after.ProjectAssets.Single(asset => asset.Id == mesh).File;
        Assert.StartsWith($"assets/edits/{leading}/geometry/", meshFile, StringComparison.Ordinal);
        Assert.EndsWith(".glb", meshFile, StringComparison.Ordinal);
        Assert.Contains(after.TargetSlots, slot => slot.Part.SameAs(Body())
            && slot.Input is TargetInputKind.BaseColor or TargetInputKind.Ramp);
    }

    /// <summary>A part opened from the install and left alone. The released shape has no field for a part with
    /// no change authored against it, which is why the pre-cutover save forgot it. Schema 2 holds it, and a
    /// save writes schema 2, so the slots the open filed are there when it comes back.</summary>
    [Fact]
    public void An_opened_part_survives_a_save()
    {
        string manifest = Saved();
        var session = new AuthoredEditSession(AuthoredProjectSerializer.Load(manifest));
        var skirt = Part("c_vesna_skirt_lod0");
        session.EnsurePartSlots(skirt, Resolve);
        var opened = session.Snapshot();
        Assert.Equal(2, opened.TargetSlots.Count(slot => slot.Part.SameAs(skirt)));
        Write(manifest, opened);

        var reopened = AuthoredProjectDocument.Load(manifest);
        reopened.Save(manifest);

        var after = AuthoredProjectSerializer.Load(manifest);
        Assert.Equal(opened.TargetSlots.Where(slot => slot.Part.SameAs(skirt)).Select(Describe),
            after.TargetSlots.Where(slot => slot.Part.SameAs(skirt)).Select(Describe));
    }

    /// <summary>An edit authored on a part the released manifest never held. The pre-cutover save refused it
    /// outright: the projection had no workspace target to mark edited, so nothing re-derived the part and
    /// the carried edit named slots the re-derivation did not have. Nothing is re-derived now — the save
    /// writes the edit and the slots under it as authored.</summary>
    [Fact]
    public void An_edit_on_an_opened_part_is_saved()
    {
        string manifest = Saved();
        var session = new AuthoredEditSession(AuthoredProjectSerializer.Load(manifest));
        var skirt = Part("c_vesna_skirt_lod0");
        session.EnsurePartSlots(skirt, Resolve);
        string edit = session.CreateEdit(skirt, "Skirt");
        string geometry = session.Slots(edit)
            .Single(slot => slot.Slot.Input == TargetInputKind.Geometry).Slot.Id;
        Directory.CreateDirectory(Path.Combine(_root, "meshes"));
        string source = Path.Combine(_root, "meshes", "skirt.glb");
        File.WriteAllBytes(source, new byte[] { 7 });
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), edit, geometry, source);
        var published = session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry, "Skirt",
            ProjectAssetIngress.Binary);
        Write(manifest, session.Snapshot());

        var reopened = AuthoredProjectDocument.Load(manifest);
        reopened.Save(manifest);

        var after = AuthoredProjectSerializer.Load(manifest);
        var kept = after.EditDefinitions.Single(candidate => candidate.Id == edit);
        Assert.Equal("Skirt", kept.Label);
        Assert.Contains(after.TargetSlots, slot => slot.Id == geometry);
        string asset = kept.Bindings.Single(binding => binding.SlotId == geometry).ProjectAssetId!;
        Assert.Equal(published.ProjectAssetId, asset);
        Assert.StartsWith($"assets/edits/{edit}/geometry/",
            after.ProjectAssets.Single(candidate => candidate.Id == asset).File, StringComparison.Ordinal);
    }

    /// <summary>Opening a part the adapter has already filed slots for, judged against the shape a real save
    /// produces rather than a hand-written one. Adaptation now files the full install answer, so opening the
    /// part is idempotent and the existing edit already gives each untouched game input its own value.</summary>
    [Fact]
    public void Opening_a_part_a_save_already_filed_keeps_its_slots_and_records_the_game_inputs()
    {
        string manifest = Saved();
        var session = new AuthoredEditSession(AuthoredProjectSerializer.Load(manifest));
        var before = session.Snapshot();

        session.EnsurePartSlots(Body(), Resolve);
        var after = session.Snapshot();

        Assert.Equal(before.TargetSlots.Select(Describe), after.TargetSlots.Select(Describe));
        var opened = after.TargetSlots.Single(slot => slot.Part.SameAs(Body())
            && slot.Input == TargetInputKind.BaseColor && slot.Domain == TargetSlotDomain.Game);
        Assert.Equal((TargetInputKind.BaseColor, TargetSlotDomain.Game, (int?)0, (string?)null),
            (opened.Input, opened.Domain, opened.MaterialSlotIndex, opened.Tier));
        Assert.Null(opened.OwnerEditId);
        var edit = after.EditDefinitions.Single(e => e.Id == "edit-0001");
        Assert.Equal(BindingKind.TargetGameValue,
            edit.Bindings.Single(binding => binding.SlotId == opened.Id).Kind);
        Assert.Equal(before.EditDefinitions.Single(e => e.Id == "edit-0001").Bindings.Count,
            edit.Bindings.Count);
        // Opening it again has nothing left to add.
        string once = AuthoredProjectSerializer.Serialize(after);
        session.EnsurePartSlots(Body(), Resolve);
        Assert.Equal(once, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>The same complete adapted answer carried through the save that follows it. The manifest
    /// keeps the installed input and the game value the answering edit records at it.</summary>
    [Fact]
    public void A_filed_parts_newly_opened_game_inputs_survive_a_save()
    {
        string manifest = Saved();
        var session = new AuthoredEditSession(AuthoredProjectSerializer.Load(manifest));
        session.EnsurePartSlots(Body(), Resolve);
        var authored = session.Snapshot();
        // The installed material's own base colour, which the replacement's edit-output one is not.
        var opened = authored.TargetSlots.Single(slot => slot.Part.SameAs(Body())
            && slot.Input == TargetInputKind.BaseColor && slot.Domain == TargetSlotDomain.Game);
        Assert.Contains(authored.EditDefinitions.SelectMany(edit => edit.Bindings),
            binding => binding.SlotId == opened.Id);
        Write(manifest, authored);

        var reopened = AuthoredProjectDocument.Load(manifest);
        reopened.Save(manifest);
        var after = AuthoredProjectSerializer.Load(manifest);

        Assert.Contains(after.TargetSlots, slot => slot.Id == opened.Id);
        Assert.Contains(after.EditDefinitions.SelectMany(edit => edit.Bindings),
            binding => binding.SlotId == opened.Id
                && binding.Kind == BindingKind.TargetGameValue);
    }

    /// <summary>A cycle longer than two states read back through the document. Serialize and Load agree on
    /// stable ids and an empty state because absence is vanilla.</summary>
    [Fact]
    public void A_longer_cycle_that_serializes_loads_back_through_the_document()
    {
        string manifest = Saved();
        var project = AuthoredProjectSerializer.Load(manifest);
        string bodyEditId = project.EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Content
            && edit.Target.SameAs(Body())).Id;
        var session = new AuthoredEditSession(project);
        string groupId = session.CreateKeyGroup("F7", bodyEditId);
        project = session.Snapshot();
        var group = project.KeyGroups.Single(keyGroup => keyGroup.Id == groupId);
        group.States.Add(new KeyGroupState
        {
            Id = "state-0003",
            Label = "Off, hair up",
        });
        Write(manifest, project);

        var reopened = AuthoredProjectDocument.Load(manifest);

        var loaded = reopened.Authored!.KeyGroups.Single();
        Assert.Equal(3, loaded.States.Count);
        Assert.Equal("state-0003", loaded.States[2].Id);
        Assert.Empty(loaded.States[2].ActiveEditIds);
        var bodyEdit = reopened.Authored.EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Content
            && edit.Target.SameAs(Body()));
        var slots = reopened.Authored.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var geometry = bodyEdit.Bindings.Single(binding =>
            slots[binding.SlotId].Input == TargetInputKind.Geometry
            && string.Equals(slots[binding.SlotId].Tier, "lod0", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("meshes/body.glb", reopened.Authored.ProjectAssets
            .Single(asset => asset.Id == geometry.ProjectAssetId).File);
    }

    /// <summary>The saved project given a second content edit for the body, selected in one state of the key
    /// its first edit already answers under. Written to <paramref name="manifest"/> and returned.</summary>
    private AuthoredProject TwoEdits(string manifest, bool secondEditLeads)
    {
        var session = new AuthoredEditSession(AuthoredProjectSerializer.Load(manifest));
        string second = session.CreateEdit(Body(), "Short body");
        string copy = session.Slots(second).Single(slot => slot.Slot.Input == TargetInputKind.Geometry
            && slot.Slot.Tier == "lod0").Slot.Id;
        string source = Path.Combine(_root, "meshes", "short.glb");
        File.WriteAllBytes(source, new byte[] { 9 });
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), second, copy, source);
        session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry, "Short",
            ProjectAssetIngress.Binary);
        session.UnplaceEdit(second);
        string first = session.Snapshot().EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Content
            && edit.Target.SameAs(Body()) && edit.Id != second).Id;
        string group = session.CreateKeyGroup("F7", first);
        var states = session.Snapshot().KeyGroups.Single(keyGroup => keyGroup.Id == group).States;
        session.PlaceEdit(second, group, states[1].Id);
        if (secondEditLeads)
        {
            session.UnplaceEdit(second, group, states[1].Id);
            session.MovePlacement(first, group, states[0].Id, group, states[1].Id);
            session.PlaceEdit(second, group, states[0].Id);
        }
        var authored = session.Snapshot();
        Write(manifest, authored);
        return authored;
    }

    private static void Write(string manifest, AuthoredProject project)
    {
        File.Delete(manifest);
        AuthoredProjectSerializer.Save(project, manifest);
    }

    private static IEnumerable<string> Bindings(AuthoredProject project) =>
        project.EditDefinitions.SelectMany(edit => edit.Bindings.Select(binding =>
            $"{edit.Id}:{binding.SlotId}={binding.Kind}/{binding.ProjectAssetId}"));

    private static string Describe(TargetSlot slot) =>
        $"{slot.Id}[{slot.OwnerEditId}]{slot.Part.RendererSlot}/{slot.Tier}/{slot.Input}";

    private static string Groups(AuthoredProject project) => string.Join(" | ",
        project.KeyGroups.Select(group => $"{group.Id}/{group.Key}: "
            + string.Join(" ; ", group.States.Select(state => string.Join(",",
                state.ActiveEditIds.Select(editId => $"{state.Id}={editId}"))))));

    /// <summary>The pinned released project, converted and saved once as schema 2 — the shape every project
    /// has before the commands under test run: game slots filed under the edit that answers each part.
    /// Returns the manifest path.</summary>
    private string Saved()
    {
        Directory.CreateDirectory(_root);
        string manifest = ModProject.ManifestPathFor(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Project", "golden", "legacy_project_v1.json"),
            manifest);
        WritePair("meshes/body.glb", "originals/body.glb", differs: true);
        WritePair("textures/body_base.png", "originals/body_base.png", differs: false);
        File.WriteAllBytes(Path.Combine(_root, "textures/body_0_base.png"), new byte[] { 4 });
        File.WriteAllBytes(Path.Combine(_root, "textures/body_0_ramp.dds"), new byte[] { 5 });
        File.WriteAllBytes(Path.Combine(_root, "textures/stock_ramp.dds"), new byte[] { 6 });

        var document = AuthoredProjectDocument.Load(manifest, Resolve);
        document.Save(manifest);
        return manifest;
    }

    private void WritePair(string replacement, string original, bool differs)
    {
        string replacePath = Path.Combine(_root, replacement);
        string originalPath = Path.Combine(_root, original);
        Directory.CreateDirectory(Path.GetDirectoryName(replacePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        File.WriteAllBytes(replacePath, differs ? new byte[] { 1, 2 } : new byte[] { 1 });
        File.WriteAllBytes(originalPath, new byte[] { 1 });
    }

    private static TargetPart Body() => Part("c_vesna_body_lod0");

    private static TargetPart Part(string renderer) => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = renderer,
    };

    private static LegacyResolvedPart? Resolve(TargetPart part)
    {
        if (part.Subject != "Vesna" || part.Outfit != "VesnaSSR01") return null;
        var renderer = Game(70000 + Math.Abs(part.RendererSlot.GetHashCode(StringComparison.Ordinal)) % 1000,
            part.RendererSlot, "characters/vesna_ssr01_prefab");
        var mesh = Game(part.RendererSlot == "c_vesna_body_lod0" ? 73002 : 72000,
            part.RendererSlot + "_mesh", "characters/vesna_ssr01_meshes");
        if (part.RendererSlot == "c_vesna_body_lod0")
            return new LegacyResolvedPart(part, renderer, mesh,
                new[]
                {
                    new LegacyResolvedMaterial(0, "body_material",
                        Game(74001, "body_material", "characters/vesna_ssr01_materials"),
                        new[]
                        {
                            new LegacyResolvedTexture(TargetInputKind.BaseColor,
                                "characters/vesna_ssr01_textures", "body_base", 81001,
                                Game(81001, "body_base", "characters/vesna_ssr01_textures")),
                        }),
                },
                new[]
                {
                    new LegacyResolvedTier("c_vesna_body_lod1", "lod1",
                        Game(70002, "c_vesna_body_lod1", "characters/vesna_ssr01_prefab"),
                        Game(73003, "c_vesna_body_lod1_mesh", "characters/vesna_ssr01_meshes")),
                });
        if (part.RendererSlot == "c_vesna_coat_lod0")
            return new LegacyResolvedPart(part, renderer, mesh,
                new[]
                {
                    new LegacyResolvedMaterial(0, "coat_material",
                        Game(74002, "coat_material", "characters/vesna_ssr01_materials"),
                        Array.Empty<LegacyResolvedTexture>()),
                });
        if (part.RendererSlot == "c_vesna_hair_lod0")
            return new LegacyResolvedPart(part, renderer, mesh, Array.Empty<LegacyResolvedMaterial>());
        // A part the released project never touched: it exists in the install and nowhere in the manifest.
        if (part.RendererSlot == "c_vesna_skirt_lod0")
            return new LegacyResolvedPart(part, renderer, mesh,
                new[]
                {
                    new LegacyResolvedMaterial(0, "skirt_material",
                        Game(74003, "skirt_material", "characters/vesna_ssr01_materials"),
                        new[]
                        {
                            new LegacyResolvedTexture(TargetInputKind.BaseColor,
                                "characters/vesna_ssr01_textures", "skirt_base", 81009,
                                Game(81009, "skirt_base", "characters/vesna_ssr01_textures")),
                        }),
                });
        return null;
    }

    private static GameAssetRef Game(long pathId, string name, string bundle) => new()
    {
        GameBuild = "1.0", LogicalBundle = bundle, PathId = pathId, Name = name,
    };
}
