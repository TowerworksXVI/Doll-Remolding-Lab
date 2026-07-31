"""Blender-headless fixtures for the bridge's collection attribution, export scope and panel.

Run them with Blender itself (nothing here works under plain CPython — it is all bpy):

    blender --background --factory-startup --python blender/bpy_test_remold_bridge.py

The filename deliberately sits outside the `test*.py` pattern: the bridge's other suite is a pure
CPython one found by `python -m unittest discover -s blender`, and that discovery must not try to
import this file against a stub bpy.

Exit code 0 = all green, 1 = a failure (each one printed with its assertion). The suite builds
synthetic scenes only: three-vertex meshes and a one-bone skeleton, never game data.

Two things are asserted for every case, because they can disagree: the (severity, message) pairs
`gf2_run_checks` returns, and the object set that actually lands in a written .glb. A check that
reports a clean scene while the export drops a part is the failure this suite exists to catch.
"""
import contextlib
import io
import json
import os
import struct
import subprocess
import sys
import tempfile

import bpy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import remold_bridge as rb   # noqa: E402

BRIDGE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "remold_bridge.py")

PART_A = "c_VesnaSSR0101_slg_P1_body1_lod0"
PART_B = "c_VesnaSSR0101_slg_cloth1_lod0"


# ---------------------------------------------------------------- scene + glb helpers

def _reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def _mesh(name, coll=None):
    """A three-vertex mesh object linked into `coll` (the scene root when omitted)."""
    me = bpy.data.meshes.new(name)
    me.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
    me.update()
    ob = bpy.data.objects.new(name, me)
    (coll or bpy.context.scene.collection).objects.link(ob)
    return ob


def _armature(name="Armature", coll=None, bone="root"):
    """A one-bone skeleton object. `bone` names the bone, so a donor rig's bones are distinguishable
    from the session armature's in what a fill writes and what a glb ships."""
    arm = bpy.data.armatures.new(name)
    ob = bpy.data.objects.new(name, arm)
    (coll or bpy.context.scene.collection).objects.link(ob)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    b = arm.edit_bones.new(bone)
    b.head, b.tail = (0, 0, 0), (0, 0, 1)
    bpy.ops.object.mode_set(mode="OBJECT")
    return ob


def _weight_all(obj, group="root"):
    """Give every vertex full weight in one group, so the unsolvable-weight check has nothing to do."""
    vg = obj.vertex_groups.new(name=group)
    vg.add([v.index for v in obj.data.vertices], 1.0, "REPLACE")
    return obj


def _bind(obj, arm):
    """Parent + Armature modifier: what makes the glTF exporter write a skin at all. An imported part
    arrives bound this way, so a fixture that sends with an armature in the scene has to match — or the
    send's skinless-glb tripwire fires on the fixture instead of on a regression."""
    obj.parent = arm
    obj.modifiers.new(name="Armature", type="ARMATURE").object = arm
    return obj


def _part_mesh(name, coll):
    """A fully weighted mesh in a part collection: what the weight checks consider unremarkable, so
    an attribution fixture reports attribution problems and nothing else."""
    return _weight_all(_mesh(name, coll))


def _collection(name, parent, part=False):
    coll = bpy.data.collections.new(name)
    parent.children.link(coll)
    if part:
        coll[rb.PART_MARKER] = name     # what gf2_build_collections stamps on a real part
    return coll


def _layout(part_names=(PART_A,), armature=False, reference=True):
    """Build the import layout by hand: Mod/<part> per name, Reference, optionally Mod/Armature.
    Returns (mod, reference or None, {part name: collection}, armature or None)."""
    root = bpy.context.scene.collection
    mod = _collection(rb.MOD_COLLECTION, root)
    ref = _collection(rb.REFERENCE_COLLECTION, root) if reference else None
    parts = {n: _collection(n, mod, part=True) for n in part_names}
    arm = _armature(coll=_collection(rb.ARMATURE_COLLECTION, mod)) if armature else None
    return mod, ref, parts, arm


def _scene_meshes():
    return [o for o in bpy.data.objects if o.type == "MESH"]


def _check():
    """The operator's own pairing: every mesh in the scene, and the SESSION armature."""
    return rb.gf2_run_checks(_scene_meshes(), rb._session_armature())


def _severities(issues, sev):
    return [m for s, m in issues if s == sev]


def _glb_json(path):
    """The JSON chunk of a .glb, parsed. Plain container walk — no importer, so what is asserted is
    what the file says rather than what a second Blender read makes of it."""
    with open(path, "rb") as f:
        data = f.read()
    magic, _version, length = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67, f"not a glb: magic {magic:#x}"
    off = 12
    while off + 8 <= length:
        clen, ctype = struct.unpack_from("<II", data, off)
        off += 8
        if ctype == 0x4E4F534A:                      # 'JSON'
            return json.loads(data[off:off + clen].decode("utf-8"))
        off += clen
    raise AssertionError("the glb carries no JSON chunk")


def _sent_mesh_names(out_dir, stem):
    """The mesh names in a sent .glb: the export scope as the file records it, under the names the app
    re-splits the glb by. glTF takes a mesh's name from the Blender mesh DATA block (the node's from
    the object), which is the name the send rewrites to the part collection's."""
    doc = _glb_json(os.path.join(out_dir, stem + ".glb"))
    return sorted(m.get("name", "") for m in doc.get("meshes", []))


def _sent_node_names(out_dir, stem):
    """The node names in a sent .glb: the Blender OBJECT names, untouched by the send's renaming."""
    doc = _glb_json(os.path.join(out_dir, stem + ".glb"))
    return sorted(n.get("name", "") for n in doc.get("nodes", []))


def _sent_doc(out_dir, stem):
    return _glb_json(os.path.join(out_dir, stem + ".glb"))


def _send(stem="part"):
    """Run a real gf2_send into a temp dir; returns (dir, mesh names in the written glb)."""
    out = tempfile.mkdtemp(prefix="gf2send_")
    rb.gf2_send(out, stem + ".glb")
    return out, _sent_mesh_names(out, stem)


def _write_session(glb_path, part=None, parts=()):
    """The app's session description beside a glb: which mesh this session may write, and which parts it
    already holds an edit for."""
    with open(rb.session_path(glb_path), "w", encoding="utf-8") as f:
        json.dump({"part": part, "parts": [{"name": n, "edited": e} for n, e in parts]}, f)
    return glb_path


def _sent_sidecar(out_dir, stem):
    with open(os.path.join(out_dir, stem + ".gf2send.json"), "r", encoding="utf-8") as f:
        return json.load(f)


def _reference_names():
    return sorted(o.name for o in rb._reference_root().all_objects if o.type == "MESH")


def _build_source_glb(part_names, path):
    """Export a synthetic multi-part rigged .glb to feed gf2_import, the way the app would."""
    _reset()
    arm = _armature()
    for n in part_names:
        ob = _mesh(n)
        _weight_all(ob)
        ob.parent = arm
        ob.modifiers.new(name="Armature", type="ARMATURE").object = arm
    for o in bpy.data.objects:
        o.select_set(True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", export_skins=True,
                              use_selection=True)
    return path


# ---------------------------------------------------------------- import lays out the collections

