"""Doll Remolding Lab — Blender bridge.

Launched with the mesh to edit:

    blender --python remold_bridge.py -- <mesh.glb> <send_dir>

It imports the mesh, sets up a clean viewport, and registers a **"Doll Remolding Lab"** panel in
the N-panel sidebar (press N) with **Check mesh** and **Send to Lab** buttons. The modder edits,
optionally runs Check, and clicks Send — the app's file watcher picks up the exported `.glb` (+ a
`.gf2send.json` write-complete sidecar). The panel reports the scene it is looking at and a live
status line. A first Send just goes; a Send that would replace an existing edit asks first, and
every successful Send confirms with a popup.

The scene is laid out so that attribution is visible in the outliner and survives a save:

    Scene Collection
    ├─ Mod
    │  ├─ <part>          one collection per part the session may write
    │  └─ Armature
    └─ Reference

A mesh's part is the **collection it sits in**, not its name: Send exports each shipping mesh under
its part collection's name, so an object moved into another part collection ships as THAT part. One
mesh per part collection — the pipeline compiles exactly one mesh per part. Send exports the marked
part collections only; a folder the modder makes under `Mod` is not a part, and `Reference` is
scenery (donor bodies, scale references, projection proxies, donor rigs) that never ships. An object
linked into both a part and `Reference` ships (`Mod` wins). The session armature is the one in the
`Mod` tree; an armature in `Reference`, or one the scene already carried before the import, is
neither exported nor consulted. Hiding an object is working state and changes nothing about the
export.

Every glb carries the WHOLE outfit on ONE armature, so a weight can be painted onto any bone of the
character with the rest of the outfit visible around the edit. Which of those meshes the session may
write is the app's call, read from a `<glb stem>.gf2session.json` file beside the glb: the named part
gets a part collection under `Mod`, and every other mesh lands in `Reference` as context. Without that
file every mesh is a part, which is what the headless round trip and a hand-opened glb want. The file
also says which parts already hold an edit in the app, so the Send confirmation can say what a send
would replace.

Deleting a part's mesh is how a part is hidden in the built mod. The send never infers that from an
absent mesh — under the one-part-per-session layout most of the outfit is absent by design — so an
emptied part collection is named explicitly in the send sidecar's `hiddenParts`, and its workspace
file is left exactly as it was. A session that names one part holds exactly one part collection, so
emptying it is the only way to hide that part: the send then carries no mesh at all, and the rest of
the mod still ships from its own files. Emptying everything in a session that names NO part is an
empty deliverable instead, and blocks.

Before an export, a sanity pass runs (also on demand via **Check mesh**): it BLOCKS the Send on
problems that would break the deliverable — a mesh with no part, a part holding more than one mesh, a
part collection excluded from the view layer or squatted out of its own name, vertices no skeleton
weight can reach, or a weighted scene with no armature — and WARNS (without blocking) on likely
mistakes: an Object-mode scale, a reordered material slot, or a renamed/removed bone. Everything but
the weight solve is cheap enough to drive the panel's live status line. On Send, any unweighted
vertices are bone-heat filled from the skeleton (authored weights are always preserved); the app then
compiles the authored skin onto the target. A part the app declares unskinned — a static prop, which
ships no weights and opens with no armature — is outside all of that.

The modder never touches glTF export settings: Send always exports with tangents, skinning, and
normals ON. The outline channel is NOT carried through Blender — it is re-baked at package time
from the finished mesh, so there is no custom outline attribute to keep.

Headless / CI use (also the no-GUI fallback): add `--send-to <dir>` to import and immediately
re-export with no edits — the no-op round-trip that verifies the pipeline:

    blender --background --python remold_bridge.py -- <mesh.glb> --send-to <dir>
"""
import bpy
import sys
import os
import json
import re
import time


# ---------------------------------------------------------------- collections carry the part

# Attribution is the collection an object sits in. `Mod` scopes what a Send exports; `Reference` holds
# scenery — donor bodies, scale references, projection proxies — and never ships.
MOD_COLLECTION = "Mod"
REFERENCE_COLLECTION = "Reference"
ARMATURE_COLLECTION = "Armature"

# Marks a collection as a part, so a folder the modder makes under `Mod` is not silently a shipping
# part. Set at import; it rides along in the .blend.
PART_MARKER = "gf2_part"

# The app's description of the session, written beside the glb it launches on: which mesh this session
# may write back, and which parts already hold an edit. Absent = every mesh in the glb is a part.
SESSION_SUFFIX = ".gf2session.json"

# Where the session description is kept once read, so the Send confirmation can reach it after a save.
SESSION_KEY = "gf2_session"

# The parts this session has already sent. The app's description is a launch-time snapshot, so this is what
# lets a second Send warn about the part the first one wrote.
SENT_KEY = "gf2_sent"

# Panel rows whose text is the same every time they appear.
REFERENCE_NOTE = "Reference never ships."
UNREADABLE = "⚠ Scene state unreadable"

# The two empty-scope refusals, shared by the checks and the send choke point so a scene the send
# would refuse never reads as ready. Nothing attributed is a layout mistake; every part deliberately
# emptied is a Hide with nothing left to send alongside it.
NO_PART_MESH = ("No mesh in a part collection, so a send would carry no geometry. Move the parts "
                f"into their collections under {MOD_COLLECTION}.")
EVERY_PART_EMPTY = "Every part is empty, so a send would carry no geometry. Keep one part's mesh."


def _ensure_collection(name, parent):
    """The `name` child of `parent`, created and linked when it isn't there yet.

    `bpy.data.collections.new` resolves a name already taken ANYWHERE in the file by appending
    `.001` rather than failing, which for a part collection means the part ships under a name the
    app has no target for. The created collection is still marked and used — a half-built layout is
    worse — but the mismatch is printed here and blocked by the part-name check."""
    child = parent.children.get(name)
    if child is None:
        child = bpy.data.collections.new(name)
        if child.name != name:
            print(f"GF2: '{name}' is taken by another collection, so this one is '{child.name}' "
                  "and will not match the part")
        parent.children.link(child)
    return child


def _move_to_collection(obj, coll):
    """Link obj into `coll` and NOTHING else. Membership is the attribution, so an object left
    linked into a second collection would have two parts."""
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    coll.objects.link(obj)


def _layer_collection_for(coll):
    """The view layer's entry for `coll`, or None when the layer doesn't carry it. An EXCLUDED
    collection still has an entry (with `exclude` set), which is how exclusion is detected."""
    def find(layer_coll):
        if layer_coll.collection is coll:
            return layer_coll
        for c in layer_coll.children:
            hit = find(c)
            if hit is not None:
                return hit
        return None
    try:
        return find(bpy.context.view_layer.layer_collection)
    except Exception:
        return None


def _is_excluded(coll):
    """True when `coll` is unticked in the outliner. Its objects are not in the view layer, so they
    can be neither selected nor exported."""
    lc = _layer_collection_for(coll)
    return bool(lc is not None and lc.exclude)


def _activate_collection(coll):
    """Point the view layer's active collection at `coll`, so geometry the modder adds is linked
    there. Advisory: when it can't be set, new geometry lands at the scene root, which the
    attribution checks report rather than ship."""
    try:
        hit = _layer_collection_for(coll)
        if hit is not None:
            bpy.context.view_layer.active_layer_collection = hit
    except Exception as e:
        print(f"GF2: could not set the active collection: {e}")


def _mod_root():
    """The `Mod` collection — the export scope. None when the scene has no such collection, e.g. a
    .blend that never went through this import."""
    return bpy.context.scene.collection.children.get(MOD_COLLECTION)


def _reference_root():
    """The `Reference` collection: scenery. Nothing in this tree ever ships."""
    return bpy.context.scene.collection.children.get(REFERENCE_COLLECTION)


