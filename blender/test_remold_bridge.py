"""Unit cover for the bridge's pure state-reporting helpers.

    python -m unittest discover -s blender

Blender is not needed: every helper here takes plain values, so the module's `import bpy` is satisfied
with a stub. The parts of the bridge that read a scene are exercised by its sibling suite,
`bpy_test_remold_bridge.py`, which runs under Blender itself — that file is deliberately named
outside the `test*.py` pattern so this discovery run never tries to import it against a stub bpy.
"""
import os
import sys
import types
import unittest
from unittest import mock

sys.modules.setdefault("bpy", types.ModuleType("bpy"))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import remold_bridge as rb   # noqa: E402  (the bpy stub has to be in place first)


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
        self.assertEqual(rb.gf2_status_line([]), "✓ Ready to send")

    def test_both_severities_tally_separately(self):
        issues = [("HARD", "a"), ("HARD", "b"), ("SOFT", "c")]
        self.assertEqual(rb.gf2_status_line(issues), "✗ 2 blocking · ⚠ 1 warning")

    def test_blocking_only_omits_the_warning_half(self):
        self.assertEqual(rb.gf2_status_line([("HARD", "a")]), "✗ 1 blocking")

    def test_warnings_only_omit_the_blocking_half(self):
        self.assertEqual(rb.gf2_status_line([("SOFT", "a"), ("SOFT", "b")]), "⚠ 2 warnings")


class DroppedExportOptionTests(unittest.TestCase):
    """The export settings are filtered against the running Blender's RNA, so an older or newer Blender can
    send without one. This line is what carries that fact to the modder."""

    def test_every_dropped_option_is_named(self):
        self.assertEqual(rb.gf2_dropped_options_line(["export_tangents", "export_apply"]),
                         "Export options this Blender doesn't support: export_apply, export_tangents.")

    def test_one_dropped_option_reads_the_same_way(self):
        self.assertEqual(rb.gf2_dropped_options_line(["export_apply"]),
                         "Export options this Blender doesn't support: export_apply.")


class SubjectLabelTests(unittest.TestCase):
    def test_workspace_layout_yields_the_subject_folder(self):
        self.assertEqual(rb.gf2_subject_label(r"C:\mods\karst_SSR0101\meshes\_combined.glb"),
                         "karst_SSR0101")

    def test_forward_slashes_resolve_the_same(self):
        self.assertEqual(rb.gf2_subject_label("/mods/karst_SSR0101/meshes/body1.glb"),
                         "karst_SSR0101")

    def test_other_layouts_fall_back_to_the_file_stem(self):
        self.assertEqual(rb.gf2_subject_label("/somewhere/else/donor.glb"), "donor")

    def test_no_path_yields_nothing(self):
        self.assertEqual(rb.gf2_subject_label(""), "")


class ShortPartNameTests(unittest.TestCase):
    def test_shared_prefix_is_cut_at_an_underscore(self):
        names = ["c_KarstSSR0101_slg_body1_lod0", "c_KarstSSR0101_slg_cloth1_lod0"]
        self.assertEqual(rb.gf2_short_part_names(names), ["body1_lod0", "cloth1_lod0"])

    def test_a_lone_name_stays_whole(self):
        self.assertEqual(rb.gf2_short_part_names(["c_KarstSSR0101_slg_body1_lod0"]),
                         ["c_KarstSSR0101_slg_body1_lod0"])

    def test_identical_names_keep_their_tail(self):
        self.assertEqual(rb.gf2_short_part_names(["a_b_c", "a_b_c"]), ["c", "c"])

    def test_no_shared_prefix_leaves_names_untouched(self):
        self.assertEqual(rb.gf2_short_part_names(["body", "cloth"]), ["body", "cloth"])


class SubjectLineTests(unittest.TestCase):
    def test_both_halves_join_on_the_separator(self):
        self.assertEqual(rb.gf2_subject_line("karst_SSR0101", ["body", "cloth", "hair"]),
                         "karst_SSR0101 · body, cloth, hair")

    def test_parts_alone_when_the_subject_is_not_derivable(self):
        self.assertEqual(rb.gf2_subject_line("", ["body"]), "body")

    def test_subject_alone_when_there_are_no_parts(self):
        self.assertEqual(rb.gf2_subject_line("karst_SSR0101", []), "karst_SSR0101")