def test_import_builds_the_collection_layout():
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _reset()
        meshes, arms = rb.gf2_import(src)

    root = bpy.context.scene.collection
    assert root.children.get(rb.MOD_COLLECTION) is not None, "import made no Mod collection"
    assert root.children.get(rb.REFERENCE_COLLECTION) is not None, "import made no Reference collection"
    parts = {c.name for c in rb.gf2_part_collections()}
    assert parts == {PART_A, PART_B}, f"part collections are {parts}"
    for mo in meshes:
        part = rb._part_of(mo)
        assert part is not None and part.name == mo.name, f"{mo.name} landed in {part}"
        assert len(mo.users_collection) == 1, f"{mo.name} is linked into {len(mo.users_collection)} collections"
    assert arms, "the fixture glb carried no armature"
    arm_coll = root.children[rb.MOD_COLLECTION].children.get(rb.ARMATURE_COLLECTION)
    assert arm_coll is not None and arm_coll.objects.get(arms[0].name) is arms[0], "armature is not in Mod/Armature"
    assert rb._in_tree(arms[0], rb._mod_root()), "armature is outside the Mod tree"
    assert _check() == [], f"a fresh import is not clean: {_check()}"


def test_import_of_several_parts_activates_mod():
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _reset()
        rb.gf2_import(src)
    active = bpy.context.view_layer.active_layer_collection.collection.name
    assert active == rb.MOD_COLLECTION, f"active collection is {active}, want Mod for a multi-part session"


def test_import_of_one_part_activates_that_part():
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "one.glb"))
        _reset()
        rb.gf2_import(src)
    active = bpy.context.view_layer.active_layer_collection.collection.name
    assert active == PART_A, f"active collection is {active}, want the single part"


def test_non_game_bones_lose_their_deform_flag():
    """Hierarchy glue (no _<hash8> in the name: connectors, wrapper roots) must not receive
    Automatic Weights — a weight painted onto one can never ship, and the send-back refuses the
    whole part when it exists. Non-deform bones are exactly what bone-heat skips."""
    _reset()
    arm = _armature(bone="spine_aabbccdd")
    bpy.ops.object.mode_set(mode="EDIT")
    eb = arm.data.edit_bones.new("helper")
    eb.head = (0, 0, 1)
    eb.tail = (0, 0, 1.2)
    bpy.ops.object.mode_set(mode="OBJECT")

    rb._demote_non_game_bones(arm)

    assert arm.data.bones["spine_aabbccdd"].use_deform, "a game bone was demoted"
    assert not arm.data.bones["helper"].use_deform, "hierarchy glue kept its deform flag"


def test_a_rig_with_no_game_bones_keeps_deforming():
    """A hand-built rig (nothing hash-named) is not ours to judge: demoting everything would break
    Automatic Weights outright."""
    _reset()
    arm = _armature(bone="root")
    rb._demote_non_game_bones(arm)
    assert arm.data.bones["root"].use_deform


def test_a_per_part_session_hides_its_reference_context():
    """Context parts import into Reference HIDDEN and unselected: the modder asked for one part, and
    the rest of the outfit drawn over it is the wrong default. They stay one outliner click away, and
    the initial selection (what the viewport frames) is the session's own part."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _write_session(src, part=PART_A, parts=[(PART_A, False), (PART_B, False)])
        _reset()
        meshes, _arms = rb.gf2_import(src)

        by = {m.name: m for m in meshes}
        assert by[PART_B].hide_get(), "the Reference context part imported visible"
        assert not by[PART_A].hide_get(), "the session's own part imported hidden"
        assert by[PART_A].select_get(), "the session part must be selected, it is what framing centres on"
        assert not by[PART_B].select_get(), "a Reference part imported selected"


def test_import_leaves_a_pre_existing_mesh_alone():
    """A prop from the user's startup .blend is not part of the session. It keeps its place, the
    attribution check reports it, and it stays out of the deliverable — the alternative is a startup
    prop silently shipping as a part."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "one.glb"))
        _reset()
        startup_prop = _mesh("StartupProp")
        rb.gf2_import(src)

    assert rb._part_of(startup_prop) is None, "the startup prop was handed a part collection"
    parts = {c.name for c in rb.gf2_part_collections()}
    assert parts == {PART_A}, f"part collections are {parts}, want only the imported part"
    hard = _severities(_check(), "HARD")
    assert any("StartupProp" in m for m in hard), f"the startup prop was not reported: {hard}"
    _out, sent = _send()
    assert "StartupProp" not in sent, f"the startup prop shipped: {sent}"


def test_import_leaves_a_pre_existing_armature_alone():
    """A rig already in the user's startup .blend is not this session's skeleton. Promoting it into
    Mod/Armature would make it the bone-heat source and the bone-rename baseline, and would ship it."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "one.glb"))
        _reset()
        startup_rig = _armature("AAA_startup_rig", bone="startup_root")
        meshes, arms = rb.gf2_import(src)

    assert startup_rig not in arms, "the import claimed the startup rig as its own"
    assert not rb._in_tree(startup_rig, rb._mod_root()), "the startup rig was promoted into Mod"
    session = rb._session_armature()
    assert session is not None and session is not startup_rig, \
        f"the session armature is {session and session.name}, want the imported one"
    hard = _severities(_check(), "HARD")
    assert not any("renamed or removed" in m for m in hard), f"the startup rig was consulted: {hard}"
    out, _sent = _send()
    assert startup_rig.name not in _sent_node_names(out, "part"), "the startup rig shipped"
    assert "startup_root" not in _sent_node_names(out, "part"), "the startup rig's bones shipped"


# ---------------------------------------------------------------- the session decides what Mod holds

def test_a_named_session_part_puts_the_rest_of_the_outfit_in_reference():
    """Every open carries the whole outfit on one armature so a weight can be painted against the whole
    skeleton. Only the named part may come back, and the collection layout is what enforces that."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "_combined.glb"))
        _write_session(src, part=PART_B, parts=[(PART_A, False), (PART_B, False)])
        _reset()
        _meshes, arms = rb.gf2_import(src)

    assert [c.name for c in rb.gf2_part_collections()] == [PART_B], \
        f"part collections are {[c.name for c in rb.gf2_part_collections()]}, want only the session part"
    assert _reference_names() == [PART_A], f"the context part is in {_reference_names()}"
    assert rb._in_tree(arms[0], rb._mod_root()), "the union armature left the Mod tree"
    assert _check() == [], f"a single-part session is not clean: {_check()}"


def test_a_named_session_part_ships_alone():
    """The end-to-end half of the same contract: a context part must not reach the deliverable."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "_combined.glb"))
        _write_session(src, part=PART_B, parts=[(PART_A, False), (PART_B, False)])
        _reset()
        rb.gf2_import(src)
    _out, sent = _send("_combined")
    assert sent == [PART_B], f"the sent glb carries {sent}, want the session part alone"


def test_no_session_description_leaves_every_mesh_writable():
    """A hand-opened glb and the headless round trip have no session file; both want every part."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _reset()
        rb.gf2_import(src)
    assert {c.name for c in rb.gf2_part_collections()} == {PART_A, PART_B}, \
        f"part collections are {[c.name for c in rb.gf2_part_collections()]}"
    assert _reference_names() == [], f"a part was held back: {_reference_names()}"