def gf2_part_collections():
    """The part collections: the children of `Mod` the import marked as parts. Each one IS a part;
    the one mesh in it, at any depth, ships as that part. A collection the modder made under `Mod`
    carries no marker and is not a part, so its meshes report as unattributed instead of shipping
    under a name the app has no target for."""
    mod = _mod_root()
    return [] if mod is None else [c for c in mod.children if c.get(PART_MARKER)]


def _session_armature():
    """THE armature of this session: the one in the `Mod` tree, preferring its `Armature` collection.
    An armature in `Reference`, or one the scene already carried before the import, is scenery — never
    the bone-heat source, never checked against the imported skeleton, never exported. None when the
    `Mod` tree carries no armature."""
    mod = _mod_root()
    if mod is None:
        return None
    arm_coll = mod.children.get(ARMATURE_COLLECTION)
    pools = ([arm_coll.all_objects] if arm_coll is not None else []) + [mod.all_objects]
    for pool in pools:
        arms = sorted((o for o in pool if o.type == "ARMATURE"), key=lambda o: o.name)
        if arms:
            return arms[0]
    return None


def gf2_shipping_meshes():
    """The mesh objects a Send exports: every mesh in a marked part collection, at any depth.

    Scoped to the part collections and not the whole `Mod` tree so the choke point agrees with the
    checks: a mesh in a folder the modder made under `Mod` is blocked by the attribution check, and
    would otherwise still have exported on the checkless headless path. Name-sorted, and an object
    linked into two parts appears once, so the export scope and the messages about it are stable."""
    found = {}
    for part in gf2_part_collections():
        for o in part.all_objects:
            if o.type == "MESH":
                found[o.name] = o
    return [found[n] for n in sorted(found)]


def _in_tree(obj, root):
    return root is not None and root.all_objects.get(obj.name) is obj


def _part_of(obj):
    """The part collection obj ships as, or None when it is in no part collection."""
    for c in gf2_part_collections():
        if c.all_objects.get(obj.name) is obj:
            return c
    return None


def _part_meshes(part):
    """The mesh objects a part collection holds, at the same depth `gf2_shipping_meshes` reads."""
    return sorted((o for o in part.all_objects if o.type == "MESH"), key=lambda o: o.name)


def gf2_emptied_parts():
    """The part collections holding no mesh. Deleting a part's mesh is how a part is hidden in the
    built mod, so these are named in the send sidecar rather than inferred from what the glb lacks —
    every other part of the outfit is absent from a send by design."""
    return [p.name for p in gf2_part_collections() if not _part_meshes(p)]


def gf2_overwrite_warning(names):
    """What a send would replace, or None when it replaces nothing. Only a part the app already holds
    an edit for can lose work to a send; a first send has nothing to say."""
    if not names:
        return None
    labels = ", ".join(gf2_label(n) for n in names)
    return (f"{labels} already carries an edit. Sending replaces it." if len(names) == 1
            else f"{labels} already carry edits. Sending replaces them.")


def gf2_edited_shipping_parts(session, shipping_part_names, sent=()):
    """The parts this send would overwrite that already hold an edit. Scoped to what actually ships: an
    emptied part's file is never written, so it has nothing to lose.

    The app's session description is a snapshot taken when Blender opened, so on its own it would leave a
    second send in the same session silent about the part the first one wrote. `sent` closes that: a part
    this session has already sent carries an edit now, whatever the app knew at launch."""
    edited = {p.get("name") for p in (session.get("parts") or []) if p.get("edited")}
    edited.update(sent)
    return [n for n in shipping_part_names if n in edited]


def gf2_emptying_is_a_hide(session):
    """Is emptying every part collection of THIS session a deliberate Hide, or an empty deliverable?

    A session that names one part of an outfit holds exactly one part collection, so emptying that part is
    the only way to say "hide it" — and the mod still ships its other parts from their own files. A session
    that names no part is an open-all: emptying everything there leaves nothing to build. So is a named-part
    session the app described as the whole mod, where the hidden part is all there was."""
    part = (session or {}).get("part")
    if not part:
        return False
    return any(p.get("name") != part for p in ((session or {}).get("parts") or []))


def _base_name(name):
    """A name with Blender's duplicate suffix dropped: `body.001` -> `body`. A duplicate object is
    still the part it was duplicated from, so the name check must not read it as a mismatch; and a
    collection that had to take a suffix wanted the base name, which is what the part-name check
    reports as squatted."""
    stem, dot, tail = name.rpartition(".")
    return stem if dot and stem and tail.isdigit() else name


def session_path(glb_path):
    """The app's session description for a glb: `<stem>.gf2session.json` beside it."""
    return os.path.splitext(glb_path)[0] + SESSION_SUFFIX


def read_session(glb_path):
    """The session description, or an empty one when the app wrote none (a hand-opened glb, the
    headless round trip). An unreadable file reads as empty too — the fallback is "every mesh is a
    part", which blocks nothing and ships what the glb carries."""
    empty = {"part": None, "parts": []}
    if not glb_path:
        return empty
    path = session_path(glb_path)
    try:
        with open(path, "r", encoding="utf-8") as f:
            got = json.load(f)
    except Exception:
        return empty
    if not isinstance(got, dict):
        return empty
    got.setdefault("part", None)
    got.setdefault("parts", [])
    return got


def _store_session(session):
    try:
        bpy.context.scene[SESSION_KEY] = json.dumps(session)
    except Exception as e:
        print(f"GF2: could not record the session description: {e}")


def load_session():
    """The session description recorded at import. Empty when the scene never carried one."""
    raw = bpy.context.scene.get(SESSION_KEY)
    if not raw:
        return {"part": None, "parts": []}
    try:
        return json.loads(raw)
    except Exception:
        return {"part": None, "parts": []}


def gf2_sent_parts():
    """The parts THIS session has already sent. Kept on the scene so it survives a save, and so the Send
    confirmation describes what is true now rather than what the app knew when Blender opened."""
    raw = bpy.context.scene.get(SENT_KEY)
    if not raw:
        return []
    try:
        got = json.loads(raw)
    except Exception:
        return []
    return [n for n in got if isinstance(n, str)] if isinstance(got, list) else []


def _record_sent(names):
    """Add the parts a completed send wrote to the in-session sent list."""
    try:
        bpy.context.scene[SENT_KEY] = json.dumps(sorted(set(gf2_sent_parts()) | set(names)))
    except Exception as e:
        print(f"GF2: could not record what this send wrote: {e}")


def gf2_session_parts(session, mesh_names):
    """Which of the imported meshes get a part collection: the one the app named, or all of them when
    it named none, minus any the app declared unwritable. A name the glb doesn't carry yields nothing,
    so the attribution checks report an empty `Mod` rather than the session quietly widening to the
    whole outfit. A part entry with no `writable` key is writable: a hand-opened glb has no session
    description at all, and every mesh in it is a part."""
    want = session.get("part")
    if want:
        return [n for n in mesh_names if n == want]
    blocked = {p.get("name") for p in (session.get("parts") or [])
               if isinstance(p, dict) and p.get("writable") is False}
    return [n for n in mesh_names if n not in blocked]


def gf2_unskinned_parts(session):
    """The part names the app declared unskinned — a static-renderer prop's mesh, which carries no
    weights and whose session has no armature. Their vertices are outside the weight gate: counting
    them as unweighted would block every send of such a part. Only an EXPLICIT true exempts a part, so
    a skinned part whose armature the modder deleted still blocks, and a session the app didn't stamp
    reads as fully skinned."""
    return {p.get("name") for p in (session.get("parts") or [])
            if isinstance(p, dict) and p.get("unskinned") is True and isinstance(p.get("name"), str)}


def _is_unskinned(obj, unskinned):
    """Whether this object ships as one of the declared unskinned parts. The exemption belongs to the
    PART, and a part is a collection — so the collection the object ships in is what answers, never the
    object's own name. A mesh someone named after a static prop, sitting inside a skinned part, is that
    part's and stays inside the weight gate. A duplicate's `.001` suffix is dropped: a collection that
    had to take a suffix is still the part it was duplicated from. An object in no part collection
    ships as nothing and is exempt from nothing."""
    part = _part_of(obj)
    return part is not None and (part.name in unskinned or _base_name(part.name) in unskinned)


