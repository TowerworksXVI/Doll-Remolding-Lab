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


def _add_uv_sets(obj, count=3):
    """Distinct per-loop UV layers, in the order glTF must preserve as TEXCOORD_0..N."""
    fixture = (
        ((0.0, 0.0), (1.0, 0.0), (0.0, 1.0)),
        ((2.0, 3.0), (4.0, 3.0), (2.0, 5.0)),
        ((-1.0, 8.0), (0.0, 8.0), (-1.0, 9.0)),
    )
    for index in range(count):
        layer = obj.data.uv_layers.new(name="UVMap" if index == 0 else f"UVMap.{index:03d}")
        for loop in obj.data.loops:
            layer.data[loop.index].uv = fixture[index][loop.vertex_index]
    return fixture[:count]


def _uv_sets(obj):
    return [[tuple(round(float(v), 6) for v in layer.data[loop.index].uv)
             for loop in obj.data.loops]
            for layer in obj.data.uv_layers]


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
    """A producer-shaped session beside a glb, including each part's live edit inventory."""
    entries = []
    for name, edited in parts:
        entry = {"name": name, "writable": True, "defaultEditName": "Edit 1", "edits": []}
        if edited:
            edit_id = name + "-edit-1"
            entry["editId"] = edit_id
            entry["edits"] = [
                {"id": edit_id, "label": "Edit 1", "holdsAuthoredMesh": True}]
        entries.append(entry)
    with open(rb.session_path(glb_path), "w", encoding="utf-8") as f:
        json.dump({"revision": 1, "part": part, "parts": entries}, f)
    return glb_path


def _sent_sidecar(out_dir, stem):
    with open(os.path.join(out_dir, stem + ".gf2send.json"), "r", encoding="utf-8") as f:
        return json.load(f)


def _reference_names():
    return sorted(o.name for o in rb._reference_root().all_objects if o.type == "MESH")


def _build_source_glb(part_names, path, uv_sets=0):
    """Export a synthetic multi-part rigged .glb to feed gf2_import, the way the app would."""
    _reset()
    arm = _armature()
    for n in part_names:
        ob = _mesh(n)
        if uv_sets:
            _add_uv_sets(ob, uv_sets)
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
        assert len(mo.users_collection) == 1, \
            f"{mo.name} is linked into {len(mo.users_collection)} collections"
    assert arms, "the fixture glb carried no armature"
    arm_coll = root.children[rb.MOD_COLLECTION].children.get(rb.ARMATURE_COLLECTION)
    assert arm_coll is not None and arm_coll.objects.get(arms[0].name) is arms[0], \
        "armature is not in Mod/Armature"
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


def test_a_per_part_session_shows_its_reference_context_by_default():
    """Reference context is visible unless viewportVisible is explicitly false, while only the session
    part is selected for initial framing."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _write_session(src, part=PART_A, parts=[(PART_A, False), (PART_B, False)])
        _reset()
        meshes, _arms = rb.gf2_import(src)

        by = {m.name: m for m in meshes}
        assert not by[PART_B].hide_get(), "the Reference context part imported hidden"
        assert not by[PART_A].hide_get(), "the session's own part imported hidden"
        assert by[PART_A].select_get(), "the session part must be selected, it is what framing centres on"
        assert not by[PART_B].select_get(), "a Reference part imported selected"


def test_viewport_visible_false_is_hidden_and_skipped_by_initial_framing():
    """A hidden writable part cannot be selected; framing skips it and leaves visible context active."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A, PART_B], os.path.join(d, "combined.glb"))
        _write_session(src, part=PART_A, parts=[(PART_A, False), (PART_B, False)])
        with open(rb.session_path(src), "r", encoding="utf-8") as stream:
            session = json.load(stream)
        session["parts"][0]["viewportVisible"] = False
        with open(rb.session_path(src), "w", encoding="utf-8") as stream:
            json.dump(session, stream)
        _reset()
        meshes, _arms = rb.gf2_import(src)

        by = {mesh.name: mesh for mesh in meshes}
        assert by[PART_A].hide_get(), "viewportVisible=false did not hide the writable part"
        assert not by[PART_A].select_get(), "the hidden writable part was selected for framing"
        assert bpy.context.view_layer.objects.active is by[PART_B], \
            "visible Reference context was not left active when no shipping mesh was visible"


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
    assert len(doc.get("skins", [])) == 1, \
        f"the send wrote {len(doc.get('skins', []))} skins, want the one union rig"


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
    assert "move a mesh back into the collection" in soft[0], \
        f"the warning must include the remedy: {soft[0]}"
    assert rb._refresh_live_status() == "1 warning — click Check Mesh for details", \
        rb._refresh_live_status()