def test_the_session_armature_is_the_whole_skeletons_and_stays_exportable():
    """One armature, always. A weight painted onto a bone that belongs to another part has to reach the
    send, so the rig is neither filtered to the session part nor duplicated."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "_combined.glb"))
        _write_session(src, part=PART_A, parts=[(PART_A, False), (PART_B, False)])
        _reset()
        rb.gf2_import(src)
    out, sent = _send("_combined")
    assert sent == [PART_A], f"the sent glb carries {sent}"
    doc = _sent_doc(out, "_combined")
    assert len(doc.get("skins", [])) == 1, f"the send wrote {len(doc.get('skins', []))} skins, want the one union rig"


# ---------------------------------------------------------------- an emptied part = Hide

def test_an_emptied_part_is_named_in_the_send_sidecar():
    """Absence is never the signal — under the session layout most of the outfit is absent by design — so
    the emptied part is named explicitly, and the part that still has a mesh ships as normal."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])              # PART_B's collection is left empty
    out, sent = _send()
    assert sent == [PART_A], f"the sent glb carries {sent}"
    assert _sent_sidecar(out, "part")["hiddenParts"] == [PART_B], \
        f"the sidecar says {_sent_sidecar(out, 'part')}"


def test_nothing_emptied_hides_nothing():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B])
    out, _sent = _send()
    assert _sent_sidecar(out, "part")["hiddenParts"] == [], \
        f"the sidecar says {_sent_sidecar(out, 'part')}"


def test_an_emptied_part_warns_and_does_not_block():
    """Deliberate, not accidental: it shows on the live status line before anything exports, and on the
    Send dialog (which draws the same warnings). It must not be a block."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    issues = _check()
    assert _severities(issues, "HARD") == [], f"an emptied part must not block: {issues}"
    soft = _severities(issues, "SOFT")
    assert len(soft) == 1 and rb.gf2_label(PART_B) in soft[0], f"want one emptied-part warning, got {soft}"
    assert "hides it in the mod" in soft[0], f"the warning must say what a send does: {soft[0]}"
    assert rb._refresh_live_status() == "⚠ 1 warning", rb._refresh_live_status()


def test_every_part_emptied_blocks_and_refuses():
    """One emptied part is a Hide. All of them is no deliverable at all, and there is nothing left to
    hide against either."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_A, PART_B], armature=True)
    hard = _severities(_check(), "HARD")
    assert any("Every part is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "part.glb")
        assert False, "an all-empty scene must refuse to send"
    except RuntimeError as e:
        assert "Every part is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


def test_an_open_all_session_emptied_to_nothing_still_blocks():
    """The same block, stated against the session that makes it meaningful: an open-all can write every
    part, so emptying them all really does leave an empty deliverable."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_A, PART_B], armature=True)
    rb._store_session({"part": None, "parts": [{"name": PART_A, "edited": False},
                                               {"name": PART_B, "edited": False}]})
    hard = _severities(_check(), "HARD")
    assert any("Every part is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "_combined.glb")
        assert False, "an all-empty open-all session must refuse to send"
    except RuntimeError as e:
        assert "Every part is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


def test_a_per_part_session_may_empty_its_one_part_to_hide_it():
    """A session that names one part holds exactly one part collection, so emptying it is the only way
    to say "hide this part". It warns, it sends, and the sidecar names the part — the rest of the mod
    still ships from its own files, so the deliverable is not empty."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_B], armature=True)
    _mesh(PART_A, rb._reference_root())          # the rest of the outfit rode along as context
    rb._store_session({"part": PART_B, "parts": [{"name": PART_A, "edited": False},
                                                 {"name": PART_B, "edited": False}]})

    issues = _check()
    assert _severities(issues, "HARD") == [], f"a deliberate Hide must not block: {issues}"
    soft = _severities(issues, "SOFT")
    assert any("hides it in the mod" in m for m in soft), f"want the Hide warning, got {soft}"

    out, sent = _send("_combined")
    assert sent == [], f"a Hide carries no mesh, but the glb holds {sent}"
    assert _sent_sidecar(out, "_combined")["hiddenParts"] == [PART_B], \
        f"the sidecar says {_sent_sidecar(out, '_combined')}"
    # a Hide writes the session glb and its sidecar, never the part's own workspace file
    assert sorted(os.listdir(out)) == ["_combined.gf2send.json", "_combined.glb"], \
        f"the send wrote {sorted(os.listdir(out))}"


def test_a_per_part_session_that_is_the_whole_mod_still_blocks_on_empty():
    """Hiding the only part the mod has leaves nothing to build, which is what the block is for. That
    session also sends over the part's own workspace file, so passing here would erase it."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_A], armature=True)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": False}]})
    hard = _severities(_check(), "HARD")
    assert any("Every part is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "part.glb")
        assert False, "hiding the mod's only part must refuse to send"
    except RuntimeError as e:
        assert "Every part is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


# ---------------------------------------------------------------- what a send would replace

def test_the_send_dialog_warns_when_the_target_already_carries_an_edit():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": True}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    op = _FakeOp()
    assert _send_operator()._gate(op, bpy.context) is None, f"a clean scene was blocked: {op.reports}"
    op._scope = rb._send_scope_lines()
    op._overwrite = rb._send_overwrite_warning()
    _send_operator().draw(op, bpy.context)

    lines = op.layout.lines
    assert lines[0].startswith("⚠ ") and "already carries an edit" in lines[0], f"the dialog drew {lines}"


def test_the_send_dialog_says_nothing_about_a_first_send():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": False}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    assert rb._send_overwrite_warning() is None, rb._send_overwrite_warning()


def test_a_second_send_in_one_session_warns_about_what_the_first_wrote():
    """The app's description is a snapshot taken when Blender opened, so on its own it would keep saying
    "nothing to replace" however many times the modder sends. After this session's own Send, that part
    holds an edit, and the next dialog has to say so."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": False}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    assert rb._send_overwrite_warning() is None, "a first send has nothing to replace"

    _send()

    op = _FakeOp()
    op._scope = rb._send_scope_lines()
    op._overwrite = rb._send_overwrite_warning()
    _send_operator().draw(op, bpy.context)
    lines = op.layout.lines
    assert lines[0].startswith("⚠ ") and "already carries an edit" in lines[0], \
        f"the second dialog drew {lines}"
    assert rb.gf2_label(PART_A) in lines[0], f"the warning must name the part: {lines[0]}"


def test_a_send_only_marks_the_parts_it_actually_wrote():
    """The re-warn is scoped to what shipped: a part the send held back has nothing to replace, so it
    must not start reading as edited afterwards."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)     # PART_B's collection is emptied — a Hide
    rb._store_session({"part": None, "parts": [{"name": PART_A, "edited": False},
                                               {"name": PART_B, "edited": False}]})
    _send()
    assert rb.gf2_sent_parts() == [PART_A], f"the send recorded {rb.gf2_sent_parts()}"


def test_no_session_description_warns_about_no_overwrite():
    """A hand-opened glb has nothing to say about the app's state, so the dialog stays quiet."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    assert rb._send_overwrite_warning() is None, rb._send_overwrite_warning()


class _FakeWm:
    """Records what invoke asked the window manager for, instead of opening a modal dialog a
    background run cannot show."""

    def __init__(self):
        self.dialogs = []

    def invoke_props_dialog(self, op, width=0):
        self.dialogs.append(op)
        return {'RUNNING_MODAL'}