def gf2_build_collections(meshes, armature, session=None):
    """Lay out the scene so each part's attribution is a collection: one collection per part under
    `Mod`, the armature in its own, and a `Reference` collection holding the rest of the outfit and
    any scenery.

    The active collection is left on the single part when the session has one and on `Mod` when it
    has several, so geometry the modder adds lands attributed where that is unambiguous and
    unattributed — never MIS-attributed — where it isn't."""
    scene_root = bpy.context.scene.collection
    mod = _ensure_collection(MOD_COLLECTION, scene_root)
    reference = _ensure_collection(REFERENCE_COLLECTION, scene_root)
    writable = set(gf2_session_parts(session or {}, [mo.name for mo in meshes]))
    parts = []
    for mo in meshes:
        if mo.name not in writable:
            _move_to_collection(mo, reference)   # context: never shipped
            _set_hidden(mo, True)                # hidden by default. Unhide in the outliner to edit against.
            continue
        part = _ensure_collection(mo.name, mod)
        part[PART_MARKER] = mo.name   # the marker carries the part's contract name; a rename is detectable
        _move_to_collection(mo, part)
        parts.append(part)
    if armature is not None:
        _move_to_collection(armature, _ensure_collection(ARMATURE_COLLECTION, mod))
    _activate_collection(parts[0] if len(parts) == 1 else mod)


# ---------------------------------------------------------------- core import/export

def gf2_import(glb_path):
    """Import an exported mesh .glb and set up a clean, edit-ready viewport.

    The glb carries the whole outfit on one armature; the app's session description decides which of
    its meshes this session may write back. The rest become `Reference` context."""
    # snapshot the empties that already existed so cleanup only touches ones THIS import created —
    # deleting every empty indiscriminately would nuke a user-added reference/pivot empty on a re-import
    pre_existing_empties = {o.name for o in bpy.data.objects if o.type == "EMPTY"}
    # and the meshes and armatures, for the mirror reason: Blender starts on the user's startup .blend,
    # so a prop or a rig already sitting in that scene must not be handed a part collection, moved into
    # Mod/Armature, or become the session skeleton. Left where they are, the attribution checks report
    # the meshes and the armature is simply scenery.
    pre_existing_meshes = {o.name for o in bpy.data.objects if o.type == "MESH"}
    pre_existing_arms = {o.name for o in bpy.data.objects if o.type == "ARMATURE"}

    # FORTUNE points each bone's tip at its child = a readable connected skeleton (re-export keys
    # off the bone hash in the node name, not orientation, so this is display-only and safe).
    try:
        bpy.ops.import_scene.gltf(filepath=glb_path, bone_heuristic="FORTUNE")
    except TypeError:
        bpy.ops.import_scene.gltf(filepath=glb_path)

    # drop Blender's startup objects + the importer's placeholder junk so only the character shows
    for nm in ("Cube", "Camera", "Light"):
        o = bpy.data.objects.get(nm)
        if o is not None:
            bpy.data.objects.remove(o, do_unlink=True)
    # remove only the placeholder empties THIS import created (glTF scene/node placeholders with no
    # children) + the importer's icosphere junk — never a pre-existing or child-bearing empty, so a
    # user's own empty and any transform-holding scene root survive
    for o in list(bpy.data.objects):
        if o.name.startswith("Icosphere"):
            bpy.data.objects.remove(o, do_unlink=True)
        elif (o.type == "EMPTY" and o.name not in pre_existing_empties and not o.children):
            bpy.data.objects.remove(o, do_unlink=True)

    meshes = [o for o in bpy.data.objects if o.type == "MESH" and o.name not in pre_existing_meshes]
    arms = [o for o in bpy.data.objects
            if o.type == "ARMATURE" and o.name not in pre_existing_arms]
    try:
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        pass
    for o in bpy.data.objects:
        o.select_set(False)
    if arms:
        a = arms[0]
        a.data.display_type = "STICK"   # thin bones, not giant octahedra
        a.show_in_front = True          # clickable through the mesh for weight painting
        a.hide_set(True)                # hidden by default -> clean mesh view; deform still applies
        _demote_non_game_bones(a)
    session = read_session(glb_path)
    _store_session(session)
    gf2_build_collections(meshes, arms[0] if arms else None, session)
    # Select the session's own part(s) only, after attribution: Reference context imports hidden,
    # and the initial framing should centre on what this session edits.
    shipping = [mo for mo in meshes
                if mo.name in set(gf2_session_parts(session, [m.name for m in meshes]))]
    for mo in shipping:
        mo.select_set(True)
    if shipping:
        bpy.context.view_layer.objects.active = shipping[0]
    _snapshot_baseline(meshes, _session_armature())
    return meshes, arms


_BONE_HASH = re.compile(r"_[0-9a-fA-F]{8}(?:\.\d+)?$")


def _demote_non_game_bones(arm_obj):
    """Mark hierarchy glue non-deform. The armature carries nodes that are not game bones —
    connector prefixes, wrapper roots — which import as bones but have no ``_<hash8>`` identity, so
    a weight painted onto one can never ship (the send-back refuses it by name). Non-deform bones
    are skipped by Automatic Weights, which is where such weights actually come from. Only demote
    when the rig carries hash-named bones at all: a hand-built or test rig with no game names keeps
    deforming as-is."""
    bones = getattr(arm_obj.data, "bones", None) or ()
    if not any(_BONE_HASH.search(b.name) for b in bones):
        return
    for b in bones:
        if not _BONE_HASH.search(b.name):
            b.use_deform = False


def _setup_viewport_ui():
    """Open the N-panel sidebar on the GF2 tab and frame the mesh, so the Send button is visible and
    the model is on-screen the moment Blender opens (the modder shouldn't have to hunt for either)."""
    screen = getattr(bpy.context, "screen", None)
    if screen is None:
        return
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        space = area.spaces.active
        space.show_region_ui = True        # open the N-panel sidebar (otherwise it's hidden)
        for region in area.regions:
            if region.type == "UI":
                try:
                    region.active_panel_category = "Doll Remolding Lab"   # land on our tab, not "Item"
                except Exception:
                    pass
            if region.type == "WINDOW":
                try:
                    with bpy.context.temp_override(area=area, region=region):
                        bpy.ops.view3d.view_selected()                # frame the imported mesh
                except Exception:
                    pass
        break


def _vertex_total_weight(obj, vi):
    """Sum of a vertex's weights across every vertex group (its total skin influence)."""
    total = 0.0
    for g in obj.data.vertices[vi].groups:
        total += g.weight
    return total


def _missing_verts(obj):
    """Indices of vertices whose total skin influence is (near-)zero — the ones that need filling."""
    return [v.index for v in obj.data.vertices if _vertex_total_weight(obj, v.index) < 1e-4]


# True while the weight solve's throwaway duplicate exists. The duplicate lives at the scene root and
# fires depsgraph updates, so without this the live status handler reads it as a blocking stray.
_SOLVING = False


