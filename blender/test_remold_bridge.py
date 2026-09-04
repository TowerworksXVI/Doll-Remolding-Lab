"""Unit cover for the bridge's pure state-reporting helpers.

    python -m unittest discover -s blender

Blender is not needed: every helper here takes plain values, so the module's `import bpy` is satisfied
with a stub. The parts of the bridge that read a scene are exercised by its sibling suite,
`bpy_test_remold_bridge.py`, which runs under Blender itself — that file is deliberately named
outside the `test*.py` pattern so this discovery run never tries to import it against a stub bpy.
"""
import os
import sys
import json
import types
import unittest
import tempfile
from unittest import mock

sys.modules.setdefault("bpy", types.ModuleType("bpy"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import remold_bridge as rb   # noqa: E402  (the bpy stub has to be in place first)


class TextureTransportCarrierTests(unittest.TestCase):
    def test_a_non_object_extras_is_replaced_before_transport_is_appended(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "transport.glb")
            rb._gf2_glb_write(path, {
                "asset": {"version": "2.0"},
                "buffers": [{"byteLength": 0}],
                "extras": ["legacy non-object extras"],
            }, b"")
            rb._gf2_append_texture_transport(path, [{
                "owner": {"mesh": "veil", "material": 0},
                "property": "_MaskTex",
                "semantic": "texture",
                "texCoord": 1,
                "stock": {"name": "mask", "bundle": "bundle", "path_id": 71},
                "srgb": False,
                "origin": "vanilla",
                "png": b"carrier bytes",
                "image_name": "Mask",
            }])

            root, _ = rb._gf2_glb_read(path)
            self.assertIsInstance(root["extras"], dict)
            returned = rb._gf2_read_texture_transport(path)
            self.assertEqual([row["property"] for row in returned], ["_MaskTex"])
            self.assertEqual([row["texCoord"] for row in returned], [1])

    def test_uv_layer_lookup_uses_import_order_and_rejects_unusable_indices(self):
        mesh = types.SimpleNamespace(data=types.SimpleNamespace(uv_layers=[
            types.SimpleNamespace(name="Primary"), types.SimpleNamespace(name="Effect layout")]))

        self.assertEqual(rb._gf2_uv_layer_name(mesh, 0), "Primary")
        self.assertEqual(rb._gf2_uv_layer_name(mesh, 1), "Effect layout")
        self.assertIsNone(rb._gf2_uv_layer_name(mesh, 2))
        self.assertIsNone(rb._gf2_uv_layer_name(mesh, -1))
        self.assertIsNone(rb._gf2_uv_layer_name(mesh, True))

    def test_coordinate_pin_refuses_an_image_without_a_vector_socket(self):
        class UvNode:
            bl_idname = "ShaderNodeUVMap"

            def __init__(self):
                self.name = ""
                self.label = ""
                self.outputs = {"UV": object()}
                self.properties = {}

            def get(self, name, default=None):
                return self.properties.get(name, default)

            def __setitem__(self, name, value):
                self.properties[name] = value

        class Nodes(list):
            def new(self, node_type):
                self.asserted_type = node_type
                node = UvNode()
                self.append(node)
                return node

        nodes = Nodes()
        mesh = types.SimpleNamespace(data=types.SimpleNamespace(
            uv_layers=[types.SimpleNamespace(name="Primary")]))
        material = types.SimpleNamespace(node_tree=types.SimpleNamespace(nodes=nodes))
        image = types.SimpleNamespace(name="GF2 _BaseMap", inputs={})

        self.assertFalse(rb._gf2_pin_texture_coordinate(mesh, material, image, 0))
        self.assertEqual(nodes, [])

    def test_two_properties_on_one_stock_resource_stay_two_exact_bindings(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "transport.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"}, "buffers": [{"byteLength": 0}]}, b"")
            stock = {"name": "shared", "bundle": "bundle", "path_id": 71}
            rows = []
            for prop in ("_MaskTex", "_TurbulenceTex"):
                rows.append({
                    "owner": {"mesh": "veil", "material": 6},
                    "property": prop,
                    "semantic": "texture",
                    "stock": stock,
                    "srgb": False,
                    "origin": "vanilla",
                    "parameters": {"floats": {"future": 2.0}, "keywords": ["FUTURE"]},
                    "png": b"different carrier bytes for " + prop.encode("ascii"),
                    "image_name": prop,
                })

            rb._gf2_append_texture_transport(path, rows)
            returned = rb._gf2_read_texture_transport(path)

            self.assertEqual([row["property"] for row in returned], ["_MaskTex", "_TurbulenceTex"])
            self.assertEqual([row["stock"]["path_id"] for row in returned], [71, 71])
            self.assertEqual(returned[0]["parameters"]["keywords"], ["FUTURE"])
            self.assertNotEqual(returned[0]["png"], returned[1]["png"])

    def test_a_legacy_glb_has_no_property_carrier(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "legacy.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"}, "buffers": [{"byteLength": 0}]}, b"")
            self.assertEqual(rb._gf2_read_texture_transport(path), [])


class LiveHandlerTests(unittest.TestCase):
    def test_the_handler_skips_while_the_weight_solve_runs(self):
        """The weight solve's throwaway duplicate fires depsgraph updates; the handler must not read
        it as scene state (it would report a blocking stray for an object that no longer exists)."""
        calls = []
        with mock.patch.object(rb, "_refresh_live_status", lambda: calls.append(1)):
            rb._SOLVING = True
            try:
                rb._gf2_depsgraph_update(None, None)
            finally:
                rb._SOLVING = False
        self.assertEqual(calls, [])


class StatusLineTests(unittest.TestCase):
    def test_clean_scene_reads_ready(self):
        self.assertEqual(rb.gf2_status_line([]), "Ready to send")

    def test_both_severities_tally_separately(self):
        issues = [("HARD", "a"), ("HARD", "b"), ("SOFT", "c")]
        self.assertEqual(rb.gf2_status_line(issues),
                         "2 blocking issues · 1 warning — click Check Mesh for details")

    def test_blocking_only_omits_the_warning_half(self):
        self.assertEqual(rb.gf2_status_line([("HARD", "a")]),
                         "1 blocking issue — click Check Mesh for details")

    def test_warnings_only_omit_the_blocking_half(self):
        self.assertEqual(rb.gf2_status_line([("SOFT", "a"), ("SOFT", "b")]),
                         "2 warnings — click Check Mesh for details")


class DroppedExportOptionTests(unittest.TestCase):
    """The export settings are filtered against the running Blender's RNA, so an older or newer Blender can
    send without one. This line is what carries that fact to the modder."""

    def test_every_dropped_option_is_named(self):
        self.assertEqual(rb.gf2_dropped_options_line(["export_tangents", "export_apply"]),
                         "The running Blender version does not support these export options: "
                         "export_apply, export_tangents.")

    def test_one_dropped_option_reads_the_same_way(self):
        self.assertEqual(rb.gf2_dropped_options_line(["export_apply"]),
                         "The running Blender version does not support these export options: export_apply.")


class ScopeLineTests(unittest.TestCase):
    def test_scope_reads_objects_and_vertices_only(self):
        """Reference never ships, so what is NOT sent is not part of the scope: the line counts only
        what the send carries."""
        self.assertEqual(rb.gf2_scope_lines(3, 12480, False), ["3 objects · 12,480 vertices"])

    def test_singulars(self):
        self.assertEqual(rb.gf2_scope_lines(1, 1, False), ["1 object · 1 vertex"])

    def test_modifier_note_only_when_one_would_be_baked(self):
        self.assertEqual(rb.gf2_scope_lines(1, 8, True),
                         ["1 object · 8 vertices", "Modifiers are baked on Send."])


class SessionPartTests(unittest.TestCase):
    """Every glb carries the whole outfit, so which meshes a session may WRITE is the app's call. The
    fallback when it says nothing is 'all of them' — what a hand-opened glb and the headless round trip
    both want."""

    def test_a_named_part_is_the_only_writable_one(self):
        names = ["body1", "cloth1", "hair"]
        self.assertEqual(rb.gf2_session_parts({"part": "cloth1"}, names), ["cloth1"])

    def test_no_named_part_makes_every_mesh_writable(self):
        names = ["body1", "cloth1"]
        self.assertEqual(rb.gf2_session_parts({}, names), names)
        self.assertEqual(rb.gf2_session_parts({"part": None}, names), names)

    def test_a_name_the_glb_does_not_carry_yields_nothing(self):
        """Better an empty Mod, which the attribution checks block on, than a session quietly widening
        to the whole outfit."""
        self.assertEqual(rb.gf2_session_parts({"part": "missing"}, ["body1"]), [])

    def test_a_part_declared_unwritable_is_left_out(self):
        """A mesh the app cannot replace still ships in the glb, so the head can be edited against the
        face it sits on — but it must land in Reference, never as a sendable part."""
        session = {"part": None, "parts": [{"name": "face", "edited": False, "writable": False},
                                           {"name": "hair", "edited": False, "writable": True}]}
        self.assertEqual(rb.gf2_session_parts(session, ["face", "hair"]), ["hair"])

    def test_a_part_entry_with_no_writable_key_is_writable(self):
        session = {"part": None, "parts": [{"name": "body1", "edited": False}]}
        self.assertEqual(rb.gf2_session_parts(session, ["body1"]), ["body1"])

    def test_a_named_part_ignores_the_writable_flags(self):
        """The single-part route is already scoped to one mesh; the app refuses to open an unreplaceable
        part that way, so the flags have nothing left to say."""
        session = {"part": "cloth1", "parts": [{"name": "cloth1", "edited": False, "writable": False}]}
        self.assertEqual(rb.gf2_session_parts(session, ["cloth1", "hair"]), ["cloth1"])


