"""Doll Remolding Lab — Blender bridge.

Launched with the mesh to edit:

    blender --python remold_bridge.py -- <mesh.glb> <send_dir>

It imports the mesh, sets up a clean viewport, and registers a **"Doll Remolding Lab"** panel in
the N-panel sidebar (press N) with the live edit destinations, session notices, **Check Mesh**, and
**Send to Lab**. The modder edits, optionally runs Check, and clicks Send — the app's file watcher
picks up the exported `.glb` (+ a `.gf2send.json` write-complete sidecar). A first Send just goes; a
Send that would replace an existing edit asks first, and every successful Send confirms with a popup.

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
also carries each writable part's live edit inventory and default destination, so the panel and Send
confirmation reflect the app's current state.

Deleting a part's mesh is how a part is hidden in the built mod. The send never infers that from an
absent mesh — under the one-part-per-session layout most of the outfit is absent by design — so an
emptied part collection is named explicitly in the send sidecar's `hiddenParts`, and its workspace
file is left exactly as it was. A session that names one part holds exactly one part collection, so
emptying it is the only way to hide that part: the send then carries no mesh at all, and the rest of
the mod still ships from its own files. Emptying everything in a session that names NO part is an
empty deliverable instead, and blocks.

Before an export, a sanity pass runs (also on demand via **Check Mesh**): it BLOCKS the Send on
problems that would break the deliverable — a mesh with no part, a part holding more than one mesh, a
part collection excluded from the view layer or squatted out of its own name, vertices no skeleton
weight can reach, or a weighted scene with no armature — and WARNS (without blocking) on likely
mistakes: an Object-mode transform on a mesh with no skeleton, a mirrored scale, a reordered
material slot, or a renamed/removed bone. Everything but the weight solve is cheap enough to drive
the panel's live status line. On Send, any unweighted vertices are bone-heat filled from the skeleton
(authored weights are always preserved); the app then compiles the authored skin onto the target. A
part the app declares unskinned — a static prop, which ships no weights and opens with no armature —
is outside all of that.

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
import struct
import tempfile
import hashlib
import textwrap


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
# may write back, plus the live edit inventory and defaults. Absent = every mesh in the glb is a part.
SESSION_SUFFIX = ".gf2session.json"

# Where the session description is kept once read, so the Send confirmation can reach it after a save.
SESSION_KEY = "gf2_session"

# The target selection captured by the last completed Blender export. It remains pending until the app
# acknowledges that return by advancing the session revision. This is deliberately not evidence that an
# edit holds mesh work: only the acknowledged live session inventory carries that fact.
SEND_SNAPSHOT_KEY = "gf2_send_snapshot"

# Enum identifier for the row's synthetic final choice. Existing edit identifiers are opaque app tokens.
NEW_EDIT_TARGET = "__gf2_new_edit__"

# Blender's dynamic EnumProperty callback does not retain its strings. Keep every tuple (and therefore all
# of its strings) referenced for as long as the corresponding CollectionProperty rows exist.
_TARGET_ITEM_REFS = {}
_TARGET_FALLBACK_ITEMS = ((NEW_EDIT_TARGET, "New Edit", ""),)

# Session refresh is timer-driven, not panel-draw-driven. The timer stats the sidecar cheaply and only
# opens it after the path or nanosecond mtime changes.
_SESSION_REFRESH_INTERVAL = 1.0
_SESSION_FILE_STATE = {"path": None, "mtime": None}

# Panel text used when a scene read itself fails. Session-file read failures are different: those retain the
# last readable scene snapshot without showing a false empty session.
UNREADABLE = "⚠ Scene state unreadable"

# Blender-only preview structure applied after the standard glTF import. Core glTF has no material
# setting for transparency overlap and cannot represent the Map Range node, so every import rebuilds
# this small graph over the original, untouched base-colour image.
ALPHA_REMAP_NODE = "GF2 Alpha 254 Ceiling"
ALPHA_REMAP_TAG = "gf2_alpha_254_remap"
ALPHA_OPAQUE_CEILING = 254.0 / 255.0

# Exact shader-property transport. The glTF carrier is deliberately independent of Blender's Principled
# graph: every binding is an ordinary glTF image plus this top-level extras row, including properties for
# which the game shader has no honest static PBR equivalent.
TEXTURE_TRANSPORT_EXTRAS = "gf2_texture_transport"
TEXTURE_TRANSPORT_NODE = "gf2_texture_binding"
TEXTURE_TRANSPORT_UV_NODE = "gf2_texture_tex_coord"
# Stamped on each installed image so the send can tell an untouched picture without reading a pixel:
# the row identity the app sent it under, the temp path it was loaded from (deleted right after, so a
# file REAPPEARING there is a user save), and the sticky touched note (see _gf2_note_dirty_images).
TEXTURE_TRANSPORT_IMAGE_HASH = "gf2_texture_transport_hash"
TEXTURE_TRANSPORT_IMAGE_PATH = "gf2_texture_transport_path"
TEXTURE_TRANSPORT_IMAGE_TOUCHED = "gf2_texture_transport_touched"

# Images observed carrying unsaved edits at any point this session, by datablock pointer. Blender's
# is_dirty clears on save, so the send's unchanged test needs the observation, not the flag. A stale
# pointer after an undo or delete only ever re-ships bytes; it never marks work unchanged.
_GF2_DIRTY_SEEN = set()


def _gf2_note_dirty_images(stamp=False):
    """Collect every image currently dirty. The depsgraph pass only collects — writing a datablock from
    inside that handler re-enters it — while the timer pass also stamps the note onto the image, where
    it survives a rename and rides the datablock through undo exactly as the paint does."""
    try:
        for image in bpy.data.images:
            dirty = getattr(image, "is_dirty", False)
            if dirty:
                _GF2_DIRTY_SEEN.add(image.as_pointer())
            if stamp and hasattr(image, "get") \
                    and (dirty or image.as_pointer() in _GF2_DIRTY_SEEN) \
                    and not image.get(TEXTURE_TRANSPORT_IMAGE_TOUCHED):
                image[TEXTURE_TRANSPORT_IMAGE_TOUCHED] = True
    except Exception as e:
        print(f"GF2: dirty-image sweep failed: {e}")


def _gf2_glb_read(path):
    """Return (json object, BIN bytes) for one glTF 2.0 binary."""
    with open(path, "rb") as f:
        raw = f.read()
    if len(raw) < 20 or struct.unpack_from("<II", raw, 0) != (0x46546C67, 2):
        raise RuntimeError("GF2: texture transport requires a glTF 2.0 binary file.")
    offset = 12
    root = None
    binary = b""
    while offset + 8 <= len(raw):
        length, kind = struct.unpack_from("<II", raw, offset)
        offset += 8
        payload = raw[offset:offset + length]
        if len(payload) != length:
            raise RuntimeError("GF2: the GLB texture-transport chunk is truncated.")
        if kind == 0x4E4F534A:
            root = json.loads(payload.rstrip(b" \0").decode("utf-8"))
        elif kind == 0x004E4942:
            binary = payload
        offset += length
    if root is None:
        raise RuntimeError("GF2: the GLB has no JSON chunk.")
    return root, binary


def _gf2_glb_write(path, root, binary):
    json_bytes = json.dumps(root, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_bytes += b" " * ((-len(json_bytes)) % 4)
    binary = bytes(binary)
    binary += b"\0" * ((-len(binary)) % 4)
    total = 12 + 8 + len(json_bytes) + (8 + len(binary) if binary else 0)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, total))
        f.write(struct.pack("<II", len(json_bytes), 0x4E4F534A))
        f.write(json_bytes)
        if binary:
            f.write(struct.pack("<II", len(binary), 0x004E4942))
            f.write(binary)


def _gf2_image_bytes(root, binary, image_index):
    image = root.get("images", [])[image_index]
    view = root.get("bufferViews", [])[image["bufferView"]]
    if view.get("buffer", 0) != 0:
        raise RuntimeError("GF2: a texture-transport image is not in GLB buffer 0.")
    start = view.get("byteOffset", 0)
    return binary[start:start + view["byteLength"]]


def _gf2_read_texture_transport(path):
    """Read carrier rows and any embedded PNG bytes. No extras means the legacy empty answer."""
    root, binary = _gf2_glb_read(path)
    carrier = root.get("extras", {}).get(TEXTURE_TRANSPORT_EXTRAS, {})
    if carrier.get("version") != 1:
        return []
    result = []
    for row in carrier.get("bindings", []):
        try:
            copied = json.loads(json.dumps(row))
            if "image" in row:
                copied["png"] = _gf2_image_bytes(root, binary, row["image"])
                copied["image_name"] = root.get("images", [])[row["image"]].get(
                    "name", "GF2 texture")
            result.append(copied)
        except (KeyError, IndexError, TypeError):
            print("GF2: ignored a malformed texture-transport binding in the opened GLB.")
    return result


def _gf2_append_texture_transport(path, rows, standard_channels=None):
    """Append the exact property rows to a freshly exported GLB: a changed picture embeds once per
    distinct picture, an untouched one rides as its row with the app-stamped hash and no bytes, and an
    untagged standard-channel picture (a hand-built material, a legacy session) is appended with ordinary
    glTF texture references so the app's channel read still finds it."""
    standard_channels = standard_channels or []
    if not rows and not standard_channels:
        return
    root, old_binary = _gf2_glb_read(path)
    binary = bytearray(old_binary)
    images = root.setdefault("images", [])
    views = root.setdefault("bufferViews", [])
    buffers = root.setdefault("buffers", [{}])
    if not buffers:
        buffers.append({})
    image_by_hash = {}
    texture_by_image = {}

    def append_image(png, image_name):
        content_hash = hashlib.sha256(png).hexdigest()
        image = image_by_hash.get(content_hash)
        if image is None:
            while len(binary) % 4:
                binary.append(0)
            start = len(binary)
            binary.extend(png)
            view = len(views)
            views.append({"buffer": 0, "byteOffset": start, "byteLength": len(png)})
            image = len(images)
            images.append({"name": image_name, "mimeType": "image/png", "bufferView": view})
            image_by_hash[content_hash] = image
        return image, content_hash

    carrier_rows = []
    for source in rows:
        row = dict(source)
        png = row.pop("png", None)
        image_name = row.pop("image_name", "GF2 texture")
        row.pop("image", None)
        if png is not None:
            image, content_hash = append_image(png, image_name)
            row["image"] = image
            row["outbound_hash"] = content_hash
        carrier_rows.append(row)

    materials_by_name = {}
    for material in root.get("materials", []):
        materials_by_name.setdefault(material.get("name", ""), []).append(material)
    textures = root.setdefault("textures", []) if standard_channels else []
    for standard in standard_channels:
        # A slot whose material the exporter wrote nothing for (an unused slot) draws nothing; its
        # picture has nowhere to land and is dropped without failing the send.
        material_rows = materials_by_name.get(standard["material"])
        if not material_rows:
            continue
        image, _content_hash = append_image(standard["png"], standard["image_name"])
        texture = texture_by_image.get(image)
        if texture is None:
            texture = len(textures)
            textures.append({"source": image})
            texture_by_image[image] = texture
        info = {"index": texture}
        for material in material_rows:
            for channel in standard["channels"]:
                if channel == "baseColor":
                    material.setdefault("pbrMetallicRoughness", {})["baseColorTexture"] = dict(info)
                elif channel == "normal":
                    material["normalTexture"] = dict(info)
                elif channel == "orm":
                    material.setdefault("pbrMetallicRoughness", {})["metallicRoughnessTexture"] = dict(info)
                    material["occlusionTexture"] = dict(info)
    buffers[0]["byteLength"] = len(binary)
    if rows:
        extras = root.get("extras")
        if not isinstance(extras, dict):
            extras = {}
            root["extras"] = extras
        extras[TEXTURE_TRANSPORT_EXTRAS] = {
            "version": 1, "bindings": carrier_rows,
        }
    _gf2_drop_empty_gltf_arrays(root)
    _gf2_glb_write(path, root, binary)