def _solve_missing_weights(obj, armature):
    """Bone-heat the unweighted vertices of ONE mesh on a throwaway DUPLICATE and read back what the
    skeleton could solve — WITHOUT ever mutating obj. Returns (missing, solved) where missing is the list
    of unweighted vertex indices and solved maps vi -> [(group_name, weight), ...] for those the skeleton
    gave positive weight. Shared by the auto-fill (which writes solved back) and the pre-send check (which
    only counts), so both agree to the vertex on what is fillable."""
    missing = _missing_verts(obj)
    solved = {}
    if not missing or armature is None:
        return missing, solved
    was_hidden = armature.hide_get()
    armature.hide_set(False)          # select_set / parent_set are silently ignored on a hidden armature
    global _SOLVING
    _SOLVING = True
    dup = obj.copy()
    dup.data = obj.data.copy()
    bpy.context.scene.collection.objects.link(dup)
    try:
        for o in bpy.data.objects:
            o.select_set(False)
        dup.select_set(True)
        armature.select_set(True)
        bpy.context.view_layer.objects.active = armature
        try:
            # ARMATURE_AUTO = bone-heat: recompute vertex groups on the duplicate from the skeleton
            bpy.ops.object.parent_set(type="ARMATURE_AUTO")
        except RuntimeError:
            return missing, solved     # bone-heat couldn't solve this mesh — all missing count as unfillable
        for vg_dup in dup.vertex_groups:
            for vi in missing:
                try:
                    w = vg_dup.weight(vi)
                except RuntimeError:
                    continue           # this vertex isn't in this group
                if w > 0.0:
                    solved.setdefault(vi, []).append((vg_dup.name, w))
    finally:
        bpy.data.objects.remove(dup, do_unlink=True)
        armature.hide_set(was_hidden)
        _SOLVING = False
    return missing, solved


def gf2_fill_missing_weights(mesh_objs, armature, unskinned=()):
    """AUTO mode: give every vertex with (near-)zero total weight a skeleton-derived weight, WITHOUT
    touching the weights of vertices that are already skinned (preserve-then-fill). The bone-heat runs on
    a throwaway duplicate (see _solve_missing_weights) so a wild result can never overwrite good authored
    weights — we copy solved weights back ONLY for the previously-unweighted vertices. Vertices bone-heat
    still can't solve stay unweighted; they are flagged rather than shipped as a guess. Returns
    (filled, still_unweighted) vertex counts summed across parts.

    `unskinned` names the parts the app declared unskinned (see gf2_unskinned_parts); they ship without
    weights, so there is nothing to fill and nothing to report."""
    mesh_objs = [o for o in mesh_objs if not _is_unskinned(o, unskinned)]
    if armature is None:
        return 0, sum(len(_missing_verts(o)) for o in mesh_objs)
    filled = still = 0
    for obj in mesh_objs:
        missing, solved = _solve_missing_weights(obj, armature)
        if not missing:
            continue
        for vi, pairs in solved.items():
            for name, w in pairs:
                vg = obj.vertex_groups.get(name) or obj.vertex_groups.new(name=name)
                vg.add([vi], w, "REPLACE")
        obj.data.update()
        for vi in missing:
            if _vertex_total_weight(obj, vi) >= 1e-4:
                filled += 1
            else:
                still += 1
    return filled, still


def _unsolvable_weights_by_object(mesh_objs, armature, unskinned=()):
    """Pre-send DRY count, PER MESH, of vertices that would remain unweighted even after bone-heat —
    i.e. exactly what gf2_fill_missing_weights would report as `still`, computed without mutating
    anything and attributed so the block can name what to fix. With no armature every unweighted
    vertex is unsolvable. A part the app declared unskinned (`unskinned`) is outside the question
    entirely, exactly as it is for the fill. Returns [(object name, count), ...] for the meshes that
    have any."""
    counts = []
    for obj in mesh_objs:
        if _is_unskinned(obj, unskinned):
            continue
        if armature is None:
            n = len(_missing_verts(obj))
        else:
            missing, solved = _solve_missing_weights(obj, armature)
            n = sum(1 for vi in missing if sum(w for _, w in solved.get(vi, ())) < 1e-4)
        if n:
            counts.append((obj.name, n))
    return counts


_PARK_PREFIX = "gf2_export_park_"


def _apply_export_names(mesh_objs):
    """Rename each shipping mesh's DATA block to its part collection's name, so the written glb names
    every mesh for the part it ships as. glTF takes a mesh's name from the data block (the node's from
    the object), and the app re-splits the returned glb by mesh name — so this is what makes the
    collection, not the object name, decide what a mesh comes back as.

    Returns the (data block, original name) pairs `_restore_export_names` needs. Renaming runs in two
    passes over EVERY mesh data block, parking them all on a throwaway name first: Blender resolves a
    name collision by appending `.001` rather than failing, so a direct rename would quietly mis-name
    a mesh whenever two blocks swap names. A non-shipping block whose own name is wanted by a part
    stays parked for the duration; it isn't in the export."""
    saved = [(me, me.name) for me in bpy.data.meshes]
    wanted = {}
    for o in mesh_objs:
        part = _part_of(o)
        if part is not None:
            wanted[o.data] = part.name
    for i, (me, _) in enumerate(saved):
        me.name = f"{_PARK_PREFIX}{i}"
    for me, name in wanted.items():
        me.name = name
    taken = set(wanted.values())
    for me, name in saved:
        if me not in wanted and name not in taken:
            me.name = name
    return saved


def _restore_export_names(saved):
    """Put every mesh data name back exactly as it was. Parks first for the same collision reason
    `_apply_export_names` does: the original names are mutually unique, so once nothing holds one of
    them the restore is exact."""
    for i, (me, _) in enumerate(saved):
        me.name = f"{_PARK_PREFIX}{i}"
    for me, name in saved:
        me.name = name


def _hide_state(obj):
    """obj's viewport hide flag, or None when there is none to read — an object in a collection
    excluded from the view layer is not there to hide or show."""
    try:
        return obj.hide_get()
    except RuntimeError:
        return None


def _set_hidden(obj, hidden):
    if hidden is None:
        return
    try:
        obj.hide_set(hidden)
    except RuntimeError:
        pass


def _set_selected(obj, selected):
    """Select or deselect, tolerating an object the view layer doesn't carry. Swallowing that failure
    is what lets the named refusal below report every unselectable object at once, instead of Blender
    aborting the send on the first one with a raw message."""
    try:
        obj.select_set(selected)
    except RuntimeError:
        pass


def _is_selected(obj):
    try:
        return obj.select_get()
    except RuntimeError:
        return False