def test_every_part_emptied_blocks_and_refuses():
    """One emptied part is a Hide. All of them is no deliverable at all, and there is nothing left to
    hide against either."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_A, PART_B], armature=True)
    hard = _severities(_check(), "HARD")
    assert any("Every part collection is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "part.glb")
        assert False, "an all-empty scene must refuse to send"
    except RuntimeError as e:
        assert "Every part collection is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


def test_an_open_all_session_emptied_to_nothing_still_blocks():
    """The same block, stated against the session that makes it meaningful: an open-all can write every
    part, so emptying them all really does leave an empty deliverable."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_A, PART_B], armature=True)
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})
    hard = _severities(_check(), "HARD")
    assert any("Every part collection is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "_combined.glb")
        assert False, "an all-empty open-all session must refuse to send"
    except RuntimeError as e:
        assert "Every part collection is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


def test_a_per_part_session_may_empty_its_one_part_to_hide_it():
    """A session that names one part holds exactly one part collection, so emptying it is the only way
    to say "hide this part". It warns, it sends, and the sidecar names the part — the rest of the mod
    still ships from its own files, so the deliverable is not empty."""
    _reset()
    _mod, _ref, _parts, _arm = _layout([PART_B], armature=True)
    _mesh(PART_A, rb._reference_root())          # the rest of the outfit rode along as context
    rb._store_session({"part": PART_B, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})

    issues = _check()
    assert _severities(issues, "HARD") == [], f"a deliberate Hide must not block: {issues}"
    soft = _severities(issues, "SOFT")
    assert any("move a mesh back into the collection" in m for m in soft), \
        f"want the Hide remedy, got {soft}"

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
    rb._store_session({"part": PART_A, "parts": [
        {"name": PART_A, "defaultEditName": "Edit 1", "edits": []}]})
    hard = _severities(_check(), "HARD")
    assert any("Every part collection is empty" in m for m in hard), f"want the all-empty block, got {hard}"
    out = tempfile.mkdtemp(prefix="gf2send_")
    try:
        rb.gf2_send(out, "part.glb")
        assert False, "hiding the mod's only part must refuse to send"
    except RuntimeError as e:
        assert "Every part collection is empty" in str(e), f"the refusal must name the cause, got: {e}"
    assert not os.listdir(out), "a refused send must write nothing"


# ---------------------------------------------------------------- the edit each part was opened on

def test_the_send_sidecar_echoes_the_edit_each_part_was_opened_on():
    """The app stamps the edit id a part was opened on, and the send hands it straight back — that is how a
    return lands on the answer the modder was editing rather than on whichever one the part shows by the
    time it arrives. Blender reads none of it, so the only rule is that what goes out comes back."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B])
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "editId": "edit-0007", "defaultEditName": "Edit 2",
         "edits": [{"id": "edit-0007", "label": "Edit 1", "holdsAuthoredMesh": True}]},
        {"name": PART_B, "editId": "edit-0012", "defaultEditName": "Edit 2",
         "edits": [{"id": "edit-0012", "label": "Edit 1", "holdsAuthoredMesh": True}]},
    ]})
    out, _sent = _send("_combined")
    assert _sent_sidecar(out, "_combined")["editIds"] == {PART_A: "edit-0007", PART_B: "edit-0012"}, \
        f"the sidecar says {_sent_sidecar(out, '_combined')}"


def test_parts_with_no_opened_edit_target_new_destinations():
    """A session inventory with no opened existing id targets New using each producer default name."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])
    _mesh(PART_B, parts[PART_B])
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []},
    ]})
    out, _sent = _send("_combined")
    assert _sent_sidecar(out, "_combined")["editIds"] == {
        PART_A: {"new": "Body Edit 1"}, PART_B: {"new": "Cloth Edit 1"}}, \
        f"the sidecar says {_sent_sidecar(out, '_combined')}"


