using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>What the Edit page's edit list can do to one part's library of answers, and what it refuses.</summary>
public sealed class AuthoredEditSessionEditCrudTests
{
    [Fact]
    public void A_new_edit_starts_from_vanilla_and_the_first_one_becomes_the_parts_answer()
    {
        var project = AuthoredEditFixtures.SlotsOnly();
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(project);

        string first = session.CreateEdit(body);
        var snapshot = session.Snapshot();
        var edit = snapshot.EditDefinitions.Single(e => e.Id == first);

        Assert.Equal("edit-0001", first);
        Assert.Equal("Edit 1", edit.Label);
        Assert.Equal(EditDefinitionKind.Content, edit.Kind);
        Assert.Equal(new[] { "slot-geometry", "slot-ramp" }, edit.Bindings.Select(b => b.SlotId));
        Assert.All(edit.Bindings, b => Assert.Equal(BindingKind.TargetGameValue, b.Kind));
        Assert.Equal(first, Assert.Single(snapshot.Always));

        string second = session.CreateEdit(body);
        Assert.Equal("edit-0002", second);
        Assert.Equal("Edit 2", session.Snapshot().EditDefinitions.Single(e => e.Id == second).Label);
        Assert.Equal(first, session.Part(body).EditDefinitionId);
    }