def gf2_send(out_dir, glb_path):
    """Export the edited mesh(es) back to the watched folder with the loader's required
    settings, plus a write-complete sidecar. Returns the written .glb path.

    The scope is the marked part collections plus the session armature; `Reference` is scenery and
    never ships. Each shipping mesh goes out under its part collection's name, so the collection is
    what decides which part an edit comes back as. A part collection the modder emptied ships no mesh
    and is named in the sidecar's `hiddenParts` instead, which is what hides it in the built mod — for a
    session that names one part, that leaves the glb carrying the armature alone, which is exactly what
    tells the app no part came back.

    Any unweighted vertices are bone-heat filled from the session armature first (authored weights are
    always preserved); the skin ALWAYS rides along (export_skins) — the app compiles the authored weights
    onto the target, never re-derives them."""
    os.makedirs(out_dir, exist_ok=True)
    session = load_session()
    # The app names the send file through the session when it must differ from the opened glb's own —
    # a combined session's send must never land on the app-published combined glb. No declared name means
    # the opened name, which for a part's own workspace glb is the overwrite-in-place contract.
    send_name = session.get("sendAs")
    if not isinstance(send_name, str) or not send_name:
        send_name = os.path.basename(glb_path)
    out_glb = os.path.join(out_dir, send_name)

    try:
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception:
        pass

    mesh_objs = gf2_shipping_meshes()
    session_arm = _session_armature()
    arms = [session_arm] if session_arm is not None else []
    emptied = gf2_emptied_parts()
    # A send with no geometry is either a Hide or a mistake, and the session says which. Emptying the one
    # part a named-part session holds IS the Hide gesture — the glb goes out carrying no mesh, the sidecar
    # names the part, and the app leaves that part's file alone. Everywhere else an empty scope means an
    # empty deliverable, and the send refuses rather than write one.
    if not mesh_objs and not (emptied and gf2_emptying_is_a_hide(session)):
        raise RuntimeError("GF2: " + (EVERY_PART_EMPTY if gf2_part_collections() else NO_PART_MESH))
    # Same agreement rule as the empty-scope pair: the checks HARD on a diverged part name, and the
    # send refuses it, so the checkless headless route cannot ship a part the app has no target for.
    renamed = [p.name for p in gf2_part_collections()
               if isinstance(p.get(PART_MARKER), str) and p.name != p[PART_MARKER]]
    if renamed:
        raise RuntimeError("GF2: renamed or suffixed part collection(s): " + ", ".join(renamed)
                           + ". The mesh would ship as a part the app has no target for.")
    filled, still = gf2_fill_missing_weights(mesh_objs, session_arm, gf2_unskinned_parts(session))
    if filled or still:
        print(f"GF2: auto-filled {filled} vertex(es) from the skeleton"
              + (f"; {still} still have no weight (they will be flagged)" if still else ""))

    # The export scope: the part collections' meshes plus the session armature, regardless of focus.
    shipping = mesh_objs + arms

    want = dict(
        filepath=out_glb, export_format="GLB",
        export_tangents=True,        # mikktspace tangents (Blender leaves these OFF by default)
        export_normals=True,
        export_skins=True,           # authored weights ride along; the app compiles them onto the target
        export_image_format="AUTO",  # keep each image's own format. an untouched texture is identified by its
                                     #   texture by its CONTENT, so a re-encode (which a user preference
                                     #   or a future default could otherwise impose) would read as an
                                     #   edit and ship a redundant copy of a stock map.
        export_apply=True,           # bake modifiers (Mirror/Subsurf/etc.) into the mesh — OFF by
                                     #   default, so without this a modifier silently vanishes on Send.
                                     #   Safe for rigged parts: the glTF exporter exempts the Armature
                                     #   modifier (skinning still ships as JOINTS/WEIGHTS, not a baked
                                     #   pose). Its other caveat, shape keys, isn't in our pipeline.
        use_selection=True,
    )
    valid = {p.identifier for p in bpy.ops.export_scene.gltf.get_rna_type().properties}
    opts = {k: v for k, v in want.items() if k in valid}
    dropped = [k for k in want if k not in valid]
    hidden = []
    saved_names = None
    # Everything from here on mutates the scene, so it all runs under the try: the finally is the only
    # thing that gives the modder their scene back, and a send can fail at any of these steps.
    try:
        # An object that must ship is unhidden first and restored after — select_set on a hidden object
        # is silently ignored by Blender, so without this a hidden object would drop out of the
        # deliverable, and hiding is transient working state that must not decide what ships. The
        # armature in particular is hidden by default, and a use_selection export without it writes the
        # meshes WITHOUT their skin — the weightless payload is refused.
        hidden = [(o, _hide_state(o)) for o in shipping]
        for o, _ in hidden:
            _set_hidden(o, False)
        # The selection IS the export scope.
        want_selected = {o.name for o in shipping}
        for o in bpy.data.objects:
            _set_selected(o, o.name in want_selected)
        # An object Blender refuses to select (its collection excluded from the view layer) would be
        # dropped from a use_selection export without a word. Say so instead of sending a short mesh.
        missed = [o.name for o in shipping if not _is_selected(o)]
        if missed:
            raise RuntimeError("GF2: these objects can't be selected for export, so a send would drop "
                               f"them: {', '.join(missed)}. Re-enable their collections in the outliner.")
        saved_names = _apply_export_names(mesh_objs)
        bpy.ops.export_scene.gltf(**opts)
    finally:
        # Everything the send borrowed goes back, on the failure path as much as the happy one: the
        # scene the modder returns to must be the scene they left.
        if saved_names is not None:
            _restore_export_names(saved_names)
        for o, was_hidden in hidden:
            _set_hidden(o, was_hidden)

    # Tripwire: a skinned scene must never send a skinless glb — that failure is silent otherwise.
    # Structural check on the written bytes, so an export-option or Blender behavior drift is caught
    # HERE with a plain message instead of surfacing as a refusal downstream.
    if arms and any(o.vertex_groups for o in mesh_objs):
        with open(out_glb, "rb") as f:
            if b"JOINTS_0" not in f.read():
                raise RuntimeError("GF2: the export dropped the skin (no JOINTS_0 in the sent glb). "
                                   "Weights would be lost. Report this.")

    # Blender computes tangents per mesh and gives up on the WHOLE mesh when any face is an n-gon,
    # logging it only to a console the modder can't see. The app derives its own in that case, so the
    # send stands, but the surface detail is no longer the one authored here. A Hide carries no mesh, so
    # there is no tangent to miss.
    if mesh_objs:
        with open(out_glb, "rb") as f:
            if b"TANGENT" not in f.read():
                print("GF2: no tangents in the sent glb. Triangulate the mesh (Ctrl+T in Edit mode) to "
                      "keep the authored surface detail.")
                _popup("Sent without tangents",
                       ["⚠ Blender couldn't compute tangents for this mesh.",
                        "Normal-map detail may shift.",
                        "Triangulate the mesh (Ctrl+T in Edit mode), then Send again."], 'INFO')

    # An option this Blender's glTF exporter doesn't carry was filtered out of the export above. The mesh
    # still ships; what it ships WITHOUT is only knowable here, so it is said where the send is read rather
    # than on a console the modder never opens.
    if dropped:
        line = gf2_dropped_options_line(dropped)
        print("GF2: " + line)
        _popup("Sent with warnings", ["⚠ " + line], 'INFO')

    # The sidecar is the write-complete sentinel (written last) that the watcher fires on, and it
    # carries the one intent the glb cannot express: which parts were emptied. An absent mesh is never
    # that signal — every part outside this session is absent by design — so `hiddenParts` is the only
    # thing that hides a part, and a part listed there keeps its workspace file untouched.
    with open(os.path.splitext(out_glb)[0] + ".gf2send.json", "w", encoding="utf-8") as f:
        json.dump({"source": "blender-send", "hiddenParts": emptied}, f)
    # Those parts now hold an edit, whatever the app knew when Blender opened, so the next Send in this
    # session says so before it replaces them.
    _record_sent(p.name for p in gf2_part_collections() if _part_meshes(p))
    print(f"GF2: sent -> {out_glb}")
    return out_glb


def gf2_dropped_options_line(dropped):
    """The one wording for glTF export options the running Blender doesn't have. The send still stands —
    this names what it went out without, so an option lost to a version difference is visible."""
    return "Export options this Blender doesn't support: " + ", ".join(sorted(dropped)) + "."


# ---------------------------------------------------------------- pre-send sanity checks

def _snapshot_baseline(meshes, armature):
    """Record the as-imported skeleton, material-slot layout and object scale so the pre-send check can
    tell what the modder CHANGED — a renamed bone or reordered slot silently breaks the compile,
    and scale is baselined against import rather than a bare identity."""
    base = {
        "bones": sorted(b.name for b in armature.data.bones) if armature else [],
        "slots": {mo.name: [ms.material.name if ms.material else "" for ms in mo.material_slots] for mo in meshes},
        "scale": {mo.name: list(mo.scale) for mo in meshes},
    }
    try:
        bpy.context.scene["gf2_baseline"] = json.dumps(base)
    except Exception as e:
        print(f"GF2: could not snapshot import baseline: {e}")


def _load_baseline():
    raw = bpy.context.scene.get("gf2_baseline")
    if not raw:
        return {}
    try:
        return json.loads(raw)
    except Exception:
        return {}