def test_an_emptied_part_still_carries_its_edit_id():
    """A Hide is an edit like any other and lands on the same named answer, so the echo covers the parts a
    session emptied as well as the ones it still holds a mesh for."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A, PART_B])
    _mesh(PART_A, parts[PART_A])                             # PART_B's collection is left empty
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "editId": "edit-0001", "edits": [
            {"id": "edit-0001", "label": "Edit 1", "holdsAuthoredMesh": True}]},
        {"name": PART_B, "editId": "edit-0002", "edits": [
            {"id": "edit-0002", "label": "Edit 1", "holdsAuthoredMesh": True}]},
    ]})
    sidecar = _sent_sidecar(_send("_combined")[0], "_combined")
    assert sidecar["hiddenParts"] == [PART_B], f"the sidecar says {sidecar}"
    assert sidecar["editIds"] == {PART_A: "edit-0001", PART_B: "edit-0002"}, \
        f"the sidecar says {sidecar}"


# ---------------------------------------------------------------- what a send would replace

def test_the_send_dialog_warns_when_the_target_already_carries_an_edit():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "editId": "edit-1",
                       "edits": [{"id": "edit-1", "label": "Edit 1",
                                  "holdsAuthoredMesh": True}]}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    op = _FakeOp()
    assert _send_operator()._gate(op, bpy.context) is None, f"a clean scene was blocked: {op.reports}"
    op._scope = rb._send_scope_lines()
    op._overwrite = rb._send_overwrite_warning(edit_targets=op._targets)
    _send_operator().draw(op, bpy.context)

    lines = op.layout.lines
    assert lines[0] == "Sending replaces the mesh work stored in Edit 1.", \
        f"the dialog drew {lines}"


def test_the_send_dialog_says_nothing_about_a_first_send():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": PART_A, "parts": [
        {"name": PART_A, "defaultEditName": "Edit 1", "edits": []}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    assert rb._send_overwrite_warning() is None, rb._send_overwrite_warning()


def test_a_pending_send_does_not_claim_overwrite_before_live_acknowledgment():
    """Export completion is not app intake; only adopted live inventory may claim authored mesh work."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"revision": 1, "part": PART_A, "parts": [
        {"name": PART_A, "defaultEditName": "Edit 1", "edits": []}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    assert rb._send_overwrite_warning() is None, "a first send has nothing to replace"

    _send()

    assert rb._send_overwrite_warning() is None, \
        "a pending export was treated as acknowledged inventory"
    assert rb._load_send_snapshot() is not None, "the pending Send snapshot was not retained"


def test_a_send_snapshot_matches_every_sidecar_target_including_a_hide():
    """The pending snapshot is the exact target union written to the sidecar, including emptied parts."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)     # PART_B's collection is emptied — a Hide
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})
    out, _sent = _send()
    sidecar = _sent_sidecar(out, "part")
    assert rb._load_send_snapshot()["targets"] == sidecar["editIds"], \
        f"snapshot {rb._load_send_snapshot()} does not match {sidecar}"


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
    rb._store_session({"part": PART_A, "parts": [
        {"name": PART_A, "defaultEditName": "Edit 1", "edits": []}]})
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
    rb._store_session({"part": PART_A, "parts": [{"name": PART_A, "editId": "edit-1",
                       "edits": [{"id": "edit-1", "label": "Edit 1",
                                  "holdsAuthoredMesh": True}]}]})
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


def test_plain_sent_popup_lists_an_emptied_part_after_export():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})
    out = tempfile.mkdtemp(prefix="gf2op_")
    _register(out)

    cls = _send_operator()
    op = _wired_op(cls)
    with _captured_popups() as seen:
        result = cls.invoke(op, _CtxWithWm(_FakeWm()), None)

    assert result == {'FINISHED'}, f"invoke returned {result} ({op.reports})"
    title, lines = seen[-1]
    assert title == "Sent", f"the final popup was {seen}"
    assert rb.gf2_emptied_part_confirm_line(PART_B) in lines, \
        f"the post-send popup omitted the emptied part: {lines}"


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
    assert "will not be sent" in hard[0], f"the message must say it is not sent: {hard[0]}"
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
    assert f"is sent as {b}" in soft[0], f"the warning must say which part it is sent as: {soft[0]}"
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
    assert any(PART_A in m and "already named" in m for m in hard), \
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

def test_reference_object_gets_no_transform_warning():
    """Reference is scenery: a donor body dragged aside to work against is exactly what the collection
    is for, and it never reaches the export."""
    _reset()
    _mod, ref, parts, _arm = _layout(armature=True)
    mine = _part_mesh(PART_A, parts[PART_A])
    donor = _mesh("donor_body", ref)
    rb._snapshot_baseline([mine, donor], rb._session_armature())
    donor.location = (0.0, 3.0, 0.0)
    donor.scale = (-2.0, 2.0, 2.0)
    assert _check() == [], f"a moved and mirrored Reference object warned: {_check()}"


def test_a_scaled_skinned_mod_object_is_not_warned():
    """The exporter bakes a skinned mesh's object transform into its vertices once and writes the node
    at identity, so the part arrives as it looks here. Nothing to say."""
    _reset()
    _mod, _ref, parts, arm = _layout(armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    mo.location = (0.0, 3.0, 0.0)
    mo.scale = (2.0, 2.0, 2.0)
    assert _check() == [], f"an ordinary Object-mode transform warned: {_check()}"


def test_a_mirrored_skinned_mod_object_reads_inside_out():
    """The one thing the bake does not carry: the geometry mirrors and the winding does not."""
    _reset()
    _mod, _ref, parts, arm = _layout(armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    mo.scale = (-1.0, 1.0, 1.0)
    soft = _severities(_check(), "SOFT")
    assert len(soft) == 1 and "inside-out" in soft[0], f"want the mirror warning, got {soft}"


def test_an_unskinned_mod_objects_transform_is_warned_as_dropped():
    """A declared unskinned part exports with no skin, so its transform rides the glb node — and the
    app reads positions only. The whole placement is lost, silently, without this."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    mo = _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "unskinned": True,
                                  "defaultEditName": "Edit 1", "edits": []}]})
    rb._snapshot_baseline([mo], None)
    mo.location = (0.0, 3.0, 0.0)
    soft = _severities(_check(), "SOFT")
    assert len(soft) == 1 and "has no skeleton" in soft[0], f"want the dropped-transform warning: {soft}"
    assert "Ctrl+A" in soft[0], f"the warning must name the fix: {soft[0]}"


