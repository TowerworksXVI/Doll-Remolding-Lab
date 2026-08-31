using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

public sealed class AuthoredEditSessionKeyGroupTests
{
    [Fact]
    public void Create_group_moves_the_picked_Always_placement_into_first_state()
    {
        var session = Session();
        string id = session.CreateKeyGroup("f6", "edit-long", " Sleeves ");

        var project = session.Snapshot();
        var group = Assert.Single(project.KeyGroups);
        Assert.Equal(id, group.Id);
        Assert.Equal("F6", group.Key);
        Assert.Equal("Sleeves", group.Label);
        Assert.Equal(new[] { "state-0001", "state-0002" }, group.States.Select(state => state.Id));
        Assert.Equal("edit-long", Assert.Single(group.States[0].ActiveEditIds));
        Assert.Empty(group.States[1].ActiveEditIds);
        Assert.DoesNotContain("edit-long", project.Always);
    }

    [Fact]
    public void Create_group_accepts_keyless_and_an_unplaced_edit()
    {
        var session = Session();
        string id = session.CreateKeyGroup(null, "edit-short");

        var group = Assert.Single(session.Snapshot().KeyGroups);
        Assert.Equal(id, group.Id);
        Assert.Null(group.Key);
        Assert.Equal("edit-short", Assert.Single(group.States[0].ActiveEditIds));
        Assert.Contains("edit-long", session.Snapshot().Always);
    }

    [Fact]
    public void Set_group_key_can_clear_and_rejects_a_shared_key()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        string body = session.CreateKeyGroup("F6", "edit-long");
        string hair = session.CreateKeyGroup("F7", "edit-hair");