def _attribution_issues(mesh_objs):
    """Collection-attribution problems, over EVERY mesh in the scene.

    A mesh's part is the collection it sits in, so a mesh in no part collection has no part: outside
    both trees it would silently not ship, and inside `Mod` there is nothing to attribute it to. A part
    collection holding more than one mesh has no single answer either — the pipeline compiles one mesh
    per part — one excluded from the view layer cannot be exported at all, and one that had to take a
    duplicate-name suffix would ship its mesh under a name the app has no target for. Those block. The
    object name is advisory, so a name disagreeing with the collection warns rather than blocks."""
    issues = []
    mod, ref = _mod_root(), _reference_root()
    parts = gf2_part_collections()
    part_names = {p.name for p in parts}
    choices = ", ".join(p.name for p in parts)
    for o in mesh_objs:
        if not _in_tree(o, mod):
            if not _in_tree(o, ref):
                issues.append(("HARD", f"'{o.name}' is outside {MOD_COLLECTION}"
                                       + (f" and {REFERENCE_COLLECTION}" if ref is not None else "")
                                       + ". It will not ship. Move it into a part collection to ship it"
                                       + (f", or into {REFERENCE_COLLECTION} to leave it out."
                                          if ref is not None else ".")))
            continue                     # Reference is scenery: no part to attribute, nothing to check
        part = _part_of(o)
        if part is None:
            issues.append(("HARD", f"'{o.name}' is in {MOD_COLLECTION} but not in a part collection. "
                                   + (f"Move it into one of: {choices}." if choices else
                                      f"Create a part collection under {MOD_COLLECTION} and move it in.")))
        elif _base_name(o.name) != part.name and _base_name(o.name) in part_names:
            issues.append(("SOFT", f"The {gf2_label(o.name)} mesh sits in {gf2_label(part.name)}, so it "
                                   f"ships as {gf2_label(part.name)}."))
    for part in parts:
        if _is_excluded(part):
            issues.append(("HARD", f"'{part.name}' is excluded from the view layer, so its meshes "
                                   "can't be exported. Re-enable it in the outliner."))
        wanted = part.get(PART_MARKER)
        if isinstance(wanted, str) and part.name != wanted:
            if _base_name(part.name) == wanted:
                issues.append(("HARD", f"'{part.name}' took a name suffix because another collection "
                                       f"already holds '{wanted}'. Its mesh would ship as a part the app "
                                       f"has no target for. Rename or delete the other '{wanted}' "
                                       "collection, then re-import."))
            else:
                issues.append(("HARD", f"'{part.name}' was renamed from '{wanted}'. Its mesh would ship "
                                       f"as a part the app has no target for. Rename the collection "
                                       f"back to '{wanted}'."))
        held = _part_meshes(part)
        if len(held) > 1:
            issues.append(("HARD", f"{gf2_label(part.name)} holds {len(held)} meshes: "
                                   f"{', '.join(gf2_label(o.name) for o in held)}. A part ships as "
                                   "one. Join them (select both, Ctrl+J), or move the extra into "
                                   f"{REFERENCE_COLLECTION}."))
    return issues


def gf2_cheap_checks(mesh_objs, armature):
    """The subset of the pre-send pass that reads only object- and collection-level state:
    attribution, an empty export scope, armature presence, Object-mode scale, material-slot layout,
    bone set. Same (severity, message) shape as gf2_run_checks, and cheap enough to run on every
    depsgraph update — nothing here duplicates geometry or walks vertices. Pure — never mutates the
    scene.

    `mesh_objs` is EVERY mesh object in the scene (the attribution checks need the ones outside `Mod`
    too) and `armature` is the SESSION armature (see `_session_armature`); the checks that describe
    the deliverable scope themselves to what actually ships, so a donor body or donor rig parked in
    `Reference` neither blocks nor warns a Send."""
    issues = _attribution_issues(mesh_objs)
    shipping = gf2_shipping_meshes()
    # SOFT — an emptied part collection hides that part in the built mod. Deliberate is the whole
    # point, so it is stated before a Send rather than discovered in the deliverable.
    for name in gf2_emptied_parts():
        issues.append(("SOFT", f"{gf2_label(name)} is empty. Sending hides it in the mod."))
    # HARD — nothing in a part collection is no deliverable at all; the send refuses rather than write
    # it. The gate matches the send's, so a scene the send would refuse never reads as ready here — which
    # includes the one case where an empty scope is fine: a named-part session whose part was emptied on
    # purpose, already warned about above.
    if not shipping and not (gf2_emptied_parts() and gf2_emptying_is_a_hide(load_session())):
        issues.append(("HARD", EVERY_PART_EMPTY if gf2_part_collections() else NO_PART_MESH))
    # HARD — a weighted scene with no session armature exports skinless; that payload is refused.
    weighted = [o for o in shipping if o.vertex_groups]
    if weighted and armature is None:
        issues.append(("HARD", f"{len(weighted)} weighted part(s) but no armature in {MOD_COLLECTION}. "
                               f"The skin would be dropped on export. Keep the skeleton in "
                               f"{MOD_COLLECTION}/{ARMATURE_COLLECTION}."))
    base = _load_baseline()
    # SOFT — Object-mode scale doubles onto the skinning (a move/rotate is warned separately).
    for mo in shipping:
        ref = (base.get("scale") or {}).get(mo.name) or [1.0, 1.0, 1.0]
        sc = list(mo.scale)
        if any(abs(sc[i] - ref[i]) > 1e-3 for i in range(3)):
            issues.append(("SOFT", f"'{mo.name}' has an Object-mode scale of "
                                   f"({sc[0]:.3f}, {sc[1]:.3f}, {sc[2]:.3f}). Scale in Edit mode instead."))
    # SOFT — each submesh keeps the material it was imported with; a reorder/count change moves them.
    for mo in shipping:
        old = (base.get("slots") or {}).get(mo.name)
        cur = [ms.material.name if ms.material else "" for ms in mo.material_slots]
        if old is not None and cur != old:
            issues.append(("SOFT", f"'{mo.name}' material slots changed since import. Reordering them "
                                   "changes which material each face range uses."))
    # SOFT — re-export keys off bone names; a renamed/removed bone breaks the weight compile.
    if base.get("bones") and armature is not None:
        cur_bones = set(b.name for b in armature.data.bones)
        gone = [b for b in base["bones"] if b not in cur_bones]
        if gone:
            issues.append(("SOFT", f"{len(gone)} imported bone(s) renamed or removed "
                                   f"(e.g. '{gone[0]}'). Re-export keys off bone names. Keep the "
                                   "skeleton as imported."))
    return issues


def gf2_run_checks(mesh_objs, armature):
    """Pre-send sanity pass. Returns a list of (severity, message) with severity in {"HARD","SOFT"}:
    HARD is what will be REJECTED downstream (surfaced here first so the modder isn't bounced after a
    round-trip); SOFT is a likely-mistake warning that still exports.

    The full pass = the cheap checks (pure) plus the unweighted-vertex count, which bone-heats a
    duplicate per mesh and so runs only here, never on a live refresh. That solve leaves geometry and
    weights untouched but borrows the selection and the active object without restoring them. The
    blocker joins the leading run of HARD entries, so a blocked send still reads its blockers before
    its warnings."""
    issues = gf2_cheap_checks(mesh_objs, armature)
    shipping = gf2_shipping_meshes()
    unsolvable = (_unsolvable_weights_by_object(shipping, armature, gf2_unskinned_parts(load_session()))
                  if shipping else [])
    if unsolvable:
        total = sum(n for _, n in unsolvable)
        named = ", ".join(f"'{n}'" for n, _ in unsolvable)
        lead = next((i for i, (sev, _) in enumerate(issues) if sev != "HARD"), len(issues))
        issues.insert(lead, ("HARD", f"{total} vertex(es) in {named} have no weight and can't be "
                                     "filled from the skeleton. They will be rejected. Weight-paint "
                                     "or delete those verts."))
    return issues


def _popup(title, lines, icon):
    """Show the check results in a Blender popup. Does nothing in a background run: there is no window
    to draw into and popup_menu takes Blender down rather than raising. Callers print the same lines to
    the console, so nothing is lost."""
    if bpy.app.background:
        return
    def draw(self, ctx):
        for ln in lines:
            self.layout.label(text=ln)
    try:
        bpy.context.window_manager.popup_menu(draw, title=title, icon=icon)
    except Exception:
        pass


# ---------------------------------------------------------------- scene state (what the panel reports)