def test_an_unskinned_mod_object_left_where_it_arrived_is_not_warned():
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    mo = _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "unskinned": True,
                                  "defaultEditName": "Edit 1", "edits": []}]})
    rb._snapshot_baseline([mo], None)
    assert _check() == [], f"an untouched unskinned part warned: {_check()}"


def test_a_baseline_recorded_before_the_move_check_warns_about_nothing():
    """An older bridge recorded scale alone. A component it never wrote cannot be compared, and an
    absent value must never read as a change."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    mo = _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "unskinned": True,
                                  "defaultEditName": "Edit 1", "edits": []}]})
    bpy.context.scene["gf2_baseline"] = json.dumps({"bones": [], "slots": {},
                                                    "scale": {PART_A: [1.0, 1.0, 1.0]}})
    mo.location = (0.0, 3.0, 0.0)
    assert _check() == [], f"a pre-move baseline produced a warning: {_check()}"


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
                       "parts": [{"name": PART_A, "unskinned": True,
                                  "defaultEditName": "Edit 1", "edits": []}]})
    assert _check() == [], f"a declared unskinned part blocked the send: {_check()}"


def test_a_skinned_part_whose_armature_is_gone_still_blocks():
    """The declaration is per part, and this one says the part IS skinned — a deleted armature is a
    real authoring error and has to keep reading as one."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "unskinned": False,
                                  "defaultEditName": "Edit 1", "edits": []}]})
    hard = _severities(_check(), "HARD")
    assert any("no weight" in m for m in hard), f"want the unweighted-vertex block, got {hard}"