def _gf2_drop_empty_gltf_arrays(root):
    """glTF forbids an empty top-level array, and the app's reader refuses the whole file over one. An
    all-hash-only append leaves the 'images' list it prepared empty; drop that and any other empty
    top-level list before writing."""
    for key in [k for k, v in list(root.items()) if isinstance(v, list) and not v]:
        del root[key]


def _gf2_strip_empty_gltf_arrays(path):
    """The no-append sanitizer for an image-less export: a send with nothing to append still must not
    ship an empty top-level array the exporter may have left."""
    root, binary = _gf2_glb_read(path)
    if any(isinstance(v, list) and not v for v in root.values()):
        _gf2_drop_empty_gltf_arrays(root)
        _gf2_glb_write(path, root, binary)


def _gf2_material_for_binding(mesh, row):
    """The projected primitive material, or a new unprojected slot for surplus inventory."""
    owner = row.get("owner", {})
    slot = owner.get("primitive")
    if not isinstance(slot, int):
        slot = owner.get("material", 0)
    while len(mesh.data.materials) <= slot:
        material = bpy.data.materials.new(name=f"GF2 material {len(mesh.data.materials) + 1}")
        material.use_nodes = True
        mesh.data.materials.append(material)
    material = mesh.data.materials[slot]
    if material is None:
        material = bpy.data.materials.new(name=f"GF2 material {slot + 1}")
        material.use_nodes = True
        mesh.data.materials[slot] = material
    material.use_nodes = True
    return material


def _gf2_replace_link(tree, output, input_socket):
    for link in list(input_socket.links):
        tree.links.remove(link)
    tree.links.new(output, input_socket)


def _gf2_connect_static_semantic(material, image_node, semantic):
    """Connect only semantics a static Principled graph represents honestly."""
    tree = material.node_tree
    principled = next((node for node in tree.nodes if node.type == "BSDF_PRINCIPLED"), None)
    if principled is None:
        return
    if semantic == "baseColor":
        if principled.inputs.get("Base Color"):
            _gf2_replace_link(tree, image_node.outputs["Color"], principled.inputs["Base Color"])
        if principled.inputs.get("Alpha"):
            _gf2_replace_link(tree, image_node.outputs["Alpha"], principled.inputs["Alpha"])
    elif semantic == "normal" and principled.inputs.get("Normal"):
        normal = tree.nodes.new("ShaderNodeNormalMap")
        normal.name = "GF2 packed normal preview"
        tree.links.new(image_node.outputs["Color"], normal.inputs["Color"])
        _gf2_replace_link(tree, normal.outputs["Normal"], principled.inputs["Normal"])
    elif semantic == "rmo":
        try:
            separate = tree.nodes.new("ShaderNodeSeparateColor")
            red, green, blue = "Red", "Green", "Blue"
        except RuntimeError:
            separate = tree.nodes.new("ShaderNodeSeparateRGB")
            red, green, blue = "R", "G", "B"
        separate.name = "GF2 ORM preview"
        tree.links.new(image_node.outputs["Color"], separate.inputs[0])
        if principled.inputs.get("Roughness"):
            _gf2_replace_link(tree, separate.outputs[green], principled.inputs["Roughness"])
        if principled.inputs.get("Metallic"):
            _gf2_replace_link(tree, separate.outputs[blue], principled.inputs["Metallic"])


def _gf2_uv_layer_name(mesh, tex_coord):
    """The imported layer at one glTF TEXCOORD index, without assuming Blender's display name."""
    if type(tex_coord) is not int or tex_coord < 0:
        return None
    layers = getattr(getattr(mesh, "data", None), "uv_layers", None)
    if layers is None or tex_coord >= len(layers):
        return None
    name = getattr(layers[tex_coord], "name", None)
    return name if isinstance(name, str) and name else None


def _gf2_pin_texture_coordinate(mesh, material, image_node, tex_coord):
    """Pin a tagged image to its explicitly carried glTF TEXCOORD set. No output shading link is added."""
    layer_name = _gf2_uv_layer_name(mesh, tex_coord)
    if layer_name is None:
        print(f"GF2: could not pin {getattr(image_node, 'name', 'a texture')} to TEXCOORD_{tex_coord}; "
              "the required UV layer is absent.")
        return False
    tree = material.node_tree
    vector = image_node.inputs.get("Vector")
    if vector is None:
        print(f"GF2: could not pin {getattr(image_node, 'name', 'a texture')} to TEXCOORD_{tex_coord}; "
              "the image has no Vector input.")
        return False
    uv_map = next((node for node in tree.nodes
                   if getattr(node, "bl_idname", "") == "ShaderNodeUVMap"
                   and node.get(TEXTURE_TRANSPORT_UV_NODE) == tex_coord), None)
    if uv_map is None:
        uv_map = tree.nodes.new("ShaderNodeUVMap")
        uv_map.name = f"GF2 UV{tex_coord}"
        uv_map.label = f"GF2: TEXCOORD_{tex_coord}"
        uv_map[TEXTURE_TRANSPORT_UV_NODE] = tex_coord
    uv_map.uv_map = layer_name
    uv = uv_map.outputs.get("UV")
    if uv is None:
        print(f"GF2: could not pin {getattr(image_node, 'name', 'a texture')} to TEXCOORD_{tex_coord}; "
              "the UV Map node has no UV output.")
        return False
    _gf2_replace_link(tree, uv, vector)
    return True


def _gf2_transport_mesh(imported_meshes, name):
    """The imported mesh a carrier row names, or None. Blender suffixes an imported object whose name the
    startup scene already holds (`body.001`), so the exact name is tried first and the suffix-stripped one
    second; a row that matches neither is the caller's to report."""
    if not isinstance(name, str):
        return None
    for mesh in imported_meshes:
        if mesh.name == name:
            return mesh
    for mesh in imported_meshes:
        if _base_name(mesh.name) == name:
            return mesh
    return None


def _gf2_install_texture_transport(glb_path, imported_meshes):
    """Materialize every carrier row as a tagged Blender image node. A row whose mesh is not in this
    import is named in a popup rather than dropped: its textures cannot be edited from a node that was
    never made, and nothing else would say so."""
    rows = _gf2_read_texture_transport(glb_path)
    installed = 0
    preview_warnings = []
    missing = []
    for row in rows:
        png = row.pop("png", None)
        if png is None:
            continue
        owner = row.get("owner", {})
        mesh = _gf2_transport_mesh(imported_meshes, owner.get("mesh"))
        if mesh is None:
            if owner.get("mesh") not in missing:
                missing.append(owner.get("mesh"))
            continue
        image_name = row.pop("image_name", "GF2 texture")
        handle, temp_path = tempfile.mkstemp(suffix=".png")
        os.close(handle)
        try:
            with open(temp_path, "wb") as f:
                f.write(png)
            image = bpy.data.images.load(temp_path, check_existing=False)
            image.name = image_name
            image.pack()
            image[TEXTURE_TRANSPORT_IMAGE_HASH] = row.get("outbound_hash", "")
            image[TEXTURE_TRANSPORT_IMAGE_PATH] = image.filepath_raw
        finally:
            try:
                os.remove(temp_path)
            except OSError:
                pass
        try:
            image.colorspace_settings.name = "sRGB" if row.get("srgb") is True else "Non-Color"
        except (TypeError, ValueError):
            pass
        material = _gf2_material_for_binding(mesh, row)
        node = material.node_tree.nodes.new("ShaderNodeTexImage")
        node.image = image
        node.name = "GF2 " + row.get("property", "texture")
        node.label = row.get("property", "texture")
        node[TEXTURE_TRANSPORT_NODE] = json.dumps(row, separators=(",", ":"))
        if "texCoord" in row:
            tex_coord = row.get("texCoord")
            if not _gf2_pin_texture_coordinate(mesh, material, node, tex_coord):
                preview_warnings.append((
                    f"'{row.get('property', 'texture')}' on '{gf2_label(mesh.name)}' could not use "
                    f"TEXCOORD_{tex_coord}.",
                    "The image was imported without a material preview."))
                continue
        _gf2_connect_static_semantic(material, node, row.get("semantic"))
        installed += 1
    for name in missing:
        shown = gf2_label(name) if isinstance(name, str) else str(name)
        preview_warnings.append((
            f"'{shown}' is not in this scene.",
            "Its textures were not installed and cannot be edited here."))
    for lead, detail in preview_warnings:
        print("GF2: " + lead + " " + detail)
    if preview_warnings:
        lines = [line for lead, detail in preview_warnings for line in ("⚠ " + lead, detail)]
        _popup("Texture Preview Warnings", lines, 'INFO')
    return installed


def _gf2_image_png(image):
    """Save one Blender image as PNG bytes without changing the image's lasting file path."""
    handle, temp_path = tempfile.mkstemp(suffix=".png")
    os.close(handle)
    old_path = getattr(image, "filepath_raw", "")
    old_format = getattr(image, "file_format", "PNG")
    try:
        image.filepath_raw = temp_path
        image.file_format = "PNG"
        image.save()
        with open(temp_path, "rb") as f:
            return f.read()
    finally:
        image.filepath_raw = old_path
        image.file_format = old_format
        try:
            os.remove(temp_path)
        except OSError:
            pass


def _gf2_shipping_materials(mesh):
    """The materials a mesh object draws with, slot by slot: the object's own override where a slot is
    linked to the object, the mesh data's material otherwise. The same read the baseline snapshot and the
    slot check make, so a texture edited on an object-linked material is not invisible to the send."""
    slots = getattr(mesh, "material_slots", None)
    if slots is not None:
        return [getattr(slot, "material", None) for slot in slots]
    return list(mesh.data.materials)