class TargetRowTests(unittest.TestCase):
    SESSION = {"revision": 4, "part": None, "parts": [
        {"name": "body1", "editId": "body-detail", "defaultEditName": "Body Edit 3",
         "edits": [
             {"id": "body-base", "label": "Body Base", "holdsAuthoredMesh": False},
             {"id": "body-detail", "label": "Body Detail", "holdsAuthoredMesh": True}]},
        {"name": "cloth1", "defaultEditName": "Cloth Edit 2", "edits": [
            {"id": "cloth-base", "label": "Cloth Base", "holdsAuthoredMesh": False}]},
        {"name": "face", "writable": False, "edits": []},
    ]}

    def test_one_row_is_built_for_each_writable_scene_part(self):
        specs = rb.gf2_target_row_specs(self.SESSION, ["body1", "cloth1", "face"])
        self.assertEqual([row["part"] for row in specs], ["body1", "cloth1"])
        self.assertEqual([edit["label"] for edit in specs[0]["edits"]],
                         ["Body Base", "Body Detail"])

    def test_opened_from_is_the_default_and_new_is_the_fallback(self):
        body, cloth = rb.gf2_target_row_specs(self.SESSION, ["body1", "cloth1"])
        self.assertEqual(body["target"], "body-detail")
        self.assertEqual(cloth["target"], rb.NEW_EDIT_TARGET)
        self.assertEqual(cloth["new_name"], "Cloth Edit 2")

    def test_previous_unsent_choices_survive_a_rebuild(self):
        previous = [{"part": "body1", "target": "body-base", "new_name": ""},
                    {"part": "cloth1", "target": rb.NEW_EDIT_TARGET,
                     "new_name": "Hand Named"}]
        body, cloth = rb.gf2_target_row_specs(self.SESSION, ["body1", "cloth1"], previous)
        self.assertEqual(body["target"], "body-base")
        self.assertEqual((cloth["target"], cloth["new_name"]),
                         (rb.NEW_EDIT_TARGET, "Hand Named"))

    def test_an_opened_id_missing_from_legacy_inventory_remains_selectable(self):
        specs = rb.gf2_target_row_specs({"part": "body1", "parts": [
            {"name": "body1", "edited": True, "editId": "legacy-edit"}]})
        self.assertEqual(specs[0]["target"], "legacy-edit")
        self.assertEqual(specs[0]["edits"][0]["label"], "legacy-edit")
        self.assertTrue(specs[0]["edits"][0]["holdsAuthoredMesh"])

    def test_a_synthetic_opened_id_uses_the_legacy_edited_flag(self):
        part = {"name": "body1", "edited": True, "editId": "missing",
                "edits": [{"id": "other", "label": "Other", "holdsAuthoredMesh": False}]}
        self.assertTrue(rb._session_edit_rows(part)[-1]["holdsAuthoredMesh"])
        self.assertEqual(rb.gf2_selected_mesh_edit_labels(
            {"parts": [part]}, {"body1": "missing"}), ["missing"])

    def test_a_named_session_builds_only_the_named_part_without_scene_names(self):
        session = dict(self.SESSION, part="cloth1")
        self.assertEqual([row["part"] for row in rb.gf2_target_row_specs(session)], ["cloth1"])


class TargetSidecarTests(unittest.TestCase):
    SESSION = {"revision": 7, "parts": [
        {"name": "body1", "editId": "body-edit", "defaultEditName": "Body Edit 2",
         "edits": [{"id": "body-edit", "label": "Body Edit", "holdsAuthoredMesh": True}]},
        {"name": "cloth1", "defaultEditName": "Cloth Edit 2", "edits": []},
    ]}

    def test_existing_and_new_targets_share_the_edit_ids_union(self):
        rows = [{"part": "body1", "target": "body-edit", "new_name": "ignored"},
                {"part": "cloth1", "target": rb.NEW_EDIT_TARGET, "new_name": "New Hem"}]
        self.assertEqual(rb.gf2_edit_targets(rows),
                         {"body1": "body-edit", "cloth1": {"new": "New Hem"}})

    def test_a_blank_new_name_stays_blank(self):
        rows = [{"part": "cloth1", "target": rb.NEW_EDIT_TARGET, "new_name": ""}]
        self.assertEqual(rb.gf2_edit_targets(rows), {"cloth1": {"new": ""}})

    def test_hidden_parts_are_unchanged_in_the_complete_sidecar(self):
        rows = [{"part": "body1", "target": "body-edit", "new_name": ""}]
        targets = rb.gf2_send_target_map(self.SESSION, rows)
        self.assertEqual(rb.gf2_send_sidecar(["cloth1"], targets, self.SESSION), {
            "source": "blender-send", "hiddenParts": ["cloth1"],
            "editIds": {"body1": "body-edit"},
        })

    def test_headless_sidecar_uses_the_same_default_selections(self):
        targets = rb.gf2_send_target_map(self.SESSION, part_names=["body1", "cloth1"])
        self.assertEqual(rb.gf2_send_sidecar([], targets, self.SESSION)["editIds"],
                         {"body1": "body-edit", "cloth1": {"new": "Cloth Edit 2"}})

    def test_sessionless_send_never_stamps_synthetic_new_targets(self):
        sessionless = {"part": None, "parts": []}
        targets = rb.gf2_send_target_map(sessionless, part_names=["body1"])
        self.assertEqual(targets, {})
        self.assertEqual(rb.gf2_send_sidecar([], targets, sessionless)["editIds"], {})

    def test_captured_targets_ignore_rows_changed_by_an_ack_before_send(self):
        rows_at_gate = [{"part": "cloth1", "target": rb.NEW_EDIT_TARGET,
                         "new_name": "This Send"}]
        captured = rb.gf2_send_target_map(self.SESSION, rows_at_gate)
        prior = rb.gf2_send_snapshot(self.SESSION, {"cloth1": {"new": "Prior Send"}})
        live = {"revision": 8, "parts": [*self.SESSION["parts"], {
            "name": "unused", "edits": []}]}
        live["parts"][1] = {"name": "cloth1", "defaultEditName": "Cloth Edit 3", "edits": [
            {"id": "prior-mint", "label": "Prior Send", "holdsAuthoredMesh": True}]}
        rows_after_ack = rb.gf2_target_row_specs(
            live, ["cloth1"], previous=rows_at_gate, acknowledged_snapshot=prior)
        self.assertEqual(rows_after_ack[0]["target"], "prior-mint")
        self.assertNotEqual(rb.gf2_edit_targets(rows_after_ack), captured)
        self.assertEqual(rb.gf2_send_sidecar([], captured, live)["editIds"],
                         {"cloth1": {"new": "This Send"}})