def gf2_status_line(issues):
    """The status line for a list of (severity, message): clean, or the tally of what bit. The glyph
    carries the severity, so the text after it counts and never restates it."""
    hard = sum(1 for sev, _ in issues if sev == "HARD")
    soft = len(issues) - hard
    if not hard and not soft:
        return "✓ Ready to send"
    bits = []
    if hard:
        bits.append(f"✗ {hard} blocking")
    if soft:
        bits.append(f"⚠ {soft} warning" + ("" if soft == 1 else "s"))
    return " · ".join(bits)


def gf2_label(name):
    """A part or object name cut down to what tells it apart. Game asset names run to 40 characters of
    shared prefix and suffix, which buries the one token that differs in a message the modder has to
    read at a glance: `c_KarstSSR0101_slg_P1_cloth2_lod0` -> `cloth2`. A name the MODDER chose carries
    no such structure, so it is left exactly as they wrote it."""
    stem = _base_name(name)
    if not stem.startswith("c_"):
        return name
    for tail in ("_lod0", "_lod1", "_lodm0"):
        if stem.endswith(tail):
            stem = stem[:-len(tail)]
            break
    return stem.rsplit("_", 1)[-1] or name


def gf2_subject_label(glb_path):
    """The subject a session is editing. The app lays a workspace out as `<subject>/meshes/<part>.glb`,
    so the folder holding `meshes` is the subject-level name the bridge is handed; anything else falls
    back to the file's own stem."""
    if not glb_path:
        return ""
    mesh_dir = os.path.dirname(os.path.normpath(glb_path))
    parent, leaf = os.path.split(mesh_dir)
    if leaf.lower() == "meshes" and os.path.basename(parent):
        return os.path.basename(parent)
    return os.path.splitext(os.path.basename(glb_path))[0]


def gf2_short_part_names(names):
    """Object names trimmed of the prefix they all share, cut back to an underscore so a label never
    starts mid-token. Fewer than two names leaves no shared prefix to measure, so those stay whole."""
    names = list(names)
    if len(names) < 2:
        return names
    cut = os.path.commonprefix(names).rfind("_") + 1
    return [n[cut:] if len(n) > cut else n for n in names]


def gf2_subject_line(subject, parts):
    """`<subject> · <part>, <part>` — either half alone when the other isn't derivable."""
    return " · ".join(b for b in (subject, ", ".join(parts)) if b)


def gf2_collections_line(mod_count, reference_count, attributed):
    """`Mod 3 · Reference 1`. A scene with no `Mod` collection has no attribution to break down, and a
    breakdown would report zero against every mesh in it, so it gets a bare count instead. That count
    is display only: with no `Mod` nothing ships and the checks block the Send."""
    if not attributed:
        return f"{mod_count} object" + ("" if mod_count == 1 else "s")
    return f"Mod {mod_count} · Reference {reference_count}"


def gf2_scope_lines(n_objects, n_verts, bakes_modifiers):
    """What the next Send carries, stated before it acts: a stray import reads as an unexpected count
    here rather than as an unexpected mesh in the built mod. Reference is not mentioned: it never
    ships, and a count of what is NOT sent is noise on every send."""
    head = (f"{n_objects} object" + ("" if n_objects == 1 else "s")
            + f" · {n_verts:,} vert" + ("" if n_verts == 1 else "s"))
    lines = [head]
    if bakes_modifiers:
        lines.append("Modifiers are baked on Send.")
    return lines


def _scene_meshes():
    return [o for o in bpy.data.objects if o.type == "MESH"]


def _reference_meshes():
    """The meshes in the `Reference` tree: what a Send holds back."""
    ref = _reference_root()
    return [] if ref is None else [o for o in ref.all_objects if o.type == "MESH"]


def _part_names():
    """The part labels for the subject line. The marked part collections ARE the part attribution, and
    one currently holding no mesh is not something the session is editing. Without `Mod` there is no
    attribution to read, so the object names are all the scene offers."""
    if _mod_root() is None:
        return gf2_short_part_names([o.name for o in _scene_meshes()])
    return [c.name for c in gf2_part_collections() if _part_meshes(c)]


def _bakes_modifiers(obj):
    """True when the export would bake a modifier into this mesh. The Armature modifier is exempt (the
    skin ships as weights, not a baked pose), and so is one switched off in the viewport — the exporter
    evaluates the same depsgraph the viewport shows."""
    return any(m.type != "ARMATURE" and m.show_viewport for m in obj.modifiers)


def _send_overwrite_warning():
    """The pre-send overwrite line, or None. Which parts already hold an edit comes from the app's session
    description plus whatever this session has already sent, scoped to the part collections this send
    actually writes."""
    shipping = [p.name for p in gf2_part_collections() if _part_meshes(p)]
    return gf2_overwrite_warning(gf2_edited_shipping_parts(load_session(), shipping, gf2_sent_parts()))


def _send_scope_lines():
    """The pre-send scope summary. Read AFTER the checks have forced Object mode, so the vertex counts
    are the datablock's and not a stale pre-edit figure."""
    shipping = gf2_shipping_meshes()
    return gf2_scope_lines(len(shipping),
                           sum(len(o.data.vertices) for o in shipping),
                           any(_bakes_modifiers(o) for o in shipping))


def _panel_lines(scene):
    """The panel's state rows. Never raises: a scene the reader can't make sense of must not take the
    Send button down with it."""
    try:
        subject = gf2_subject_label(getattr(scene, "gf2_glb_path", "") or "")
        attributed = _mod_root() is not None
        count = len(gf2_shipping_meshes()) if attributed else len(_scene_meshes())
        rows = [gf2_subject_line(subject, _part_names()),
                gf2_collections_line(count, len(_reference_meshes()), attributed)]
        if _reference_root() is not None:
            rows.append(REFERENCE_NOTE)
        return [r for r in rows if r]
    except Exception as e:
        print(f"GF2: panel state read failed: {e}")
        return [UNREADABLE]


# ---------------------------------------------------------------- live status

# The status line the panel draws, refreshed from the depsgraph handler. A module global, NOT a scene
# property: writing to the scene from a depsgraph handler retriggers the depsgraph.
_LIVE = {"text": "", "ran": 0.0, "pending": False}
_LIVE_INTERVAL = 0.3     # seconds between passes; a vertex drag fires the depsgraph continuously


def _tag_sidebar_redraw():
    """The status text lives outside Blender's property system, so the sidebar has no property to
    invalidate on; tag it so a change shows without waiting for the next unrelated redraw."""
    try:
        for win in bpy.context.window_manager.windows:
            for area in win.screen.areas:
                if area.type == "VIEW_3D":
                    area.tag_redraw()
    except Exception:
        pass


def _refresh_live_status():
    """Recompute the status line from the cheap checks and return it. Never raises: this runs from a
    handler (which Blender drops on an exception) and from draw, and a stale ✓ would misreport a
    scene that has since gone dirty."""
    try:
        text = gf2_status_line(gf2_cheap_checks(_scene_meshes(), _session_armature()))
    except Exception as e:
        print(f"GF2: live check failed: {e}")
        text = UNREADABLE
    _LIVE["ran"] = time.monotonic()
    if text != _LIVE["text"]:
        _LIVE["text"] = text
        _tag_sidebar_redraw()
    return text


def _live_status_trailing():
    """The trailing pass of a throttled burst — without it the last edit before a pause keeps whatever
    the previous pass concluded."""
    _LIVE["pending"] = False
    _refresh_live_status()
    return None          # returning None unregisters the timer -> one-shot


def _gf2_depsgraph_update(scene, depsgraph):
    """Throttled cheap-check refresh. Runs no operator and writes nothing to the scene — either would
    re-enter the depsgraph from inside its own handler."""
    if _SOLVING:
        return               # the weight solve's throwaway duplicate is not scene state worth reporting
    try:
        if time.monotonic() - _LIVE["ran"] < _LIVE_INTERVAL:
            if not _LIVE["pending"]:
                _LIVE["pending"] = True
                bpy.app.timers.register(_live_status_trailing, first_interval=_LIVE_INTERVAL)
            return
        _refresh_live_status()
    except Exception as e:
        print(f"GF2: live status handler failed: {e}")