def test_a_declared_unskinned_part_sends():
    """The gate is only half of it: the send itself runs the bone-heat fill, which must also leave an
    unskinned part alone rather than report on it."""
    _reset()
    _mod, _ref, parts, _arm = _layout([PART_A])
    _mesh(PART_A, parts[PART_A])
    rb._store_session({"part": PART_A,
                       "parts": [{"name": PART_A, "unskinned": True,
                                  "defaultEditName": "Edit 1", "edits": []}]})
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
    assert seen and seen[0][0] == "Send Blocked", f"want the blocked popup, got {seen}"
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

    def box(self):
        return self

    def row(self, **_kw):
        return self

    def label(self, text=""):
        self.lines.append(text)

    def separator(self):
        self.lines.append("")

    def prop(self, data, name, text=""):
        self.lines.append(f"<property {text or name}: {getattr(data, name)}>")

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
        self._emptied = None
        self._targets = None

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
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    mo.scale = (-1.0, 1.0, 1.0)
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    op = _FakeOp()
    blocked = _send_operator()._gate(op, bpy.context)
    assert blocked is None, f"a scene with only warnings must not be blocked: {op.reports}"
    assert len(op._soft) == 1 and "inside-out" in op._soft[0], f"the gate kept {op._soft}"
    op._scope = rb._send_scope_lines()
    _send_operator().draw(op, bpy.context)

    lines = op.layout.lines
    warned = [i for i, ln in enumerate(lines) if ln.startswith("⚠ ")]
    scoped = [i for i, ln in enumerate(lines) if "vert" in ln]
    assert len(warned) == 1, f"the dialog drew {lines}"
    assert scoped and warned[0] < scoped[0], f"the warning must lead the scope: {lines}"
    assert "inside-out" in lines[warned[0]], lines[warned[0]]


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
    assert line == "1 object · 3 vertices", line


def test_the_check_popup_carries_the_severity_glyphs():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    first = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    second = _bind(_part_mesh(PART_B, parts[PART_B]), arm)
    rb._snapshot_baseline([first, second], arm)
    second.scale = (-1.0, 1.0, 1.0)                                        # the warning
    _part_mesh("added_horn", parts[PART_A])                                # the blocker
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    with _captured_popups() as seen:
        _result, err = _run_op(bpy.ops.gf2.check_mesh)
    assert len(seen) == 1, f"the check popped up {len(seen)} times"
    title, lines = seen[0]
    assert title == "Check Mesh", f"the popup is titled {title!r}"
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
    assert seen == [("Check Mesh", ["Ready to send."])], f"the popup read {seen}"
    assert rb.gf2_status_line([]) == "Ready to send"


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
    assert title == "Send Blocked", f"the popup is titled {title!r}"
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
    assert "3 vertices have" in named[0], named[0]


def test_the_panel_draws_edit_rows_notices_and_the_reference_tip():
    _reset()
    _mod, ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _bind(_part_mesh(PART_B, parts[PART_B]), arm)
    _mesh("donor_body", ref)
    rb._store_session({"part": None, "notices": ["Texture table was unreadable."], "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"), stem="body1")

    panel = _FakeOp()
    bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    lines = panel.layout.lines
    assert rb.gf2_label(PART_A) in lines and rb.gf2_label(PART_B) in lines, lines
    assert "⚠ Texture table was unreadable." in lines, lines
    # The tip wraps at a width the running Blender decides, so the sentence is asserted across the
    # joined lines rather than as one exact row — the wrap point is presentation, not contract.
    assert "Reference parts are shown for context and are not sent." in " ".join(lines), lines


def test_the_panel_calls_parts_by_the_apps_session_label():
    """The app names each part's own short token in the session document; the panel shows THAT —
    a multi-token part name is exactly what no structural cut of the asset name can recover."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _bind(_part_mesh(PART_B, parts[PART_B]), arm)
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": [], "label": "P3_body_fight"},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": [], "label": "P1_cloth2"}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    panel = _FakeOp()
    bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    lines = panel.layout.lines
    assert "P3_body_fight" in lines and "P1_cloth2" in lines, lines


def test_the_panel_keeps_an_emptied_parts_destination_row():
    """A Hide still needs an existing-or-New destination, so its empty part retains a target row."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A, PART_B], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._store_session({"part": None, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []},
        {"name": PART_B, "defaultEditName": "Cloth Edit 1", "edits": []}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    panel = _FakeOp()
    bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    assert rb.gf2_label(PART_B) in panel.layout.lines, panel.layout.lines


def test_the_panel_does_not_make_a_target_row_for_an_unmarked_folder():
    _reset()
    mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _part_mesh("wip_blob", _collection("scratch stuff", mod))
    rb._store_session({"part": PART_A, "parts": [
        {"name": PART_A, "defaultEditName": "Body Edit 1", "edits": []}]})
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    panel = _FakeOp()
    bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    assert not any("scratch stuff" in line or "wip_blob" in line for line in panel.layout.lines), \
        panel.layout.lines