def _gf2_standard_image_channels(image_node):
    """Standard glTF channels reached downstream from one untagged image node, walked through any
    intermediate colour nodes. Visited nodes are keyed by name — unique per tree — because Blender
    recreates the link wrapper objects between reads and their addresses collide."""
    reached = set()
    pending = [link for output in getattr(image_node, "outputs", [])
               for link in getattr(output, "links", [])]
    visited = set()
    while pending:
        link = pending.pop()
        node = getattr(link, "to_node", None)
        if node is None:
            continue
        marker = getattr(node, "name", None) or str(id(node))
        if marker in visited:
            continue
        visited.add(marker)
        socket = getattr(link, "to_socket", None)
        if getattr(node, "type", None) == "BSDF_PRINCIPLED":
            name = getattr(socket, "name", "")
            if name == "Base Color":
                reached.add("baseColor")
            elif name == "Normal":
                reached.add("normal")
            elif name in {"Metallic", "Roughness"}:
                reached.add("orm")
            continue
        for output in getattr(node, "outputs", []):
            pending.extend(getattr(output, "links", []))
    return [channel for channel in ("baseColor", "normal", "orm") if channel in reached]


def _gf2_collect_standard_channels(meshes):
    """Collect untagged image nodes on supported PBR routes, keyed by material name — the join the
    exported glb offers. This is how a material built by hand, or a whole legacy session with no tagged
    nodes, keeps its pictures now that the exporter itself embeds none. Returns the rows and the
    warnings the send popup owes: a picture that could not be read, and a channel two pictures reach
    (the first one seen is sent)."""
    materials = []
    seen = set()
    for mesh in meshes:
        for material in _gf2_shipping_materials(mesh):
            if material is None or id(material) in seen:
                continue
            seen.add(id(material))
            materials.append(material)

    rows = []
    warnings = []
    for material in materials:
        tree = getattr(material, "node_tree", None)
        if tree is None:
            continue
        claimed = {}
        for node in tree.nodes:
            if hasattr(node, "get") and node.get(TEXTURE_TRANSPORT_NODE) is not None:
                continue
            image = getattr(node, "image", None)
            if image is None or getattr(node, "type", None) != "TEX_IMAGE":
                continue
            fresh = []
            for channel in _gf2_standard_image_channels(node):
                if channel in claimed:
                    if claimed[channel] != image.name:
                        warnings.append(f"Two pictures reach the {channel} channel on "
                                        f"'{getattr(material, 'name', '')}'; "
                                        f"'{claimed[channel]}' was sent.")
                else:
                    fresh.append(channel)
            if not fresh:
                continue
            try:
                png = _gf2_image_png(image)
            except (TypeError, ValueError, OSError, RuntimeError) as error:
                print(f"GF2: could not carry {image.name} back: {error}")
                warnings.append(f"'{image.name}' on '{getattr(material, 'name', '')}' could not be "
                                "read and was not sent.")
                continue
            for channel in fresh:
                claimed[channel] = image.name
            rows.append({"material": material.name, "channels": fresh,
                         "png": png, "image_name": image.name})
    return rows, warnings


def _gf2_image_is_unchanged(image, row):
    """Whether this is the clean, packed image datablock installed for this exact outbound row. Decided
    from touch tracking, never from pixels: the image was never seen dirty this session, still carries
    the install stamps for THIS row, still sits packed at its recorded (deleted) install path, and no
    file has reappeared there — a reappeared file is a user save. Any doubt ships the bytes."""
    if image is None or not hasattr(image, "get"):
        return False
    if getattr(image, "is_dirty", True):
        return False
    if image.get(TEXTURE_TRANSPORT_IMAGE_TOUCHED):
        return False
    if hasattr(image, "as_pointer") and image.as_pointer() in _GF2_DIRTY_SEEN:
        return False
    packed = getattr(image, "packed_file", None) is not None
    if not packed:
        try:
            packed = len(image.packed_files) > 0
        except (AttributeError, TypeError):
            packed = False
    if not packed:
        return False
    outbound_hash = row.get("outbound_hash")
    if not (isinstance(outbound_hash, str) and outbound_hash):
        return False
    if image.get(TEXTURE_TRANSPORT_IMAGE_HASH) != outbound_hash:
        return False
    recorded_path = image.get(TEXTURE_TRANSPORT_IMAGE_PATH)
    if recorded_path != getattr(image, "filepath_raw", None):
        return False
    if isinstance(recorded_path, str) and recorded_path and os.path.exists(recorded_path):
        return False
    return True


def _gf2_collect_texture_transport(meshes):
    """Read exact-property tags only from materials owned by shipping meshes. Returns the rows and the
    duplicates: a tag two nodes carry (a duplicated node, a duplicated or copied material) names one slot
    twice, the first one seen is sent, and the caller says so. An untouched picture ships as its row
    alone; a touched one is stamped as touched — the send's own save clears Blender's dirty flag, and
    without the stamp the NEXT send would read the picture as clean under a hash the app never saw."""
    rows = []
    seen = set()
    duplicates = []
    for mesh in meshes:
        for material in _gf2_shipping_materials(mesh):
            tree = getattr(material, "node_tree", None) if material is not None else None
            if tree is None:
                continue
            for node in tree.nodes:
                raw = node.get(TEXTURE_TRANSPORT_NODE) if hasattr(node, "get") else None
                image = getattr(node, "image", None)
                if not isinstance(raw, str) or image is None:
                    continue
                try:
                    row = json.loads(raw)
                    owner = row.get("owner", {})
                    key = (owner.get("mesh"), owner.get("material"), owner.get("primitive"),
                           row.get("property"))
                    if key in seen:
                        duplicates.append((row.get("property", "texture"),
                                           getattr(material, "name", "") or "",
                                           getattr(mesh, "name", "") or ""))
                        continue
                    seen.add(key)
                    row.pop("image", None)
                    if not _gf2_image_is_unchanged(image, row):
                        # Only dirt is volatile: the send's own save below clears Blender's flag, so it
                        # is stamped here. Every other "changed" reason persists on the datablock.
                        if getattr(image, "is_dirty", False) and hasattr(image, "get") \
                                and not image.get(TEXTURE_TRANSPORT_IMAGE_TOUCHED):
                            image[TEXTURE_TRANSPORT_IMAGE_TOUCHED] = True
                        row["png"] = _gf2_image_png(image)
                        row["image_name"] = image.name
                    rows.append(row)
                except (TypeError, ValueError, OSError) as error:
                    print(f"GF2: could not carry {getattr(node, 'name', 'a texture')} back: {error}")
    return rows, duplicates


def gf2_duplicate_tag_lines(duplicates):
    """One line per slot two nodes claimed, in the words of the send popup."""
    return [f"Two nodes carry '{prop}' on '{material}' ({gf2_label(mesh)}); the first one was sent."
            for prop, material, mesh in duplicates]

# The two empty-scope refusals, shared by the checks and the send choke point so a scene the send
# would refuse never reads as ready. Nothing attributed is a layout mistake; every part deliberately
# emptied is a Hide with nothing left to send alongside it.
NO_PART_MESH = (f"There are no part collections under {MOD_COLLECTION}, so a send carries nothing. "
                "Re-open the part from Doll Remolding Lab to recreate the part collections.")
EVERY_PART_EMPTY = ("Every part collection is empty, so a send carries nothing. Move a mesh into "
                    "at least one part collection.")


def _ensure_collection(name, parent):
    """The `name` child of `parent`, created and linked when it is not there yet.

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
    """The view layer's entry for `coll`, or None when the layer does not carry it. An EXCLUDED
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
    there. Advisory: when it cannot be set, new geometry lands at the scene root, which the
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


def gf2_emptied_part_line(name):
    """The Check Mesh remedy for one part collection with no geometry."""
    return (f"{gf2_label(name)} is emptied and sends as a hide. To keep the part, move a mesh back "
            "into the collection.")


def gf2_emptied_part_confirm_line(name):
    """The terse confirmation and post-send line for an emptied part."""
    return f"{gf2_label(name)} is emptied and sends as a hide."


def gf2_overwrite_warning(edit_labels):
    """The confirmation lead for selected edits that currently hold authored mesh work."""
    if not edit_labels:
        return None
    return "Sending replaces the mesh work stored in " + ", ".join(edit_labels) + "."


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


def _read_live_session(glb_path):
    """Read one complete session document, or None while the file is absent or unreadable.

    Unlike ``read_session``, this helper has no broadening fallback. Panel refresh and Send use it after a
    scene snapshot exists, so a transient app-side atomic replacement must retain that snapshot rather than
    briefly turn every mesh into a writable part.
    """
    if not glb_path:
        return None
    try:
        with open(session_path(glb_path), "r", encoding="utf-8") as f:
            got = json.load(f)
    except Exception:
        return None
    if not isinstance(got, dict):
        return None
    got.setdefault("part", None)
    got.setdefault("parts", [])
    return got


def gf2_session_revision(session):
    """The non-negative integer revision of a session, with legacy documents at revision zero."""
    revision = (session or {}).get("revision", 0)
    return revision if isinstance(revision, int) and not isinstance(revision, bool) and revision >= 0 else 0


def gf2_newer_session(current, incoming):
    """The incoming document only when a readable monotonic revision advances the scene snapshot."""
    if not isinstance(incoming, dict):
        return None
    return incoming if gf2_session_revision(incoming) > gf2_session_revision(current) else None


def _store_session(session, scene=None):
    try:
        target_scene = scene if scene is not None else bpy.context.scene
        target_scene[SESSION_KEY] = json.dumps(session)
    except Exception as e:
        print(f"GF2: could not record the session description: {e}")


def load_session(scene=None):
    """The session description recorded at import. Empty when the scene never carried one."""
    target_scene = scene if scene is not None else bpy.context.scene
    raw = target_scene.get(SESSION_KEY)
    if not raw:
        return {"part": None, "parts": []}
    try:
        return json.loads(raw)
    except Exception:
        return {"part": None, "parts": []}


def gf2_session_parts(session, mesh_names):
    """Which of the imported meshes get a part collection: the one the app named, or all of them when
    it named none, minus any the app declared unwritable. A name the glb does not carry yields nothing,
    so the attribution checks report an empty `Mod` rather than the session quietly widening to the
    whole outfit. A part entry with no `writable` key is writable: a hand-opened glb has no session
    description at all, and every mesh in it is a part."""
    want = session.get("part")
    if want:
        return [n for n in mesh_names if n == want]
    blocked = {p.get("name") for p in (session.get("parts") or [])
               if isinstance(p, dict) and p.get("writable") is False}
    return [n for n in mesh_names if n not in blocked]


def gf2_claimed_meshes(session, mesh_names):
    """Which mesh stands in for a declared writable part the glb does not carry, as
    ``{mesh name: part name}``. An edited part's workspace glb holds the modder's mesh under the
    modder's own name, so at re-open the name the app declared matches nothing. When exactly one
    declared writable part is missing from the glb and exactly one mesh matches no declared part,
    that mesh IS the part and its collection carries the part's contract name. Anything less exact
    claims nothing — a mesh files under Reference rather than into the wrong part."""
    declared = {p.get("name") for p in (session.get("parts") or [])
                if isinstance(p, dict) and isinstance(p.get("name"), str)}
    want = session.get("part")
    if want:
        declared.add(want)
        writable = [want]
    else:
        blocked = {p.get("name") for p in (session.get("parts") or [])
                   if isinstance(p, dict) and p.get("writable") is False}
        writable = [n for n in declared if n not in blocked]
    missing = [n for n in writable if n not in mesh_names]
    strays = [n for n in mesh_names if n not in declared]
    if len(missing) == 1 and len(strays) == 1:
        return {strays[0]: missing[0]}
    return {}