def _register_live_status():
    """Register the depsgraph handler exactly once. Re-running this script leaves the previous run's
    function object in the handler list, so match on the name rather than on identity."""
    for h in list(bpy.app.handlers.depsgraph_update_post):
        if getattr(h, "__name__", "") == _gf2_depsgraph_update.__name__:
            bpy.app.handlers.depsgraph_update_post.remove(h)
    bpy.app.handlers.depsgraph_update_post.append(_gf2_depsgraph_update)


# ---------------------------------------------------------------- UI (panel + operator)

def _register_ui(glb_path, send_dir):
    bpy.types.Scene.gf2_send_dir = bpy.props.StringProperty(name="Send dir", default=send_dir or "")
    bpy.types.Scene.gf2_glb_path = bpy.props.StringProperty(name="Source glb", default=glb_path or "")

    def _scene_meshes_arm():
        # Every mesh in the scene (the attribution checks need the ones outside Mod too) paired with
        # the SESSION armature, so a donor rig in Reference is never read as the skeleton.
        # parent_set (bone-heat) and a clean scale read both need Object mode; the checks touch neither
        # the edit the modder made nor their mode beyond this.
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass
        return _scene_meshes(), _session_armature()

    class GF2_OT_check(bpy.types.Operator):
        bl_idname = "gf2.check_mesh"
        bl_label = "Check mesh"
        bl_description = "Run the pre-send checks without exporting"

        def execute(self, ctx):
            meshes, arm = _scene_meshes_arm()
            issues = gf2_run_checks(meshes, arm)
            if not issues:
                self.report({'INFO'}, "Ready to send")
                _popup("Check", ["✓ Ready to send"], 'CHECKMARK')
                return {'FINISHED'}
            lines = []
            for sev, msg in issues:
                print(f"GF2 {'BLOCK' if sev == 'HARD' else 'warn'}: {msg}")
                lines.append(("✗ " if sev == "HARD" else "⚠ ") + msg)
            hard = sum(1 for sev, _ in issues if sev == "HARD")
            _popup("Check", lines, 'ERROR' if hard else 'INFO')
            # The popup IS the surface. An {'ERROR'} report draws its own popup on top of it, so the
            # modder gets two overlapping boxes, the top one only pointing at the one it covers.
            self.report({'WARNING'}, gf2_status_line(issues))
            return {'FINISHED'}

    class GF2_OT_send(bpy.types.Operator):
        bl_idname = "gf2.send_to_lab"
        bl_label = "Send to Lab"
        bl_description = "Export the edit back to Doll Remolding Lab"

        _soft = None
        _scope = None
        _overwrite = None

        def _gate(self, ctx):
            """Run the full pre-send pass and keep its warnings for the send. Returns a result set when
            the Send must not proceed, None when it may."""
            if not ctx.scene.gf2_send_dir:
                self.report({'ERROR'}, "No send folder set")
                return {'CANCELLED'}
            meshes, arm = _scene_meshes_arm()
            issues = gf2_run_checks(meshes, arm)
            hard = [m for sev, m in issues if sev == "HARD"]
            self._soft = [m for sev, m in issues if sev == "SOFT"]
            for m in self._soft:
                print(f"GF2 warn: {m}")
            if hard:
                for m in hard:
                    print(f"GF2 BLOCK: {m}")
                _popup("Send blocked", ["✗ " + m for m in hard] + ["", "Fix these, then Send again."], 'ERROR')
                # WARNING, not ERROR: an ERROR report opens a second popup over the one just shown.
                self.report({'WARNING'}, f"Send blocked. {len(hard)} to fix.")
                return {'CANCELLED'}
            return None

        def invoke(self, ctx, event):
            # Gate first: a send that is already blocked has no scope worth confirming. The scope is read
            # after the gate, whose mode_set leaves the vertex counts current.
            blocked = self._gate(ctx)
            if blocked is not None:
                return blocked
            self._overwrite = _send_overwrite_warning()
            # The confirm step exists to protect work that already exists, so it appears only when this
            # send would replace an edit. A first send just goes; either way the export confirms itself.
            if not self._overwrite:
                return self.execute(ctx)
            self._scope = _send_scope_lines()
            return ctx.window_manager.invoke_props_dialog(self, width=340)

        def draw(self, ctx):
            # The overwrite is why this dialog is on screen (invoke skips it otherwise), so it leads;
            # the soft warnings and the scope give the decision its context.
            col = self.layout.column(align=True)
            if self._overwrite:
                col.label(text="⚠ " + self._overwrite)
            for m in (self._soft or ()):
                col.label(text="⚠ " + m)
            if self._overwrite or self._soft:
                col.separator()
            for ln in (self._scope or ()):
                col.label(text=ln)

        def execute(self, ctx):
            if self._soft is None:       # reached without the confirm step (a script or the search menu)
                blocked = self._gate(ctx)
                if blocked is not None:
                    return blocked
            s = ctx.scene
            out = gf2_send(s.gf2_send_dir, s.gf2_glb_path)
            soft = self._soft
            if soft:
                _popup("Sent with warnings", ["⚠ " + m for m in soft], 'INFO')
                self.report({'WARNING'}, f"Sent with {len(soft)} warning(s). See the console.")
            else:
                # The status-bar report alone is easy to miss: confirm the export where the modder is looking.
                _popup("Sent", [f"✓ {os.path.basename(out)} · back in the Lab"], 'INFO')
                self.report({'INFO'}, f"Sent: {os.path.basename(out)}")
            return {'FINISHED'}

    class GF2_PT_panel(bpy.types.Panel):
        bl_label = "Doll Remolding Lab"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "Doll Remolding Lab"

        def draw(self, ctx):
            col = self.layout.column()
            rows = _panel_lines(ctx.scene)
            for ln in rows:
                col.label(text=ln)
            col.separator()
            col.operator("gf2.check_mesh", icon="CHECKMARK")
            col.operator("gf2.send_to_lab", icon="EXPORT")
            # A scene the reader couldn't make sense of has already said so in the rows above, and the
            # live pass reads the same scene: saying it twice adds nothing.
            if rows != [UNREADABLE]:
                col.label(text=_LIVE["text"] or _refresh_live_status())

    bpy.utils.register_class(GF2_OT_check)
    bpy.utils.register_class(GF2_OT_send)
    bpy.utils.register_class(GF2_PT_panel)
    _register_live_status()


# ---------------------------------------------------------------- entry point

def _argv_after_dashdash():
    return sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def main():
    args = _argv_after_dashdash()
    glb = args[0] if args else ""
    send_to = None
    if "--send-to" in args:
        send_to = args[args.index("--send-to") + 1]
    # positional send_dir (interactive launch): "<glb> <send_dir>"
    send_dir = args[1] if len(args) > 1 and not args[1].startswith("--") else (send_to or "")

    if send_to is not None:        # headless / no-op round-trip — must run synchronously (no event loop)
        if glb:
            gf2_import(glb)
        gf2_send(send_to, glb)
        return

    # Interactive launch. Register the sidebar panel NOW (class registration is global, so it survives a
    # scene reload), but DEFER the import to a one-shot timer. Running the import inline from --python can
    # land the mesh in a scene that Blender's startup .blend then replaces a beat later — leaving only the
    # default cube/camera/light while the panel sticks around. The timer fires once after startup has
    # settled, when the import is safe and the 3D view exists for framing.
    _register_ui(glb, send_dir)

    def _deferred_open():
        try:
            if glb:
                gf2_import(glb)
            _setup_viewport_ui()
        except Exception as e:
            print(f"GF2: deferred import failed: {e}")
        return None   # returning None unregisters the timer -> one-shot

    bpy.app.timers.register(_deferred_open, first_interval=0.1)
    print("GF2: ready. Edit, then use the 'Doll Remolding Lab' sidebar (press N) -> Send to Lab")


if __name__ == "__main__":
    main()