    [Fact]
    public void A_new_edit_never_displaces_an_answer_the_part_already_has()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());

        string added = session.CreateEdit(AuthoredEditFixtures.Body, "Freckled");

        Assert.Equal("Freckled", session.Snapshot().EditDefinitions.Single(e => e.Id == added).Label);
        Assert.Equal("edit-long", session.Part(AuthoredEditFixtures.Body).EditDefinitionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("freckled")]
    public void A_blank_or_case_insensitively_duplicate_requested_label_uses_the_default(string requested)
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.SlotsOnly());
        session.CreateEdit(AuthoredEditFixtures.Body, "Freckled");

        string added = session.CreateEdit(AuthoredEditFixtures.Body, requested);

        Assert.Equal("Edit 2", session.Snapshot().EditDefinitions.Single(e => e.Id == added).Label);
    }

    /// <summary>A third content edit for a part whose game slots two edits are already filed under. An edit
    /// answers once for each place the part addresses: the copies its predecessors hold are the same route
    /// as the slots they were copied from, so the candidates collapse to one representative per route
    /// instead of the whole pile growing by a power of two. Binding the pile would have this edit demand
    /// two geometry replacements of one mesh, which the planner refuses through rows that are all its
    /// own — leaving the modder nothing to take back.</summary>
    [Fact]
    public void A_third_edit_answers_once_per_route_rather_than_once_per_copy_before_it()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());
        var body = AuthoredEditFixtures.Body;

        session.CreateEdit(body);
        string third = session.CreateEdit(body);

        var bound = session.Slots(third).Select(state => state.Slot).ToList();
        Assert.Equal(2, bound.Count);
        Assert.All(bound, slot => Assert.Single(bound, other => other.SameRoute(slot)));
        // Two routes, each held by the original and one copy per edit that came after it.
        Assert.Equal(6, session.Snapshot().TargetSlots.Count(slot => slot.Part.SameAs(body)));
    }

    /// <summary>The other route to a part whose slots are held more than once: a duplicate takes its own
    /// copy of everything its source is filed under, and a new edit after it answers once per route all the
    /// same.</summary>
    [Fact]
    public void A_new_edit_after_a_duplicate_answers_once_per_route_too()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());
        var body = AuthoredEditFixtures.Body;

        session.DuplicateEdit("edit-long");
        string created = session.CreateEdit(body);

        var bound = session.Slots(created).Select(state => state.Slot).ToList();
        Assert.Equal(2, bound.Count);
        Assert.All(bound, slot => Assert.Single(bound, other => other.SameRoute(slot)));
        Assert.Equal(4, session.Snapshot().TargetSlots.Count(slot => slot.Part.SameAs(body)));
    }

    [Fact]
    public void The_same_commands_from_the_same_project_mint_the_same_ids_and_names()
    {
        string Run()
        {
            var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());
            session.CreateEdit(AuthoredEditFixtures.Body);
            session.DuplicateEdit("edit-long");
            return AuthoredProjectSerializer.Serialize(session.Snapshot());
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void A_part_with_no_game_slots_cannot_be_given_an_edit_and_leaves_no_residue()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<AuthoredRefusalException>(() =>
            session.CreateEdit(AuthoredEditFixtures.Part("c_vesna_nothing_lod0")));

        Assert.Equal(AuthoredEditSession.NowhereToRecord("an edit"), error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    [Fact]
    public void A_duplicate_shares_the_assets_and_is_given_its_own_exact_output_slots()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());

        string copy = session.DuplicateEdit("edit-long");
        var snapshot = session.Snapshot();
        var duplicated = snapshot.EditDefinitions.Single(e => e.Id == copy);
        var duplicatedSlotIds = duplicated.Bindings.Select(binding => binding.SlotId)
            .ToHashSet(StringComparer.Ordinal);
        var owned = snapshot.TargetSlots.Where(slot => duplicatedSlotIds.Contains(slot.Id)
                && slot.Domain == TargetSlotDomain.EditOutput)
            .OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

        Assert.Equal("edit-0001", copy);
        Assert.Equal("Edit 3", duplicated.Label);
        Assert.Equal(new[] { "slot-0001", "slot-0002" }, owned.Select(s => s.Id));
        Assert.Equal(new int?[] { 0, 1 }, owned.Select(s => s.SubmeshIndex));
        // Assets are shared until a fork sends two uses apart, so the copy names the very same files.
        Assert.Equal("mesh-long", Binding(duplicated, "slot-geometry").ProjectAssetId);
        Assert.Equal("skin-base", Binding(duplicated, "slot-0001").ProjectAssetId);
        // A value the source took from a slot it owns is taken from the copy's own slot instead.
        var borrowed = Binding(duplicated, "slot-0002").SourceSlot!;
        Assert.Equal("slot-0001", borrowed.SlotId);
        Assert.Equal(copy, borrowed.EditDefinitionId);

        var source = snapshot.EditDefinitions.Single(e => e.Id == "edit-long");
        Assert.Equal("slot-owned", Binding(source, "slot-owned-2").SourceSlot!.SlotId);
        Assert.Equal("edit-long", Binding(source, "slot-owned-2").SourceSlot!.EditDefinitionId);
        Assert.Equal("edit-long", session.Part(AuthoredEditFixtures.Body).EditDefinitionId);
    }

    [Fact]
    public void A_hide_is_the_one_answer_that_is_not_duplicated()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string hide = session.CreateHideEdit(AuthoredEditFixtures.Body);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        Assert.Throws<AuthoredRefusalException>(() => session.DuplicateEdit(hide));
        Assert.Throws<KeyNotFoundException>(() => session.DuplicateEdit("edit-missing"));

        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    [Fact]
    public void Clearing_an_edits_name_restores_the_one_it_would_have_been_given()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());

        session.RenameEdit("edit-short", "Cropped");
        Assert.Equal("Cropped", session.Snapshot().EditDefinitions.Single(e => e.Id == "edit-short").Label);

        session.RenameEdit("edit-short", "  ");
        Assert.Equal("Edit 2", session.Snapshot().EditDefinitions.Single(e => e.Id == "edit-short").Label);
        Assert.Throws<KeyNotFoundException>(() => session.RenameEdit("edit-missing", "Anything"));
    }

    /// <summary>The name a cleared edit falls back to is one no sibling is already using: the default names
    /// are the ones a user is most likely to have typed by hand, so the fallback has to walk past them or two
    /// of a part's edits end up called the same thing with nothing to tell them apart in the list.</summary>
    [Fact]
    public void Clearing_a_name_never_hands_an_edit_the_name_another_one_is_already_using()
    {
        string Run()
        {
            var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
            session.RenameEdit("edit-short", "Edit 1");
            session.RenameEdit("edit-long", "");
            var snapshot = session.Snapshot();
            return snapshot.EditDefinitions.Single(e => e.Id == "edit-long").Label;
        }

        var final = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        final.RenameEdit("edit-short", "Edit 1");
        final.RenameEdit("edit-long", "");
        var edits = final.Snapshot().EditDefinitions;

        Assert.Equal("Edit 1", edits.Single(e => e.Id == "edit-short").Label);
        Assert.NotEqual("Edit 1", edits.Single(e => e.Id == "edit-long").Label);
        // Clearing the same name again is the same command over the same state, so it lands in the same place.
        final.RenameEdit("edit-long", "  ");
        Assert.Equal(Run(), final.Snapshot().EditDefinitions.Single(e => e.Id == "edit-long").Label);
    }

    [Fact]
    public void An_explicit_rename_refuses_a_trimmed_case_insensitive_sibling_collision()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());

        var refusal = Assert.Throws<AuthoredRefusalException>(() =>
            session.RenameEdit("edit-short", "  long BODY  "));

        Assert.Contains("Long body", refusal.Message);
        Assert.Equal("Short body",
            session.Snapshot().EditDefinitions.Single(edit => edit.Id == "edit-short").Label);
    }

    [Fact]
    public void Deleting_an_edit_takes_its_slots_and_placements()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());

        session.DeleteEdit("edit-long");
        var snapshot = session.Snapshot();

        Assert.DoesNotContain(snapshot.EditDefinitions, e => e.Id == "edit-long");
        Assert.DoesNotContain(snapshot.TargetSlots, s => s.Id is "slot-owned" or "slot-owned-2");
        Assert.DoesNotContain("edit-long", snapshot.Always);
        Assert.Contains(snapshot.EditDefinitions, e => e.Id == "edit-short");
        // Asset lifetime is not this command's business: a file no binding names is still the user's.
        Assert.Contains(snapshot.ProjectAssets, a => a.Id == "mesh-long");
        Assert.Contains(snapshot.ProjectAssets, a => a.Id == "skin-base");
    }

    [Fact]
    public void An_edit_another_edit_takes_a_value_from_is_not_deleted_out_from_under_it()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithBorrowedSlot());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<AuthoredRefusalException>(() => session.DeleteEdit("edit-long"));

        // The refusal is shown as it is wherever it surfaces, so it names the two edits the way the modder
        // named them. Ids are the model's own vocabulary and belong nowhere a person reads.
        Assert.Equal("'Long body' cannot be deleted while 'Short body' takes a value from it.",
            error.Message);
        Assert.DoesNotContain("edit-long", error.Message);
        Assert.DoesNotContain("edit-short", error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>The claimed-part half of Hide on ② Edit: a key group's own surface is where a hide is chosen,
    /// so the page can only create the edit. It is one object per part, so asking twice is asking once.</summary>
    [Fact]
    public void A_hide_edit_can_be_created_without_being_chosen_anywhere()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string group = session.CreateKeyGroup("F6", "edit-long");

        string hide = session.CreateHideEdit(body);
        var snapshot = session.Snapshot();

        var edit = snapshot.EditDefinitions.Single(e => e.Id == hide);
        Assert.Equal(EditDefinitionKind.Hide, edit.Kind);
        Assert.Contains(snapshot.TargetSlots, slot => edit.Bindings.Any(binding => binding.SlotId == slot.Id)
            && slot.Input == TargetInputKind.Visibility);
        // It is created, not selected: nothing in the always-on set or the group's states points at it.
        Assert.DoesNotContain(hide, snapshot.Always);
        Assert.DoesNotContain(snapshot.KeyGroups.Single(g => g.Id == group).States
            .SelectMany(state => state.ActiveEditIds), editId => editId == hide);
        Assert.Empty(AuthoredProjectValidator.Errors(snapshot));

        // Asking again answers with the one that is there.
        Assert.Equal(hide, session.CreateHideEdit(body));
        Assert.Single(session.Snapshot().EditDefinitions, e => e.Kind == EditDefinitionKind.Hide);
    }

    /// <summary>Selecting hidden takes the same edit through the same derivation, so the two routes cannot
    /// disagree about which slot a hide binds.</summary>
    [Fact]
    public void Placing_hidden_takes_the_hide_edit_that_is_already_there()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string hide = session.CreateHideEdit(body);

        session.PlaceEdit(hide);

        Assert.Contains(hide, session.Snapshot().Always);
        Assert.Single(session.Snapshot().EditDefinitions, e => e.Kind == EditDefinitionKind.Hide);
    }

    [Fact]
    public void A_part_with_more_than_one_edit_is_placed_by_id()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());

        session.UnplaceEdit("edit-long");
        session.PlaceEdit("edit-short");
        Assert.Equal("edit-short", session.Part(body).EditDefinitionId);

        Assert.Throws<KeyNotFoundException>(() => session.PlaceEdit("edit-missing"));
        Assert.Equal("edit-short", session.Part(body).EditDefinitionId);
    }

    [Fact]
    public void An_edit_may_be_placed_in_Always_and_a_key_group_state()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.PlaceEdit("edit-long");
        session.PlaceEdit("edit-long", group, 1);

        var snapshot = session.Snapshot();
        Assert.Contains("edit-long", snapshot.Always);
        Assert.All(snapshot.KeyGroups.Single().States,
            state => Assert.Contains("edit-long", state.ActiveEditIds));
    }

    /// <summary>Adding a second edit to a part whose game slots an earlier edit is filed under. That is every
    /// saved project — a save re-derives a part's game slots through the legacy adapter, which files them
    /// under the edit answering the part — so selecting the unowned would leave this command nothing it may
    /// legally bind. It asks the slots what they address and gives itself copies of the ones taken.</summary>
    [Fact]
    public void A_second_edit_is_given_its_own_copies_of_the_game_slots_the_first_is_filed_under()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());

        string second = session.CreateEdit(body);
        var snapshot = session.Snapshot();
        var edit = snapshot.EditDefinitions.Single(e => e.Id == second);
        var copies = snapshot.TargetSlots.Where(slot => edit.Bindings.Any(binding => binding.SlotId == slot.Id))
            .OrderBy(slot => slot.Id, StringComparer.Ordinal).ToList();

        Assert.Equal("edit-0001", second);
        Assert.Equal(new[] { "slot-0001", "slot-0002" }, copies.Select(slot => slot.Id));
        Assert.Equal(new[] { "slot-0001", "slot-0002" }, edit.Bindings.Select(b => b.SlotId));
        Assert.All(edit.Bindings, b => Assert.Equal(BindingKind.TargetGameValue, b.Kind));
        // A copy is a second slot on the same game object: it addresses the install, so it stays in the game
        // domain and keeps the exact structural route and references the original was resolved to.
        Assert.All(copies, copy => Assert.Equal(TargetSlotDomain.Game, copy.Domain));
        var originals = snapshot.TargetSlots.Where(slot => slot.Id is "slot-geometry" or "slot-ramp")
            .OrderBy(slot => slot.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(originals.Select(Route), copies.Select(Route));

        // The first edit is untouched: same slots and same bindings.
        var first = snapshot.EditDefinitions.Single(e => e.Id == "edit-long");
        Assert.Equal(new[] { "slot-geometry", "slot-ramp" }, first.Bindings.Select(b => b.SlotId));
        Assert.Equal("mesh-long", Binding(first, "slot-geometry").ProjectAssetId);
        // A second edit is a library entry: the part's answer is still the one it had.
        Assert.Equal("edit-long", session.Part(body).EditDefinitionId);
    }

    [Fact]
    public void Deleting_either_of_a_parts_two_edits_leaves_the_other_holding_its_own_slots()
    {
        var body = AuthoredEditFixtures.Body;
        foreach (string deleted in new[] { "edit-long", "edit-0001" })
        {
            var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());
            string second = session.CreateEdit(body);
            string kept = deleted == "edit-long" ? second : "edit-long";

            session.DeleteEdit(deleted);
            var snapshot = session.Snapshot();

            Assert.DoesNotContain(snapshot.EditDefinitions, e => e.Id == deleted);
            var survivor = snapshot.EditDefinitions.Single(e => e.Id == kept);
            Assert.Equal(2, survivor.Bindings.Count);
            Assert.All(survivor.Bindings, binding => Assert.Contains(snapshot.TargetSlots,
                slot => slot.Id == binding.SlotId));
            Assert.Empty(AuthoredProjectValidator.Errors(snapshot));
        }
    }

    /// <summary>Content edits bind geometry, pictures, ramp and material values, never visibility: whether a
    /// part draws at all is the one thing a hide answers, and a content edit reaching for the hide's slot
    /// would give the part two answers to that question — and a second visibility slot for a hide to
    /// re-anchor on, which the planner reads as a conflict.</summary>
    [Fact]
    public void A_content_edit_leaves_the_visibility_slot_to_the_hide_that_owns_it()
    {
        var body = AuthoredEditFixtures.Body;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());
        string placedHide = session.CreateHideEdit(body);
        session.PlaceEdit(placedHide);
        var snapshot = session.Snapshot();
        string hide = snapshot.EditDefinitions.Single(e => e.Kind == EditDefinitionKind.Hide).Id;
        Assert.Equal(placedHide, hide);
        Assert.Contains(snapshot.TargetSlots, slot => slot.Part.SameAs(body)
            && slot.Input == TargetInputKind.Visibility && slot.Domain == TargetSlotDomain.Game);

        string content = session.CreateEdit(body);
        snapshot = session.Snapshot();
        var edit = snapshot.EditDefinitions.Single(e => e.Id == content);

        Assert.All(edit.Bindings, binding => Assert.NotEqual(TargetInputKind.Visibility,
            snapshot.TargetSlots.Single(slot => slot.Id == binding.SlotId).Input));
        Assert.Single(snapshot.TargetSlots, slot => slot.Input == TargetInputKind.Visibility);
        Assert.Contains(hide, snapshot.Always);
    }

    /// <summary>The same law at the choke point every route funnels through: a content edit binding a
    /// visibility slot is refused wherever it was authored, and is not owed a binding for one either.</summary>
    [Fact]
    public void A_content_edit_that_binds_a_visibility_slot_is_refused_by_the_validator()
    {
        var project = AuthoredEditFixtures.Saved();
        var geometry = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-visibility",
            Part = AuthoredEditFixtures.Body,
            Tier = "lod0",
            Input = TargetInputKind.Visibility,
            Renderer = geometry.Renderer,
            Mesh = geometry.Mesh,
        });
        // A visibility slot no hide has claimed yet is still not a binding a content edit owes.
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        project.EditDefinitions.Single(e => e.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = "slot-visibility", Kind = BindingKind.TargetGameValue,
        });

        Assert.Contains(AuthoredProjectValidator.Errors(project),
            error => error.Contains("binds a visibility slot but is not a hide edit"));
    }

    private static Binding Binding(EditDefinition edit, string slotId) =>
        edit.Bindings.Single(binding => binding.SlotId == slotId);

    /// <summary>Everything about a slot except which edit filed it and what it is called: the structural
    /// route and the exact objects that route was resolved to.</summary>
    private static string Route(TargetSlot slot) => string.Join("|", slot.Part.RendererSlot, slot.Tier,
        slot.SubmeshIndex, slot.MaterialSlotIndex, slot.Input, slot.Domain, slot.Semantic,
        slot.Renderer.PathId, slot.Mesh?.PathId, slot.Material?.PathId);
}