class _CtxWithWm:
    """bpy.context with the window manager swapped for the recording fake: invoke decides between
    "dialog first" and "just send", and the decision is only observable through what it calls."""

    def __init__(self, wm):
        self.window_manager = wm

    def __getattr__(self, name):
        return getattr(bpy.context, name)


def _wired_op(cls):
    """A _FakeOp whose gate/execute route back through the operator class, so cls.invoke(op, …) runs
    the real decision logic against the fake instance."""
    op = _FakeOp()
    op._gate = lambda ctx: cls._gate(op, ctx)
    op.execute = lambda ctx: cls.execute(op, ctx)
    return op


def test_a_first_send_skips_the_dialog_and_confirms():
    """Nothing is replaced, so there is nothing to confirm BEFORE the send: it just goes, and the
    popup after the export is the feedback (the status-bar report alone is easy to miss)."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": False}]})
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)

    cls = _send_operator()
    op = _wired_op(cls)
    wm = _FakeWm()
    with _captured_popups() as seen:
        result = cls.invoke(op, _CtxWithWm(wm), None)

    assert result == {'FINISHED'}, f"invoke returned {result} ({op.reports})"
    assert wm.dialogs == [], "a first send opened the confirm dialog"
    # the synthetic mesh also earns the tangent warning; the contract here is that the confirmation
    # popup CLOSES the send, whatever advisories preceded it
    assert [t for t, _ in seen][-1:] == ["Sent"], f"the export must confirm itself, popped: {seen}"
    assert os.path.exists(os.path.join(out, "part.glb")), "nothing was exported"


def test_an_overwriting_send_asks_before_writing_anything():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "edited": True}]})
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)

    cls = _send_operator()
    op = _wired_op(cls)
    wm = _FakeWm()
    with _captured_popups() as seen:
        cls.invoke(op, _CtxWithWm(wm), None)

    assert wm.dialogs == [op], "an overwriting send must confirm first"
    assert seen == [], f"nothing should pop before the dialog decides: {seen}"
    assert not os.path.exists(os.path.join(out, "part.glb")), "the send ran ahead of the confirm"


# ---------------------------------------------------------------- attribution checks

def test_object_in_a_part_collection_is_clean():
    _reset()
    _mod, _ref, parts, _arm = _layout(armature=True)
    _part_mesh(PART_A, parts[PART_A])
    assert _check() == [], f"a correctly attributed part reported {_check()}"


def test_mesh_directly_in_mod_is_hard_and_names_the_parts():
    _reset()
    mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh(PART_B, parts[PART_B])
    _part_mesh("new_piece", mod)
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1, f"want one HARD, got {hard}"
    assert "new_piece" in hard[0] and PART_A in hard[0] and PART_B in hard[0], \
        f"the message must name the parts it could move to: {hard[0]}"


def test_mesh_in_the_armature_collection_is_hard():
    _reset()
    mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh("stray", mod.children[rb.ARMATURE_COLLECTION])
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1 and "stray" in hard[0], f"want one HARD naming stray, got {hard}"


def test_object_in_reference_raises_nothing():
    _reset()
    _mod, ref, parts, _arm = _layout(armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _mesh("donor_body", ref)
    assert _check() == [], f"a Reference object reported {_check()}"


def test_scene_root_stray_is_hard():
    _reset()
    _mod, _ref, parts, _arm = _layout(armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _mesh("dropped_here")                     # scene root: in neither tree
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1, f"want one HARD, got {hard}"
    assert "dropped_here" in hard[0], hard[0]
    assert "will not ship" in hard[0], f"the message must say it will not ship: {hard[0]}"
    assert rb.REFERENCE_COLLECTION in hard[0], f"the message must offer Reference: {hard[0]}"


def test_name_disagreeing_with_the_collection_is_soft():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    _part_mesh(PART_A, parts[PART_B])         # named for one part, sitting in the other
    _part_mesh("Cube", parts[PART_A])         # a name that claims nothing, so only the mismatch warns
    issues = _check()
    assert _severities(issues, "HARD") == [], f"a name mismatch must not block: {issues}"
    soft = _severities(issues, "SOFT")
    a, b = rb.gf2_label(PART_A), rb.gf2_label(PART_B)
    assert len(soft) == 1 and a in soft[0] and b in soft[0], f"want one SOFT, got {soft}"
    assert f"ships as {b}" in soft[0], f"the warning must say which part it ships as: {soft[0]}"
    # the message is read at a glance in a narrow sidebar popup, not parsed from full asset names
    assert PART_A not in soft[0] and len(soft[0]) < 110, f"the warning is too long to read: {soft[0]}"


def test_duplicate_of_a_part_in_its_own_collection_is_hard():
    """Blender's duplicate suffix means the copy is the same part by name, but a part compiles to one
    mesh, so two objects in one collection is a block and not a name warning."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh(PART_A + ".001", parts[PART_A])
    issues = _check()
    assert _severities(issues, "SOFT") == [], f"a duplicate must not read as a name mismatch: {issues}"
    hard = _severities(issues, "HARD")
    assert len(hard) == 1 and "2 meshes" in hard[0], f"want the one-mesh-per-part block, got {hard}"


def test_unnamed_new_object_in_a_part_is_clean():
    """A name that matches no part carries no claim, so only the collection speaks."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh("Cube", parts[PART_B])
    assert _check() == [], f"a freshly named object reported {_check()}"


def test_two_meshes_in_one_part_is_hard():
    """The designed workflow puts new geometry in the active part collection, which is exactly the case
    that must not slip through: unblocked, the addition exports alongside the part and the app reads
    one of them as the part."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh(PART_B, parts[PART_B])
    _part_mesh("added_horn", parts[PART_A])
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1, f"want one HARD, got {hard}"
    assert rb.gf2_label(PART_A) in hard[0] and "added_horn" in hard[0], \
        f"the message must name both: {hard[0]}"
    assert "Ctrl+J" in hard[0], f"the message must say how to fix it: {hard[0]}"


def test_single_part_session_with_a_stray_added_mesh_is_hard():
    """The single-part session: the app reads the returned glb with FirstOrDefault, so an extra mesh
    landing at index 0 replaces the part's geometry. It must never get that far."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh("Cube", parts[PART_A])         # added with the part collection active
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1 and "Cube" in hard[0], f"want one HARD naming the addition, got {hard}"


def test_nested_mesh_counts_toward_the_part():
    """The count reads the whole part tree, the same depth the export scope does."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _part_mesh("tucked_away", _collection("sub", parts[PART_A]))
    hard = _severities(_check(), "HARD")
    assert any("tucked_away" in m and "2 meshes" in m for m in hard), \
        f"a mesh nested under the part must count: {hard}"


def test_empty_mod_blocks():
    _reset()
    _mod, ref, _parts, _arm = _layout([], armature=True)
    _mesh("donor_body", ref)
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1 and rb.MOD_COLLECTION in hard[0], f"want one HARD about Mod, got {hard}"


def test_an_empty_scene_blocks():
    """The check gate has to match the send's, or an all-empty scene reads as ready and then the Send
    raises instead of reporting."""
    _reset()
    _layout([], armature=True)
    hard = _severities(_check(), "HARD")
    assert any(rb.MOD_COLLECTION in m for m in hard), f"an empty scene must block, got {_check()}"