        session.SetGroupKey(body, "  ");
        Assert.Null(session.Snapshot().KeyGroups.Single(group => group.Id == body).Key);
        var taken = Assert.Throws<AuthoredRefusalException>(() => session.SetGroupKey(body, "f7"));
        Assert.Equal("Key F7 is already used by another key group.", taken.Message);
        Assert.Equal("F7", session.Snapshot().KeyGroups.Single(group => group.Id == hair).Key);
    }

    [Fact]
    public void Delete_group_deletes_states_and_placements_never_edits_or_transfers_to_Always()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.PlaceEdit("edit-short", group, "state-0002");

        session.DeleteKeyGroup(group);

        var project = session.Snapshot();
        Assert.Empty(project.KeyGroups);
        Assert.Empty(project.Always);
        Assert.Equal(2, project.EditDefinitions.Count(edit => edit.Kind == EditDefinitionKind.Content));
    }

    [Fact]
    public void Place_and_unplace_edit_use_Always_and_stable_state_ids()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        session.PlaceEdit("edit-short");
        session.PlaceEdit("edit-short", group, "state-0002");
        session.UnplaceEdit("edit-short");

        var project = session.Snapshot();
        Assert.DoesNotContain("edit-short", project.Always);
        Assert.Contains("edit-short", project.KeyGroups.Single().States[1].ActiveEditIds);
        Assert.Equal("Short body isn't used in Always.",
            Assert.Throws<AuthoredRefusalException>(() => session.UnplaceEdit("edit-short")).Message);
        Assert.Equal("Short body is already used in F6 · State 2.",
            Assert.Throws<AuthoredRefusalException>(() =>
                session.PlaceEdit("edit-short", group, "state-0002")).Message);
    }

    [Fact]
    public void Move_placement_is_one_transaction()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        session.MovePlacement("edit-long", group, "state-0001", null, null);

        var project = session.Snapshot();
        Assert.Contains("edit-long", project.Always);
        Assert.DoesNotContain("edit-long", project.KeyGroups.Single().States[0].ActiveEditIds);
    }

    [Fact]
    public void Failed_move_keeps_the_source_placement()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        Assert.Throws<KeyNotFoundException>(() =>
            session.MovePlacement("edit-long", group, "missing", null, null));

        Assert.Contains("edit-long", session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds);
    }

    /// <summary>A move takes the destination's one-content-per-part seat, and takes it in the SAME
    /// transaction: a move the model then refuses leaves the edit that answered there answering there. Two
    /// transactions would have unseated it and kept the refusal, which is an answer nobody authored.</summary>
    [Fact]
    public void A_refused_move_leaves_the_destinations_answer_seated()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-short");
        session.PlaceEdit("edit-short");
        long revision = session.Revision;

        // The destination already uses this edit, so the move is refused — at the point where the seat it
        // takes on the way in has already unseated the part's other answer.
        var refused = Assert.Throws<AuthoredRefusalException>(() =>
            session.MovePlacement("edit-short", group, "state-0001", null, null));

        Assert.Equal("Short body is already used in Always.", refused.Message);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(new[] { "edit-long", "edit-short" }, session.Snapshot().Always);
        Assert.Equal(new[] { "edit-short" },
            session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds);
    }

    /// <summary>Moving a content edit onto a place another edit of the same part answers takes that seat,
    /// because a part is answered exactly once anywhere.</summary>
    [Fact]
    public void A_move_takes_the_destinations_content_seat()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.PlaceEdit("edit-short", group, "state-0002");

        session.MovePlacement("edit-long", group, "state-0001", group, "state-0002");

        var states = session.Snapshot().KeyGroups.Single().States;
        Assert.Empty(states[0].ActiveEditIds);
        Assert.Equal(new[] { "edit-long" }, states[1].ActiveEditIds);
    }

    /// <summary>Seating is one transaction too, in Always exactly as in a state, and the edit it unseats
    /// can be read before the write so a surface can say what it took away.</summary>
    [Fact]
    public void Seating_an_edit_replaces_the_parts_answer_in_one_change()
    {
        var session = Session();
        int changes = 0;
        session.Changed += (_, _) => changes++;

        Assert.Equal("edit-long", session.SeatHolder("edit-short"));
        session.SeatEdit("edit-short");

        Assert.Equal(1, changes);
        Assert.Equal(new[] { "edit-short" }, session.Snapshot().Always);
        Assert.Null(session.SeatHolder("edit-short"));
    }

    /// <summary>A hide answers for whether the part draws at all, so it takes no seat from the content
    /// edit beside it and unseats nothing.</summary>
    [Fact]
    public void Seating_a_hide_takes_no_seat()
    {
        var session = Session();
        string hide = session.CreateHideEdit(AuthoredEditFixtures.Body);

        Assert.Null(session.SeatHolder(hide));
        session.SeatEdit(hide);

        Assert.Equal(new[] { "edit-long", hide }, session.Snapshot().Always);
    }

    /// <summary>A named state is called by its name wherever a place is named — here, in the refusal a
    /// second placement raises.</summary>
    [Fact]
    public void A_named_state_is_named_by_the_modders_own_word()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.RenameState(group, "state-0001", "Coat on");

        var refused = Assert.Throws<AuthoredRefusalException>(() =>
            session.PlaceEdit("edit-long", group, "state-0001"));

        Assert.Equal("Long body is already used in F6 · Coat on.", refused.Message);
    }

    [Fact]
    public void Duplicate_state_copies_placements_and_mints_a_stable_id()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        string state = session.DuplicateState(group, "state-0001", "Copy");

        var copy = session.Snapshot().KeyGroups.Single().States[2];
        Assert.Equal("state-0003", state);
        Assert.Equal(state, copy.Id);
        Assert.Equal("Copy", copy.Label);
        Assert.Equal("edit-long", Assert.Single(copy.ActiveEditIds));
    }

    [Fact]
    public void Duplicate_state_mints_after_the_highest_existing_state_id()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.DuplicateState(group, "state-0001");
        session.RemoveState(group, "state-0002");

        string state = session.DuplicateState(group, "state-0001");

        Assert.Equal("state-0004", state);
    }

    [Fact]
    public void Remove_state_keeps_two_state_floor_wording()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        var error = Assert.Throws<AuthoredRefusalException>(() =>
            session.RemoveState(group, "state-0001"));

        Assert.Equal(AuthoredEditSession.TwoStateFloor, error.Message);
    }

    [Fact]
    public void Reorder_changes_positions_not_state_identity()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        string third = session.DuplicateState(group, "state-0002");

        session.ReorderState(group, 2, 0);

        Assert.Equal(third, session.Snapshot().KeyGroups.Single().States[0].Id);
    }

    /// <summary>The part's one hide edit is minted once and placed by the ordinary verb, in as many places
    /// as anything else can be placed.</summary>
    [Fact]
    public void A_minted_hide_is_placed_by_the_verb_that_places_every_edit()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        string group = session.CreateKeyGroup("F6", "edit-long");

        string hide = session.CreateHideEdit(AuthoredEditFixtures.Cape);
        session.PlaceEdit(hide, group, "state-0002");
        session.PlaceEdit(hide);

        Assert.Equal(hide, session.CreateHideEdit(AuthoredEditFixtures.Cape));
        var project = session.Snapshot();
        var edit = Assert.Single(project.EditDefinitions, candidate => candidate.Id == hide);
        Assert.Equal(EditDefinitionKind.Hide, edit.Kind);
        var binding = Assert.Single(edit.Bindings);
        Assert.Equal(BindingKind.Hidden, binding.Kind);
        Assert.Equal(TargetInputKind.Visibility,
            project.TargetSlots.Single(slot => slot.Id == binding.SlotId).Input);
        Assert.Contains(hide, project.Always);
        Assert.Contains(hide, project.KeyGroups.Single().States[1].ActiveEditIds);
    }

    /// <summary>Placing a hide where it already is refuses, in the same words and by the same route as
    /// placing any other edit twice. A hide used to swallow the repeat silently, which is the one behaviour
    /// no content edit has.</summary>
    [Fact]
    public void Placing_a_hide_where_it_already_is_refuses_like_any_other_edit()
    {
        var session = Session();
        string hide = session.CreateHideEdit(AuthoredEditFixtures.Body);
        session.PlaceEdit(hide);

        var refused = Assert.Throws<AuthoredRefusalException>(() => session.PlaceEdit(hide));

        Assert.Equal("Hidden is already used in Always.", refused.Message);
        Assert.Equal(1, session.Snapshot().Always.Count(id => id == hide));
    }

    [Fact]
    public void Placing_a_hide_in_a_state_twice_refuses_on_both_state_overloads()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        string hide = session.CreateHideEdit(AuthoredEditFixtures.Body);
        session.PlaceEdit(hide, group, "state-0002");

        var byId = Assert.Throws<AuthoredRefusalException>(() =>
            session.PlaceEdit(hide, group, "state-0002"));
        var byIndex = Assert.Throws<AuthoredRefusalException>(() => session.PlaceEdit(hide, group, 1));

        Assert.Equal("Hidden is already used in F6 \u00b7 State 2.", byId.Message);
        Assert.Equal(byId.Message, byIndex.Message);
        Assert.Equal(1, session.Snapshot().KeyGroups.Single().States[1].ActiveEditIds
            .Count(id => id == hide));
    }

    [Fact]
    public void Same_state_refuses_a_second_content_edit_and_rolls_back()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");

        Assert.Throws<InvalidDataException>(() =>
            session.PlaceEdit("edit-short", group, "state-0001"));

        Assert.Equal("edit-long",
            Assert.Single(session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds));
    }

    [Fact]
    public void One_edit_can_have_many_placements()
    {
        var session = Session();
        string first = session.CreateKeyGroup("F6", "edit-long");
        string second = session.CreateKeyGroup("F7", "edit-short");

        session.PlaceEdit("edit-long", first, "state-0002");
        session.PlaceEdit("edit-long", second, "state-0002");

        Assert.Equal(3, session.Outline().Edits.Single(edit => edit.Id == "edit-long").Placements.Count);
    }

    [Fact]
    public void Outline_is_edit_first_and_carries_Always_groups_and_states()
    {
        var session = Session();
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.PlaceEdit("edit-short");

        var outline = session.Outline();
        Assert.Equal(new[] { "edit-long", "edit-short" }, outline.Edits.Select(edit => edit.Id));
        Assert.Equal("edit-short", Assert.Single(outline.Always));
        var placement = Assert.Single(outline.Edits.Single(edit => edit.Id == "edit-long").Placements);
        Assert.Equal(group, placement.KeyGroupId);
        Assert.Equal("state-0001", placement.StateId);
        Assert.Equal(new[] { "state-0001", "state-0002" },
            outline.Groups.Single().States.Select(state => state.Id));
    }

    [Fact]
    public void Forget_subject_removes_its_edits_and_placements_but_keeps_group_states()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        string group = session.CreateKeyGroup("F6", "edit-long");

        session.ForgetSubject("Vesna", "VesnaSSR01");

        var project = session.Snapshot();
        Assert.Empty(project.EditDefinitions);
        Assert.Empty(project.Always);
        Assert.Equal(2, project.KeyGroups.Single(candidate => candidate.Id == group).States.Count);
        Assert.All(project.KeyGroups.Single().States, state => Assert.Empty(state.ActiveEditIds));
    }

    /// <summary>The same remove driven the way ① Pick's uncheck and ② Edit's subject header drive it, end to
    /// end: what the mod DECIDED about a subject is the session's, so the remove has to reach the model
    /// itself and survive a save. Across a save and a reopen nothing of the subject is left — no edit, no
    /// slot, no Always placement, no key-group membership, no ledger row and no workspace record — and the
    /// mod's other subject is untouched by any of it.</summary>
    [Fact]
    public void Forgetting_a_subject_leaves_nothing_of_it_across_a_save_and_a_reopen()
    {
        const string hidden = "c_KarstSSR01_slg_cloth1_lod0", keyed = "c_KarstSSR01_slg_body_lod0";
        const string survivor = "c_WrenSSR01_slg_body_lod0";
        string root = Path.Combine(Path.GetTempPath(), "gf2-forget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document = AuthoredProjectDocument.New();
            document.RebaseRoot(root);
            var session = document.Session;
            var hiddenPart = AuthoredParts.Part("Karst", "KarstSSR01", hidden);
            var keyedPart = AuthoredParts.Part("Karst", "KarstSSR01", keyed);
            var survivorPart = AuthoredParts.Part("Wren", "WrenSSR01", survivor);
            foreach (var part in new[] { hiddenPart, keyedPart, survivorPart })
                session.EnsurePartSlots(part, AuthoredParts.Resolve);
            session.AddHideEdit(hiddenPart);
            string keyedEdit = session.CreateEdit(keyedPart);
            session.CreateKeyGroup("F7", keyedEdit);
            session.CreateEdit(survivorPart);
            session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
            {
                Selection = new List<SelectionEntry>
                {
                    new() { Character = "Karst", Outfit = "KarstSSR01" },
                    new() { Character = "Wren", Outfit = "WrenSSR01" },
                },
            });
            Assert.Single(document.Authored.KeyGroups);

            session.ForgetSubject("Karst", "KarstSSR01");
            document.Save(root);
            var authored = AuthoredProjectDocument.Load(ModProject.ManifestPathFor(root)).Authored;

            Assert.DoesNotContain(authored.EditDefinitions, e => Owns(e.Target));
            Assert.DoesNotContain(authored.TargetSlots, s => Owns(s.Part));
            Assert.DoesNotContain(authored.Always,
                id => authored.EditDefinitions.Any(e => e.Id == id && Owns(e.Target)));
            Assert.All(authored.KeyGroups.SelectMany(group => group.States),
                state => Assert.Empty(state.ActiveEditIds));
            Assert.DoesNotContain(authored.WorkspaceIndex!.Selection, s => s.Character == "Karst");
            Assert.DoesNotContain(authored.WorkspaceIndex.Records, record => Owns(record.Part));
            // the mod's other subject is untouched by any of it
            Assert.Contains(authored.EditDefinitions, e => e.Target.RendererSlot == survivor);
            Assert.Contains(authored.WorkspaceIndex.Selection, s => s.Character == "Wren");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        static bool Owns(TargetPart part) =>
            string.Equals(part.Subject, "Karst", StringComparison.OrdinalIgnoreCase);
    }

    private static AuthoredEditSession Session() => new(AuthoredEditFixtures.Golden());
}