def gf2_unskinned_parts(session):
    """The part names the app declared unskinned — a static-renderer prop's mesh, which carries no
    weights and whose session has no armature. Their vertices are outside the weight gate: counting
    them as unweighted would block every send of such a part. Only an EXPLICIT true exempts a part, so
    a skinned part whose armature the modder deleted still blocks, and a session the app didn't stamp
    reads as fully skinned."""
    return {p.get("name") for p in (session.get("parts") or [])
            if isinstance(p, dict) and p.get("unskinned") is True and isinstance(p.get("name"), str)}


def _session_part_entry(session, name):
    return next((p for p in (session or {}).get("parts") or []
                 if isinstance(p, dict) and p.get("name") == name), {})


def _target_state_map(rows):
    """Plain ``{part: {target, new_name}}`` state from row dictionaries or Blender rows."""
    if isinstance(rows, dict):
        return rows
    result = {}
    for row in rows or ():
        if isinstance(row, dict):
            part = row.get("part") or row.get("part_name")
            target = row.get("target")
            new_name = row.get("new_name", "")
        else:
            part = getattr(row, "part_name", "")
            target = getattr(row, "target", "")
            new_name = getattr(row, "new_name", "")
        if isinstance(part, str) and part:
            result[part] = {"target": target, "new_name": new_name}
    return result


def _session_edit_rows(part):
    """The valid inventory rows for one session part, preserving the app's order."""
    rows, seen = [], set()
    for edit in part.get("edits") or []:
        if not isinstance(edit, dict):
            continue
        edit_id = edit.get("id")
        if not isinstance(edit_id, str) or not edit_id or edit_id in seen:
            continue
        label = edit.get("label")
        rows.append({"id": edit_id,
                     "label": label if isinstance(label, str) and label else edit_id,
                     "holdsAuthoredMesh": edit.get("holdsAuthoredMesh") is True})
        seen.add(edit_id)
    opened = part.get("editId")
    # A legacy or partially written contract can name the opened edit without inventory. Keeping a
    # synthetic choice is the only way to honor the stated opened-from default without inventing a New.
    if isinstance(opened, str) and opened and opened not in seen:
        rows.append({"id": opened, "label": opened,
                     "holdsAuthoredMesh": part.get("edited") is True})
    return rows


def _snapshot_minted_edit(part, part_name, snapshot):
    """The inventory edit minted for one snapshotted New target, or None until it is unambiguous."""
    sent = ((snapshot or {}).get("targets") or {}).get(part_name)
    if not isinstance(sent, dict) or not isinstance(sent.get("new"), str):
        return None
    known = set(((snapshot or {}).get("knownEditIds") or {}).get(part_name) or ())
    new_edits = [edit for edit in _session_edit_rows(part) if edit["id"] not in known]
    match_name = ((snapshot or {}).get("newMatches") or {}).get(part_name)
    matches = [edit for edit in new_edits
               if isinstance(match_name, str) and edit["label"] == match_name]
    if matches:
        return matches[0]
    return new_edits[0] if len(new_edits) == 1 else None


def gf2_target_row_specs(session, part_names=None, previous=None, acknowledged_snapshot=None):
    """Build stable, plain target-row specifications from a session document.

    ``previous`` preserves panel choices across a live revision refresh. An acknowledged existing-id
    target never displaces that live choice; acknowledgement only promotes a sent New target to the newly
    added inventory edit whose label matches the sent name (or the prior default for a blank name), or to
    the sole new identity when the app had to replace that name.
    """
    session = session or {}
    if part_names is None:
        named = session.get("part")
        if isinstance(named, str) and named:
            part_names = [named]
        else:
            part_names = [p.get("name") for p in session.get("parts") or []
                          if isinstance(p, dict) and isinstance(p.get("name"), str)
                          and p.get("name") and p.get("writable") is not False]
    previous = _target_state_map(previous)
    acknowledged = (acknowledged_snapshot or {}).get("targets") or {}
    specs = []
    for name in part_names or ():
        if not isinstance(name, str) or not name:
            continue
        part = _session_part_entry(session, name)
        if part.get("writable") is False:
            continue
        edits = _session_edit_rows(part)
        edit_ids = {edit["id"] for edit in edits}
        default_name = part.get("defaultEditName")
        default_name = default_name if isinstance(default_name, str) else ""
        target = None
        new_name = default_name

        sent_target = acknowledged.get(name)
        if isinstance(sent_target, dict) and isinstance(sent_target.get("new"), str):
            # Promote ONLY a row whose edit actually minted; a part whose send landed nothing
            # keeps whatever its row says now, exactly like an existing-id target does.
            minted = _snapshot_minted_edit(part, name, acknowledged_snapshot)
            if minted is not None:
                new_name = sent_target["new"]
                target = minted["id"]

        if target is None:
            old = previous.get(name) or {}
            old_target = old.get("target")
            if old_target == NEW_EDIT_TARGET:
                target = NEW_EDIT_TARGET
                old_name = old.get("new_name")
                new_name = old_name if isinstance(old_name, str) else default_name
            elif isinstance(old_target, str) and old_target in edit_ids:
                target = old_target
            else:
                opened = part.get("editId")
                target = opened if isinstance(opened, str) and opened in edit_ids else NEW_EDIT_TARGET

        specs.append({"part": name, "label": gf2_label(name), "edits": edits,
                      "target": target, "new_name": new_name})
    return specs


def gf2_edit_targets(rows):
    """Serialize row selections to the sidecar's existing-id/new-edit union."""
    result = {}
    for part, state in _target_state_map(rows).items():
        target = state.get("target")
        if target == NEW_EDIT_TARGET:
            name = state.get("new_name")
            result[part] = {"new": name if isinstance(name, str) else ""}
        elif isinstance(target, str) and target:
            result[part] = target
    return result


def gf2_default_edit_targets(session, part_names=None):
    """The initial selections for a headless Send, where no Blender CollectionProperty exists."""
    return gf2_edit_targets(gf2_target_row_specs(session, part_names))


def gf2_send_target_map(session, rows=(), part_names=None):
    """Capture one Send's target map. A run with no session inventory emits no target stamps."""
    parts = (session or {}).get("parts")
    if not isinstance(parts, list) or not parts:
        return {}
    targets = gf2_edit_targets(rows)
    return targets or gf2_default_edit_targets(session, part_names)


def gf2_selected_mesh_edit_labels(session, edit_targets):
    """Labels of selected existing edits whose live inventory says authored mesh work is present."""
    selected = []
    seen_ids = set()
    for part in (session or {}).get("parts") or []:
        if not isinstance(part, dict):
            continue
        name = part.get("name")
        edit_id = edit_targets.get(name) if isinstance(edit_targets, dict) else None
        if not isinstance(edit_id, str) or edit_id in seen_ids:
            continue
        edit = next((row for row in _session_edit_rows(part) if row["id"] == edit_id), None)
        if edit is not None and edit["holdsAuthoredMesh"]:
            selected.append((edit_id, gf2_label(name), edit["label"]))
            seen_ids.add(edit_id)
    label_counts = {}
    for _edit_id, _part_label, edit_label in selected:
        label_counts[edit_label] = label_counts.get(edit_label, 0) + 1
    return [(f"{part_label} — {edit_label}" if label_counts[edit_label] > 1 else edit_label)
            for _edit_id, part_label, edit_label in selected]


def gf2_send_snapshot(session, edit_targets):
    """Capture the pre-send target state that may be promoted after a revision acknowledgment."""
    known, matches = {}, {}
    for part in (session or {}).get("parts") or []:
        if not isinstance(part, dict) or not isinstance(part.get("name"), str):
            continue
        name = part["name"]
        known[name] = [edit["id"] for edit in _session_edit_rows(part)]
        target = edit_targets.get(name)
        if isinstance(target, dict) and isinstance(target.get("new"), str):
            requested = target["new"]
            default = part.get("defaultEditName")
            matches[name] = requested if requested != "" else (default if isinstance(default, str) else "")
    return {"revision": gf2_session_revision(session), "targets": edit_targets,
            "newMatches": matches, "knownEditIds": known}


def gf2_send_sidecar(hidden_parts, edit_targets, session):
    """The write-complete sidecar document, pure so the target union is testable without Blender."""
    parts = (session or {}).get("parts")
    targets = dict(edit_targets) if isinstance(parts, list) and parts else {}
    return {"source": "blender-send", "hiddenParts": list(hidden_parts), "editIds": targets}


def _gf2_target_items(row, context):
    return _TARGET_ITEM_REFS.get(getattr(row, "part_name", ""), _TARGET_FALLBACK_ITEMS)


def _scene_target_states(scene):
    return _target_state_map(getattr(scene, "gf2_target_rows", ()))


def _rebuild_target_rows(scene, session, previous=None, acknowledged_snapshot=None):
    """Replace the scene's persistent target rows and their Python-held EnumProperty item tuples."""
    rows = getattr(scene, "gf2_target_rows", None)
    if rows is None:
        return []
    part_names = [part.name for part in gf2_part_collections()]
    specs = gf2_target_row_specs(session, part_names, previous, acknowledged_snapshot)
    rows.clear()
    _TARGET_ITEM_REFS.clear()
    for spec in specs:
        items = tuple([(edit["id"], edit["label"], "") for edit in spec["edits"]]
                      + [(NEW_EDIT_TARGET, "New Edit", "")])
        _TARGET_ITEM_REFS[spec["part"]] = items
        row = rows.add()
        row.part_name = spec["part"]
        row.part_label = spec["label"]
        row.new_name = spec["new_name"]
        row.target = spec["target"]
    return specs


def _load_send_snapshot(scene=None):
    scene = scene if scene is not None else bpy.context.scene
    raw = scene.get(SEND_SNAPSHOT_KEY)
    if not raw:
        return None
    try:
        got = json.loads(raw)
    except Exception:
        return None
    return got if isinstance(got, dict) else None


def _store_send_snapshot(snapshot, scene=None):
    try:
        target_scene = scene if scene is not None else bpy.context.scene
        target_scene[SEND_SNAPSHOT_KEY] = json.dumps(snapshot)
    except Exception as e:
        print(f"GF2: could not record the pending Send snapshot: {e}")


def _refresh_session_snapshot(scene=None, glb_path=None):
    """Adopt a higher readable revision and consume a pending Send it acknowledges.

    Acknowledgement is PER PART inside the row rebuild: a New target whose minted edit appears in
    the adopted inventory promotes its row, and a part whose send landed nothing (an unchanged
    part on an all-parts send) keeps its row as it was. The session file belongs to this run
    alone, so any revision past the snapshot's means the app processed that send — the snapshot
    is consumed either way rather than lingering on parts that will never mint."""
    scene = scene if scene is not None else bpy.context.scene
    path = glb_path if glb_path is not None else (getattr(scene, "gf2_glb_path", "") or "")
    incoming = _read_live_session(path)
    current = load_session(scene)
    adopted = gf2_newer_session(current, incoming)
    if adopted is None:
        return current
    incoming = adopted
    previous = _scene_target_states(scene)
    pending = _load_send_snapshot(scene)
    acknowledged = (pending if pending is not None
                    and gf2_session_revision(incoming) > gf2_session_revision(pending) else None)
    _store_session(incoming, scene)
    _rebuild_target_rows(scene, incoming, previous, acknowledged)
    if acknowledged is not None:
        try:
            del scene[SEND_SNAPSHOT_KEY]
        except Exception:
            pass
    return incoming