def test_excluded_part_collection_is_hard():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    lc = rb._layer_collection_for(parts[PART_A])
    lc.exclude = True
    hard = _severities(_check(), "HARD")
    assert any(PART_A in m and "excluded" in m for m in hard), \
        f"an excluded part collection must block, got {hard}"


def test_a_modders_own_folder_under_mod_is_not_a_part():
    """A collection the modder makes under Mod is a folder, not a part. Its meshes have no part, and
    the message offering somewhere to move them must not offer the folder."""
    _reset()
    mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    scratch = _collection("scratch stuff", mod)
    _part_mesh("wip_blob", scratch)
    assert [c.name for c in rb.gf2_part_collections()] == [PART_A], \
        f"an unmarked folder became a part: {[c.name for c in rb.gf2_part_collections()]}"
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1 and "wip_blob" in hard[0], f"want one HARD naming the blob, got {hard}"
    assert "scratch stuff" not in hard[0], f"the folder must not be offered as a part: {hard[0]}"


def test_a_part_collection_squatted_out_of_its_name_is_hard():
    """`bpy.data.collections.new` resolves a name taken anywhere in the file by suffixing `.001`, so
    a collection already carrying the part's name pushes the real part off its own name and the mesh
    would ship as a part the app has no target for."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "one.glb"))
        _reset()
        _collection(PART_A, bpy.context.scene.collection)   # a squatter, outside Mod entirely
        rb.gf2_import(src)

    parts = [c.name for c in rb.gf2_part_collections()]
    assert parts == [PART_A + ".001"], f"the part collection is {parts}, want the suffixed one"
    hard = _severities(_check(), "HARD")
    assert any(PART_A in m and "name suffix" in m for m in hard), \
        f"a squatted part name must block, got {hard}"


def test_an_unsquatted_part_name_raises_nothing():
    """The suffix check keys on the name, so the ordinary case must stay silent."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    assert _check() == [], f"an unsquatted part reported {_check()}"


def test_a_renamed_part_collection_is_hard_and_send_refuses():
    """The marker carries the part's contract name. A renamed collection would ship its mesh as a
    part the app has no target for, so it blocks at the checks AND at the send choke point."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    parts[PART_A].name = "my_edit"
    hard = _severities(_check(), "HARD")
    assert any("my_edit" in m and "renamed" in m for m in hard), \
        f"a renamed part collection must block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "part.glb")
        assert False, "a renamed part collection must refuse to send"
    except RuntimeError as e:
        assert "renamed" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


def test_stray_message_offers_reference_only_when_it_exists():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True, reference=False)
    _part_mesh(PART_A, parts[PART_A])
    _mesh("dropped_here")
    hard = _severities(_check(), "HARD")
    assert len(hard) == 1 and "dropped_here" in hard[0], f"want one HARD, got {hard}"
    assert rb.REFERENCE_COLLECTION not in hard[0], \
        f"a collection that isn't there must not be offered: {hard[0]}"


def test_object_linked_into_both_trees_ships():
    """Mod wins. The docstring states it; this pins it."""
    _reset()
    _mod, ref, parts, _arm = _layout([PART_A])
    both = _mesh(PART_A, parts[PART_A])
    ref.objects.link(both)
    assert rb._in_tree(both, ref), "the fixture failed to link the object into Reference"
    _out, sent = _send()
    assert sent == [PART_A], f"an object in both trees must ship: {sent}"


def test_material_slot_change_is_soft():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A], armature=True)
    mo = _part_mesh(PART_A, parts[PART_A])
    rb._snapshot_baseline([mo], None)
    mo.data.materials.append(bpy.data.materials.new("added_mat"))
    soft = _severities(_check(), "SOFT")
    assert len(soft) == 1 and "material slots changed" in soft[0], f"want the slot warning, got {soft}"
    assert "face range" in soft[0], f"the warning must say what a reorder does: {soft[0]}"


# ---------------------------------------------------------------- checks scope to what ships

def test_reference_object_gets_no_scale_warning():
    _reset()
    _mod, ref, parts, _arm = _layout(armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _mesh("donor_body", ref).scale = (2.0, 2.0, 2.0)
    assert _check() == [], f"a scaled Reference object warned: {_check()}"


def test_mod_object_still_gets_its_scale_warning():
    _reset()
    _mod, _ref, parts, _arm = _layout(armature=True)
    _part_mesh(PART_A, parts[PART_A]).scale = (2.0, 2.0, 2.0)
    soft = _severities(_check(), "SOFT")
    assert len(soft) == 1 and "Object-mode scale" in soft[0], f"want the scale warning, got {soft}"


def test_reference_object_does_not_block_on_weights():
    _reset()
    _mod, ref, parts, arm = _layout([PART_A], armature=True)
    _part_mesh(PART_A, parts[PART_A])
    _mesh("donor_body", ref)                  # unweighted, and it must not matter
    assert _check() == [], f"an unweighted Reference object blocked the send: {_check()}"


def test_unweighted_mod_object_still_blocks():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])              # unweighted, no armature to solve it
    hard = _severities(_check(), "HARD")
    assert any("no weight" in m for m in hard), f"want the unweighted-vertex block, got {hard}"


def test_a_declared_unskinned_part_does_not_block():
    """A static prop's session: no armature, no weights. Every vertex would count as unsolvable, so
    without the app's declaration the gate blocks the part's every send."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "edited": False, "unskinned": True}]})
    assert _check() == [], f"a declared unskinned part blocked the send: {_check()}"


def test_a_skinned_part_whose_armature_is_gone_still_blocks():
    """The declaration is per part, and this one says the part IS skinned — a deleted armature is a
    real authoring error and has to keep reading as one."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "edited": False, "unskinned": False}]})
    hard = _severities(_check(), "HARD")
    assert any("no weight" in m for m in hard), f"want the unweighted-vertex block, got {hard}"


def test_a_declared_unskinned_part_sends():
    """The gate is only half of it: the send itself runs the bone-heat fill, which must also leave an
    unskinned part alone rather than report on it."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "edited": False, "unskinned": True}]})
    _out, sent = _send()
    assert sent == [PART_A], f"the sent glb carries {sent}"


# ---------------------------------------------------------------- export scope

def test_send_exports_mod_only():
    _reset()
    _mod, ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B])
    _mesh("donor_body", ref)
    _out, sent = _send()
    assert sent == sorted([PART_A, PART_B]), f"the sent glb carries {sent}"


def test_send_leaves_a_modders_own_folder_under_mod_out():
    """The checks bless only the marked part collections, so the export scope must read the same
    place. A mesh in a folder the modder made under Mod is HARD-checked; on the checkless headless
    path nothing else would keep it out of the deliverable."""
    _reset()
    mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    _mesh("wip_blob", _collection("scratch stuff", mod))
    assert [o.name for o in rb.gf2_shipping_meshes()] == [PART_A], \
        f"the export scope is {[o.name for o in rb.gf2_shipping_meshes()]}"
    _out, sent = _send()
    assert sent == [PART_A], f"the folder's mesh shipped: {sent}"