def test_the_panel_says_session_state_is_unreadable_exactly_once():
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))

    panel = _FakeOp()
    original = rb.load_session
    rb.load_session = lambda scene=None: (_ for _ in ()).throw(RuntimeError("scene gone"))
    try:
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    finally:
        rb.load_session = original

    lines = panel.layout.lines
    assert lines.count(rb.UNREADABLE) == 1, f"the panel drew {lines}"
    assert lines[0] == rb.UNREADABLE, f"the unreadable row must lead: {lines}"


def test_the_panel_draws_a_status_line_when_the_scene_reads():
    """draw consumes the cached status and never launches a live recomputation itself."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    _register(tempfile.mkdtemp(prefix="gf2op_"))
    rb._LIVE["text"] = "Ready to send"
    panel = _FakeOp()
    original = rb._refresh_live_status
    rb._refresh_live_status = lambda: (_ for _ in ()).throw(AssertionError("draw recomputed"))
    try:
        bpy.types.GF2_PT_panel.draw(panel, bpy.context)
    finally:
        rb._refresh_live_status = original
    assert "Ready to send" in panel.layout.lines, f"the panel drew {panel.layout.lines}"


def test_the_live_status_reflects_a_collection_level_block():
    """The cheap pass carries the attribution blocks, so the status line goes red on a misplaced
    object without waiting for a Check."""
    _reset()
    _mod, _ref, parts, arm = _layout([PART_A], armature=True)
    _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    assert rb._refresh_live_status() == "Ready to send", rb._refresh_live_status()
    _mesh("dropped_here")                     # scene root: in neither tree
    assert rb._refresh_live_status() == "1 blocking issue — click Check Mesh for details", \
        rb._refresh_live_status()


def test_the_live_status_pairs_the_session_armature():
    """Never `arms[0]` of every armature: a donor rig in Reference would read as the skeleton and
    report the whole imported bone set as renamed."""
    _reset()
    _mod, ref, parts, arm = _layout([PART_A], armature=True)
    mo = _bind(_part_mesh(PART_A, parts[PART_A]), arm)
    rb._snapshot_baseline([mo], arm)
    _armature("AAA_donor_rig", coll=ref, bone="donor_root")
    assert rb._refresh_live_status() == "Ready to send", rb._refresh_live_status()


# ---------------------------------------------------------------- the headless round trip

def test_blended_preview_setup_is_scoped_remapped_and_idempotent():
    """The post-import helper changes only the new BLENDED material it is handed. Its Blender-only
    overlap/culling settings and exact 254 ceiling graph are pinned against real bpy, while a pre-existing
    shared material and a non-BLENDED imported material prove the scope and class guards. A node squatting
    on the display name proves the bridge's custom-property identity remains idempotent."""
    _reset()

    def alpha_material(name, method):
        material = bpy.data.materials.new(name)
        material.use_nodes = True
        material.surface_render_method = method
        material.use_transparency_overlap = True
        material.use_backface_culling = True
        tree = material.node_tree
        tree.nodes.clear()
        texture = tree.nodes.new("ShaderNodeTexImage")
        texture.name = name + " base"
        principled = tree.nodes.new("ShaderNodeBsdfPrincipled")
        output = tree.nodes.new("ShaderNodeOutputMaterial")
        tree.links.new(texture.outputs["Color"], principled.inputs["Base Color"])
        tree.links.new(texture.outputs["Alpha"], principled.inputs["Alpha"])
        tree.links.new(principled.outputs["BSDF"], output.inputs["Surface"])
        return material, texture, principled

    blended, texture, principled = alpha_material("new blended", "BLENDED")
    squatter = blended.node_tree.nodes.new("ShaderNodeMapRange")
    squatter.name = rb.ALPHA_REMAP_NODE
    opaque, _opaque_texture, _opaque_principled = alpha_material("new opaque", "DITHERED")
    existing, _existing_texture, _existing_principled = alpha_material("user material", "BLENDED")
    imported = _mesh("imported")
    imported.data.materials.append(blended)
    imported.data.materials.append(opaque)
    imported.data.materials.append(existing)  # sharing an old datablock must not broaden the scope
    _mesh("user object").data.materials.append(existing)

    got = rb.gf2_prepare_imported_alpha_materials([imported], [existing])
    assert got == {"blended": 1, "remapped": 1, "missing_overlap": (),
                   "missing_alpha_link": ()}, got
    assert blended.surface_render_method == "BLENDED"
    assert blended.use_transparency_overlap is False
    assert blended.use_backface_culling is False
    # 4.3 and 5.1 retain the 4.0/4.1 property names as live aliases. Their agreement pins that the
    # legacy selector/fallback describes the same render state on every project-supported version.
    assert blended.blend_method == "BLEND"
    assert blended.show_transparent_back is False
    remap = next((node for node in blended.node_tree.nodes if node.get(rb.ALPHA_REMAP_TAG)), None)
    assert remap is not None and remap.bl_idname == "ShaderNodeMapRange"
    assert remap.name != rb.ALPHA_REMAP_NODE, "the squatter should force Blender to mint a suffixed name"
    assert remap.clamp is True
    assert remap.inputs["From Min"].default_value == 0.0
    assert abs(remap.inputs["From Max"].default_value - 254.0 / 255.0) < 1e-7
    assert remap.inputs["To Min"].default_value == 0.0
    assert remap.inputs["To Max"].default_value == 1.0
    assert principled.inputs["Alpha"].links[0].from_node == remap
    assert remap.inputs["Value"].links[0].from_node == texture
    assert remap.inputs["Value"].links[0].from_socket.name == "Alpha"

    # Neither a different imported class nor a material owned before the import is touched.
    assert opaque.use_transparency_overlap is True and opaque.use_backface_culling is True
    assert opaque.node_tree.nodes.get(rb.ALPHA_REMAP_NODE) is None
    assert existing.use_transparency_overlap is True and existing.use_backface_culling is True
    assert existing.node_tree.nodes.get(rb.ALPHA_REMAP_NODE) is None

    second = rb.gf2_prepare_imported_alpha_materials([imported], [existing])
    assert second["remapped"] == 1 and second["missing_alpha_link"] == (), second
    assert len([node for node in blended.node_tree.nodes if node.get(rb.ALPHA_REMAP_TAG)]) == 1

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
        assert _sent_sidecar(out, "combined")["editIds"] == {}, \
            "a sessionless round trip stamped synthetic New targets"