class SessionAcknowledgmentTests(unittest.TestCase):
    def _opening(self, default="Cloth Edit 2"):
        return {"revision": 10, "parts": [{
            "name": "cloth1", "defaultEditName": default,
            "edits": [{"id": "base", "label": "Cloth Base", "holdsAuthoredMesh": False}],
        }]}

    def test_only_a_higher_revision_is_adoptable(self):
        current = {"revision": 10}
        self.assertEqual(rb.gf2_newer_session(current, {"revision": 11}), {"revision": 11})
        self.assertIsNone(rb.gf2_newer_session(current, {"revision": 10}))
        self.assertIsNone(rb.gf2_newer_session(current, {"revision": 9}))
        self.assertIsNone(rb.gf2_newer_session(current, None))

    def test_acknowledged_named_new_edit_becomes_the_selected_existing_edit(self):
        opening = self._opening()
        targets = {"cloth1": {"new": "Hand Hem"}}
        snapshot = rb.gf2_send_snapshot(opening, targets)
        live = {"revision": 11, "parts": [{
            "name": "cloth1", "defaultEditName": "Cloth Edit 3", "edits": [
                {"id": "base", "label": "Cloth Base", "holdsAuthoredMesh": False},
                {"id": "minted", "label": "Hand Hem", "holdsAuthoredMesh": True}],
        }]}
        spec = rb.gf2_target_row_specs(live, ["cloth1"], acknowledged_snapshot=snapshot)[0]
        self.assertEqual(spec["target"], "minted")
        self.assertEqual(rb.gf2_edit_targets([spec]), {"cloth1": "minted"})
        self.assertEqual(rb.gf2_selected_mesh_edit_labels(live, {"cloth1": "minted"}),
                         ["Hand Hem"])

    def test_acknowledged_duplicate_name_uses_the_sole_app_minted_fallback(self):
        opening = self._opening()
        opening["parts"][0]["edits"].append(
            {"id": "fresh", "label": "Fresh", "holdsAuthoredMesh": True})
        snapshot = rb.gf2_send_snapshot(opening, {"cloth1": {"new": "fresh"}})
        live = {"revision": 11, "parts": [{
            "name": "cloth1", "defaultEditName": "Edit 3", "edits": [
                *opening["parts"][0]["edits"],
                {"id": "minted", "label": "Edit 2", "holdsAuthoredMesh": True}],
        }]}

        spec = rb.gf2_target_row_specs(live, ["cloth1"], acknowledged_snapshot=snapshot)[0]

        self.assertEqual(spec["target"], "minted")
        self.assertEqual(rb.gf2_edit_targets([spec]), {"cloth1": "minted"})

    def test_renamed_new_edit_stays_new_when_the_acknowledgement_is_ambiguous(self):
        opening = self._opening()
        snapshot = rb.gf2_send_snapshot(opening, {"cloth1": {"new": "Taken Name"}})
        live = {"revision": 11, "parts": [{
            "name": "cloth1", "defaultEditName": "Edit 4", "edits": [
                *opening["parts"][0]["edits"],
                {"id": "candidate-a", "label": "Edit 2", "holdsAuthoredMesh": True},
                {"id": "candidate-b", "label": "Edit 3", "holdsAuthoredMesh": True}],
        }]}

        spec = rb.gf2_target_row_specs(live, ["cloth1"], acknowledged_snapshot=snapshot)[0]

        self.assertEqual(spec["target"], rb.NEW_EDIT_TARGET)

    def test_blank_new_edit_matches_the_pre_send_default_after_acknowledgment(self):
        opening = self._opening(default="Cloth Edit 2")
        snapshot = rb.gf2_send_snapshot(opening, {"cloth1": {"new": ""}})
        self.assertEqual(snapshot["newMatches"], {"cloth1": "Cloth Edit 2"})
        live = {"revision": 11, "parts": [{"name": "cloth1", "edits": [
            {"id": "base", "label": "Cloth Base", "holdsAuthoredMesh": False},
            {"id": "minted", "label": "Cloth Edit 2", "holdsAuthoredMesh": True}],
        }]}
        spec = rb.gf2_target_row_specs(live, ["cloth1"], acknowledged_snapshot=snapshot)[0]
        self.assertEqual(spec["target"], "minted")

    def test_unacknowledged_refresh_preserves_the_current_new_choice(self):
        current = self._opening()
        previous = [{"part": "cloth1", "target": rb.NEW_EDIT_TARGET,
                     "new_name": "Not Sent Yet"}]
        spec = rb.gf2_target_row_specs(current, ["cloth1"], previous=previous)[0]
        self.assertEqual((spec["target"], spec["new_name"]),
                          (rb.NEW_EDIT_TARGET, "Not Sent Yet"))

    def test_acknowledged_existing_target_does_not_override_a_reselection(self):
        opening = {"revision": 10, "parts": [{"name": "cloth1", "editId": "edit-a",
                    "edits": [{"id": "edit-a", "label": "Edit A"},
                              {"id": "edit-b", "label": "Edit B"}]}]}
        snapshot = rb.gf2_send_snapshot(opening, {"cloth1": "edit-a"})
        live = dict(opening, revision=11)
        previous = [{"part": "cloth1", "target": "edit-b", "new_name": ""}]
        spec = rb.gf2_target_row_specs(
            live, ["cloth1"], previous=previous, acknowledged_snapshot=snapshot)[0]
        self.assertEqual(spec["target"], "edit-b")

    def test_send_snapshot_remembers_inventory_before_the_send(self):
        snapshot = rb.gf2_send_snapshot(self._opening(), {"cloth1": {"new": "Next"}})
        self.assertEqual(snapshot["revision"], 10)
        self.assertEqual(snapshot["knownEditIds"], {"cloth1": ["base"]})

    def test_partial_ack_promotes_only_the_part_that_minted(self):
        # An all-parts send where only cloth1 changed: cloth1's New row flips to its minted
        # edit while cloth2 — whose send landed nothing and never mints — keeps its row.
        opening = {"revision": 10, "parts": [
            {"name": "cloth1", "edits": [], "defaultEditName": "Edit 1"},
            {"name": "cloth2", "edits": [], "defaultEditName": "Edit 1"}]}
        snapshot = rb.gf2_send_snapshot(opening, {"cloth1": {"new": "Edit 1"},
                                                  "cloth2": {"new": "Edit 1"}})
        live = {"revision": 11, "parts": [
            {"name": "cloth1", "edits": [
                {"id": "minted", "label": "Edit 1", "holdsAuthoredMesh": True}],
             "defaultEditName": "Edit 2"},
            {"name": "cloth2", "edits": [], "defaultEditName": "Edit 1"}]}
        previous = [{"part": "cloth1", "target": rb.NEW_EDIT_TARGET, "new_name": "Edit 1"},
                    {"part": "cloth2", "target": rb.NEW_EDIT_TARGET, "new_name": "Edit 1"}]
        specs = rb.gf2_target_row_specs(live, ["cloth1", "cloth2"], previous=previous,
                                        acknowledged_snapshot=snapshot)
        self.assertEqual(specs[0]["target"], "minted")
        self.assertEqual((specs[1]["target"], specs[1]["new_name"]),
                         (rb.NEW_EDIT_TARGET, "Edit 1"))

    def test_new_send_snapshot_replaces_the_old_duplicate_mint_candidate(self):
        unrelated = {"revision": 11, "parts": [{"name": "cloth1", "edits": [
            {"id": "base", "label": "Cloth Base", "holdsAuthoredMesh": False},
            {"id": "unrelated", "label": "Other", "holdsAuthoredMesh": True}]}]}
        second = rb.gf2_send_snapshot(unrelated, {"cloth1": {"new": "Second"}})
        fulfilled = {"revision": 12, "parts": [{"name": "cloth1", "edits": [
            *unrelated["parts"][0]["edits"],
            {"id": "old-mint", "label": "First", "holdsAuthoredMesh": True},
            {"id": "minted", "label": "Second", "holdsAuthoredMesh": True}]}]}
        self.assertEqual(rb._snapshot_minted_edit(
            fulfilled["parts"][0], "cloth1", second)["id"], "minted")

    def test_panel_wrap_width_scales_and_survives_garbage(self):
        self.assertEqual(rb.gf2_panel_wrap_width(384, 1.0), 48)
        self.assertEqual(rb.gf2_panel_wrap_width(384, 1.5), 32)
        self.assertEqual(rb.gf2_panel_wrap_width(0, 1.0), 48)
        self.assertEqual(rb.gf2_panel_wrap_width(None, None), 48)

    def test_wrapped_lines_never_exceed_the_width_and_indent_continuations(self):
        tip = "Deleting all of a part's geometry sends it as a hide."
        lines = rb.gf2_wrapped_lines(tip, 32)
        self.assertGreater(len(lines), 1)
        for line in lines:
            self.assertLessEqual(len(line), 32)
        self.assertEqual(" ".join(line.strip() for line in lines), tip)


class _FakeTargetRows(list):
    def add(self):
        row = types.SimpleNamespace(part_name="", part_label="", target="", new_name="")
        self.append(row)
        return row


class _FakeScene(dict):
    def __init__(self, glb_path=""):
        super().__init__()
        self.gf2_glb_path = glb_path
        self.gf2_target_rows = _FakeTargetRows()