def _session_file_mtime(glb_path):
    if not glb_path:
        return None
    try:
        return os.stat(session_path(glb_path)).st_mtime_ns
    except OSError:
        return None


def _prime_session_refresh(glb_path):
    """Record the current sidecar signature without opening it."""
    _SESSION_FILE_STATE["path"] = glb_path or ""
    _SESSION_FILE_STATE["mtime"] = _session_file_mtime(glb_path)


def _session_refresh_tick():
    """Periodically adopt a changed session file without doing any work from panel draw."""
    _gf2_note_dirty_images(stamp=True)
    try:
        scene = bpy.context.scene
        path = getattr(scene, "gf2_glb_path", "") or ""
        mtime = _session_file_mtime(path)
        changed = path != _SESSION_FILE_STATE["path"] or mtime != _SESSION_FILE_STATE["mtime"]
        if changed:
            _SESSION_FILE_STATE["path"] = path
            _SESSION_FILE_STATE["mtime"] = mtime
            if mtime is not None:
                before = gf2_session_revision(load_session(scene))
                after = gf2_session_revision(_refresh_session_snapshot(scene, path))
                if after != before:
                    _tag_sidebar_redraw()
    except Exception as e:
        print(f"GF2: session refresh timer failed: {e}")
    return _SESSION_REFRESH_INTERVAL


def _is_unskinned(obj, unskinned):
    """Whether this object ships as one of the declared unskinned parts. The exemption belongs to the
    PART, and a part is a collection — so the collection the object ships in is what answers, never the
    object's own name. A mesh someone named after a static prop, sitting inside a skinned part, is that
    part's and stays inside the weight gate. A duplicate's `.001` suffix is dropped: a collection that
    had to take a suffix is still the part it was duplicated from. An object in no part collection
    ships as nothing and is exempt from nothing."""
    part = _part_of(obj)
    return part is not None and (part.name in unskinned or _base_name(part.name) in unskinned)


def gf2_part_viewport_visible(session, part_name):
    """The app's initial viewport choice for a declared part. Missing metadata remains visible."""
    return _session_part_entry(session or {}, part_name).get("viewportVisible") is not False


def gf2_build_collections(meshes, armature, session=None):
    """Lay out the scene so each part's attribution is a collection: one collection per part under
    `Mod`, the armature in its own, and a `Reference` collection holding the rest of the outfit and
    any scenery.

    The active collection is left on the single part when the session has one and on `Mod` when it
    has several, so geometry the modder adds lands attributed where that is unambiguous and
    unattributed — never MIS-attributed — where it is not."""
    scene_root = bpy.context.scene.collection
    mod = _ensure_collection(MOD_COLLECTION, scene_root)
    reference = _ensure_collection(REFERENCE_COLLECTION, scene_root)
    names = [mo.name for mo in meshes]
    writable = set(gf2_session_parts(session or {}, names))
    claims = gf2_claimed_meshes(session or {}, names)
    parts = []
    for mo in meshes:
        part_name = claims.get(mo.name) or (mo.name if mo.name in writable else None)
        if part_name is None:
            _move_to_collection(mo, reference)   # context: never shipped
            contract_name = mo.name
        else:
            contract_name = part_name
            if part_name != mo.name:
                print(f"GF2: '{mo.name}' opened as part '{part_name}'")
            part = _ensure_collection(part_name, mod)
            part[PART_MARKER] = part_name   # marker carries the contract name; a rename is detectable
            _move_to_collection(mo, part)
            parts.append(part)
        if not gf2_part_viewport_visible(session or {}, contract_name):
            _set_hidden(mo, True)
    if armature is not None:
        _move_to_collection(armature, _ensure_collection(ARMATURE_COLLECTION, mod))
    _activate_collection(parts[0] if len(parts) == 1 else mod)


# ---------------------------------------------------------------- core import/export

def _data_identity(value):
    """Stable identity for a Blender datablock, with a plain-object fallback for unit fixtures."""
    pointer = getattr(value, "as_pointer", None)
    return pointer() if callable(pointer) else id(value)


def _gf2_new_objects(objects, object_type, pre_existing_names):
    """Derive an import's still-live objects from the current Blender collection.

    Callers invoke this again after cleanup rather than retaining bpy wrappers for objects cleanup may
    remove: reading any property from such a wrapper raises ``ReferenceError``.
    """
    return [obj for obj in objects if obj.type == object_type and obj.name not in pre_existing_names]


def _gf2_use_dithered_transparency(material):
    """Select the non-overlapping transparency fallback exposed by this Blender version."""
    if getattr(material, "surface_render_method", None) is not None:
        material.surface_render_method = "DITHERED"
    else:
        material.blend_method = "HASHED"


def gf2_prepare_imported_alpha_materials(imported_objects, pre_existing_materials=()):
    """Apply the EEVEE preview contract to BLENDED materials owned by this import only.

    ``imported_objects`` and ``pre_existing_materials`` are explicit parameters so this helper never
    scans or mutates the wider scene. A material already present before the glTF operator is excluded
    even if an imported object happens to reference it. For each remaining BLENDED material, keep the
    blended render method, keep both sides visible, disable same-material transparency overlap, and map
    texture alpha 0..254/255 to Principled alpha 0..1.

    The standard glTF importer connects the base-colour image's Alpha directly to Principled Alpha when
    the writer invariants documented at the shape check hold. If the running Blender cannot provide the
    overlap control or the graph does not have that safe shape, the material is switched to dithered
    transparency without partially applying the blended-preview settings. A tagged node makes the graph
    operation idempotent even when its display name is occupied.
    """
    excluded = {_data_identity(material) for material in pre_existing_materials}
    seen = set()
    blended = []
    for obj in imported_objects:
        data = getattr(obj, "data", None)
        for material in (getattr(data, "materials", None) or ()):
            if material is None:
                continue
            identity = _data_identity(material)
            if identity in excluded or identity in seen:
                continue
            seen.add(identity)
            method = getattr(material, "surface_render_method", None)
            is_blended = (method == "BLENDED" if method is not None
                          else getattr(material, "blend_method", None) == "BLEND")
            if is_blended:
                blended.append(material)

    missing_overlap = []
    missing_alpha_link = []
    remapped = 0
    for material in blended:
        overlap_property = None
        if hasattr(material, "use_transparency_overlap"):
            overlap_property = "use_transparency_overlap"
        elif hasattr(material, "show_transparent_back"):
            overlap_property = "show_transparent_back"
        else:
            missing_overlap.append(material.name)

        tree = getattr(material, "node_tree", None)
        if tree is None:
            missing_alpha_link.append(material.name)
            _gf2_use_dithered_transparency(material)
            continue
        principled = next((node for node in tree.nodes if node.type == "BSDF_PRINCIPLED"), None)
        alpha_input = principled.inputs.get("Alpha") if principled is not None else None
        if alpha_input is None or not alpha_input.links:
            missing_alpha_link.append(material.name)
            _gf2_use_dithered_transparency(material)
            continue

        alpha_link = alpha_input.links[0]
        remap = next((node for node in tree.nodes
                      if node.bl_idname == "ShaderNodeMapRange"
                      and bool(node.get(ALPHA_REMAP_TAG, False))), None)
        if remap is None:
            named = tree.nodes.get(ALPHA_REMAP_NODE)
            # Name-only fallback migrates graphs written by the untagged bridge version, but only when that
            # node is already the Principled Alpha feed. An unrelated same-named Map Range is a squatter.
            remap = (named if named is not None and named.bl_idname == "ShaderNodeMapRange"
                     and alpha_link.from_node == named else None)
        if remap is not None and alpha_link.from_node == remap:
            value_input = remap.inputs.get("Value")
            source = (value_input.links[0].from_socket
                      if value_input is not None and value_input.links else None)
        # Remold's writer emits no COLOR_0 and leaves baseColorFactor.a at 1. Either invariant changing makes
        # Blender insert a Math node here; only the standard direct Image Alpha shape is safe to rewrite.
        elif alpha_link.from_node.type == "TEX_IMAGE" and alpha_link.from_socket.name == "Alpha":
            source = alpha_link.from_socket
        else:
            source = None
        if (source is not None
                and (source.node.type != "TEX_IMAGE" or source.name != "Alpha")):
            source = None
        if source is None:
            missing_alpha_link.append(material.name)

        if overlap_property is None or source is None:
            _gf2_use_dithered_transparency(material)
            continue

        method = getattr(material, "surface_render_method", None)
        if method is not None:
            material.surface_render_method = "BLENDED"
        else:
            material.blend_method = "BLEND"
        if hasattr(material, "use_backface_culling"):
            material.use_backface_culling = False
        setattr(material, overlap_property, False)

        if remap is None:
            remap = tree.nodes.new("ShaderNodeMapRange")
            remap.name = ALPHA_REMAP_NODE
            remap.label = "GF2: 0..254 -> 0..1"
            remap.location = ((source.node.location.x + principled.location.x) / 2,
                              (source.node.location.y + principled.location.y) / 2 - 180)
        remap[ALPHA_REMAP_TAG] = True
        remap.clamp = True
        remap.inputs["From Min"].default_value = 0.0
        remap.inputs["From Max"].default_value = ALPHA_OPAQUE_CEILING
        remap.inputs["To Min"].default_value = 0.0
        remap.inputs["To Max"].default_value = 1.0

        value_input = remap.inputs["Value"]
        for link in list(value_input.links):
            if link.from_socket != source:
                tree.links.remove(link)
        if not value_input.links:
            tree.links.new(source, value_input)
        for link in list(alpha_input.links):
            if link.from_node != remap:
                tree.links.remove(link)
        if not alpha_input.links:
            tree.links.new(remap.outputs["Result"], alpha_input)
        remapped += 1

    warnings = []
    if missing_overlap:
        warnings.append("These materials were switched to dithered transparency because Blender cannot "
                        "control overlapping transparent surfaces: "
                        + ", ".join(sorted(missing_overlap)) + ".")
    if missing_alpha_link:
        warnings.append("These materials were switched to dithered transparency because their alpha "
                        "inputs could not be prepared: "
                        + ", ".join(sorted(missing_alpha_link)) + ".")
    for warning in warnings:
        print("GF2: " + warning)
    if warnings:
        _popup("Alpha Preview Warnings", ["⚠ " + warning for warning in warnings], 'INFO')
    return {
        "blended": len(blended),
        "remapped": remapped,
        "missing_overlap": tuple(missing_overlap),
        "missing_alpha_link": tuple(missing_alpha_link),
    }

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
    pre_existing_materials = tuple(bpy.data.materials)

    # FORTUNE points each bone's tip at its child = a readable connected skeleton (re-export keys
    # off the bone hash in the node name, not orientation, so this is display-only and safe).
    # merge_vertices is pinned OFF rather than trusted as the default: the app ships the game's
    # duplicate faces on split vertex copies precisely so Blender keeps them, and a merging import
    # would weld the copies back together and silently delete those faces again.
    try:
        bpy.ops.import_scene.gltf(filepath=glb_path, bone_heuristic="FORTUNE", merge_vertices=False)
    except TypeError:
        try:
            bpy.ops.import_scene.gltf(filepath=glb_path, merge_vertices=False)
        except TypeError:
            bpy.ops.import_scene.gltf(filepath=glb_path)

    # The glTF operator has finished and these are exactly its meshes. Apply Blender-only BLEND
    # mitigation now, before any layout work, excluding every material the user's scene already owned.
    imported_meshes = _gf2_new_objects(bpy.data.objects, "MESH", pre_existing_meshes)
    _gf2_install_texture_transport(glb_path, imported_meshes)
    gf2_prepare_imported_alpha_materials(imported_meshes, pre_existing_materials)

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

    # Re-derive from Blender's live collection after cleanup. `imported_meshes` may contain the importer's
    # Icosphere placeholder, whose bpy wrapper became invalid when it was removed above.
    meshes = _gf2_new_objects(bpy.data.objects, "MESH", pre_existing_meshes)
    arms = _gf2_new_objects(bpy.data.objects, "ARMATURE", pre_existing_arms)
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
    _rebuild_target_rows(bpy.context.scene, session)
    # Select only visible session parts after attribution. Reference context stays visible unless the
    # session explicitly hides it, while framing remains centred on what this session edits.
    names = [m.name for m in meshes]
    own = set(gf2_session_parts(session, names)) | set(gf2_claimed_meshes(session, names))
    shipping = [mo for mo in meshes if mo.name in own]
    visible_shipping = [mo for mo in shipping if not _hide_state(mo)]
    for mo in visible_shipping:
        mo.select_set(True)
    if visible_shipping:
        bpy.context.view_layer.objects.active = visible_shipping[0]
    else:
        visible = next((mo for mo in meshes if not _hide_state(mo)), None)
        if visible is not None:
            bpy.context.view_layer.objects.active = visible
        elif arms:
            bpy.context.view_layer.objects.active = arms[0]
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