class CollectionsLineTests(unittest.TestCase):
    def test_attributed_scene_breaks_down_by_collection(self):
        self.assertEqual(rb.gf2_collections_line(3, 1, True), "Mod 3 · Reference 1")

    def test_unattributed_scene_reports_a_bare_count(self):
        self.assertEqual(rb.gf2_collections_line(3, 0, False), "3 objects")

    def test_bare_count_is_singular_for_one(self):
        self.assertEqual(rb.gf2_collections_line(1, 0, False), "1 object")


class ScopeLineTests(unittest.TestCase):
    def test_scope_reads_objects_and_verts_only(self):
        """Reference never ships, so what is NOT sent is not part of the scope: the line counts only
        what the send carries."""
        self.assertEqual(rb.gf2_scope_lines(3, 12480, False), ["3 objects · 12,480 verts"])

    def test_singulars(self):
        self.assertEqual(rb.gf2_scope_lines(1, 1, False), ["1 object · 1 vert"])

    def test_modifier_note_only_when_one_would_be_baked(self):
        self.assertEqual(rb.gf2_scope_lines(1, 8, True),
                         ["1 object · 8 verts", "Modifiers are baked on Send."])


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


class OverwriteWarningTests(unittest.TestCase):
    def test_nothing_edited_says_nothing(self):
        self.assertIsNone(rb.gf2_overwrite_warning([]))

    def test_one_edited_part_reads_singular(self):
        self.assertEqual(rb.gf2_overwrite_warning(["c_KarstSSR0101_slg_cloth2_lod0"]),
                         "cloth2 already carries an edit. Sending replaces it.")

    def test_several_edited_parts_read_plural(self):
        self.assertEqual(rb.gf2_overwrite_warning(["c_KarstSSR0101_slg_body1_lod0",
                                                   "c_KarstSSR0101_slg_cloth2_lod0"]),
                         "body1, cloth2 already carry edits. Sending replaces them.")


class EditedShippingPartTests(unittest.TestCase):
    SESSION = {"parts": [{"name": "body1", "edited": True},
                         {"name": "cloth1", "edited": False},
                         {"name": "hair", "edited": True}]}

    def test_only_the_edited_parts_that_actually_ship_are_named(self):
        self.assertEqual(rb.gf2_edited_shipping_parts(self.SESSION, ["body1", "cloth1"]), ["body1"])

    def test_an_emptied_part_has_nothing_to_lose(self):
        """Its workspace file is never written, so an edit it holds is not at risk."""
        self.assertEqual(rb.gf2_edited_shipping_parts(self.SESSION, ["cloth1"]), [])

    def test_no_session_description_warns_about_nothing(self):
        self.assertEqual(rb.gf2_edited_shipping_parts({}, ["body1", "hair"]), [])

    def test_a_part_this_session_already_sent_counts_as_edited(self):
        """The app's description is a launch-time snapshot. Without this, a second Send in one session
        says nothing about the part the first one just wrote over."""
        self.assertEqual(rb.gf2_edited_shipping_parts(self.SESSION, ["cloth1"], sent=["cloth1"]),
                         ["cloth1"])

    def test_the_sent_list_adds_to_the_apps_view_rather_than_replacing_it(self):
        self.assertEqual(rb.gf2_edited_shipping_parts(self.SESSION, ["body1", "cloth1"], sent=["cloth1"]),
                         ["body1", "cloth1"])


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
        self.assertIn("4 vertex(es)", got[1][1])

    def test_the_blocker_names_the_objects_it_found(self):
        """The blocker names every object it found, so the modder knows where to look."""
        got = _full_pass([], unsolvable=[("body1", 4), ("cloth1", 2)])
        self.assertEqual(len(got), 1)
        self.assertIn("6 vertex(es)", got[0][1])
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


if __name__ == "__main__":
    unittest.main()