class SceneSessionRefreshTests(unittest.TestCase):
    def test_rebuild_keeps_dynamic_enum_strings_referenced(self):
        scene = _FakeScene()
        session = {"parts": [{"name": "body1", "editId": "body-edit", "edits": [
            {"id": "body-edit", "label": "Body Edit", "holdsAuthoredMesh": False}]}]}
        with mock.patch.object(rb, "gf2_part_collections",
                               lambda: [types.SimpleNamespace(name="body1")]):
            rb._rebuild_target_rows(scene, session)
        items = rb._TARGET_ITEM_REFS["body1"]
        self.assertIs(rb._gf2_target_items(scene.gf2_target_rows[0], None), items)
        self.assertEqual([item[1] for item in items], ["Body Edit", "New Edit"])

    def test_higher_revision_promotes_the_send_snapshot_and_clears_pending_state(self):
        with tempfile.TemporaryDirectory() as directory:
            glb = os.path.join(directory, "cloth.glb")
            opening = {"revision": 4, "parts": [{"name": "cloth1",
                       "defaultEditName": "Cloth Edit 2", "edits": []}]}
            scene = _FakeScene(glb)
            scene[rb.SESSION_KEY] = json.dumps(opening)
            scene[rb.SEND_SNAPSHOT_KEY] = json.dumps(
                rb.gf2_send_snapshot(opening, {"cloth1": {"new": ""}}))
            old_row = scene.gf2_target_rows.add()
            old_row.part_name = "cloth1"
            old_row.target = rb.NEW_EDIT_TARGET
            old_row.new_name = ""
            live = {"revision": 5, "parts": [{"name": "cloth1", "edits": [
                {"id": "minted", "label": "Cloth Edit 2", "holdsAuthoredMesh": True}]}]}
            with open(rb.session_path(glb), "w", encoding="utf-8") as stream:
                json.dump(live, stream)
            context = types.SimpleNamespace(scene=scene)
            with mock.patch.object(rb.bpy, "context", context, create=True), \
                    mock.patch.object(rb, "gf2_part_collections",
                                      lambda: [types.SimpleNamespace(name="cloth1")]):
                adopted = rb._refresh_session_snapshot(scene)
            self.assertEqual(adopted["revision"], 5)
            self.assertEqual(scene.gf2_target_rows[0].target, "minted")
            self.assertNotIn(rb.SEND_SNAPSHOT_KEY, scene)

    def test_revision_advance_consumes_the_snapshot_and_keeps_unminted_rows(self):
        # The session file belongs to this run alone, so a revision past the snapshot's means the
        # app processed that send. A part that minted nothing keeps its New row as it stands.
        with tempfile.TemporaryDirectory() as directory:
            glb = os.path.join(directory, "cloth.glb")
            opening = {"revision": 4, "parts": [{"name": "cloth1",
                       "defaultEditName": "Cloth Edit 2", "edits": []}]}
            snapshot = rb.gf2_send_snapshot(opening, {"cloth1": {"new": "Cloth Edit 2"}})
            scene = _FakeScene(glb)
            scene[rb.SESSION_KEY] = json.dumps(opening)
            scene[rb.SEND_SNAPSHOT_KEY] = json.dumps(snapshot)
            row = scene.gf2_target_rows.add()
            row.part_name = "cloth1"
            row.target = rb.NEW_EDIT_TARGET
            row.new_name = "Cloth Edit 2"
            unrelated = {"revision": 5, "parts": [{"name": "cloth1",
                         "defaultEditName": "Cloth Edit 2", "edits": []}]}
            with open(rb.session_path(glb), "w", encoding="utf-8") as stream:
                json.dump(unrelated, stream)
            with mock.patch.object(rb.bpy, "context", types.SimpleNamespace(scene=scene), create=True), \
                    mock.patch.object(rb, "gf2_part_collections",
                                      lambda: [types.SimpleNamespace(name="cloth1")]):
                adopted = rb._refresh_session_snapshot(scene)
            self.assertEqual(adopted["revision"], 5)
            self.assertNotIn(rb.SEND_SNAPSHOT_KEY, scene)
            self.assertEqual(scene.gf2_target_rows[0].target, rb.NEW_EDIT_TARGET)
            self.assertEqual(scene.gf2_target_rows[0].new_name, "Cloth Edit 2")

    def test_unreadable_refresh_retains_session_and_rows(self):
        with tempfile.TemporaryDirectory() as directory:
            glb = os.path.join(directory, "cloth.glb")
            scene = _FakeScene(glb)
            current = {"revision": 4, "parts": [{"name": "cloth1"}]}
            scene[rb.SESSION_KEY] = json.dumps(current)
            row = scene.gf2_target_rows.add()
            row.part_name = "cloth1"
            row.target = rb.NEW_EDIT_TARGET
            row.new_name = "Work In Progress"
            with open(rb.session_path(glb), "w", encoding="utf-8") as stream:
                stream.write("{")
            with mock.patch.object(rb.bpy, "context", types.SimpleNamespace(scene=scene), create=True):
                retained = rb._refresh_session_snapshot(scene)
            self.assertEqual(retained, current)
            self.assertIs(scene.gf2_target_rows[0], row)
            self.assertEqual(row.new_name, "Work In Progress")

    def test_store_session_writes_the_explicit_scene_even_when_it_is_empty(self):
        explicit = _FakeScene()
        context_scene = _FakeScene()
        with mock.patch.object(
                rb.bpy, "context", types.SimpleNamespace(scene=context_scene), create=True):
            rb._store_session({"revision": 2}, explicit)
        self.assertEqual(json.loads(explicit[rb.SESSION_KEY]), {"revision": 2})
        self.assertNotIn(rb.SESSION_KEY, context_scene)


class LiveSessionReadTests(unittest.TestCase):
    def test_a_complete_document_is_read_with_forward_compatible_notices(self):
        with tempfile.TemporaryDirectory() as directory:
            glb = os.path.join(directory, "part.glb")
            with open(rb.session_path(glb), "w", encoding="utf-8") as stream:
                json.dump({"revision": 3, "parts": [], "notices": ["Texture table was unreadable."]},
                          stream)
            self.assertEqual(rb._read_live_session(glb)["revision"], 3)
            self.assertEqual(rb._read_live_session(glb)["notices"],
                             ["Texture table was unreadable."])

    def test_an_unreadable_document_returns_no_replacement_snapshot(self):
        with tempfile.TemporaryDirectory() as directory:
            glb = os.path.join(directory, "part.glb")
            with open(rb.session_path(glb), "w", encoding="utf-8") as stream:
                stream.write('{"revision":')
            self.assertIsNone(rb._read_live_session(glb))
            self.assertIsNone(rb.gf2_newer_session({"revision": 3}, rb._read_live_session(glb)))


class SessionPresentationTests(unittest.TestCase):
    def test_only_explicit_false_starts_a_part_hidden(self):
        session = {"parts": [{"name": "body1", "viewportVisible": False},
                             {"name": "cloth1", "viewportVisible": True},
                             {"name": "hair"}]}
        self.assertFalse(rb.gf2_part_viewport_visible(session, "body1"))
        self.assertTrue(rb.gf2_part_viewport_visible(session, "cloth1"))
        self.assertTrue(rb.gf2_part_viewport_visible(session, "hair"))
        self.assertTrue(rb.gf2_part_viewport_visible(session, "unknown"))

    def test_notice_list_ignores_non_sentences_and_wraps_with_one_warning_prefix(self):
        notices = rb.gf2_session_notices(
            {"notices": [" A readable notice sentence. ", "   ", "", 12]})
        self.assertEqual(notices, ["A readable notice sentence."])
        lines = rb.gf2_wrapped_notice_lines("A notice sentence that must wrap in a narrow panel.", 24)
        self.assertGreater(len(lines), 1)
        self.assertTrue(lines[0].startswith("⚠ "))
        self.assertTrue(all(line.startswith("  ") for line in lines[1:]))