def test_higher_uv_sets_survive_the_real_bpy_round_trip_in_numeric_order():
    """The app's TEXCOORD_0/1/2 become Blender UV layers 0/1/2 and export back under the same numeric
    semantics. Distinct tiled values make a compacted, swapped or UV0-copied layer visible."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "uvs.glb"), uv_sets=3)
        source_attrs = _glb_json(src)["meshes"][0]["primitives"][0]["attributes"]
        assert all(f"TEXCOORD_{i}" in source_attrs for i in range(3)), source_attrs

        _reset()
        imported, _arms = rb.gf2_import(src)
        expected = [[tuple(round(v, 6) for v in uv) for uv in layer]
                    for layer in (((0.0, 0.0), (1.0, 0.0), (0.0, 1.0)),
                                  ((2.0, 3.0), (4.0, 3.0), (2.0, 5.0)),
                                  ((-1.0, 8.0), (0.0, 8.0), (-1.0, 9.0)))]
        assert _uv_sets(imported[0]) == expected, _uv_sets(imported[0])

        out, _sent = _send("uvs-return")
        returned_path = os.path.join(out, "uvs-return.glb")
        attrs = _glb_json(returned_path)["meshes"][0]["primitives"][0]["attributes"]
        assert all(f"TEXCOORD_{i}" in attrs for i in range(3)), attrs

        _reset()
        returned, _arms = rb.gf2_import(returned_path)
        assert _uv_sets(returned[0]) == expected, _uv_sets(returned[0])


def test_property_keyed_images_survive_the_real_bpy_round_trip():
    """Effect and generic bindings share one stock resource but remain two tagged Blender images. Neither
    is wired into a made-up game-shader graph; both return through the exact extras identity, including the
    reserved future parameter container."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "combined.glb"))
        image = bpy.data.images.new("transport fixture", width=2, height=2, alpha=True)
        image.pixels = [0.2, 0.4, 0.6, 1.0] * 4
        png = rb._gf2_image_png(image)
        bpy.data.images.remove(image)
        stock = {"name": "shared", "bundle": "bundle", "path_id": 71}
        rows = []
        for prop, semantic in (("_BlendTex", "blend"), ("_TurbulenceTex", "texture")):
            rows.append({
                "owner": {"mesh": PART_A, "material": 0, "primitive": 0},
                "property": prop,
                "semantic": semantic,
                "stock": stock,
                "srgb": True,
                "origin": "vanilla",
                "parameters": {"floats": {"future": 2.0}, "keywords": ["FUTURE"]},
                "png": png,
                "image_name": prop,
            })
        rb._gf2_append_texture_transport(src, rows)
        out = os.path.join(d, "sent")
        result = subprocess.run([bpy.app.binary_path, "--background", "--factory-startup",
                                 "--python", BRIDGE, "--", src, "--send-to", out],
                                capture_output=True, text=True)
        assert result.returncode == 0, f"the property transport failed:\n{result.stdout}\n{result.stderr}"

        sent = [os.path.join(out, name) for name in os.listdir(out) if name.lower().endswith(".glb")]
        assert len(sent) == 1, f"the send wrote these files: {os.listdir(out)}\n{result.stdout}"
        returned = rb._gf2_read_texture_transport(sent[0])
        assert [row["property"] for row in returned] == ["_BlendTex", "_TurbulenceTex"], returned
        assert all(row["stock"]["path_id"] == 71 for row in returned), returned
        assert all(row["parameters"]["keywords"] == ["FUTURE"] for row in returned), returned