def test_send_leaves_a_mesh_sitting_directly_in_mod_out():
    _reset()
    mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    _mesh("new_piece", mod)
    _out, sent = _send()
    assert sent == [PART_A], f"an unattributed mesh in Mod shipped: {sent}"


def test_send_refuses_when_meshes_exist_but_none_is_in_a_part():
    """The empty-scope refusal covers more than a literally empty Mod: with nothing attributed there
    is no deliverable, and overwriting the workspace glb with an empty one destroys the modder's work."""
    _reset()
    mod, _ref, _parts, _arm = _layout([])
    _mesh("wip_blob", _collection("scratch stuff", mod))
    try:
        _send()
    except RuntimeError as e:
        assert "part collection" in str(e), f"unhelpful refusal: {e}"
        return
    raise AssertionError("a send with nothing in a part collection must refuse")


def test_send_leaves_a_scene_root_stray_out():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    _mesh("dropped_here")
    _out, sent = _send()
    assert sent == [PART_A], f"the sent glb carries {sent}"


def test_send_ships_a_hidden_mod_mesh():
    """Hiding is working state. It must not change the deliverable."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B]).hide_set(True)
    _out, sent = _send()
    assert sent == sorted([PART_A, PART_B]), f"a hidden part dropped out: {sent}"
    assert bpy.data.objects[PART_B].hide_get(), "the send left the hidden part visible"


def test_send_writes_the_sidecar_last():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    out, _sent = _send("one")
    assert os.path.exists(os.path.join(out, "one.gf2send.json")), "no write-complete sidecar"


def test_send_names_each_mesh_for_its_part_collection():
    """The collection IS the part. An object named for one part sitting in another must come back as
    the collection's part, because that name is what the app re-splits the glb by."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_B])              # named body, sitting in cloth
    out, sent = _send()
    assert sent == [PART_B], f"the glb names the mesh {sent}, want the collection's name"
    assert _sent_node_names(out, "part") == [PART_A], \
        f"the object's own name belongs on the node: {_sent_node_names(out, 'part')}"


def test_send_names_a_freshly_added_object_for_its_part():
    """A brand-new object dragged into a part collection ships as that part, not under `Cube`."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    ob = _mesh("Cube", parts[PART_A])
    ob.data.name = "Cube"
    _out, sent = _send()
    assert sent == [PART_A], f"the glb names the mesh {sent}"


def test_send_restores_the_mesh_data_names():
    """The renaming is export plumbing. The modder's scene comes back exactly as it was."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    a = _mesh(PART_A, parts[PART_B])
    b = _mesh("scratch", parts[PART_A])
    b.data.name = PART_B                      # a data name another part wants: the collision case
    before = {me.name for me in bpy.data.meshes}
    _send()
    assert a.data.name == PART_A, f"data name left as {a.data.name}"
    assert b.data.name == PART_B, f"data name left as {b.data.name}"
    assert {me.name for me in bpy.data.meshes} == before, "the send left renamed data blocks behind"


def test_a_failed_send_restores_hide_state():
    """The failure path restores as thoroughly as the happy one. An excluded part collection aborts the
    send midway through, and hide state the send unwound is not something the modder can put back."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    a = _mesh(PART_A, parts[PART_A])
    b = _mesh(PART_B, parts[PART_B])
    a.hide_set(True)
    rb._layer_collection_for(parts[PART_B]).exclude = True
    try:
        _send()
    except RuntimeError as e:
        assert PART_B in str(e), f"the refusal must name what it could not select: {e}"
    else:
        raise AssertionError("a send that cannot select a shipping object must refuse")
    assert a.hide_get(), "the failed send left a hidden part unhidden"
    assert b.data.name == PART_B, f"the failed send left a renamed data block: {b.data.name}"


def test_a_failed_send_restores_the_armature_hide_state():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B], armature=True)
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B])
    _arm.hide_set(True)                       # the armature is hidden by default after an import
    rb._layer_collection_for(parts[PART_B]).exclude = True
    try:
        _send()
    except RuntimeError:
        pass
    assert _arm.hide_get(), "the failed send left the armature unhidden"


def test_a_donor_rig_in_reference_neither_ships_nor_is_consulted():
    """Reference is where a donor body and its rig belong. Neither may reach the deliverable, and the
    donor rig must not become the bone-heat source or the bone-rename baseline."""
    _reset()
    _mod, ref, parts, arm = _layout([PART_A], armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    # name-sorts ahead of the session armature, and its bones are named differently, so consulting it
    # would show up as renamed bones
    donor_rig = _armature("AAA_donor_rig", coll=ref, bone="donor_root")
    _bind(_weight_all(_mesh("AAA_donor_body", ref), group="donor_root"), donor_rig)
    rb._snapshot_baseline([mo], rb._session_armature())
    assert rb._session_armature() is arm, \
        f"the session armature is {rb._session_armature().name}, want the Mod tree's"
    assert _check() == [], f"a Reference donor rig disturbed the checks: {_check()}"

    out, sent = _send()
    assert sent == [PART_A], f"the sent glb carries {sent}"
    doc = _sent_doc(out, "part")
    assert len(doc.get("skins", [])) == 1, f"the glb carries {len(doc.get('skins', []))} skins, want 1"
    nodes = _sent_node_names(out, "part")
    assert donor_rig.name not in nodes and "AAA_donor_body" not in nodes, f"donor nodes shipped: {nodes}"
    assert "donor_root" not in nodes, f"the donor rig's bones shipped: {nodes}"


def test_the_fill_uses_the_mod_armature_not_a_reference_rig():
    """Bone-heat runs off the session armature, so an unweighted vertex comes back weighted to the
    skeleton the app will compile against."""
    _reset()
    _mod, ref, parts, _arm = _layout([PART_A], armature=True)
    mo = _mesh(PART_A, parts[PART_A])                  # unweighted: the fill has work to do
    _armature("AAA_donor_rig", coll=ref, bone="donor_root")
    filled, still = rb.gf2_fill_missing_weights([mo], rb._session_armature())
    assert (filled, still) == (3, 0), f"the fill solved {filled} and left {still}"
    assert {vg.name for vg in mo.vertex_groups} == {"root"}, \
        f"the fill wrote {[vg.name for vg in mo.vertex_groups]}, want the Mod armature's bone"


def test_send_refuses_an_empty_mod():
    _reset()
    _mod, ref, _parts, _arm = _layout([])
    _mesh("donor_body", ref)
    try:
        _send()
    except RuntimeError as e:
        assert rb.MOD_COLLECTION in str(e), f"unhelpful refusal: {e}"
        return
    raise AssertionError("a send with an empty Mod must refuse, not write a geometry-less glb")


# ---------------------------------------------------------------- the operators themselves

def _register(send_dir, stem="part"):
    """Register the panel's operators against a send folder, the way an interactive launch does."""
    rb._register_ui(stem + ".glb", send_dir)
    bpy.context.scene.gf2_send_dir = send_dir
    bpy.context.scene.gf2_glb_path = stem + ".glb"


def _run_op(op):
    """Run a panel operator and return (result set, reported error text). `bpy.ops` re-raises an
    operator's ERROR report as a RuntimeError, so the blocked paths are only observable through the
    exception; the clean paths return their result set."""
    try:
        return op(), None
    except RuntimeError as e:
        return None, str(e)