def _activate_sidebar_category():
    """Open the sidebar, select the Lab category where supported, and tag the affected area."""
    screen = getattr(bpy.context, "screen", None)
    if screen is None:
        return
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        space = area.spaces.active
        space.show_region_ui = True
        for region in area.regions:
            if region.type == "UI":
                try:
                    region.active_panel_category = "Doll Remolding Lab"
                except Exception as e:
                    print(f"GF2: Lab sidebar category activation is unavailable: {e}")
        area.tag_redraw()
        break


def _retry_sidebar_category():
    _activate_sidebar_category()
    return None


def _setup_viewport_ui():
    """Open the sidebar on the Lab category and frame the mesh after Blender startup settles."""
    _activate_sidebar_category()
    screen = getattr(bpy.context, "screen", None)
    if screen is None:
        return
    for area in screen.areas:
        if area.type != "VIEW_3D":
            continue
        for region in area.regions:
            if region.type == "WINDOW":
                try:
                    with bpy.context.temp_override(area=area, region=region):
                        bpy.ops.view3d.view_selected()
                except Exception:
                    pass
        break
    # Category assignment during registration/import can be overwritten by the next UI rebuild. Retry once
    # on Blender's next tick after the tagged redraw; no polling or private UI API is involved.
    bpy.app.timers.register(_retry_sidebar_category, first_interval=0.0)


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
            return missing, solved     # bone heat could not solve this mesh — all missing count as unfillable
        for vg_dup in dup.vertex_groups:
            for vi in missing:
                try:
                    w = vg_dup.weight(vi)
                except RuntimeError:
                    continue           # this vertex is not in this group
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
    still cannot solve stay unweighted; they are flagged rather than shipped as a guess. Returns
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
    stays parked for the duration; it is not in the export."""
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
    """Select or deselect, tolerating an object the view layer does not carry. Swallowing that failure
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


def gf2_send(out_dir, glb_path, edit_targets=None):
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
    captured_targets = dict(edit_targets) if isinstance(edit_targets, dict) else None
    # Send always asks the app-owned file first. A missing/truncated read keeps the scene snapshot; only a
    # higher complete revision is adopted and allowed to rebuild the target rows.
    session = _refresh_session_snapshot(bpy.context.scene, glb_path)
    part_names = [part.name for part in gf2_part_collections()]
    if captured_targets is None:
        captured_targets = gf2_send_target_map(session, part_names=part_names)
    # App-created sessions name a distinct return file: Blender's raw export is external input until the app
    # validates and normalizes it, so it must never land directly on a canonical workspace glb. Falling back
    # to the opened name keeps hand-written and older session files compatible.
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
        print(f"GF2: auto-filled {filled} {'vertex' if filled == 1 else 'vertices'} from the skeleton"
              + (f"; {still} still have no weight (they will be flagged)" if still else ""))

    # The export scope: the part collections' meshes plus the session armature, regardless of focus.
    shipping = mesh_objs + arms
    texture_transport, duplicate_tags = _gf2_collect_texture_transport(mesh_objs)
    standard_channels, standard_warnings = _gf2_collect_standard_channels(mesh_objs)

    want = dict(
        filepath=out_glb, export_format="GLB",
        export_tangents=True,        # mikktspace tangents (Blender leaves these OFF by default)
        export_normals=True,
        export_texcoords=True,       # every UV layer rides as TEXCOORD_0..N in Blender layer order
        export_skins=True,           # authored weights ride along; the app compiles them onto the target
        export_extras=True,          # keep object/material extras; exact node identity is re-stamped below
        export_image_format="NONE",  # the appended exact-property rows and standard-channel appends are
                                     #   the return's only image carriers: an untouched picture rides as
                                     #   a hash-only row, so the exporter must not re-embed every image
                                     #   the preview graphs reference.
        export_apply=True,           # bake modifiers (Mirror/Subsurf/etc.) into the mesh — OFF by
                                     #   default, so without this a modifier silently vanishes on Send.
                                     #   Safe for rigged parts: the glTF exporter exempts the Armature
                                     #   modifier (skinning still ships as JOINTS/WEIGHTS, not a baked
                                     #   pose). Its other caveat, shape keys, is not in our pipeline.
        use_selection=True,
    )
    properties = list(bpy.ops.export_scene.gltf.get_rna_type().properties)
    valid = {p.identifier for p in properties}
    # A version whose exporter cannot skip images would silently fall back to embedding them all under
    # whatever format it defaults to — refuse instead of shipping an unbounded return.
    image_format = next((p for p in properties if p.identifier == "export_image_format"), None)
    image_formats = {item.identifier for item in image_format.enum_items} if image_format is not None else set()
    if "NONE" not in image_formats:
        raise RuntimeError("GF2: The running Blender version cannot export a geometry-only GLB.")
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
            raise RuntimeError("GF2: Send cannot select the following objects for export: "
                               f"{', '.join(missed)}. Re-enable the named collections in the outliner.")
        saved_names = _apply_export_names(mesh_objs)
        bpy.ops.export_scene.gltf(**opts)
    finally:
        # Everything the send borrowed goes back, on the failure path as much as the happy one: the
        # scene the modder returns to must be the scene they left.
        if saved_names is not None:
            _restore_export_names(saved_names)
        for o, was_hidden in hidden:
            _set_hidden(o, was_hidden)

    # Blender does not export node custom properties. Re-append the exact property records — changed
    # pixels, or a hash-only marker per untouched picture — after its geometry writer has produced the
    # valid GLB, together with any untagged standard-channel pictures the image-less export left behind.
    if texture_transport or standard_channels:
        _gf2_append_texture_transport(out_glb, texture_transport, standard_channels)
    else:
        _gf2_strip_empty_gltf_arrays(out_glb)

    # Tripwire: a skinned scene must never send a skinless glb — that failure is silent otherwise.
    # Structural check on the written bytes, so an export-option or Blender behavior drift is caught
    # HERE with a plain message instead of surfacing as a refusal downstream.
    if arms and any(o.vertex_groups for o in mesh_objs):
        with open(out_glb, "rb") as f:
            if b"JOINTS_0" not in f.read():
                raise RuntimeError("GF2: the export dropped the skin because JOINTS_0 is absent from "
                                   "the sent GLB. Sending would lose weights. Report the export failure.")

    # Blender computes tangents per mesh and gives up on the WHOLE mesh when any face is an n-gon,
    # logging it only to a console the modder cannot see. The app derives its own in that case, so the
    # send stands, but the surface detail is no longer the one authored here. A Hide carries no mesh, so
    # there is no tangent to miss.
    if mesh_objs:
        with open(out_glb, "rb") as f:
            if b"TANGENT" not in f.read():
                print("GF2: no tangents in the sent GLB. Triangulate the mesh (Ctrl+T in Edit Mode) to "
                      "keep the authored surface detail.")
                _popup("Sent Without Tangents",
                       ["⚠ Blender could not compute tangents for the sent mesh.",
                        "Normal map detail may shift.",
                        "Triangulate the mesh (Ctrl+T in Edit Mode), then Send again."], 'INFO')

    # An option this Blender's glTF exporter does not carry was filtered out of the export above. The mesh
    # still ships; what it ships WITHOUT is only knowable here, so it is said where the send is read rather
    # than on a console the modder never opens.
    warning_lines = []
    if dropped:
        warning_lines.append(gf2_dropped_options_line(dropped))
    # A slot two nodes claimed sent one of them; which one is not the modder's choice, so it is named.
    warning_lines.extend(gf2_duplicate_tag_lines(duplicate_tags))
    # An untagged picture the send skipped or could not read — silence would read as a clean send.
    warning_lines.extend(standard_warnings)
    if warning_lines:
        for line in warning_lines:
            print("GF2: " + line)
        _popup("Sent With Warnings", ["⚠ " + line for line in warning_lines], 'INFO')

    # The sidecar is the write-complete sentinel (written last) that the watcher fires on, and it
    # carries the one intent the glb cannot express: which parts were emptied. An absent mesh is never
    # that signal — every part outside this session is absent by design — so `hiddenParts` is the only
    # thing that hides a part, and a part listed there keeps its workspace file untouched.
    # `editIds` is the target selection for every writable part, emptied ones included: a Hide is an edit
    # like any other and lands on the row's existing or new destination.
    sidecar = gf2_send_sidecar(emptied, captured_targets, session)
    with open(os.path.splitext(out_glb)[0] + ".gf2send.json", "w", encoding="utf-8") as f:
        json.dump(sidecar, f)
    # Export completion alone is not proof of intake. Retain the pre-send target snapshot until a higher
    # live revision acknowledges it; only that contract may say an edit now holds authored mesh work.
    _store_send_snapshot(gf2_send_snapshot(session, sidecar["editIds"]))
    print(f"GF2: sent -> {out_glb}")
    return out_glb