def test_explicit_texture_coordinates_pin_images_without_wiring_effect_shading():
    """Known bindings get UV Map inputs by imported layer order. Effect receives coordinates only;
    generic properties remain unpinned, and all three records survive Send unchanged."""
    with tempfile.TemporaryDirectory() as d:
        src = _build_source_glb([PART_A], os.path.join(d, "combined.glb"), uv_sets=2)
        image = bpy.data.images.new("coordinate fixture", width=2, height=2, alpha=True)
        image.pixels = [0.2, 0.4, 0.6, 1.0] * 4
        png = rb._gf2_image_png(image)
        bpy.data.images.remove(image)
        stock = {"name": "shared", "bundle": "bundle", "path_id": 71}
        rows = []
        for prop, semantic, tex_coord in (
                ("_BaseMap", "baseColor", 0), ("_BlendTex", "blend", 1),
                ("_TurbulenceTex", "texture", None)):
            row = {
                "owner": {"mesh": PART_A, "material": 0, "primitive": 0},
                "property": prop,
                "semantic": semantic,
                "stock": stock,
                "srgb": True,
                "origin": "vanilla",
                "png": png,
                "image_name": prop,
            }
            if tex_coord is not None:
                row["texCoord"] = tex_coord
            rows.append(row)
        rb._gf2_append_texture_transport(src, rows)

        _reset()
        imported, _arms = rb.gf2_import(src)
        mesh = imported[0]
        material = mesh.data.materials[0]
        tagged = {}
        for node in material.node_tree.nodes:
            raw = node.get(rb.TEXTURE_TRANSPORT_NODE) if hasattr(node, "get") else None
            if isinstance(raw, str):
                tagged[json.loads(raw)["property"]] = node

        assert set(tagged) == {"_BaseMap", "_BlendTex", "_TurbulenceTex"}, tagged
        for prop, index in (("_BaseMap", 0), ("_BlendTex", 1)):
            links = tagged[prop].inputs["Vector"].links
            assert len(links) == 1, f"{prop} has {len(links)} Vector links"
            uv_map = links[0].from_node
            assert uv_map.bl_idname == "ShaderNodeUVMap", uv_map.bl_idname
            assert uv_map.get(rb.TEXTURE_TRANSPORT_UV_NODE) == index, uv_map.items()
            assert uv_map.uv_map == mesh.data.uv_layers[index].name, (uv_map.uv_map, index)
        assert not tagged["_TurbulenceTex"].inputs["Vector"].links, "generic texture was guessed"
        assert all(not output.links for output in tagged["_BlendTex"].outputs), \
            "Effect was connected into invented shading"
        principled = next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
        assert principled.inputs["Base Color"].links[0].from_node == tagged["_BaseMap"], \
            "the tagged base-color preview is not connected"

        out, _sent = _send("coordinate-return")
        returned = rb._gf2_read_texture_transport(os.path.join(out, "coordinate-return.glb"))
        assert [(row["property"], row.get("texCoord")) for row in returned] == [
            ("_BaseMap", 0), ("_BlendTex", 1), ("_TurbulenceTex", None)], returned


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
        sidecar = _sent_sidecar(out, "_combined")
        assert sidecar["hiddenParts"] == [], \
            "a context part must not read as an emptied one"
        assert sidecar["editIds"] == {PART_B: {"new": "Edit 1"}}, sidecar


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