class ClaimedMeshTests(unittest.TestCase):
    """A re-opened edited part comes back under the modder's own mesh name, not the name the app
    declared — the claim is what still lands it in a part collection instead of Reference."""

    def test_a_lone_sessions_stray_mesh_is_claimed_for_the_missing_part(self):
        session = {"part": "cloth1", "parts": [{"name": "cloth1", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["DonorSkirt"]), {"DonorSkirt": "cloth1"})

    def test_a_part_the_glb_carries_by_name_claims_nothing(self):
        session = {"part": "cloth1", "parts": [{"name": "cloth1", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["cloth1"]), {})

    def test_two_stray_meshes_claim_nothing(self):
        """One missing part and two candidates is a guess, and a wrong claim would ship the wrong
        mesh under the part's name. Both stay Reference."""
        session = {"part": "cloth1", "parts": [{"name": "cloth1", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["DonorA", "DonorB"]), {})

    def test_no_session_claims_nothing(self):
        self.assertEqual(rb.gf2_claimed_meshes({"part": None, "parts": []}, ["anything"]), {})

    def test_an_open_all_session_claims_its_one_renamed_part(self):
        session = {"part": None, "parts": [{"name": "body1", "edited": False},
                                           {"name": "cloth1", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["body1", "DonorSkirt"]),
                         {"DonorSkirt": "cloth1"})

    def test_an_unwritable_parts_mesh_is_spoken_for_not_stray(self):
        """The reference mesh matched its declared name, so the one stray pairs with the one missing
        WRITABLE part — the unwritable entry neither claims nor blocks."""
        session = {"part": None, "parts": [{"name": "face", "edited": False, "writable": False},
                                           {"name": "hair", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["face", "DonorHair"]),
                         {"DonorHair": "hair"})

    def test_two_missing_writable_parts_claim_nothing(self):
        session = {"part": None, "parts": [{"name": "hair", "edited": True},
                                           {"name": "cloth1", "edited": True}]}
        self.assertEqual(rb.gf2_claimed_meshes(session, ["DonorHair"]), {})


class TransportCollectionTests(unittest.TestCase):
    """The send reads tags off the materials a mesh draws with, says when one slot was claimed twice, and
    the import finds a mesh whose name Blender suffixed."""

    @staticmethod
    def _tagged_node(mesh, material, primitive, prop):
        class Node(dict):
            pass
        node = Node({rb.TEXTURE_TRANSPORT_NODE: json.dumps({
            "owner": {"mesh": mesh, "material": material, "primitive": primitive},
            "property": prop, "semantic": "texture",
            "stock": {"name": "s", "bundle": "b", "path_id": 1}})})
        node.image = types.SimpleNamespace(name=prop + " image")
        node.name = "GF2 " + prop
        return node

    def _material(self, name, *nodes):
        return types.SimpleNamespace(name=name, node_tree=types.SimpleNamespace(nodes=list(nodes)))

    def test_an_object_linked_material_slot_is_read_the_way_the_checks_read_it(self):
        data_material = self._material("data-side", self._tagged_node("body", 0, 0, "_MaskTex"))
        object_material = self._material("object-side", self._tagged_node("body", 0, 0, "_BaseMap"))
        mesh = types.SimpleNamespace(
            name="body",
            material_slots=[types.SimpleNamespace(material=object_material)],
            data=types.SimpleNamespace(materials=[data_material]))

        with mock.patch.object(rb, "_gf2_image_png", return_value=b"png"):
            rows, duplicates = rb._gf2_collect_texture_transport([mesh])

        self.assertEqual([row["property"] for row in rows], ["_BaseMap"])
        self.assertEqual(duplicates, [])

    def test_a_mesh_without_slots_falls_back_to_its_data_materials(self):
        mesh = types.SimpleNamespace(
            name="body",
            data=types.SimpleNamespace(materials=[
                self._material("m", self._tagged_node("body", 0, 0, "_BaseMap"))]))

        with mock.patch.object(rb, "_gf2_image_png", return_value=b"png"):
            rows, duplicates = rb._gf2_collect_texture_transport([mesh])

        self.assertEqual([row["property"] for row in rows], ["_BaseMap"])

    def test_a_slot_two_nodes_claim_sends_the_first_and_names_the_second(self):
        first = self._tagged_node("body", 0, 0, "_BaseMap")
        first.image = types.SimpleNamespace(name="first")
        second = self._tagged_node("body", 0, 0, "_BaseMap")
        second.image = types.SimpleNamespace(name="second")
        mesh = types.SimpleNamespace(
            name="body",
            material_slots=[types.SimpleNamespace(material=self._material("cloth", first, second))])

        with mock.patch.object(rb, "_gf2_image_png", return_value=b"png"):
            rows, duplicates = rb._gf2_collect_texture_transport([mesh])

        self.assertEqual([row["image_name"] for row in rows], ["first"])
        self.assertEqual(duplicates, [("_BaseMap", "cloth", "body")])
        self.assertEqual(rb.gf2_duplicate_tag_lines(duplicates),
                         ["Two nodes carry '_BaseMap' on 'cloth' (body); the first one was sent."])

    def test_the_import_finds_a_mesh_blender_suffixed_and_misses_by_name_otherwise(self):
        suffixed = types.SimpleNamespace(name="body.001")
        exact = types.SimpleNamespace(name="cloth")

        self.assertIs(rb._gf2_transport_mesh([suffixed, exact], "cloth"), exact)
        self.assertIs(rb._gf2_transport_mesh([suffixed, exact], "body"), suffixed)
        self.assertIsNone(rb._gf2_transport_mesh([suffixed, exact], "hair"))
        self.assertIsNone(rb._gf2_transport_mesh([suffixed, exact], None))


class OverwriteWarningTests(unittest.TestCase):
    def test_nothing_edited_says_nothing(self):
        self.assertIsNone(rb.gf2_overwrite_warning([]))

    def test_one_selected_edit_names_the_stored_mesh_work(self):
        self.assertEqual(rb.gf2_overwrite_warning(["Hem Fix"]),
                         "Sending replaces the mesh work stored in Hem Fix.")

    def test_several_selected_edits_share_the_exact_lead(self):
        self.assertEqual(rb.gf2_overwrite_warning(["Body Sculpt", "Hem Fix"]),
                         "Sending replaces the mesh work stored in Body Sculpt, Hem Fix.")


class SelectedMeshEditTests(unittest.TestCase):
    SESSION = {"parts": [
        {"name": "body1", "edits": [
            {"id": "body-stock", "label": "Body Stock", "holdsAuthoredMesh": False},
            {"id": "body-sculpt", "label": "Body Sculpt", "holdsAuthoredMesh": True}]},
        {"name": "cloth1", "edits": [
            {"id": "cloth-fix", "label": "Hem Fix", "holdsAuthoredMesh": True}]},
    ]}

    def test_only_selected_existing_targets_with_mesh_work_are_named(self):
        targets = {"body1": "body-sculpt", "cloth1": {"new": "Fresh Hem"}}
        self.assertEqual(rb.gf2_selected_mesh_edit_labels(self.SESSION, targets), ["Body Sculpt"])

    def test_an_empty_existing_edit_does_not_trigger_confirmation(self):
        self.assertEqual(rb.gf2_selected_mesh_edit_labels(self.SESSION,
                                                          {"body1": "body-stock"}), [])

    def test_every_selected_mesh_holding_edit_is_named_in_part_order(self):
        targets = {"body1": "body-sculpt", "cloth1": "cloth-fix"}
        self.assertEqual(rb.gf2_selected_mesh_edit_labels(self.SESSION, targets),
                         ["Body Sculpt", "Hem Fix"])

    def test_equal_edit_labels_are_qualified_by_part_and_deduped_by_id(self):
        session = {"parts": [
            {"name": "body1", "edits": [
                {"id": "body-edit", "label": "Edit 1", "holdsAuthoredMesh": True}]},
            {"name": "cloth1", "edits": [
                {"id": "cloth-edit", "label": "Edit 1", "holdsAuthoredMesh": True}]},
        ]}
        labels = rb.gf2_selected_mesh_edit_labels(
            session, {"body1": "body-edit", "cloth1": "cloth-edit"})
        self.assertEqual(labels, ["body1 — Edit 1", "cloth1 — Edit 1"])


class EmptyingIsAHideTests(unittest.TestCase):
    """Emptying every part collection is a Hide in a per-part session and an empty deliverable
    everywhere else — the session description is what tells them apart."""

    OUTFIT = {"part": "cloth1", "parts": [{"name": "body1"}, {"name": "cloth1"}, {"name": "hair"}]}

    def test_a_named_part_of_an_outfit_may_be_emptied(self):
        self.assertTrue(rb.gf2_emptying_is_a_hide(self.OUTFIT))

    def test_an_open_all_session_emptied_to_nothing_is_not_a_hide(self):
        self.assertFalse(rb.gf2_emptying_is_a_hide(
            {"part": None, "parts": [{"name": "body1"}, {"name": "cloth1"}]}))

    def test_a_session_whose_part_is_the_whole_mod_is_not_a_hide(self):
        """Hiding the only part leaves nothing to build, which is the case the block exists for."""
        self.assertFalse(rb.gf2_emptying_is_a_hide({"part": "body1", "parts": [{"name": "body1"}]}))

    def test_no_session_at_all_is_not_a_hide(self):
        self.assertFalse(rb.gf2_emptying_is_a_hide({}))
        self.assertFalse(rb.gf2_emptying_is_a_hide(None))


def _parts(by_object):
    """Stand in for the scene's part collections: object name -> the part collection it ships in.
    An object absent from the map is in no part."""
    return mock.patch.object(
        rb, "_part_of",
        lambda o: types.SimpleNamespace(name=by_object[o.name]) if o.name in by_object else None)


class UnskinnedPartTests(unittest.TestCase):
    """A static prop's mesh carries no weights and its session carries no armature, so the weight gate
    would count every vertex as unsolvable and block every send. The app declares which parts are in
    that state; nothing here infers it from the scene."""

    def test_only_an_explicit_true_exempts_a_part(self):
        session = {"parts": [{"name": "prop", "unskinned": True},
                             {"name": "body1", "unskinned": False},
                             {"name": "hair"}]}
        self.assertEqual(rb.gf2_unskinned_parts(session), {"prop"})

    def test_a_session_that_says_nothing_exempts_nothing(self):
        """A skinned part whose armature the modder deleted must still block: that is a real authoring
        error, and an unstamped session must never read as unskinned."""
        self.assertEqual(rb.gf2_unskinned_parts({}), set())
        self.assertEqual(rb.gf2_unskinned_parts({"parts": []}), set())

    def test_a_duplicate_suffix_still_reads_as_its_part(self):
        obj = types.SimpleNamespace(name="whatever")
        with _parts({"whatever": "prop.001"}):
            self.assertTrue(rb._is_unskinned(obj, {"prop"}))
        with _parts({"whatever": "body1"}):
            self.assertFalse(rb._is_unskinned(obj, {"prop"}))

    def test_a_mesh_named_after_the_prop_inside_a_skinned_part_still_blocks(self):
        """The exemption belongs to the part collection, not to a name. A mesh hand-named after the
        static prop and sitting in a skinned part would otherwise carry the exemption out of the part
        it was declared for, and its unweighted vertices would stop blocking."""
        obj = types.SimpleNamespace(name="prop")
        with _parts({"prop": "body1"}):
            self.assertFalse(rb._is_unskinned(obj, {"prop"}))

    def test_an_object_in_no_part_is_exempt_from_nothing(self):
        with _parts({}):
            self.assertFalse(rb._is_unskinned(types.SimpleNamespace(name="prop"), {"prop"}))

    def test_the_solve_skips_a_declared_unskinned_mesh(self):
        """The exemption is what keeps a static prop out of the blocker: with no armature every
        vertex of it counts as unsolvable."""
        prop = types.SimpleNamespace(name="prop")
        body = types.SimpleNamespace(name="body1")
        with mock.patch.object(rb, "_missing_verts", lambda o: [0, 1, 2]), \
                _parts({"prop": "prop", "body1": "body1"}):
            self.assertEqual(rb._unsolvable_weights_by_object([prop, body], None, {"prop"}),
                             [("body1", 3)])
            self.assertEqual(rb._unsolvable_weights_by_object([prop, body], None),
                             [("prop", 3), ("body1", 3)])

    def test_the_fill_leaves_a_declared_unskinned_mesh_alone(self):
        prop = types.SimpleNamespace(name="prop")
        with mock.patch.object(rb, "_missing_verts", lambda o: [0, 1, 2]), \
                _parts({"prop": "prop"}):
            self.assertEqual(rb.gf2_fill_missing_weights([prop], None, {"prop"}), (0, 0))
            self.assertEqual(rb.gf2_fill_missing_weights([prop], None), (0, 3))


class TransformWarningTests(unittest.TestCase):
    """What an Object-mode transform actually costs, per skin state.

    A skinned mesh's object transform is baked into the exported vertices once and its node written at
    identity, so moving, rotating or scaling one is not a mistake and must not be reported as one. The
    single exception is a mirroring scale, which flips the geometry without flipping the winding. An
    unskinned mesh keeps its local positions and carries the transform on the node, which the app never
    reads — so the whole of it is lost, and that IS worth stopping for."""

    IDENTITY = {"location": [0.0, 0.0, 0.0], "rotation": [1.0, 0.0, 0.0, 0.0], "scale": [1.0, 1.0, 1.0]}

    def _after(self, **changes):
        after = {k: list(v) for k, v in self.IDENTITY.items()}
        after.update({k: list(v) for k, v in changes.items()})
        return after

    def _warn(self, skinned, before=None, **changes):
        return rb.gf2_transform_warning("body1", self.IDENTITY if before is None else before,
                                        self._after(**changes), skinned)

    # ---- skinned: the exporter bakes it, so ordinary placement says nothing

    def test_a_scaled_skinned_mesh_says_nothing(self):
        self.assertIsNone(self._warn(True, scale=[2.0, 2.0, 2.0]))

    def test_a_moved_skinned_mesh_says_nothing(self):
        self.assertIsNone(self._warn(True, location=[0.0, 3.0, 0.0]))

    def test_a_rotated_skinned_mesh_says_nothing(self):
        self.assertIsNone(self._warn(True, rotation=[0.707, 0.707, 0.0, 0.0]))

    # ---- skinned: a mirroring scale ships geometry the winding no longer matches

    def test_a_negatively_scaled_skinned_mesh_reads_inside_out(self):
        sev, message = self._warn(True, scale=[-1.0, 1.0, 1.0])
        self.assertEqual(sev, "SOFT")
        self.assertEqual(message, "'body1' has a negative Object Mode scale. The mesh ships mirrored "
                                  "without flipped faces and renders inside-out.")

    def test_two_negative_axes_are_a_rotation_not_a_mirror(self):
        """Handedness is what matters, and an even number of flipped axes keeps it."""
        self.assertIsNone(self._warn(True, scale=[-1.0, -1.0, 1.0]))

    def test_a_mesh_imported_mirrored_is_not_warned_for_staying_mirrored(self):
        before = dict(self.IDENTITY, scale=[-1.0, 1.0, 1.0])
        self.assertIsNone(self._warn(True, before=before, scale=[-2.0, 2.0, 2.0]))

    # ---- unskinned: the node carries it and the app drops it

    def test_a_moved_unskinned_mesh_is_warned(self):
        sev, message = self._warn(False, location=[0.0, 3.0, 0.0])
        self.assertEqual(sev, "SOFT")
        self.assertEqual(message, "'body1' has no skeleton. Object Mode position, rotation, and scale "
                                  "are dropped on Send. Apply the transform (Ctrl+A in Object Mode), "
                                  "or edit the geometry in Edit Mode.")

    def test_a_rotated_unskinned_mesh_is_warned(self):
        self.assertIsNotNone(self._warn(False, rotation=[0.707, 0.707, 0.0, 0.0]))

    def test_a_scaled_unskinned_mesh_is_warned(self):
        self.assertIsNotNone(self._warn(False, scale=[2.0, 2.0, 2.0]))

    def test_an_unskinned_mesh_left_where_it_arrived_says_nothing(self):
        self.assertIsNone(self._warn(False))

    def test_float_noise_is_not_a_change(self):
        self.assertIsNone(self._warn(False, location=[0.0, 1e-5, 0.0]))

    # ---- a baseline that predates a component leaves it uncompared

    def test_an_older_baseline_without_a_move_warns_about_nothing(self):
        before = {"scale": [1.0, 1.0, 1.0]}
        self.assertIsNone(rb.gf2_transform_warning("body1", before,
                                                   self._after(location=[0.0, 3.0, 0.0]), False))

    def test_no_baseline_scale_leaves_a_mirror_uncompared(self):
        self.assertIsNone(rb.gf2_transform_warning("body1", {}, self._after(scale=[-1.0, 1.0, 1.0]),
                                                   True))


def _full_pass(cheap, shipping=("mesh",), unsolvable=(), session=None):
    """Run gf2_run_checks with the scene reads it makes stubbed out, so the composition of the full
    pass is testable without Blender: what the cheap pass returned, what ships, the session, and what
    the weight solve found are the only inputs it has."""
    with mock.patch.object(rb, "gf2_cheap_checks", lambda m, a: list(cheap)), \
            mock.patch.object(rb, "gf2_shipping_meshes", lambda: list(shipping)), \
            mock.patch.object(rb, "load_session", lambda: session or {}), \
            mock.patch.object(rb, "_unsolvable_weights_by_object", lambda m, a, u=(): list(unsolvable)):
        return rb.gf2_run_checks(["mesh"], None)


class FullPassTests(unittest.TestCase):
    """The full pass is the cheap pass plus the unweighted-vertex blocker, which joins the leading
    HARD entries so a blocked send reads its blockers before its warnings."""

    def test_full_pass_adds_the_slow_blocker_ahead_of_the_warnings(self):
        got = _full_pass([("HARD", "no armature"), ("SOFT", "scale")], unsolvable=[("body1", 4)])
        self.assertEqual([sev for sev, _ in got], ["HARD", "HARD", "SOFT"])
        self.assertIn("4 vertices have", got[1][1])

    def test_the_blocker_names_the_objects_it_found(self):
        """The blocker names every object it found, so the modder knows where to look."""
        got = _full_pass([], unsolvable=[("body1", 4), ("cloth1", 2)])
        self.assertEqual(len(got), 1)
        self.assertIn("6 vertices have", got[0][1])
        self.assertIn("'body1', 'cloth1'", got[0][1])

    def test_the_blocker_joins_the_leading_hards_not_the_hard_count(self):
        """A SOFT can precede a HARD (a name warning ahead of an excluded collection), so the insert
        point is the end of the LEADING run of blockers, not the number of them."""
        cheap = [("HARD", "stray"), ("SOFT", "name"), ("HARD", "excluded")]
        got = _full_pass(cheap, unsolvable=[("body1", 1)])
        self.assertEqual([sev for sev, _ in got], ["HARD", "HARD", "SOFT", "HARD"])
        self.assertIn("no weight", got[1][1])

    def test_an_all_soft_cheap_pass_puts_the_blocker_first(self):
        got = _full_pass([("SOFT", "scale")], unsolvable=[("body1", 1)])
        self.assertEqual([sev for sev, _ in got], ["HARD", "SOFT"])

    def test_nothing_shipping_skips_the_solve(self):
        """The solve is the expensive half. With no shipping mesh there is nothing to solve, and the
        empty-scope blocker the cheap pass raised is the whole answer."""
        calls = []

        def _solve(meshes, arm, unskinned=()):
            calls.append(meshes)
            return [("body1", 3)]

        with mock.patch.object(rb, "gf2_cheap_checks", lambda m, a: [("HARD", "empty")]), \
                mock.patch.object(rb, "gf2_shipping_meshes", list), \
                mock.patch.object(rb, "load_session", dict), \
                mock.patch.object(rb, "_unsolvable_weights_by_object", _solve):
            got = rb.gf2_run_checks([], None)
        self.assertEqual(calls, [])
        self.assertEqual(got, [("HARD", "empty")])


class _AlphaSocket:
    def __init__(self, node, name):
        self.node = node
        self.name = name
        self.links = []
        self.default_value = None


class _AlphaLink:
    def __init__(self, source, target):
        self.from_socket = source
        self.from_node = source.node
        self.to_socket = target


class _AlphaLinks:
    def new(self, source, target):
        link = _AlphaLink(source, target)
        source.links.append(link)
        target.links.append(link)
        return link

    @staticmethod
    def remove(link):
        link.from_socket.links.remove(link)
        link.to_socket.links.remove(link)


class _AlphaNode:
    def __init__(self, node_type, bl_idname, name, input_names=(), output_names=()):
        self.type = node_type
        self.bl_idname = bl_idname
        self.name = name
        self.label = ""
        self.location = types.SimpleNamespace(x=0, y=0)
        self.clamp = False
        self.inputs = {name: _AlphaSocket(self, name) for name in input_names}
        self.outputs = {name: _AlphaSocket(self, name) for name in output_names}
        self._properties = {}

    def get(self, name, default=None):
        return self._properties.get(name, default)

    def __setitem__(self, name, value):
        self._properties[name] = value


class _AlphaNodes(list):
    def get(self, name):
        return next((node for node in self if node.name == name), None)

    def new(self, bl_idname):
        if bl_idname != "ShaderNodeMapRange":
            raise AssertionError("fixture only creates Map Range nodes")
        node = _AlphaNode("MAP_RANGE", bl_idname, "Map Range",
                          ("Value", "From Min", "From Max", "To Min", "To Max"), ("Result",))
        self.append(node)
        return node


class _AlphaTree:
    def __init__(self, source_socket="Alpha"):
        self.nodes = _AlphaNodes()
        self.links = _AlphaLinks()
        texture = _AlphaNode("TEX_IMAGE", "ShaderNodeTexImage", "Base Color", (), ("Color", "Alpha"))
        principled = _AlphaNode("BSDF_PRINCIPLED", "ShaderNodeBsdfPrincipled", "Principled",
                                ("Alpha",), ())
        self.nodes.extend((texture, principled))
        self.links.new(texture.outputs[source_socket], principled.inputs["Alpha"])


class _AlphaObject:
    def __init__(self, material):
        self.data = types.SimpleNamespace(materials=[material])


class ImportedAlphaPreparationTests(unittest.TestCase):
    def test_post_cleanup_derivation_never_touches_a_removed_import_wrapper(self):
        class RemovedMesh:
            @property
            def name(self):
                raise ReferenceError("StructRNA of type Object has been removed")

            @property
            def type(self):
                raise ReferenceError("StructRNA of type Object has been removed")

        live = types.SimpleNamespace(type="MESH", name="cloth")
        removed = RemovedMesh()
        live_collection = [live, removed]
        imported_before_cleanup = tuple(live_collection)
        live_collection.remove(removed)  # cleanup deletes the imported Icosphere before derivation
        self.assertEqual(len(imported_before_cleanup), 2)

        self.assertEqual(rb._gf2_new_objects(live_collection, "MESH", set()), [live])

    def test_legacy_blend_is_selected_uses_legacy_overlap_and_gets_the_map_range(self):
        material = types.SimpleNamespace(name="legacy blend", blend_method="BLEND",
                                         show_transparent_back=True, use_backface_culling=True,
                                         node_tree=_AlphaTree())

        got = rb.gf2_prepare_imported_alpha_materials([_AlphaObject(material)])

        self.assertEqual(got, {"blended": 1, "remapped": 1, "missing_overlap": (),
                               "missing_alpha_link": ()})
        self.assertEqual(material.blend_method, "BLEND")
        self.assertFalse(material.show_transparent_back)
        self.assertFalse(material.use_backface_culling)
        remap = next(node for node in material.node_tree.nodes if node.get(rb.ALPHA_REMAP_TAG))
        self.assertEqual(remap.inputs["From Max"].default_value, 254.0 / 255.0)
        self.assertEqual(remap.inputs["Value"].links[0].from_socket.name, "Alpha")

    def test_color_feed_falls_back_transactionally_and_reports_the_dithered_preview(self):
        material = types.SimpleNamespace(name="unexpected graph", surface_render_method="BLENDED",
                                         use_backface_culling=True, use_transparency_overlap=True,
                                         node_tree=_AlphaTree("Color"))
        popups = []
        with mock.patch.object(rb, "_popup", lambda title, lines, icon: popups.append((title, lines, icon))):
            got = rb.gf2_prepare_imported_alpha_materials([_AlphaObject(material)])

        self.assertEqual(got["remapped"], 0)
        self.assertEqual(got["missing_overlap"], ())
        self.assertEqual(got["missing_alpha_link"], ("unexpected graph",))
        self.assertEqual(material.surface_render_method, "DITHERED")
        self.assertTrue(material.use_backface_culling)
        self.assertTrue(material.use_transparency_overlap)
        principled = next(node for node in material.node_tree.nodes
                          if node.type == "BSDF_PRINCIPLED")
        self.assertEqual(principled.inputs["Alpha"].links[0].from_socket.name, "Color")
        self.assertFalse(any(node.get(rb.ALPHA_REMAP_TAG) for node in material.node_tree.nodes))
        self.assertEqual(len(popups), 1)
        self.assertEqual(popups[0][0], "Alpha Preview Warnings")
        self.assertEqual(len(popups[0][1]), 1)
        self.assertTrue(all(line.startswith("⚠ ") for line in popups[0][1]))
        self.assertTrue(all("dithered transparency" in line for line in popups[0][1]))

    def test_missing_overlap_control_keeps_the_graph_and_uses_the_legacy_dithered_fallback(self):
        material = types.SimpleNamespace(name="no overlap control", blend_method="BLEND",
                                         use_backface_culling=True, node_tree=_AlphaTree())
        popups = []
        with mock.patch.object(rb, "_popup", lambda title, lines, icon: popups.append((title, lines, icon))):
            got = rb.gf2_prepare_imported_alpha_materials([_AlphaObject(material)])

        self.assertEqual(got["missing_overlap"], ("no overlap control",))
        self.assertEqual(got["missing_alpha_link"], ())
        self.assertEqual(material.blend_method, "HASHED")
        self.assertTrue(material.use_backface_culling)
        principled = next(node for node in material.node_tree.nodes
                          if node.type == "BSDF_PRINCIPLED")
        self.assertEqual(principled.inputs["Alpha"].links[0].from_socket.name, "Alpha")
        self.assertFalse(any(node.get(rb.ALPHA_REMAP_TAG) for node in material.node_tree.nodes))
        self.assertIn("dithered transparency", popups[0][1][0])


class PartLabelTests(unittest.TestCase):
    """The app's session label IS the part's display name; the structural cut is only the
    no-session fallback. Multi-token part names (`P3_body_fight`) are exactly what the fallback
    cannot derive, which is why the label rides the session document."""

    def _scene(self, session):
        return types.SimpleNamespace(
            context=types.SimpleNamespace(scene={rb.SESSION_KEY: json.dumps(session)}))

    def test_the_session_label_wins_over_the_structural_cut(self):
        session = {"parts": [
            {"name": "c_KarstSSR0101_slg_P3_body_fight_lod0", "label": "P3_body_fight"},
            {"name": "c_KarstSSR0101_slg_P1_cloth2_lod0", "label": "P1_cloth2"},
        ]}
        with mock.patch.object(rb, "bpy", self._scene(session)):
            rb._LABELS_CACHE["raw"] = None
            self.assertEqual(rb.gf2_label("c_KarstSSR0101_slg_P3_body_fight_lod0"), "P3_body_fight")
            # a Blender duplicate suffix still resolves to the labeled part
            self.assertEqual(rb.gf2_label("c_KarstSSR0101_slg_P1_cloth2_lod0.001"), "P1_cloth2")

    def test_without_a_session_the_structural_cut_and_modder_names_stand(self):
        with mock.patch.object(rb, "bpy", self._scene({"parts": []})):
            rb._LABELS_CACHE["raw"] = None
            self.assertEqual(rb.gf2_label("c_KarstSSR0101_slg_P1_cloth2_lod0"), "cloth2")
            self.assertEqual(rb.gf2_label("my own mesh"), "my own mesh")

    def test_a_rewritten_session_refreshes_the_labels(self):
        first = self._scene({"parts": [{"name": "c_X_slg_body_lod0", "label": "body"}]})
        second = self._scene({"parts": [{"name": "c_X_slg_body_lod0", "label": "P2_body"}]})
        with mock.patch.object(rb, "bpy", first):
            rb._LABELS_CACHE["raw"] = None
            self.assertEqual(rb.gf2_label("c_X_slg_body_lod0"), "body")
        with mock.patch.object(rb, "bpy", second):
            self.assertEqual(rb.gf2_label("c_X_slg_body_lod0"), "P2_body")


class UnchangedImageDetectorTests(unittest.TestCase):
    """The send's unchanged test is touch tracking, never pixels: clean + still stamped for this row +
    still packed at the deleted install path = hash-only; any doubt ships the bytes."""

    ROW = {"outbound_hash": "a" * 64}

    def setUp(self):
        self.addCleanup(rb._GF2_DIRTY_SEEN.clear)

    @staticmethod
    def _installed(**overrides):
        class Image(dict):
            name = "Base image"
            is_dirty = False
            packed_file = object()
            filepath_raw = "installed.png"
        image = Image({rb.TEXTURE_TRANSPORT_IMAGE_HASH: "a" * 64,
                       rb.TEXTURE_TRANSPORT_IMAGE_PATH: "installed.png"})
        for key, value in overrides.items():
            setattr(image, key, value)
        return image

    def test_the_clean_stamped_packed_image_is_unchanged(self):
        self.assertTrue(rb._gf2_image_is_unchanged(self._installed(), self.ROW))

    def test_dirty_now_is_changed(self):
        self.assertFalse(rb._gf2_image_is_unchanged(self._installed(is_dirty=True), self.ROW))

    def test_seen_dirty_earlier_is_changed_even_after_a_save_cleared_the_flag(self):
        image = self._installed(as_pointer=lambda: 4711)
        rb._GF2_DIRTY_SEEN.add(4711)
        self.assertFalse(rb._gf2_image_is_unchanged(image, self.ROW))

    def test_the_touched_stamp_is_changed(self):
        image = self._installed()
        image[rb.TEXTURE_TRANSPORT_IMAGE_TOUCHED] = True
        self.assertFalse(rb._gf2_image_is_unchanged(image, self.ROW))

    def test_an_unpacked_image_is_changed(self):
        self.assertFalse(rb._gf2_image_is_unchanged(self._installed(packed_file=None), self.ROW))

    def test_another_rows_stamp_is_changed(self):
        self.assertFalse(rb._gf2_image_is_unchanged(self._installed(), {"outbound_hash": "b" * 64}))

    def test_a_repathed_image_is_changed(self):
        self.assertFalse(rb._gf2_image_is_unchanged(self._installed(filepath_raw="elsewhere.png"),
                                                    self.ROW))

    def test_a_file_reappearing_at_the_install_path_is_a_user_save(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "installed.png")
            image = self._installed(filepath_raw=path)
            image[rb.TEXTURE_TRANSPORT_IMAGE_PATH] = path
            self.assertTrue(rb._gf2_image_is_unchanged(image, self.ROW))
            with open(path, "wb") as f:
                f.write(b"saved paint")
            self.assertFalse(rb._gf2_image_is_unchanged(image, self.ROW))

    def test_the_dirty_sweep_remembers_and_the_timer_pass_stamps(self):
        image = self._installed(is_dirty=True, as_pointer=lambda: 4712)
        stub = types.SimpleNamespace(data=types.SimpleNamespace(images=[image]))
        with mock.patch.object(rb, "bpy", stub):
            rb._gf2_note_dirty_images()
        self.assertIn(4712, rb._GF2_DIRTY_SEEN)
        self.assertNotIn(rb.TEXTURE_TRANSPORT_IMAGE_TOUCHED, image)
        image.is_dirty = False
        with mock.patch.object(rb, "bpy", stub):
            rb._gf2_note_dirty_images(stamp=True)
        self.assertTrue(image[rb.TEXTURE_TRANSPORT_IMAGE_TOUCHED])


class HashOnlyCollectionTests(unittest.TestCase):
    """Collect ships an untouched picture as its row alone, and the volatile dirty reason is stamped so
    the send's own save cannot read the next send as clean."""

    def setUp(self):
        self.addCleanup(rb._GF2_DIRTY_SEEN.clear)

    @staticmethod
    def _tagged_node(prop, image):
        class Node(dict):
            pass
        node = Node({rb.TEXTURE_TRANSPORT_NODE: json.dumps({
            "owner": {"mesh": "body", "material": 0, "primitive": 0},
            "property": prop, "semantic": "baseColor", "outbound_hash": "a" * 64,
            "stock": {"name": "s", "bundle": "b", "path_id": 1}})})
        node.image = image
        node.name = "GF2 " + prop
        return node

    @staticmethod
    def _mesh(*nodes):
        material = types.SimpleNamespace(name="cloth",
                                         node_tree=types.SimpleNamespace(nodes=list(nodes)))
        return types.SimpleNamespace(name="body",
                                     material_slots=[types.SimpleNamespace(material=material)])

    def test_the_clean_installed_image_returns_as_hash_only_and_is_never_encoded(self):
        image = UnchangedImageDetectorTests._installed()
        mesh = self._mesh(self._tagged_node("_BaseMap", image))
        with mock.patch.object(rb, "_gf2_image_png",
                               side_effect=AssertionError("image was encoded")):
            rows, duplicates = rb._gf2_collect_texture_transport([mesh])
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["outbound_hash"], "a" * 64)
        self.assertNotIn("png", rows[0])
        self.assertNotIn("image", rows[0])
        self.assertEqual(duplicates, [])

    def test_a_dirty_image_ships_bytes_and_stays_on_the_byte_route_after_its_flag_clears(self):
        image = UnchangedImageDetectorTests._installed(is_dirty=True)
        mesh = self._mesh(self._tagged_node("_BaseMap", image))
        with mock.patch.object(rb, "_gf2_image_png", return_value=b"painted picture"):
            first, _ = rb._gf2_collect_texture_transport([mesh])
        self.assertEqual(first[0]["png"], b"painted picture")
        self.assertTrue(image[rb.TEXTURE_TRANSPORT_IMAGE_TOUCHED])
        image.is_dirty = False   # what _gf2_image_png's save does to a painted image
        with mock.patch.object(rb, "_gf2_image_png", return_value=b"painted picture"):
            second, _ = rb._gf2_collect_texture_transport([mesh])
        self.assertEqual(second[0]["png"], b"painted picture")


class StandardChannelCollectionTests(unittest.TestCase):
    """Untagged pictures on PBR routes ride the return as ordinary glTF channel references — the
    hand-built-material and legacy-session route now that the exporter embeds nothing."""

    @staticmethod
    def _link(node, socket_name):
        return types.SimpleNamespace(to_node=node,
                                     to_socket=types.SimpleNamespace(name=socket_name))

    @classmethod
    def _image_node(cls, name, *links):
        return types.SimpleNamespace(
            name=name, type="TEX_IMAGE",
            image=types.SimpleNamespace(name=name + " picture"),
            outputs=[types.SimpleNamespace(links=list(links))])

    @staticmethod
    def _mesh(*nodes):
        material = types.SimpleNamespace(name="handmade",
                                         node_tree=types.SimpleNamespace(nodes=list(nodes)))
        return types.SimpleNamespace(name="body",
                                     material_slots=[types.SimpleNamespace(material=material)])

    def _principled(self):
        return types.SimpleNamespace(name="Principled BSDF", type="BSDF_PRINCIPLED", outputs=[])

    def test_a_picture_reaches_its_channel_through_intermediate_nodes(self):
        principled = self._principled()
        # image -> mix -> mix2 -> Base Color: three hops, each with its own link objects.
        mix2 = types.SimpleNamespace(name="mix2", type="MIX",
                                     outputs=[types.SimpleNamespace(
                                         links=[self._link(principled, "Base Color")])])
        mix = types.SimpleNamespace(name="mix", type="MIX",
                                    outputs=[types.SimpleNamespace(links=[self._link(mix2, "A")])])
        node = self._image_node("painted", self._link(mix, "A"))
        with mock.patch.object(rb, "_gf2_image_png", return_value=b"png"):
            rows, warnings = rb._gf2_collect_standard_channels(
                [self._mesh(node, mix, mix2, principled)])
        self.assertEqual([(row["material"], row["channels"]) for row in rows],
                         [("handmade", ["baseColor"])])
        self.assertEqual(warnings, [])

    def test_an_unreadable_picture_is_skipped_and_named(self):
        principled = self._principled()
        node = self._image_node("broken", self._link(principled, "Base Color"))
        with mock.patch.object(rb, "_gf2_image_png", side_effect=RuntimeError("no file")):
            rows, warnings = rb._gf2_collect_standard_channels([self._mesh(node, principled)])
        self.assertEqual(rows, [])
        self.assertEqual(warnings,
                         ["'broken picture' on 'handmade' could not be read and was not sent."])

    def test_two_pictures_on_one_channel_send_the_first_and_name_the_second(self):
        principled = self._principled()
        first = self._image_node("first", self._link(principled, "Base Color"))
        second = self._image_node("second", self._link(principled, "Base Color"))
        with mock.patch.object(rb, "_gf2_image_png", return_value=b"png"):
            rows, warnings = rb._gf2_collect_standard_channels(
                [self._mesh(first, second, principled)])
        self.assertEqual([row["image_name"] for row in rows], ["first picture"])
        self.assertEqual(warnings, ["Two pictures reach the baseColor channel on 'handmade'; "
                                    "'first picture' was sent."])

    def test_a_tagged_node_is_not_collected_as_a_standard_channel(self):
        principled = self._principled()
        tagged = HashOnlyCollectionTests._tagged_node(
            "_BaseMap", types.SimpleNamespace(name="stock"))
        tagged.type = "TEX_IMAGE"
        tagged.outputs = [types.SimpleNamespace(links=[self._link(principled, "Base Color")])]
        rows, warnings = rb._gf2_collect_standard_channels([self._mesh(tagged, principled)])
        self.assertEqual(rows, [])
        self.assertEqual(warnings, [])

    def test_the_append_drops_a_picture_whose_material_the_export_did_not_write(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "return.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"},
                                     "materials": [{"name": "written"}],
                                     "buffers": [{"byteLength": 0}]}, b"")
            rb._gf2_append_texture_transport(path, [], [
                {"material": "unused-slot", "channels": ["baseColor"],
                 "png": b"png", "image_name": "orphan"},
                {"material": "written", "channels": ["normal"],
                 "png": b"png", "image_name": "kept"}])
            root, _ = rb._gf2_glb_read(path)
        self.assertEqual(len(root.get("images", [])), 1)
        self.assertEqual(root["materials"][0]["normalTexture"], {"index": 0})

    def test_an_all_hash_only_append_leaves_no_empty_top_level_array(self):
        # glTF forbids empty top-level arrays and the app's reader (SharpGLTF) refuses the whole file
        # over one — the moved-parts-only send, where every picture is untouched, must stay openable.
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "moved-only.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"}, "buffers": [{"byteLength": 0}]}, b"")
            rb._gf2_append_texture_transport(path, [{
                "owner": {"mesh": "veil", "material": 0, "primitive": 0},
                "property": "_BaseMap",
                "semantic": "baseColor",
                "stock": {"name": "base", "bundle": "bundle", "path_id": 71},
                "outbound_hash": "a" * 64,
            }])
            root, _ = rb._gf2_glb_read(path)
            empties = [key for key, value in root.items() if isinstance(value, list) and not value]
            self.assertEqual(empties, [])
            self.assertNotIn("images", root)
            self.assertEqual(len(rb._gf2_read_texture_transport(path)), 1)

    def test_the_no_append_sanitizer_drops_an_exporter_left_empty_array(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "hide.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"}, "images": [],
                                     "buffers": [{"byteLength": 0}]}, b"")
            rb._gf2_strip_empty_gltf_arrays(path)
            root, _ = rb._gf2_glb_read(path)
            self.assertNotIn("images", root)

    def test_hash_only_rows_append_no_image_and_changed_duplicates_share_one_image(self):
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "transport.glb")
            rb._gf2_glb_write(path, {"asset": {"version": "2.0"}, "buffers": [{"byteLength": 0}]}, b"")
            shared = b"one changed picture"
            rows = [{
                "owner": {"mesh": "veil", "material": 0, "primitive": index},
                "property": "_BaseMap",
                "semantic": "baseColor",
                "stock": {"name": "base", "bundle": "bundle", "path_id": 71},
                "outbound_hash": "a" * 64,
            } for index in range(3)]
            rows[1]["png"] = shared
            rows[2]["png"] = shared

            rb._gf2_append_texture_transport(path, rows)
            root, _ = rb._gf2_glb_read(path)
            returned = rb._gf2_read_texture_transport(path)

            self.assertEqual(len(root.get("images", [])), 1)
            self.assertNotIn("image", returned[0])
            self.assertNotIn("png", returned[0])
            self.assertEqual(returned[0]["outbound_hash"], "a" * 64)
            self.assertEqual(returned[1]["image"], returned[2]["image"])
            self.assertEqual(returned[1]["outbound_hash"], returned[2]["outbound_hash"])


if __name__ == "__main__":
    unittest.main()