def gf2_dropped_options_line(dropped):
    """The one wording for glTF export options the running Blender does not have. The send still stands —
    this names what it went out without, so an option lost to a version difference is visible."""
    return "The running Blender version does not support these export options: " \
        + ", ".join(sorted(dropped)) + "."


# ---------------------------------------------------------------- pre-send sanity checks

def _object_transform(mo):
    """One mesh object's Object-mode transform, in the three parts the pre-send check compares: the
    location and scale the N panel shows, and the rotation as a quaternion so the comparison holds
    whatever rotation mode the object is in."""
    return {
        "location": list(mo.location),
        "rotation": list(mo.matrix_basis.to_quaternion()),
        "scale": list(mo.scale),
    }


def _snapshot_baseline(meshes, armature):
    """Record the as-imported skeleton, material-slot layout and object transform so the pre-send check
    can tell what the modder CHANGED — a renamed bone or reordered slot silently breaks the compile,
    and a transform is baselined against import rather than a bare identity (an unskinned part arrives
    carrying the one its glb node holds)."""
    placed = {mo.name: _object_transform(mo) for mo in meshes}
    base = {
        "bones": sorted(b.name for b in armature.data.bones) if armature else [],
        "slots": {
            mo.name: [ms.material.name if ms.material else "" for ms in mo.material_slots]
            for mo in meshes
        },
    }
    # One map per component, keeping the shape `scale` has always had: a baseline written before the
    # move and rotate were recorded simply has no map for them, which is what leaves them uncompared.
    for key in ("location", "rotation", "scale"):
        base[key] = {name: values[key] for name, values in placed.items()}
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


def _transform_differs(before, after):
    """Whether two recorded transform components disagree beyond float noise. A component the baseline
    does not carry, or one recorded in a different shape, is not comparable — and something that cannot
    be compared must never produce a warning."""
    if before is None or after is None or len(before) != len(after):
        return False
    return any(abs(after[i] - before[i]) > 1e-3 for i in range(len(before)))


def gf2_transform_warning(name, before, after, skinned):
    """The one wording for an Object-mode transform that will not arrive the way the viewport shows it.

    A SKINNED mesh's object transform is baked into the exported vertex positions once, and its glb node
    is written at identity, so a moved, rotated or scaled part arrives placed as it looks here. The
    exception is a negative scale: the geometry mirrors, the triangle winding does not, and the mesh
    renders inside-out. An UNSKINNED mesh keeps its local positions and carries the transform on its glb
    node instead, and the app reads positions only — so that transform is dropped.

    `before` is the as-imported baseline and `after` the object now, each a map of "location",
    "rotation" and "scale" to lists of floats. A component the baseline does not carry is not compared.
    Returns a (severity, message) pair or None."""
    if not skinned:
        if any(_transform_differs(before.get(k), after.get(k))
               for k in ("location", "rotation", "scale")):
            return ("SOFT", f"'{name}' has no skeleton. Object Mode position, rotation, and scale are "
                            "dropped on Send. Apply the transform (Ctrl+A in Object Mode), or edit "
                            "the geometry in Edit Mode.")
        return None
    was, now = before.get("scale"), after.get("scale")
    if was is not None and now is not None and _mirrored(now) and not _mirrored(was):
        return ("SOFT", f"'{name}' has a negative Object Mode scale. The mesh ships mirrored without "
                        "flipped faces and renders inside-out.")
    return None


def _mirrored(scale):
    """Whether a scale flips handedness: the product of its axes is what says so, however many of them
    are negative."""
    return scale[0] * scale[1] * scale[2] < 0


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
                                       + ", so the object will not be sent. Move the object into a part "
                                         "collection to include the object"
                                       + (f", or into {REFERENCE_COLLECTION} to exclude the object."
                                          if ref is not None else ".")))
            continue                     # Reference is scenery: no part to attribute, nothing to check
        part = _part_of(o)
        if part is None:
            issues.append(("HARD", f"'{o.name}' is in {MOD_COLLECTION} but not in a part collection. "
                                   + (f"Move the object into one of: {choices}." if choices else
                                      f"Create a part collection under {MOD_COLLECTION} and move "
                                      "the object.")))
        elif _base_name(o.name) != part.name and _base_name(o.name) in part_names:
            issues.append(("SOFT", f"The {gf2_label(o.name)} mesh is in the {gf2_label(part.name)} "
                                   f"collection and is sent as {gf2_label(part.name)}. Move it into "
                                   f"{gf2_label(o.name)} if that is a mistake."))
    for part in parts:
        if _is_excluded(part):
            issues.append(("HARD", f"'{part.name}' is excluded from the view layer. The part meshes "
                                   "cannot be sent. Re-enable the collection in the outliner."))
        wanted = part.get(PART_MARKER)
        if isinstance(wanted, str) and part.name != wanted:
            if _base_name(part.name) == wanted:
                issues.append(("HARD", f"Another collection is already named '{wanted}', forcing the "
                                       f"part collection to use '{part.name}', which cannot be sent. "
                                       f"Rename or delete the other '{wanted}' collection, then re-import."))
            else:
                issues.append(("HARD", f"The '{wanted}' collection was renamed to '{part.name}' and "
                                       f"cannot be sent. Rename the collection back to '{wanted}'."))
        held = _part_meshes(part)
        if len(held) > 1:
            issues.append(("HARD", f"The {gf2_label(part.name)} collection holds {len(held)} meshes: "
                                   f"{', '.join(gf2_label(o.name) for o in held)}. A part sends as one "
                                   "mesh. Join the meshes (select them, then Ctrl+J), or move the "
                                   f"extras into {REFERENCE_COLLECTION}."))
    return issues