def _capture_op(op):
    """`_run_op` plus the operator's console output. The panel prints every issue it found, which is
    the only way to read a WARNING-level result back: a WARNING report, unlike an ERROR one, does not
    come back through bpy.ops."""
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        result, err = _run_op(op)
    return result, err, buf.getvalue()


def test_check_operator_ignores_a_reference_donor_rig():
    """The operator pairs the scene's meshes with the SESSION armature. Reading a donor rig parked in
    Reference as the skeleton reports the whole imported skeleton as renamed."""
    _reset()
    _mod, ref, parts, arm = _layout([PART_A], armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    _armature("AAA_donor_rig", coll=ref, bone="donor_root")
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    result, err, log = _capture_op(bpy.ops.gf2.check_mesh)
    assert err is None, f"a Reference donor rig blocked the check: {err}"
    assert "renamed or removed" not in log, f"the donor rig was read as the session skeleton: {log}"
    assert result == {'FINISHED'}, f"the check operator returned {result}"


def test_check_operator_reports_a_clean_scene():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    result, err = _run_op(bpy.ops.gf2.check_mesh)
    assert err is None, f"a clean scene reported {err}"
    assert result == {'FINISHED'}, f"the check operator returned {result}"


def test_check_operator_survives_a_blocked_scene():
    """The blocked path draws a popup, and a background run has no window to draw into. The operator
    has to finish either way, and it reports at WARNING level so its findings popup is not buried
    under a second one from Blender, so the console print is where the block is readable."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _part_mesh("Cube", parts[PART_A])
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    result, err, out = _capture_op(bpy.ops.gf2.check_mesh)
    assert err is None and result == {'FINISHED'}, f"the check operator returned {result} / {err}"
    assert "GF2 BLOCK:" in out, f"the block must reach the console, got: {out}"


def test_send_operator_is_blocked_by_a_hard_and_writes_nothing():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _part_mesh("Cube", parts[PART_A])         # the one-mesh-per-part block
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)
    # The blocked send reports at WARNING level so its own popup isn't buried under Blender's error
    # popup, so the block is observable as the result set plus an empty output dir, not an exception.
    with _captured_popups() as seen:
        result, err = _run_op(bpy.ops.gf2.send_to_lab)
    assert err is None, f"a blocked send must not raise: {err}"
    assert result == {'CANCELLED'}, f"the send operator returned {result}"
    assert seen and seen[0][0] == "Send blocked", f"want the blocked popup, got {seen}"
    assert os.listdir(out) == [], f"a blocked send wrote {os.listdir(out)}"


def test_send_operator_writes_on_a_clean_scene():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)
    result, err = _run_op(bpy.ops.gf2.send_to_lab)
    assert err is None, f"a clean send reported {err}"
    assert result == {'FINISHED'}, f"the send operator returned {result}"
    assert os.path.exists(os.path.join(out, "part.glb")), f"no glb written: {os.listdir(out)}"
    assert os.path.exists(os.path.join(out, "part.gf2send.json")), f"no sidecar: {os.listdir(out)}"
    assert _sent_mesh_names(out, "part") == [PART_A], f"the operator sent {_sent_mesh_names(out, 'part')}"


# ---------------------------------------------------------------- what the modder is shown

class _FakeLayout:
    """Enough of a Blender UILayout to record what a draw() put on screen, in order. The real
    widgets need a window; what the drawing DECIDES is what these tests are about."""

    def __init__(self):
        self.lines = []

    def column(self, **_kw):
        return self

    def label(self, text=""):
        self.lines.append(text)

    def separator(self):
        self.lines.append("")

    def operator(self, idname, **_kw):
        self.lines.append(f"<button {idname}>")


class _FakeOp:
    """A stand-in for the operator instance, so a draw() or a gate can be driven without Blender's
    modal machinery. `reports` keeps what the operator would have put in the status bar."""

    def __init__(self):
        self.layout = _FakeLayout()
        self.reports = []
        self._soft = None
        self._scope = None
        self._overwrite = None

    def report(self, level, message):
        self.reports.append((set(level), message))


def _send_operator():
    """The registered Send operator class. Blender names an operator's RNA type from its bl_idname
    and not from the Python class name, so this is `GF2_OT_send_to_lab` however the class is
    spelled in the bridge."""
    cls = getattr(bpy.types, "GF2_OT_send_to_lab", None)
    assert cls is not None and hasattr(cls, "_gate"), "the Send operator is not registered"
    return cls


@contextlib.contextmanager
def _captured_popups():
    """Collect the (title, lines) the bridge would have popped up. A background run draws nothing, so
    this is the only way to read the popup text back."""
    seen = []
    original = rb._popup
    rb._popup = lambda title, lines, icon: seen.append((title, list(lines)))
    try:
        yield seen
    finally:
        rb._popup = original


def test_the_send_dialog_lists_the_warnings_above_the_scope():
    """The confirm step is where the send is decided, so the warnings the gate already found belong
    on it — not only in the popup that follows the export."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm).scale = (2.0, 2.0, 2.0)
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    op = _FakeOp()
    blocked = _send_operator()._gate(op, bpy.context)
    assert blocked is None, f"a scene with only warnings must not be blocked: {op.reports}"
    assert len(op._soft) == 1 and "Object-mode scale" in op._soft[0], f"the gate kept {op._soft}"
    op._scope = rb._send_scope_lines()
    _send_operator().draw(op, bpy.context)

    lines = op.layout.lines
    warned = [i for i, ln in enumerate(lines) if ln.startswith("⚠ ")]
    scoped = [i for i, ln in enumerate(lines) if "vert" in ln]
    assert len(warned) == 1, f"the dialog drew {lines}"
    assert scoped and warned[0] < scoped[0], f"the warning must lead the scope: {lines}"
    assert "Object-mode scale" in lines[warned[0]], lines[warned[0]]


def test_the_send_dialog_of_a_clean_scene_is_scope_only():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    op = _FakeOp()
    assert _send_operator()._gate(op, bpy.context) is None, f"a clean scene was blocked: {op.reports}"
    assert op._soft == [], f"the gate found {op._soft}"
    op._scope = rb._send_scope_lines()
    _send_operator().draw(op, bpy.context)
    assert not any(ln.startswith("⚠ ") for ln in op.layout.lines), f"the dialog drew {op.layout.lines}"


def test_the_scope_line_does_not_count_reference():
    """Reference never ships, so the scope counts only what does. The panel's collections row is where
    the Reference population lives."""
    _reset()
    _mod, ref, parts, _arm = _layout([PART_A])
    _part_mesh(PART_A, parts[PART_A])
    _mesh("donor_body", ref)
    line = rb._send_scope_lines()[0]
    assert line == "1 object · 3 verts", line


def test_the_check_popup_carries_the_severity_glyphs():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _bind(_part_mesh(PART_B, parts[PART_B]), arm).scale = (2.0, 2.0, 2.0)   # the warning
    _part_mesh("added_horn", parts[PART_A])                                # the blocker
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    with _captured_popups() as seen:
        _result, err = _run_op(bpy.ops.gf2.check_mesh)
    assert len(seen) == 1, f"the check popped up {len(seen)} times"
    title, lines = seen[0]
    assert title == "Check", f"the popup is titled {title!r}"
    assert sum(1 for ln in lines if ln.startswith("✗ ")) == 1, lines
    assert sum(1 for ln in lines if ln.startswith("⚠ ")) == 1, lines
    # exactly one popup: an ERROR-level report would draw a second one over this one
    assert err is None, f"the check must not raise its findings as an exception: {err}"


def test_a_clean_check_reports_the_status_lines_wording():
    """One phrasing of ready, everywhere it appears."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    with _captured_popups() as seen:
        result, err = _run_op(bpy.ops.gf2.check_mesh)
    assert err is None and result == {'FINISHED'}, f"a clean check reported {err}"
    assert seen == [("Check", ["✓ Ready to send"])], f"the popup read {seen}"
    assert rb.gf2_status_line([]) == "✓ Ready to send"


def test_a_blocked_send_popup_uses_the_blocking_glyph():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _part_mesh("Cube", parts[PART_A])
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)
    with _captured_popups() as seen:
        result, err = _run_op(bpy.ops.gf2.send_to_lab)
    assert err is None and result == {'CANCELLED'}, f"want a quiet cancel, got {result} / {err}"
    title, lines = seen[0]
    assert title == "Send blocked", f"the popup is titled {title!r}"
    assert lines[0].startswith("✗ "), lines
    assert lines[-1] == "Fix these, then Send again.", lines


def test_the_unweighted_block_names_the_mesh_it_found():
    """Every other check message quotes its subject; this one has to as well, or the modder is told
    a count with nowhere to look."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])              # unweighted, no armature to solve it
    hard = _severities(_check(), "HARD")
    named = [m for m in hard if "no weight" in m]
    assert len(named) == 1, f"want one weight block, got {hard}"
    assert f"'{PART_A}'" in named[0], f"the block must name its mesh: {named[0]}"
    assert "3 vertex(es)" in named[0], named[0]


def test_the_panel_reports_the_scene_it_is_looking_at():
    _reset()
    _mod, ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _bind(_part_mesh(PART_B, parts[PART_B]), arm)
    _mesh("donor_body", ref)
    _register(tempfile.mkdtemp(prefix="gf2op_"), stem="body1")

    rows = rb._panel_lines(bpy.context.scene)
    assert rows[0] == f"body1 · {PART_A}, {PART_B}", f"the subject row reads {rows[0]}"
    assert rows[1] == "Mod 2 · Reference 1", f"the count row reads {rows[1]}"
    assert rb.REFERENCE_NOTE in rows, rows


def test_the_panel_leaves_an_empty_part_collection_out_of_the_subject_row():
    """A part collection with nothing in it is not something the session is editing."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rows = rb._panel_lines(bpy.context.scene)
    assert PART_B not in rows[0], f"an empty part was listed: {rows[0]}"
    assert PART_A in rows[0], rows[0]


def test_the_panel_does_not_count_an_unmarked_folder_as_shipping():
    _reset()
    mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _part_mesh("wip_blob", _collection("scratch stuff", mod))
    rows = rb._panel_lines(bpy.context.scene)
    assert rows[1] == "Mod 1 · Reference 0", f"the count row reads {rows[1]}"


def test_the_panel_says_the_scene_is_unreadable_exactly_once():
    """The rows and the status line read the same scene. When that read fails, the panel says so in
    the rows; repeating it underneath the buttons tells the modder nothing new."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    panel = _FakeOp()
    original = rb._mod_root
    rb._mod_root = lambda: (_ for _ in ()).throw(RuntimeError("scene gone"))
    try:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    finally:
        rb._mod_root = original

    lines = panel.layout.lines
    assert lines.count(rb.UNREADABLE) == 1, f"the panel drew {lines}"
    assert lines[0] == rb.UNREADABLE, f"the unreadable row must lead: {lines}"
    assert lines[-1].startswith("<button "), f"nothing may follow the buttons: {lines}"


def test_the_panel_draws_a_status_line_when_the_scene_reads():
    """The control for the suppression above: normally the status line IS drawn, under the buttons."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    rb._LIVE["text"] = ""            # the cache outlives a scene swap; a fixture reads this scene
    panel = _FakeOp()
    bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    assert panel.layout.lines[-1] == "✓ Ready to send", f"the panel drew {panel.layout.lines}"


def test_the_live_status_reflects_a_collection_level_block():
    """The cheap pass carries the attribution blocks, so the status line goes red on a misplaced
    object without waiting for a Check."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    assert rb._refresh_live_status() == "✓ Ready to send", rb._refresh_live_status()
    _mesh("dropped_here")                     # scene root: in neither tree
    assert rb._refresh_live_status() == "✗ 1 blocking", rb._refresh_live_status()


def test_the_live_status_pairs_the_session_armature():
    """Never `arms[0]` of every armature: a donor rig in Reference would read as the skeleton and
    report the whole imported bone set as renamed."""
    _reset()
    _mod, ref, parts, arm = _layout([PART_A], armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    _armature("AAA_donor_rig", coll=ref, bone="donor_root")
    assert rb._refresh_live_status() == "✓ Ready to send", rb._refresh_live_status()


# ---------------------------------------------------------------- the headless round trip

def test_headless_send_to_round_trip():
    """The `--send-to` no-op path end to end, in its own Blender: import builds the layout and the
    send exports the Mod tree it just built, which for a no-op is everything imported."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        out = os.path.join(d, "sent")
        r = subprocess.run([bpy.app.binary_path, "--background", "--factory-startup",
                            "--python", BRIDGE, "--", src, "--send-to", out],
                           capture_output=True, text=True)
        assert r.returncode == 0, f"the headless round trip failed:\n{r.stdout}\n{r.stderr}"
        assert os.path.exists(os.path.join(out, "combined.gf2send.json")), "no sidecar was written"
        sent = _sent_mesh_names(out, "combined")
        assert sent == sorted([PART_A, PART_B]), f"the round trip carries {sent}"


def test_headless_send_to_honours_the_session_description():
    """End to end, in its own Blender and through the script's own entry point: a session that names one
    part reads its description off disk, holds the rest of the outfit in `Reference`, and sends the named
    part alone. A context part reaching the deliverable is the failure this guards."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "_combined.glb"))
        _write_session(src, part=PART_B, parts=[(PART_A, True), (PART_B, False)])
        out = os.path.join(d, "sent")
        r = subprocess.run([bpy.app.binary_path, "--background", "--factory-startup",
                            "--python", BRIDGE, "--", src, "--send-to", out],
                           capture_output=True, text=True)
        assert r.returncode == 0, f"the headless round trip failed:\n{r.stdout}\n{r.stderr}"
        sent = _sent_mesh_names(out, "_combined")
        assert sent == [PART_B], f"the round trip carries {sent}, want the session part alone"
        assert _sent_sidecar(out, "_combined")["hiddenParts"] == [], \
            "a context part must not read as an emptied one"


# ---------------------------------------------------------------- runner

def main():
    tests = [v for k, v in sorted(globals().items()) if k.startswith("test_") and callable(v)]
    failed = []
    for t in tests:
        try:
            t()
            print(f"  ok   {t.__name__}")
        except Exception as e:
            failed.append(t.__name__)
            print(f"  FAIL {t.__name__}: {type(e).__name__}: {e}")
    print(f"\n{len(tests) - len(failed)}/{len(tests)} passed"
          + (f"; failed: {', '.join(failed)}" if failed else ""))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