def gf2_cheap_checks(mesh_objs, armature):
    """The subset of the pre-send pass that reads only object- and collection-level state:
    attribution, an empty export scope, armature presence, Object-mode transform, material-slot layout,
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
        issues.append(("SOFT", gf2_emptied_part_line(name)))
    # HARD — nothing in a part collection is no deliverable at all; the send refuses rather than write
    # it. The gate matches the send's, so a scene the send would refuse never reads as ready here — which
    # includes the one case where an empty scope is fine: a named-part session whose part was emptied on
    # purpose, already warned about above.
    if not shipping and not (gf2_emptied_parts() and gf2_emptying_is_a_hide(load_session())):
        issues.append(("HARD", EVERY_PART_EMPTY if gf2_part_collections() else NO_PART_MESH))
    # HARD — a weighted scene with no session armature exports skinless; that payload is refused.
    weighted = [o for o in shipping if o.vertex_groups]
    if weighted and armature is None:
        issues.append(("HARD", f"{len(weighted)} weighted part{'' if len(weighted) == 1 else 's'} but "
                               f"no armature in {MOD_COLLECTION}, so Send would lose the skin. "
                               f"Keep the skeleton in {MOD_COLLECTION}/{ARMATURE_COLLECTION}."))
    base = _load_baseline()
    # SOFT — the export bakes a skinned mesh's object transform into its vertices once, so only a
    # mirroring scale is worth saying; an unskinned mesh carries its transform on the glb node, which
    # the app does not read, so the whole of it is dropped.
    unskinned = gf2_unskinned_parts(load_session())
    for mo in shipping:
        before = {k: (base.get(k) or {}).get(mo.name) for k in ("location", "rotation", "scale")}
        issue = gf2_transform_warning(mo.name, before, _object_transform(mo),
                                      not _is_unskinned(mo, unskinned))
        if issue is not None:
            issues.append(issue)
    # SOFT — each submesh keeps the material it was imported with; a reorder/count change moves them.
    for mo in shipping:
        old = (base.get("slots") or {}).get(mo.name)
        cur = [ms.material.name if ms.material else "" for ms in mo.material_slots]
        # Slots appended after the imported ones are new submeshes the app takes; only the imported
        # slots have face ranges that a reorder, rename or removal would move.
        if old is not None and cur[:len(old)] != old:
            issues.append(("SOFT", f"'{mo.name}' material slots changed since import, so face ranges "
                                   "may now use different materials. Restore the imported slot order "
                                   "if the change was not intended."))
    # SOFT — re-export keys off bone names; a renamed/removed bone breaks the weight compile.
    if base.get("bones") and armature is not None:
        cur_bones = set(b.name for b in armature.data.bones)
        gone = [b for b in base["bones"] if b not in cur_bones]
        if gone:
            issues.append(("SOFT", f"{len(gone)} imported bone{'' if len(gone) == 1 else 's'} renamed "
                                   f"or removed (for example, '{gone[0]}'). Weights on those bones are lost "
                                   "on Send. Rename the bones, or undo the change."))
    return issues


def gf2_run_checks(mesh_objs, armature):
    """Pre-send sanity pass. Returns a list of (severity, message) with severity in {"HARD","SOFT"}:
    HARD is what will be REJECTED downstream (surfaced here first so the modder is not bounced after a
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
        issues.insert(lead, ("HARD", f"{total} {'vertex has' if total == 1 else 'vertices have'} no "
                                     f"weight in {named} and cannot be filled from the skeleton, so "
                                     "Send is blocked. Weight-paint or delete the affected vertices."))
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
    """The exact live status for a list of blocking and warning check results."""
    hard = sum(1 for sev, _ in issues if sev == "HARD")
    soft = len(issues) - hard
    if not hard and not soft:
        return "Ready to send"
    bits = []
    if hard:
        bits.append(f"{hard} blocking issue" + ("" if hard == 1 else "s"))
    if soft:
        bits.append(f"{soft} warning" + ("" if soft == 1 else "s"))
    return " · ".join(bits) + " — click Check Mesh for details"


_LABELS_CACHE = {"raw": None, "labels": {}}


def _session_labels():
    """Part name (lower-cased) -> the app's own short token, off the scene's stored session. Cached per
    raw session string, so a panel redraw costs one dictionary lookup and a session rewrite re-reads."""
    try:
        raw = (bpy.context.scene or {}).get(SESSION_KEY)
    except Exception:
        return {}
    if raw == _LABELS_CACHE["raw"]:
        return _LABELS_CACHE["labels"]
    labels = {}
    try:
        doc = json.loads(raw) if raw else {}
        for p in (doc.get("parts") or []) if isinstance(doc, dict) else []:
            if not isinstance(p, dict):
                continue
            name, label = p.get("name"), p.get("label")
            if isinstance(name, str) and name and isinstance(label, str) and label:
                labels[name.lower()] = label
    except Exception:
        labels = {}
    _LABELS_CACHE["raw"] = raw
    _LABELS_CACHE["labels"] = labels
    return labels


def gf2_label(name):
    """What the panel and its messages call a part. The app names each part's own short token in the
    session document (`cloth2`, `P3_body_fight`), and that token IS the label whenever the session
    carries it — a name's structure is never re-derived where its owner already said what it means.
    Only a scene with no session (a hand-opened glb) falls back to the structural cut: game asset
    names run to 40 characters of shared prefix and suffix, which buries the one token that differs,
    `c_KarstSSR0101_slg_P1_cloth2_lod0` -> `cloth2`. A name the MODDER chose carries no such
    structure, so it is left exactly as they wrote it."""
    stem = _base_name(name)
    label = _session_labels().get(stem.lower())
    if label:
        return label
    if not stem.startswith("c_"):
        return name
    for tail in ("_lod0", "_lod1", "_lodm0"):
        if stem.endswith(tail):
            stem = stem[:-len(tail)]
            break
    return stem.rsplit("_", 1)[-1] or name


def gf2_scope_lines(n_objects, n_verts, bakes_modifiers):
    """What the next Send carries, stated before it acts: a stray import reads as an unexpected count
    here rather than as an unexpected mesh in the built mod. Reference is not mentioned: it never
    ships, and a count of what is NOT sent is noise on every send."""
    head = (f"{n_objects} object" + ("" if n_objects == 1 else "s")
            + f" · {n_verts:,} " + ("vertex" if n_verts == 1 else "vertices"))
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


def _bakes_modifiers(obj):
    """True when the export would bake a modifier into this mesh. The Armature modifier is exempt (the
    skin ships as weights, not a baked pose), and so is one switched off in the viewport — the exporter
    evaluates the same depsgraph the viewport shows."""
    return any(m.type != "ARMATURE" and m.show_viewport for m in obj.modifiers)


def _send_overwrite_warning(session=None, edit_targets=None):
    """The live-inventory confirmation lead for the currently selected existing targets."""
    session = load_session() if session is None else session
    if edit_targets is None:
        part_names = [part.name for part in gf2_part_collections()]
        edit_targets = gf2_send_target_map(
            session, getattr(bpy.context.scene, "gf2_target_rows", ()), part_names)
    return gf2_overwrite_warning(gf2_selected_mesh_edit_labels(session, edit_targets))


def _send_scope_lines():
    """The pre-send scope summary. Read AFTER the checks have forced Object mode, so the vertex counts
    are the datablock's and not a stale pre-edit figure."""
    shipping = gf2_shipping_meshes()
    return gf2_scope_lines(len(shipping),
                           sum(len(o.data.vertices) for o in shipping),
                           any(_bakes_modifiers(o) for o in shipping))


def gf2_session_notices(session):
    """Non-empty notice sentences from a forward-compatible session document."""
    return [notice.strip() for notice in (session or {}).get("notices") or []
            if isinstance(notice, str) and notice.strip()]


def gf2_panel_wrap_width(region_width, ui_scale=1.0):
    """Label characters that fit one sidebar row, from the region's pixel width and the UI scale
    (~8 px per character at scale 1). Unknown or degenerate inputs fall back to the narrow-sidebar
    default rather than guessing wide."""
    try:
        width = float(region_width)
        scale = float(ui_scale) if ui_scale else 1.0
    except (TypeError, ValueError):
        return 48
    if width <= 0 or scale <= 0:
        return 48
    return max(16, int(width / (8.0 * scale)))


def gf2_wrapped_lines(text, width=48, lead=""):
    """Clipped-sidebar-safe label rows: the lead on the first wrapped line, matching indentation
    after. Blender labels never wrap on their own — they ellipsize mid-sentence."""
    wrapped = textwrap.wrap(text, width=max(12, width - len(lead)), break_long_words=False,
                            break_on_hyphens=False) or [""]
    return [lead + wrapped[0]] + [" " * len(lead) + line for line in wrapped[1:]]


def gf2_wrapped_notice_lines(notice, width=48):
    """A notice block row set: warning prefix on the first line."""
    return gf2_wrapped_lines(notice, width, "⚠ ")


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
    handler (which Blender drops on an exception), and a stale ✓ would misreport a dirty scene."""
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
    _gf2_note_dirty_images()
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
    _refresh_live_status()


def _register_session_refresh(glb_path):
    _prime_session_refresh(glb_path)
    if not bpy.app.timers.is_registered(_session_refresh_tick):
        bpy.app.timers.register(_session_refresh_tick, first_interval=_SESSION_REFRESH_INTERVAL)


# ---------------------------------------------------------------- UI (panel + operator)

def _register_ui(glb_path, send_dir):
    class GF2_PG_target_row(bpy.types.PropertyGroup):
        part_name: bpy.props.StringProperty(name="Part")
        part_label: bpy.props.StringProperty(name="Part Label")
        target: bpy.props.EnumProperty(name="Edit", items=_gf2_target_items)
        new_name: bpy.props.StringProperty(name="Name")

    bpy.utils.register_class(GF2_PG_target_row)
    bpy.types.Scene.gf2_target_rows = bpy.props.CollectionProperty(type=GF2_PG_target_row)
    bpy.types.Scene.gf2_send_dir = bpy.props.StringProperty(name="Send Directory", default=send_dir or "")
    bpy.types.Scene.gf2_glb_path = bpy.props.StringProperty(name="Source GLB", default=glb_path or "")

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
        bl_label = "Check Mesh"
        bl_description = "Run the pre-send checks without exporting"

        def execute(self, ctx):
            meshes, arm = _scene_meshes_arm()
            issues = gf2_run_checks(meshes, arm)
            if not issues:
                self.report({'INFO'}, "Ready to send")
                _popup("Check Mesh", ["Ready to send."], 'CHECKMARK')
                return {'FINISHED'}
            lines = []
            for sev, msg in issues:
                print(f"GF2 {'BLOCK' if sev == 'HARD' else 'warn'}: {msg}")
                lines.append(("✗ " if sev == "HARD" else "⚠ ") + msg)
            hard = sum(1 for sev, _ in issues if sev == "HARD")
            _popup("Check Mesh", lines, 'ERROR' if hard else 'INFO')
            # The popup IS the surface. An {'ERROR'} report draws its own popup on top of it, so the
            # modder gets two overlapping boxes, the top one only pointing at the one it covers.
            self.report({'WARNING'}, gf2_status_line(issues))
            return {'FINISHED'}

    class GF2_OT_send(bpy.types.Operator):
        bl_idname = "gf2.send_to_lab"
        bl_label = "Send to Lab"
        bl_description = "Export the edited parts back to Doll Remolding Lab"

        _soft = None
        _scope = None
        _overwrite = None
        _emptied = None
        _targets = None

        def _gate(self, ctx):
            """Run the full pre-send pass and keep its warnings for the send. Returns a result set when
            the Send must not proceed, None when it may."""
            session = _refresh_session_snapshot(ctx.scene)
            part_names = [part.name for part in gf2_part_collections()]
            self._targets = gf2_send_target_map(
                session, getattr(ctx.scene, "gf2_target_rows", ()), part_names)
            if not ctx.scene.gf2_send_dir:
                self.report({'ERROR'}, "No send directory is set.")
                return {'CANCELLED'}
            meshes, arm = _scene_meshes_arm()
            issues = gf2_run_checks(meshes, arm)
            hard = [m for sev, m in issues if sev == "HARD"]
            self._emptied = gf2_emptied_parts()
            emptied_lines = {gf2_emptied_part_line(name) for name in self._emptied}
            self._soft = [m for sev, m in issues if sev == "SOFT" and m not in emptied_lines]
            for m in self._soft:
                print(f"GF2 warn: {m}")
            if hard:
                for m in hard:
                    print(f"GF2 BLOCK: {m}")
                _popup("Send Blocked", ["✗ " + m for m in hard]
                       + ["", "Fix these, then Send again."], 'ERROR')
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
            self._overwrite = _send_overwrite_warning(load_session(ctx.scene), self._targets)
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
                col.label(text=self._overwrite)
            for m in (self._soft or ()):
                col.label(text="⚠ " + m)
            for name in (self._emptied or ()):
                col.label(text=gf2_emptied_part_confirm_line(name))
            if self._overwrite or self._soft or self._emptied:
                col.separator()
            for ln in (self._scope or ()):
                col.label(text=ln)

        def execute(self, ctx):
            if self._soft is None:       # reached without the confirm step (a script or the search menu)
                blocked = self._gate(ctx)
                if blocked is not None:
                    return blocked
            s = ctx.scene
            out = gf2_send(s.gf2_send_dir, s.gf2_glb_path, self._targets)
            soft = self._soft
            sent_line = f"Sent: {os.path.basename(out)}"
            emptied = [gf2_emptied_part_confirm_line(name) for name in (self._emptied or ())]
            if soft:
                _popup("Sent With Warnings", ["⚠ " + m for m in soft] + emptied
                       + ["", sent_line], 'INFO')
                self.report({'WARNING'}, f"Sent with {len(soft)} warning(s). See the console.")
            else:
                # The status-bar report alone is easy to miss: confirm the export where the modder is looking.
                _popup("Sent", emptied + ([""] if emptied else []) + [sent_line], 'INFO')
                self.report({'INFO'}, sent_line)
            return {'FINISHED'}

    class GF2_PT_panel(bpy.types.Panel):
        bl_label = "Doll Remolding Lab"
        bl_space_type = "VIEW_3D"
        bl_region_type = "UI"
        bl_category = "Doll Remolding Lab"

        def draw(self, ctx):
            col = self.layout.column()
            has_reference_meshes = False
            wrap = gf2_panel_wrap_width(
                getattr(getattr(ctx, "region", None), "width", 0),
                getattr(getattr(bpy.context.preferences, "system", None), "ui_scale", 1.0))
            try:
                session = load_session(ctx.scene)
                notices = gf2_session_notices(session)
                if notices:
                    notice_box = col.box()
                    for index, notice in enumerate(notices):
                        if index:
                            notice_box.separator()
                        for line in gf2_wrapped_notice_lines(notice, wrap):
                            notice_box.label(text=line)
                for target in ctx.scene.gf2_target_rows:
                    target_box = col.box()
                    row = target_box.row(align=True)
                    row.label(text=target.part_label)
                    row.prop(target, "target", text="")
                    if target.target == NEW_EDIT_TARGET:
                        target_box.prop(target, "new_name", text="Name")
                has_reference_meshes = bool(_reference_meshes())
            except Exception as e:
                print(f"GF2: panel state read failed: {e}")
                col.label(text=UNREADABLE)
            col.operator("gf2.check_mesh", icon="CHECKMARK")
            col.operator("gf2.send_to_lab", icon="EXPORT")
            for line in gf2_wrapped_lines(_LIVE["text"] or "Checking mesh…", wrap):
                col.label(text=line)
            tips = col.box()
            tip_lines = ["An unchanged part sends nothing.",
                         "Deleting all of a part's geometry sends it as a hide."]
            if has_reference_meshes:
                tip_lines.append("Reference parts are shown for context and are not sent.")
            tip_lines.append("The skeleton is hidden by default — unhide it to weight paint.")
            for tip in tip_lines:
                for line in gf2_wrapped_lines(tip, wrap):
                    tips.label(text=line)

    bpy.utils.register_class(GF2_OT_check)
    bpy.utils.register_class(GF2_OT_send)
    bpy.utils.register_class(GF2_PT_panel)
    _rebuild_target_rows(bpy.context.scene, load_session(), _scene_target_states(bpy.context.scene))
    _register_live_status()
    _register_session_refresh(glb_path)


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
