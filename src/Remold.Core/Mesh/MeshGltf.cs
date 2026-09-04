using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Project;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using GltfImage = SharpGLTF.Schema2.Image;   // disambiguate from SixLabors.ImageSharp.Image

namespace Remold.Core.Mesh;

/// <summary>
/// Reads/writes a decoded <see cref="UnityMesh"/> as a Blender-facing <c>.glb</c>: geometry
/// (POSITION/NORMAL/TANGENT/a consecutive TEXCOORD_0..N prefix + per-submesh indices) plus the named, posed armature and
/// JOINTS/WEIGHTS. Every glb here is glTF right-handed space, converted from Unity's left-handed space
/// via <see cref="AxisConvention"/>; the flip is applied on export and un-applied on import, never crossed.
/// The outline channel (Unity vertex COLOR) is computed, not transported — it is re-baked at package time
/// by <see cref="MeshApply.BakeOutline"/>, so this codec neither writes nor reads it.
/// </summary>
public static class MeshGltf
{
    /// <summary>Content-key version of the shared-armature combined GLB writer.</summary>
    public const string CombinedWriterSpec = "combined-rigged-writer-v1";

    internal const int MaxTexCoordSets = 8;

    /// <summary>How many UV sets can ride Blender transport without renumbering: the consecutive prefix
    /// beginning at UV0 whose game channels each carry at least the two components glTF transports.</summary>
    internal static int TransportedTexCoordCount(UnityMesh mesh)
    {
        int count = 0;
        for (; count < MaxTexCoordSets; count++)
        {
            string channel = $"TexCoord{count}";
            if (!mesh.Has(channel) || !mesh.Dims.TryGetValue(channel, out int dim) || dim < 2) break;
        }
        return count;
    }

    /// <summary>The transported UV-prefix count for one named mesh in an already parsed GLB.</summary>
    public static int TransportedTexCoordCount(ParsedGlb glb, string? meshName) =>
        TransportedTexCoordCount(ImportPayload(glb, meshName).Mesh);

    /// <summary>User-facing notices for game UV channels outside the transportable prefix. Every present
    /// channel that will stay game-authored is named; later channels never compact around a missing or undersized
    /// predecessor.</summary>
    public static IReadOnlyList<string> TexCoordTransportWarnings(UnityMesh mesh, string part)
    {
        int prefix = TransportedTexCoordCount(mesh);
        if (prefix == MaxTexCoordSets) return Array.Empty<string>();

        string blocker = $"TexCoord{prefix}";
        bool missing = !mesh.Has(blocker);
        int blockerDim = mesh.Dims.GetValueOrDefault(blocker);
        var layers = new List<string>();
        for (int i = prefix; i < MaxTexCoordSets; i++)
        {
            string channel = $"TexCoord{i}";
            if (!mesh.Has(channel)) continue;
            layers.Add($"UV{i}");
        }
        if (layers.Count == 0) return Array.Empty<string>();
        string named = layers.Count == 1
            ? layers[0]
            : layers.Count == 2
                ? $"{layers[0]} and {layers[1]}"
                : string.Join(", ", layers.Take(layers.Count - 1)) + $", and {layers[^1]}";
        string reason = missing
            ? $"UV{prefix} is missing"
            : $"UV{prefix} has {blockerDim} {(blockerDim == 1 ? "value" : "values")} per vertex instead of at least two";
        return new[]
        {
            $"{part}: {named} cannot be edited in Blender because {reason}. The game values will be kept.",
        };
    }

    /// <summary>Named notices for Blender-created UV layers that the per-part game baseline does not carry,
    /// and for supported baseline layers Blender deleted and the return will restore. The caller reports
    /// these even when that is the return's only difference.</summary>
    public static IReadOnlyList<string> ReturnedTexCoordWarnings(ParsedGlb returned, string meshName,
        int allowed)
    {
        // A negative count is the return preparation's explicit no-filtering degradation for an unreadable
        // prepared workspace. It has already emitted the named reason, so there is no contract to compare.
        if (allowed < 0) return Array.Empty<string>();
        allowed = Math.Min(allowed, MaxTexCoordSets);
        var sent = ImportPayload(returned, meshName).Mesh;
        var warnings = new List<string>();
        for (int i = 0; i < allowed; i++)
            if (!sent.Has($"TexCoord{i}"))
                warnings.Add($"Restored UV{i} on {meshName} from the part's game mesh because that UV "
                    + "layer was deleted in Blender.");
        for (int i = allowed; i < MaxTexCoordSets; i++)
            if (sent.Has($"TexCoord{i}"))
                warnings.Add($"Ignored UV{i} on {meshName} because that UV layer is not supported by "
                    + "the part's game mesh.");
        return warnings;
    }

    /// <summary>Write a Blender-facing glb (axis-converted). Given base-color/normal PNGs, a preview PBR
    /// material referencing them is embedded — round-trip safe, since <see cref="ImportGlb"/> ignores
    /// materials. <paramref name="uprighting"/> bakes a prefab body's scene-rest rotation into the geometry
    /// (see <see cref="RestBake"/>) — recorded on the project target and undone at package build.</summary>
    /// <param name="authoredSources">The subset of the embedded PNG paths that are the MODDER's own authored
    /// maps rather than stock ones, so the record beside the glb tells the two apart. See
    /// <see cref="PreviewImageSet"/>.</param>
    /// <param name="onUnreadableMap">handed the path of every map that would not decode (see
    /// <see cref="PreviewImageSet"/>).</param>
    /// <inheritdoc cref="ExportGlb(UnityMesh, string, string?, string?, IReadOnlyList{ValueTuple{string?, string?, string?}}?, Matrix4x4?)"/>
    public static void ExportGlb(UnityMesh mesh, string outPath, string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null, Matrix4x4? uprighting = null,
        IReadOnlySet<string>? authoredSources = null, Action<string>? onUnreadableMap = null,
        IReadOnlyList<TextureTransportSource>? textureTransport = null,
        PreviewBlobMemo? previewMemo = null) =>
        Write(uprighting is { } g ? RestBake.Apply(mesh, g) : mesh, outPath, baseColorPng, normalPng, perSubmesh,
            authoredSources, onUnreadableMap, textureTransport, previewMemo);

    private static void Write(UnityMesh mesh, string outPath,
        string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null,
        IReadOnlySet<string>? authoredSources = null, Action<string>? onUnreadableMap = null,
        IReadOnlyList<TextureTransportSource>? textureTransport = null,
        PreviewBlobMemo? previewMemo = null)
    {
        previewMemo ??= new PreviewBlobMemo();
        mesh = SplitDuplicateFaces(mesh);
        var model = ModelRoot.CreateModel();
        var glMesh = model.CreateMesh(mesh.Name);

        // A preview PBR material so the part shows textured in Blender — an approximation, not the in-game
        // toon/uber shader. A per-submesh assignment overrides the single material where given. One image
        // cache spans every material, so a shared texture embeds ONCE.
        var imgCache = new PreviewImageSet(authoredSources, onUnreadableMap, previewMemo)
            { Labels = ImageLabels(textureTransport) };
        Material? material = baseColorPng is not null || normalPng is not null
            ? BuildPreviewMaterial(model, mesh.Name, baseColorPng, normalPng, imageCache: imgCache)
            : null;
        var submeshMats = BuildSubmeshMaterials(model, mesh.Name, mesh.Submeshes.Count, perSubmesh, imgCache);

        // Unity (LH) → glTF (RH): negate X on directional channels, reverse winding, flip V on the UVs.
        var positions = Map3(mesh, "Vertex", AxisConvention.Position);
        // glTF requires unit-length normals; half-precision scene/prop normals can be off-unit or zero,
        // which SharpGLTF rejects ("Invalid Normal"). A zero normal falls back to +Z.
        var normals = mesh.Has("Normal") ? Normalize(Map3(mesh, "Normal", AxisConvention.Normal)) : null;
        var tangents = mesh.Has("Tangent")
            ? SanitizeTangents(Map4(mesh, "Tangent", AxisConvention.Tangent), normals)
            : null;
        var uvs = TransportUvs(mesh);

        // ONE shared vertex pool for the whole mesh: build the attribute accessors once, then point every
        // submesh's primitive at them (only the index buffer differs). SharpGLTF's WithVertexAccessor mints
        // a NEW accessor per call, so per-primitive calls duplicate the vertex buffer once per submesh —
        // ImportGlb reads those as distinct pools and concatenates, inflating the vertex count
        // ×(submesh count) and breaking the by-index Exact-Match path.
        MeshPrimitive? shared = null;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var prim = glMesh.CreatePrimitive();
            if (shared is null)
            {
                prim.WithVertexAccessor("POSITION", positions);
                if (normals is not null) prim.WithVertexAccessor("NORMAL", normals);
                if (tangents is not null) prim.WithVertexAccessor("TANGENT", tangents);
                for (int i = 0; i < uvs.Count; i++)
                    prim.WithVertexAccessor($"TEXCOORD_{i}", uvs[i]);
                shared = prim;
            }
            else
            {
                foreach (var attr in shared.VertexAccessors)
                    prim.SetVertexAccessor(attr.Key, attr.Value);
            }
            var tris = AxisConvention.ReverseWinding(mesh.Submeshes[s]);   // reflection inverts winding
            prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, tris);
            var pm = submeshMats?[s] ?? material;
            if (pm is not null) prim.Material = pm;
        }

        var scene = model.UseScene("scene");
        scene.CreateNode(mesh.Name).WithMesh(glMesh);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        model.SaveGLB(outPath);
        var transport = GltfTextureTransport.Write(outPath, textureTransport, onUnreadableMap, previewMemo);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes, imgCache.Slots, transport);
    }

    /// <summary>Re-point every face Blender's mesh model cannot hold at fresh copies of its own vertices.
    /// Blender forbids two polygons over one vertex SET, and a polygon naming a vertex twice; its importer
    /// silently deletes such faces — and the game legitimately ships them (a cloth region re-listed in the
    /// index buffer draws twice for density). Copying the offending face's vertex rows (every channel, skin
    /// included) gives each face a unique vertex set, which Blender keeps and re-exports in place, so the
    /// send-back's corner walk still pairs the round trip corner for corner. A mesh with no offending face
    /// returns unchanged, which also makes the transform idempotent over its own output.</summary>
    internal static UnityMesh SplitDuplicateFaces(UnityMesh mesh)
    {
        List<(int Submesh, int Face)>? offending = null;
        // one set across every submesh: Blender folds all primitives into ONE mesh before validating
        var seen = new HashSet<(int, int, int)>();
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var tri = mesh.Submeshes[s];
            for (int f = 0; f + 2 < tri.Length; f += 3)
            {
                int a = tri[f], b = tri[f + 1], c = tri[f + 2];
                int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c));
                bool repeats = a == b || b == c || a == c;
                if (!repeats && seen.Add((lo, a + b + c - lo - hi, hi))) continue;
                (offending ??= new()).Add((s, f));
            }
        }
        if (offending is null) return mesh;

        int added = offending.Count * 3;
        var channels = new Dictionary<string, float[]>(mesh.Channels.Count);
        foreach (var (name, values) in mesh.Channels)
        {
            int dim = mesh.VertexCount > 0 ? values.Length / mesh.VertexCount : 0;
            var grown = new float[values.Length + added * dim];
            Array.Copy(values, grown, values.Length);
            channels[name] = grown;
        }
        var submeshes = mesh.Submeshes.Select(t => (int[])t.Clone()).ToList();
        int next = mesh.VertexCount;
        foreach (var (s, f) in offending)
        {
            var tri = submeshes[s];
            for (int k = 0; k < 3; k++)
            {
                int src = tri[f + k];
                foreach (var (name, values) in mesh.Channels)
                {
                    int dim = mesh.VertexCount > 0 ? values.Length / mesh.VertexCount : 0;
                    // an index past a short channel copies nothing and leaves the copy's row zero,
                    // matching what every reader of the short channel sees for the original row
                    if (dim == 0 || (long)(src + 1) * dim > values.Length) continue;
                    Array.Copy(values, src * dim, channels[name], next * dim, dim);
                }
                tri[f + k] = next++;
            }
        }
        return new UnityMesh
        {
            Name = mesh.Name,
            VertexCount = mesh.VertexCount + added,
            Channels = channels,
            Dims = new Dictionary<string, int>(mesh.Dims),
            Submeshes = submeshes,
        };
    }

    /// <summary>Whether every triangle of this mesh has EXACTLY zero area — collapsed billboard points the
    /// game's shader inflates at draw time, their authored normals the inflation directions. Blender stores
    /// custom normals relative to each corner's computed base normal, which a zero-area face does not have,
    /// so those directions cannot survive any Blender round trip; the Blender-edit gate
    /// (<see cref="Workbench.PartSkinGate"/>) refuses such a mesh on this answer. Exact float zero is
    /// deliberate: the corpus collapses these to coincident corner positions, while a real sliver triangle
    /// stays editable.</summary>
    internal static bool AllFacesZeroArea(UnityMesh mesh)
    {
        if (!mesh.Has("Vertex")) return false;
        var pos = mesh.Channels["Vertex"];
        int dim = mesh.Dims.GetValueOrDefault("Vertex", 3);
        if (dim < 3) return false;
        bool any = false;
        foreach (var tri in mesh.Submeshes)
            for (int f = 0; f + 2 < tri.Length; f += 3)
            {
                long a = (long)tri[f] * dim, b = (long)tri[f + 1] * dim, c = (long)tri[f + 2] * dim;
                long m = Math.Max(a, Math.Max(b, c));
                if (a < 0 || b < 0 || c < 0 || m + 3 > pos.Length) return false;   // unreadable ≠ billboard
                any = true;
                float ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
                float vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
                if (uy * vz - uz * vy != 0f || uz * vx - ux * vz != 0f || ux * vy - uy * vx != 0f)
                    return false;
            }
        return any;
    }

    /// <summary>
    /// Write a Blender-facing RIGGED glb: geometry (as <see cref="ExportGlb"/>) plus a posed, named
    /// armature and per-vertex JOINTS_0/WEIGHTS_0. Each bone's rest world = <c>inverse(bindPose)</c> (bind
    /// poses are rigid corpus-wide, so no orthonormalization), reflected by the X-flip
    /// (<see cref="AxisConvention.Reflect"/>), parented by splitting each bone path on '/'. Bone nodes are
    /// named <c>&lt;leaf&gt;_&lt;hash8&gt;</c> so a remap-import recovers the bone by hash even after
    /// Blender renames/wraps the rig; <paramref name="resolveBone"/> (e.g.
    /// <see cref="Skeleton.BoneTable.Path"/>) supplies the path for a hash, falling back to a flat
    /// <c>bone_&lt;hash8&gt;</c> node when the corpus has no transform for it.
    ///
    /// <para>For a prefab-shipped body carrying a <see cref="Skeleton.SceneRig"/>:
    /// <paramref name="scenePaths"/> overrides the per-bone paths (real names + parenting even on a Bip001
    /// rig the corpus table can't resolve), and <paramref name="uprighting"/> bakes the scene-rest rotation
    /// into the geometry while posing each bone at <c>inverse(bindPose)·G</c>, so model and rig stand
    /// upright together (see <see cref="RestBake"/>; undone at package build).</para>
    /// </summary>
    /// <param name="extraBones">Bones of the SUBJECT this geometry does not pose, so the armature a modder
    /// sees covers the whole outfit rather than the one part they opened (see <see cref="ExtraBone"/>).</param>
    /// <param name="log">where an extra bone this export had to refuse is named (see
    /// <see cref="AddExtraBones"/>); null drops those lines.</param>
    /// <param name="onUnreadableMap">handed the path of every map that would not decode (see
    /// <see cref="PreviewImageSet"/>).</param>
    public static void ExportRiggedGlb(UnityMesh mesh, MeshSkin skin, Func<uint, string?> resolveBone,
        string outPath, string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null,
        IReadOnlyList<string>? scenePaths = null, Matrix4x4? uprighting = null,
        IReadOnlyDictionary<string, Matrix4x4>? connectorRests = null,
        IReadOnlyList<ExtraBone>? extraBones = null, Action<string>? log = null,
        Action<string>? onUnreadableMap = null, IReadOnlyList<TextureTransportSource>? textureTransport = null,
        PreviewBlobMemo? previewMemo = null)
    {
        previewMemo ??= new PreviewBlobMemo();
        // Uprighting is the ONLY placement this export applies, and it moves geometry and joints together.
        // A part whose prefab mounts it by an unbakeable offset gets no placement at all: its bytes have to
        // stay raw bind space for the compile to round-trip, so mesh and armature sit together at the part's
        // own origin rather than the joints alone standing at the mount.
        if (uprighting is { } g) mesh = RestBake.Apply(mesh, g);
        mesh = SplitDuplicateFaces(mesh);
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        var (jointNodes, armature) = BuildArmature(scene, skin, resolveBone, scenePaths,
            uprighting, connectorRests, extraBones, log);
        var glSkin = BindSkin(model, mesh.Name + "_skin", jointNodes, armature);

        var (joints, weights) = SkinAttributes(mesh);
        var imgCache = new PreviewImageSet(onUnreadable: onUnreadableMap, previewMemo: previewMemo)
            { Labels = ImageLabels(textureTransport) };
        Material? material = baseColorPng is not null || normalPng is not null
            ? BuildPreviewMaterial(model, mesh.Name, baseColorPng, normalPng, imageCache: imgCache)
            : null;
        var submeshMats = BuildSubmeshMaterials(model, mesh.Name, mesh.Submeshes.Count, perSubmesh, imgCache);
        AddSkinnedMeshNode(model, scene, mesh, joints, weights, glSkin, material, submeshMats);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        model.SaveGLB(outPath);
        var transport = GltfTextureTransport.Write(outPath, textureTransport, onUnreadableMap, previewMemo);
        // The record says which space the file is in, so a prepared copy, a cache restore and the return
        // that comes back through it all read the bake this export applied.
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes, imgCache.Slots, transport,
            uprighting is { } baked ? RestBake.ToList(baked) : null);
    }

    /// <summary>One part to combine into a multi-mesh rigged glb: its mesh, skin, optional preview maps,
    /// and its scene-rig overrides (see <see cref="ExportRiggedGlb"/>). Paths and uprighting are per-part —
    /// a character's face can be upright while its body ships lying down. Without
    /// <see cref="PerSubmesh"/> every submesh flattens onto the part-level maps, so a multi-material part
    /// previews wrong in the combined session. A null ENTRY in <see cref="ScenePaths"/> takes the
    /// resolver's answer for that bone, so a part whose skin outruns the paths it was given still names
    /// what the rest of the subject knows.
    ///
    /// <para><see cref="ContextPose"/> — per-bone prefab scene rest worlds (Unity space, this skin's bone
    /// order) for a CONTEXT part: geometry and joints both export posed there, so a weapon sits at its
    /// mount instead of at its own origin. Display-only, for parts no session can send back — the posed
    /// bytes are not the raw bind-space round trip a writable part's compile needs. Excludes
    /// <see cref="Uprighting"/> (a baked part's geometry already carries its rest). A part with neither
    /// exports at its own bind rest, mesh and joints together.</para></summary>
    public readonly record struct RiggedPart(UnityMesh Mesh, MeshSkin Skin, string? BaseColorPng = null, string? NormalPng = null,
        IReadOnlyList<string?>? ScenePaths = null, Matrix4x4? Uprighting = null,
        IReadOnlyDictionary<string, Matrix4x4>? ConnectorRests = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? PerSubmesh = null,
        IReadOnlyList<Matrix4x4>? ContextPose = null,
        IReadOnlyList<TextureTransportSource>? TextureTransport = null);

    /// <summary>One bone of the SUBJECT that the exported geometry does not pose: its bone-name hash, the
    /// '/'-joined path it hangs on, and its rest world in Unity space — already composed with whatever
    /// uprighting the export bakes, so this codec only reflects it (see
    /// <see cref="AxisConvention.Reflect"/>).
    ///
    /// <para>These join the skin as JOINTS, appended after every joint the geometry poses and referenced by
    /// no vertex — a zero-weighted TAIL on the joint list. Joints is what they have to be: Blender's glTF
    /// importer turns into armature bones only the skin's joints and their node ancestors, so an extra
    /// carried as a plain node lands in the scene as a loose empty, outside the armature and impossible to
    /// weight-paint against (a vertex group binds to a bone, never to an empty). Appending at the TAIL is
    /// what keeps the invariant the node representation was protecting: every joint the geometry poses holds
    /// the index and the position it would have held with no extras at all, so a send that touched nothing
    /// re-splits onto the same bones and JOINTS_0/WEIGHTS_0 are byte-identical either way. Each is
    /// hash-named like any other joint, so weight painted onto one comes back on a joint
    /// <see cref="MeshApply.ResolveAuthoredJoints"/> maps by hash like every other.</para>
    ///
    /// <para>BYTE-stability of the glb is NOT among the invariants, and the gap opens where an extra's path
    /// is a CONNECTOR PREFIX of a bone the skin binds. Unplaced, that connector has no world; the extra gives
    /// it one, so <see cref="BuildNodeTree"/> re-expresses each child's local as
    /// <c>world·inverse(parentWorld)</c> and <see cref="BindSkin"/> derives the inverse-bind matrix from the
    /// recomposed world — the joints under it shift by float epsilon (nothing at all on a translation-only
    /// rig, ~1e-7 measured on a rotated one). The connector node also
    /// gains the <c>_&lt;hash8&gt;</c> suffix it has no hash for while unplaced. Both are invisible to the
    /// re-split, which keys on joint names and hashes; neither leaves room for a byte-compare across the
    /// with/without pair.</para>
    ///
    /// <para>A bone the geometry DOES pose keeps its own placement: an entry whose path already carries a
    /// bone is skipped, so nothing here can move a joint the skin binds — or duplicate it into the
    /// tail.</para></summary>
    public readonly record struct ExtraBone(uint Hash, string Path, Matrix4x4 RestWorld);

    /// <summary>
    /// Write several skinned parts into ONE glb sharing ONE armature = the <b>union</b> of all their bones.
    /// Each part keeps its own geometry + preview material and binds to the shared skin, its per-vertex
    /// JOINTS remapped from its own bone order into the union order. <c>ImportGlb(path, meshName)</c> reads
    /// any one part back by name, so the round-trip and package payload stay per-part.
    /// </summary>
    /// <param name="extraBones">Bones of the SUBJECT that none of <paramref name="parts"/> poses — the rest
    /// of the outfit's skeleton (see <see cref="ExtraBone"/>).</param>
    /// <param name="log">where an extra bone this export had to refuse is named (see
    /// <see cref="AddExtraBones"/>); null drops those lines.</param>
    /// <param name="onUnreadableMap">handed the path of every map that would not decode (see
    /// <see cref="PreviewImageSet"/>).</param>
    public static void ExportCombinedRiggedGlb(IReadOnlyList<RiggedPart> parts, Func<uint, string?> resolveBone,
        string outPath, IReadOnlyList<ExtraBone>? extraBones = null, Action<string>? log = null,
        Action<string>? onUnreadableMap = null, PreviewBlobMemo? previewMemo = null)
    {
        previewMemo ??= new PreviewBlobMemo();
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");

        // 1. union of bones across every part (first part to use a bone fixes its pose), then the subject's
        // remaining bones — added LAST so no part's joint index moves
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var partPaths = parts.Select(p => CollectBones(p.Skin, resolveBone, worldOf, hashOf, order, seen,
            p.ScenePaths, p.Uprighting, p.ConnectorRests, p.ContextPose)).ToList();
        var extraPaths = AddExtraBones(extraBones, worldOf, hashOf, order, seen, log);
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);

        // 2. union skin: the parts' own bones in hierarchy order, then the subject's remaining bones as a
        // zero-weighted TAIL (see ExtraBone) — appended, never merged into the hierarchy walk, so every
        // index a part remaps onto is the index it would have had with no extras at all.
        // Keyed on hashOf, not worldOf — a connector prefix gains a world when the scene rig supplies its
        // rest (see CollectBones), but only hash-named bones may be skin joints: a hash-less joint in a
        // send-back would poison the per-part re-split and every later open of that part.
        var extraSet = new HashSet<string>(extraPaths, StringComparer.Ordinal);
        var unionBones = order.Where(p => hashOf.ContainsKey(p) && !extraSet.Contains(p)).ToList();
        unionBones.AddRange(extraPaths);
        var combinedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < unionBones.Count; i++) combinedIndex[unionBones[i]] = i;
        var glSkin = BindSkin(model, "combined_skin", unionBones.Select(p => nodeOf[p]).ToList(), armature);

        // 3. each part: own mesh, JOINTS remapped local→combined, bound to the shared skin
        // Dedup an image shared across parts. No authored marking: the send back for a part is classified
        // against that part's OWN prepared glb's record, never this file's, so a mark written here would be
        // read by nothing.
        var imgCache = new PreviewImageSet(onUnreadable: onUnreadableMap, previewMemo: previewMemo)
            { Labels = ImageLabels(parts.SelectMany(part => part.TextureTransport ?? Array.Empty<TextureTransportSource>())) };
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            var srcMesh = SplitDuplicateFaces(part.Mesh);
            var l2c = partPaths[pi].Select(p => combinedIndex[p]).ToArray();
            var (joints, weights) = SkinAttributes(srcMesh, l2c);
            Material? material = part.BaseColorPng is not null || part.NormalPng is not null
                ? BuildPreviewMaterial(model, part.Mesh.Name, part.BaseColorPng, part.NormalPng, imageCache: imgCache)
                : null;
            var submeshMats = BuildSubmeshMaterials(model, part.Mesh.Name, part.Mesh.Submeshes.Count,
                part.PerSubmesh, imgCache);
            var partMesh = part.Uprighting is { } g ? RestBake.Apply(srcMesh, g)
                : part.ContextPose is { } pose ? PoseAtRest(srcMesh, part.Skin, pose)
                : srcMesh;
            AddSkinnedMeshNode(model, scene, partMesh, joints, weights, glSkin, material, submeshMats);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        model.SaveGLB(outPath);
        var transport = GltfTextureTransport.Write(outPath,
            parts.SelectMany(part => part.TextureTransport ?? Array.Empty<TextureTransportSource>()),
            onUnreadableMap, previewMemo);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes, imgCache.Slots, transport);
    }

    /// <summary>
    /// Re-split: rewrite ONE part out of a multi-mesh rigged glb (a combined Blender send-back) as its own
    /// rigged workspace glb, <b>preserving the skin</b>. The armature is reconstructed from the source's
    /// own joint nodes (same names, rest worlds, parenting). Connector nodes survive on the ancestor chains
    /// but are excluded from the SKIN — only hash-named joints are bindable, so the re-open's
    /// <see cref="ReadRiggedGlb"/> always accepts what this writes; a weight painted onto a non-bone joint
    /// refuses the re-split with a message naming it. A skinless part falls back to the un-rigged workspace
    /// shape. Either way the part's preview materials and map sidecar are rebuilt over the maps it came back
    /// on (<see cref="WorkspaceSubmeshMaps"/>); the GEOMETRY written is exactly the returned edit.
    /// Returns the imported payload so a caller needing it avoids a second parse.
    ///
    /// <para><paramref name="authoredMaps"/> is the send-back's own authored files per submesh, absolute — the
    /// files the intake has already written. They are embedded in preference to the stock maps they replace,
    /// so re-opening the part alone shows the modder's own work rather than the game textures it covers. Null
    /// leaves the part on the stock maps alone.</para>
    ///
    /// <para><paramref name="beforeWrite"/> is handed <paramref name="outPath"/> immediately before that file
    /// is written, and only when it is about to be: everything that can refuse the re-split runs ahead of it,
    /// so a refusal leaves it silent. That ordering is the invariant the tests hold this method to; no
    /// production caller passes one.</para>
    ///
    /// <para><paramref name="recordGlb"/> names the glb the map-origin record was written beside, as in
    /// <see cref="ReadSubmeshMaps(ParsedGlb, string?, string?)"/>: the stock maps re-embedded here are the
    /// ones that record settles, so a send arriving under a name of its own resolves none without it. Null
    /// reads the record beside <paramref name="source"/> itself.</para>
    ///
    /// <para><paramref name="refitTo"/> REFITS the armature: the source's joints are reduced to the ones its
    /// geometry actually rides, and the joints that file offers are appended after them as a zero-weighted
    /// tail. It is what a session's prepare passes when the source is an EDIT: a workspace glb froze whatever
    /// armature its last send carried — a stale tail, or a whole outfit's union armature if that send came
    /// from a combined session — and the file the modder opens has to offer THIS run's bones instead. The
    /// modder's own paint is what survives the reduction, so a bone they genuinely weight rides through even
    /// when no part of the offer names it. Null leaves the source's joint list exactly as it stands, which is
    /// what every send-back re-split wants. A refit file carrying no skin is a rigid part with no armature to
    /// offer and refits nothing; a refit file that DOES pose, against a source that carries no skin at all,
    /// is refused — the part is posed and what came back is not.</para>
    ///
    /// <para><paramref name="afterSourceRead"/> is invoked at the last moment a failure could still be
    /// <paramref name="source"/>'s own: its geometry, skin and bone hashes are read and accepted, and
    /// everything past it — the map record beside <paramref name="recordGlb"/>, the write of
    /// <paramref name="outPath"/> — is about files this call was pointed AT. A caller that answers "the
    /// source could not be read" reads it to keep that answer off a full disk and a damaged record.</para>
    /// </summary>
    public static MeshApply.Payload ReexportPartGlb(string sourcePath, string? meshName, string outPath,
        Action<string>? beforeWrite = null, string? recordGlb = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authoredMaps = null,
        ParsedGlb? refitTo = null, Action? afterSourceRead = null,
        IReadOnlyList<TextureTransportOverride>? authoredTextures = null,
        ParsedGlb? geometryBaseline = null, PreviewBlobMemo? previewMemo = null,
        Matrix4x4? uprighting = null, IReadOnlyList<float>? bakedRest = null) =>
        // Lenient: the source is a Blender send-back (see LoadModel).
        ReexportPartGlb(ParsedGlb.Open(sourcePath), meshName, outPath, beforeWrite, recordGlb, authoredMaps,
            refitTo, afterSourceRead, authoredTextures, geometryBaseline, previewMemo, uprighting, bakedRest);

    /// <inheritdoc cref="ReexportPartGlb(string, string?, string, Action{string}?, string?, IReadOnlyList{ValueTuple{string?, string?, string?}}?, ParsedGlb?, Action?)"/>
    /// <param name="uprighting">stands a BIND-SPACE source up into scene-rest space, geometry and armature
    /// together (see <see cref="RestBake"/>) — an edit sent back before its part opened upright joins
    /// the session in the space the session is in.</param>
    /// <param name="bakedRest">the rest the WRITTEN file is baked by, recorded beside it so a send-back
    /// through it marks its asset with the space it came from.</param>
    public static MeshApply.Payload ReexportPartGlb(ParsedGlb source, string? meshName, string outPath,
        Action<string>? beforeWrite = null, string? recordGlb = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authoredMaps = null,
        ParsedGlb? refitTo = null, Action? afterSourceRead = null,
        IReadOnlyList<TextureTransportOverride>? authoredTextures = null,
        ParsedGlb? geometryBaseline = null, PreviewBlobMemo? previewMemo = null,
        Matrix4x4? uprighting = null, IReadOnlyList<float>? bakedRest = null)
    {
        previewMemo ??= new PreviewBlobMemo();
        var model = source.Model;
        var record = recordGlb ?? source.Path;
        // Read BEFORE the source's own payload: a refit file that will not answer is not the source's fault,
        // and a caller classifying failures by afterSourceRead must not be handed this one as the source's.
        // (In production the refit file is the run's own rigged build of the very part named here, so a miss
        // is unreachable — the bare branch's re-split of that same file would already have refused it.)
        var offered = refitTo is null ? null : OfferedJoints(refitTo, meshName);
        var contract = geometryBaseline ?? refitTo;
        var baselineMesh = contract is null ? null : ImportCorePayload(contract.Model, meshName).Mesh;
        var payload = ImportCorePayload(model, meshName);
        if (baselineMesh is not null) MeshApply.ConformTransportUvs(baselineMesh, payload.Mesh);
        var authoredSources = AuthoredSources(authoredMaps);
        if (!payload.HasSkin)
        {
            // A part the offer poses, standing on geometry that came back with no armature at all. The
            // combined route refuses the same file by name (its union armature has nothing to join it by),
            // and a lone open that accepted it would hand Blender an unrigged part under a posed part's name.
            if (offered is not null)
                throw new InvalidOperationException(
                    $"'{payload.Mesh.Name}' carries no armature, but the part it stands for is posed by "
                    + "one. Send it again with its skinned mesh, armature and all.");
            afterSourceRead?.Invoke();
            var unriggedTextureTransport = WorkspaceTextureTransport(record, meshName,
                payload.Mesh.Submeshes.Count, authoredMaps, authoredTextures);
            // The source's own carrier rides into the read: a send-back whose untouched maps came back as
            // hash-only markers re-embeds those slots from the record's pictures rather than losing them
            // with the image-less standard channels.
            var maps = WorkspaceSubmeshMaps(ReadSubmeshMaps(model, record, meshName, source.Stock,
                source.Transport), authoredMaps);
            beforeWrite?.Invoke(outPath);
            ExportGlb(payload.Mesh, outPath, perSubmesh: maps, authoredSources: authoredSources,
                textureTransport: unriggedTextureTransport);
            return payload;
        }

        // A bind-space source stands up here: the mesh takes the rotation as Unity floats, and every node
        // world the source recorded — glTF-space already — takes it below through the same reflection the
        // exporter applies, so mesh and armature move together.
        if (uprighting is { } stand)
            payload = new MeshApply.Payload
            {
                Mesh = RestBake.Apply(payload.Mesh, stand),
                JointIndices = payload.JointIndices,
                JointWeights = payload.JointWeights,
                SkinJointHashes = payload.SkinJointHashes,
            };
        var glMesh = meshName is null
            ? model.LogicalMeshes.First()
            : model.LogicalMeshes.First(m => m.Name == meshName);   // ImportPayload already gave the nice miss error
        var skin = FindSkin(model, glMesh)!;                        // non-null: the payload's skin came from it

        // The armature WRAPPER (skin.Skeleton and its ancestors) is not part of any bone path — both our
        // exports and Blender's re-wrap on write, so keeping it would nest one level per round trip.
        // Never exclude an actual joint (some exporters point Skeleton at the root bone).
        var jointNodes = Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i]).ToList();
        var jointSet = new HashSet<Node>(jointNodes);
        var wrapper = new HashSet<Node>();
        for (var s = skin.Skeleton; s is not null; s = s.VisualParent)
            if (!jointSet.Contains(s)) wrapper.Add(s);

        // The workspace SKIN carries only joints with a recoverable bone hash. A send-back can hand back
        // connector nodes — hierarchy glue with no _<hash8> in the name — as skin joints; written into the
        // workspace skin, such a joint makes the next open refuse the whole file. They drop out of the skin
        // here and survive as plain armature nodes. A non-zero weight on one is work we cannot ship —
        // refuse loudly rather than discard it.
        var srcHashes = payload.SkinJointHashes!;
        var hashed = new List<int>();
        for (int i = 0; i < jointNodes.Count; i++) if (srcHashes[i] != 0) hashed.Add(i);
        if (hashed.Count == 0)
            throw new InvalidOperationException(
                $"none of the skin joints on '{payload.Mesh.Name}' carry a bone hash in their names - "
                + "the armature doesn't look like one this app exported, so the edit can't be read back");
        if (hashed.Count < jointNodes.Count)
        {
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < payload.JointIndices!.Length; k++)
                if (payload.JointWeights![k] != 0 && srcHashes[payload.JointIndices[k]] == 0)
                    offenders.Add(jointNodes[payload.JointIndices[k]].Name ?? "(unnamed)");
            if (offenders.Count > 0)
                throw new InvalidOperationException(
                    $"'{payload.Mesh.Name}' has weights on {string.Join(", ", offenders)}, which "
                    + "are not the game's bones. Move those weights onto the armature this session "
                    + "opened (or delete those vertex groups) and send again.");
        }

        // A REFIT keeps only the joints the geometry RIDES. Everything else the source carried — the tail
        // its last send froze, another part's bones if that send came out of a combined session — is
        // dropped here and re-offered below from the file that says what THIS run poses. The modder's own
        // paint decides: a bone they weighted survives whether or not the offer names it.
        var keep = hashed;
        if (offered is not null)
        {
            var rides = new bool[jointNodes.Count];
            for (int k = 0; k < payload.JointIndices!.Length; k++)
                if (payload.JointWeights![k] != 0) rides[payload.JointIndices[k]] = true;
            keep = hashed.Where(i => rides[i]).ToList();
            if (keep.Count == 0)
                throw new InvalidOperationException(
                    $"'{payload.Mesh.Name}' came back with no vertex weighted to any bone, so there is "
                    + "nothing to stand it on. Paint it onto the session armature's bones and send again.");
        }
        var oldToNew = new int[jointNodes.Count];
        Array.Fill(oldToNew, -1);
        for (int k = 0; k < keep.Count; k++) oldToNew[keep[k]] = k;

        // Every joint's ancestor chain (below the wrapper) becomes a path; every node on it — connectors
        // too — records its ACTUAL world from the source, so nothing snaps back to the origin. Those worlds
        // are already glTF-space (no Reflect: they came from a glb, not from Unity bind poses).
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new string[keep.Count];
        for (int ki = 0; ki < keep.Count; ki++)
        {
            paths[ki] = ChainPath(jointNodes[keep[ki]], wrapper, worldOf);
            RegisterPrefixes(paths[ki], order, seen);
            hashOf.TryAdd(paths[ki], srcHashes[keep[ki]]);
        }
        // Unity world W' = W·G reflects to glTF as Reflect(W)·Reflect(G): the source's own nodes stand up
        // by the reflected rotation. The offer below is this run's build of the same part, already in
        // that space, so its worlds are taken as they are.
        if (uprighting is { } lift)
        {
            var reflected = AxisConvention.Reflect(lift);
            foreach (var key in worldOf.Keys.ToList()) worldOf[key] = worldOf[key] * reflected;
        }
        // The offer, appended AFTER every joint the geometry rides, so those joints keep their indices and
        // this is a tail in the same sense AddExtraBones writes one. Matched BY HASH, never by path: the
        // source's node names came back through Blender and need not spell the path this run's build spells
        // for the same bone. A path a source joint already owns is left to it — one node, one joint.
        var tail = new List<string>();
        if (offered is not null)
        {
            var have = new HashSet<uint>(keep.Select(i => srcHashes[i]));
            foreach (var (path, hash) in offered.Joints)
            {
                if (hashOf.ContainsKey(path) || !have.Add(hash)) continue;
                RegisterPrefixes(path, order, seen);
                foreach (var prefix in Prefixes(path))
                    if (offered.Worlds.TryGetValue(prefix, out var w)) worldOf.TryAdd(prefix, w);
                hashOf[path] = hash;
                tail.Add(path);
            }
        }

        var outModel = ModelRoot.CreateModel();
        var scene = outModel.UseScene("scene");
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);
        var glSkin = BindSkin(outModel, payload.Mesh.Name + "_skin",
            paths.Concat(tail).Select(p => nodeOf[p]).ToList(), armature);

        int n4 = payload.VertexCount;
        var joints = new ushort[n4 * 4];
        var weights = new Vector4[n4];
        for (int v = 0; v < n4; v++)
        {
            for (int k = 0; k < 4; k++)
            {
                // a dropped joint only reaches here with weight 0 (checked above) — park it on joint 0
                int mapped = oldToNew[payload.JointIndices![v * 4 + k]];
                joints[v * 4 + k] = (ushort)(mapped < 0 ? 0 : mapped);
            }
            weights[v] = new Vector4(payload.JointWeights![v * 4], payload.JointWeights[v * 4 + 1],
                                     payload.JointWeights[v * 4 + 2], payload.JointWeights[v * 4 + 3]);
        }
        // Everything the SOURCE could refuse is behind us; the record read and the write below answer for
        // the files this call was pointed at.
        afterSourceRead?.Invoke();
        var textureTransport = WorkspaceTextureTransport(record, meshName, payload.Mesh.Submeshes.Count,
            authoredMaps, authoredTextures);
        // The part's own preview materials + map sidecar, rebuilt over the maps it came back on, so a re-open
        // ALONE opens textured and its next send-back classifies those maps against the same record the
        // session's own read used.
        var imgCache = new PreviewImageSet(authoredSources, previewMemo: previewMemo)
            { Labels = ImageLabels(textureTransport) };
        var submeshMats = BuildSubmeshMaterials(outModel, payload.Mesh.Name, payload.Mesh.Submeshes.Count,
            WorkspaceSubmeshMaps(ReadSubmeshMaps(model, record, meshName, source.Stock, source.Transport),
                authoredMaps), imgCache);
        AddSkinnedMeshNode(outModel, scene, payload.Mesh, joints, weights, glSkin, material: null, submeshMats);

        beforeWrite?.Invoke(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        outModel.SaveGLB(outPath);
        var transport = GltfTextureTransport.Write(outPath, textureTransport, previewMemo: previewMemo);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes, imgCache.Slots, transport,
            bakedRest);
        return payload;
    }

    /// <summary>The project labels the transport carries for the modder's own pictures, by picture path, for
    /// naming the embedded images Blender lists; null when it carries none.</summary>
    private static IReadOnlyDictionary<string, string>? ImageLabels(IEnumerable<TextureTransportSource>? sources)
    {
        if (sources is null) return null;
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            if (source.Label is { Length: > 0 } label) labels.TryAdd(source.Png, label);
        return labels.Count == 0 ? null : labels;
    }

    /// <summary>Rebuild the outbound property inventory over the modder's authored pictures. The sidecar's
    /// stock resource and owner stay authoritative; an override changes only the pixels, origin and the
    /// label Blender lists the picture under.</summary>
    private static IReadOnlyList<TextureTransportSource> WorkspaceTextureTransport(string recordGlb,
        string? meshName, int primitiveCount,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? legacyAuthored,
        IReadOnlyList<TextureTransportOverride>? authored)
    {
        // Folded onto the primitives the re-split WRITES: what came back may carry more submeshes than the
        // record was projected over, and each extra one takes the last drawable material's rows exactly as
        // the build draws it and the edit's cards slot it (see MaterialFold).
        var rows = MaterialFold.FoldOntoPrimitives(PreviewMaps.ReadTransportBindings(recordGlb)
            .Where(binding => meshName is null || string.Equals(binding.Mesh, meshName, StringComparison.Ordinal))
            .ToList(), primitiveCount);
        if (rows.Count == 0) return Array.Empty<TextureTransportSource>();
        return rows.Select(binding =>
        {
            var exact = authored?.FirstOrDefault(candidate =>
                candidate.Covers(binding.MaterialIndex, binding.PrimitiveIndex)
                && string.Equals(candidate.ShaderProperty, binding.ShaderProperty, StringComparison.Ordinal));
            if (exact?.Png is not { Length: > 0 })
                exact = authored?.FirstOrDefault(candidate =>
                    candidate.Covers(binding.MaterialIndex, binding.PrimitiveIndex)
                    && candidate.ShaderProperty.Length == 0 && candidate.Kind == binding.Kind);
            string? replacement = exact?.Png is { Length: > 0 } png ? png : null;
            string? label = replacement is null ? null : exact?.Label;
            if (replacement is null && legacyAuthored is not null
                && binding.PrimitiveIndex is { } primitive && primitive >= 0 && primitive < legacyAuthored.Count)
            {
                var fixedMaps = legacyAuthored[primitive];
                replacement = binding.Kind switch
                {
                    MapKind.BaseColor => fixedMaps.Base,
                    MapKind.Normal => fixedMaps.Normal,
                    MapKind.Rmo => fixedMaps.Rmo,
                    _ => null,
                };
            }
            return new TextureTransportSource(binding.Mesh, binding.MaterialIndex, binding.PrimitiveIndex,
                binding.ShaderProperty, binding.Kind, replacement ?? binding.Source, binding.Stock.Name,
                binding.Stock.Bundle, binding.Stock.PathId, binding.Srgb,
                replacement is null ? binding.Origin : MapOrigin.Authored, binding.Parameters, binding.TexCoord,
                binding.Drawable, label);
        }).ToList();
    }

    /// <summary>The bones one already-parsed rigged glb OFFERS for a part: every skin joint of that mesh
    /// whose name carries a bone hash, as the node path it sits at, paired with the glTF-space world of
    /// every node on its ancestor chain (connectors included, so nothing an append brings in snaps back to
    /// the origin). Null when the file carries no such mesh, no skin, or no hash-named joint — a rigid part
    /// offers no armature, and there is nothing to refit onto.</summary>
    private static ArmatureOffer? OfferedJoints(ParsedGlb glb, string? meshName)
    {
        var model = glb.Model;
        var glMesh = meshName is null
            ? model.LogicalMeshes.FirstOrDefault()
            : model.LogicalMeshes.FirstOrDefault(m => m.Name == meshName);
        if (glMesh is null) return null;
        var skin = FindSkin(model, glMesh);
        if (skin is null) return null;
        var hashes = ReadJointHashes(skin);
        var jointNodes = Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i]).ToList();
        // The armature WRAPPER is excluded exactly as the re-split excludes it, so the two files' paths for
        // one bone are spelled the same way whenever their node names are.
        var jointSet = new HashSet<Node>(jointNodes);
        var wrapper = new HashSet<Node>();
        for (var s = skin.Skeleton; s is not null; s = s.VisualParent)
            if (!jointSet.Contains(s)) wrapper.Add(s);

        var joints = new List<(string Path, uint Hash)>();
        var worlds = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        for (int i = 0; i < jointNodes.Count && i < hashes.Length; i++)
            if (hashes[i] != 0) joints.Add((ChainPath(jointNodes[i], wrapper, worlds), hashes[i]));
        return joints.Count == 0 ? null : new ArmatureOffer(joints, worlds);
    }

    /// <summary>What <see cref="OfferedJoints"/> hands the refit: the offered joints in the offer file's own
    /// order, and the world of every node their chains pass through, keyed by path.</summary>
    private sealed record ArmatureOffer(IReadOnlyList<(string Path, uint Hash)> Joints,
        IReadOnlyDictionary<string, Matrix4x4> Worlds);

    /// <summary>The '/'-joined path one joint node sits at, walking up to (not through) the armature
    /// wrapper, recording every node on the way at its own glTF-space world. FIRST world wins, the union
    /// rule every other accumulator here follows.</summary>
    private static string ChainPath(Node joint, HashSet<Node> wrapper, Dictionary<string, Matrix4x4> worlds)
    {
        var chain = new List<Node>();
        for (var n = joint; n is not null && !wrapper.Contains(n); n = n.VisualParent) chain.Add(n);
        chain.Reverse();
        string path = "";
        foreach (var n in chain)
        {
            path = path.Length == 0 ? NodeKey(n) : path + "/" + NodeKey(n);
            worlds.TryAdd(path, n.WorldMatrix);
        }
        return path;
    }

    /// <summary>A node's path segment: its own name with '/' folded away, since '/' is what separates
    /// segments, or a positional stand-in for an unnamed node.</summary>
    private static string NodeKey(Node n) =>
        string.IsNullOrEmpty(n.Name) ? $"node{n.LogicalIndex}" : n.Name.Replace('/', '_');

    /// <summary>Every '/'-prefix of a path, parents first — the order <see cref="BuildNodeTree"/> needs its
    /// nodes registered in.</summary>
    private static IEnumerable<string> Prefixes(string path)
    {
        var segs = path.Split('/');
        for (int k = 1; k <= segs.Length; k++) yield return string.Join("/", segs.Take(k));
    }

    /// <summary>Register a path and every ancestor prefix of it in the node ordering, once each.</summary>
    private static void RegisterPrefixes(string path, List<string> order, HashSet<string> seen)
    {
        foreach (var prefix in Prefixes(path)) if (seen.Add(prefix)) order.Add(prefix);
    }

    /// <summary>Create a skin over the given joint nodes (in joint order), deriving each inverse-bind matrix
    /// from the node's exact rest world, and anchor it on the armature root.
    ///
    /// <para>The invert is unchecked because every joint reaching here already has an invertible rest: a
    /// skinned bone's comes from inverting its bind pose, and an extra's is refused at
    /// <see cref="AddExtraBones"/>. Were one to slip through, <see cref="Matrix4x4.Invert(Matrix4x4, out
    /// Matrix4x4)"/> would leave the result full of NaN (measured), not zeroed.</para></summary>
    private static Skin BindSkin(ModelRoot model, string name, List<Node> jointNodes, Node armature)
    {
        var skin = model.CreateSkin(name);
        skin.BindJoints(jointNodes.Select(n =>
        {
            Matrix4x4.Invert(n.WorldMatrix, out var ibm);   // rest world is exact; IBM = its inverse
            return (n, ibm);
        }).ToArray());
        skin.Skeleton = armature;
        return skin;
    }

    /// <summary>Add a mesh (geometry + per-submesh primitives sharing one vertex pool, plus
    /// JOINTS_0/WEIGHTS_0) as a scene node bound to <paramref name="glSkin"/>. The geometry mapping matches
    /// <see cref="ExportGlb"/>, so rig and mesh share one un-mirrored space.</summary>
    private static void AddSkinnedMeshNode(ModelRoot model, Scene scene, UnityMesh mesh,
        ushort[] joints, Vector4[] weights, Skin glSkin, Material? material, Material?[]? submeshMats = null)
    {
        var glMesh = model.CreateMesh(mesh.Name);
        var positions = Map3(mesh, "Vertex", AxisConvention.Position);
        // Unit-length normals, as the plain Write path does (see its note).
        var normals = mesh.Has("Normal") ? Normalize(Map3(mesh, "Normal", AxisConvention.Normal)) : null;
        var tangents = mesh.Has("Tangent")
            ? SanitizeTangents(Map4(mesh, "Tangent", AxisConvention.Tangent), normals)
            : null;
        var uvs = TransportUvs(mesh);

        MeshPrimitive? shared = null;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var prim = glMesh.CreatePrimitive();
            if (shared is null)
            {
                prim.WithVertexAccessor("POSITION", positions);
                if (normals is not null) prim.WithVertexAccessor("NORMAL", normals);
                if (tangents is not null) prim.WithVertexAccessor("TANGENT", tangents);
                for (int i = 0; i < uvs.Count; i++)
                    prim.WithVertexAccessor($"TEXCOORD_{i}", uvs[i]);
                prim.SetVertexAccessor("JOINTS_0", JointAccessor(model, joints));
                prim.WithVertexAccessor("WEIGHTS_0", weights);
                shared = prim;
            }
            else
            {
                foreach (var attr in shared.VertexAccessors)
                    prim.SetVertexAccessor(attr.Key, attr.Value);
            }
            prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, AxisConvention.ReverseWinding(mesh.Submeshes[s]));
            var pm = submeshMats?[s] ?? material;
            if (pm is not null) prim.Material = pm;
        }

        scene.CreateNode(mesh.Name).WithMesh(glMesh).Skin = glSkin;
    }

    /// <summary>
    /// Build the armature for a skin and return the joint nodes <b>in bone order</b> (so a vertex's
    /// BlendIndices index straight into <c>skin.Joints</c> with no remap), followed by
    /// <paramref name="extraBones"/> as a zero-weighted tail no vertex names (see <see cref="ExtraBone"/>),
    /// plus the skeleton root. Each
    /// bone's rest world = <c>Reflect(inverse(bindPose))</c>; the path splits on '/' into a parented node
    /// chain, and intermediate "connector" prefixes that aren't skinned bones get an identity world. A
    /// node's local = <c>world · inverse(parentWorld)</c>, so every node's world lands back on its assigned
    /// rest world regardless of connectors. All path roots hang under one synthetic <c>armature</c> node:
    /// <c>Skin.BindJoints</c> requires a single common ancestor, and fallback <c>bone_&lt;hash&gt;</c>
    /// bones are disconnected from the body's <c>root</c> hierarchy.
    /// </summary>
    private static (List<Node> jointNodes, Node armature) BuildArmature(
        Scene scene, MeshSkin skin, Func<uint, string?> resolveBone,
        IReadOnlyList<string?>? scenePaths = null, Matrix4x4? uprighting = null,
        IReadOnlyDictionary<string, Matrix4x4>? connectorRests = null,
        IReadOnlyList<ExtraBone>? extraBones = null, Action<string>? log = null)
    {
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = CollectBones(skin, resolveBone, worldOf, hashOf, order, seen,
            scenePaths, uprighting, connectorRests);
        var extraPaths = AddExtraBones(extraBones, worldOf, hashOf, order, seen, log);
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);
        // this skin's bone order first — index-for-index what it would be with no extras — then the tail
        return (paths.Concat(extraPaths).Select(p => nodeOf[p]).ToList(), armature);
    }

    /// <summary>
    /// Fold the subject's remaining bones (<see cref="ExtraBone"/>) into the accumulated armature state,
    /// AFTER every skinned bone is in: an entry whose path already carries a bone is skipped, so the joints
    /// the skin binds keep both their placement and their index. Returns the paths that became bones here,
    /// IN THE ORDER THEY WERE ADDED — the tail segment each export appends to its joint list.
    ///
    /// <para>That tail is this list, never a filter over <paramref name="order"/>: <paramref name="order"/>
    /// is the path-ordering the node tree is built from, where an extra sitting on a CONNECTOR PREFIX of a
    /// skinned bone was already registered as a prefix and would come back mid-list. Deriving the tail that
    /// way would insert a joint ahead of joints the geometry poses and move their indices, which is the one
    /// thing appending is for.</para>
    ///
    /// <para>A prefix these introduce that is neither a bone nor a known connector gets
    /// <see cref="BuildNodeTree"/>'s identity world, as any unplaced connector does.</para>
    ///
    /// <para>A rest world that will not INVERT is refused here, named through <paramref name="log"/>.
    /// <see cref="Matrix4x4.Invert(Matrix4x4, out Matrix4x4)"/> fills its result with NaN when it fails
    /// (measured; it does not zero it), and two things downstream invert this world unchecked:
    /// <see cref="BindSkin"/> for the joint's inverse-bind matrix, and <see cref="BuildNodeTree"/> for
    /// every CHILD's local. One degenerate extra would therefore hand Blender an armature with NaN in it
    /// rather than one missing a bone.</para>
    /// </summary>
    /// <param name="log">named refusals, one line per bone; null keeps them to the caller's silence.</param>
    private static List<string> AddExtraBones(IReadOnlyList<ExtraBone>? extras,
        Dictionary<string, Matrix4x4> worldOf, Dictionary<string, uint> hashOf, List<string> order,
        HashSet<string> seen, Action<string>? log = null)
    {
        var added = new List<string>();
        if (extras is null) return added;
        foreach (var bone in extras)
        {
            // hashOf carries every bone already placed, skinned or extra, so a path can never join the tail
            // twice and an extra can never shadow a joint the geometry poses
            if (bone.Path.Length == 0 || hashOf.ContainsKey(bone.Path)) continue;
            if (!Matrix4x4.Invert(bone.RestWorld, out _))
            {
                log?.Invoke($"Bone {bone.Path} is off the armature: no usable rest pose.");
                continue;
            }
            var segs = bone.Path.Split('/');
            for (int k = 1; k <= segs.Length; k++)
            {
                var prefix = string.Join("/", segs.Take(k));
                if (seen.Add(prefix)) order.Add(prefix);
            }
            // a connector rest already placed this prefix from the prefab's own scene worlds — keep it
            if (!worldOf.ContainsKey(bone.Path)) worldOf[bone.Path] = AxisConvention.Reflect(bone.RestWorld);
            hashOf[bone.Path] = bone.Hash;
            added.Add(bone.Path);
        }
        return added;
    }

    private static (Dictionary<string, Matrix4x4> worldOf, Dictionary<string, uint> hashOf, List<string> order)
        NewBoneAccumulators() =>
        (new(StringComparer.Ordinal), new(StringComparer.Ordinal), new());

    /// <summary>
    /// Accumulate one skin's bones into the shared armature state, so several skins can union into one rig:
    /// resolve each bone's path, record its reflected rest world (<b>first path wins</b> — a bone shared
    /// across parts keeps the first part's pose), and register every '/'-split prefix in
    /// <paramref name="order"/> (parents before children). Returns this skin's per-bone paths in bone order.
    /// <paramref name="scenePaths"/> overrides the hash lookup per bone — a null entry falls through to
    /// the resolver; <paramref name="uprighting"/> composes each rest world to <c>inverse(bindPose)·G</c>
    /// so the rig stands inside the G-baked geometry. <paramref name="boneWorlds"/> instead places each
    /// bone at its given world outright (a context part's prefab scene rests, matching its posed
    /// geometry); a bone past its count falls back to the bind-derived world.
    /// </summary>
    private static string[] CollectBones(MeshSkin skin, Func<uint, string?> resolveBone,
        Dictionary<string, Matrix4x4> worldOf, Dictionary<string, uint> hashOf, List<string> order, HashSet<string> seen,
        IReadOnlyList<string?>? scenePaths = null, Matrix4x4? uprighting = null,
        IReadOnlyDictionary<string, Matrix4x4>? connectorRests = null,
        IReadOnlyList<Matrix4x4>? boneWorlds = null)
    {
        var paths = new string[skin.BoneCount];
        for (int i = 0; i < skin.BoneCount; i++)
        {
            uint h = skin.BoneHashes[i];
            string path = (scenePaths is not null && i < scenePaths.Count ? scenePaths[i] : null)
                          ?? resolveBone(h) ?? $"bone_{h:x8}";
            paths[i] = path;
            if (!worldOf.ContainsKey(path))
            {
                if (boneWorlds is not null && i < boneWorlds.Count)
                    worldOf[path] = AxisConvention.Reflect(boneWorlds[i]);
                else
                {
                    Matrix4x4.Invert(skin.BindPoses[i], out var restUnity);
                    if (uprighting is { } g) restUnity *= g;
                    worldOf[path] = AxisConvention.Reflect(restUnity);
                }
            }
            // name the node by hash even when its WORLD came from elsewhere — remap-import recovers by it
            hashOf.TryAdd(path, h);

            var segs = path.Split('/');
            for (int k = 1; k <= segs.Length; k++)
            {
                var prefix = string.Join("/", segs.Take(k));
                if (seen.Add(prefix)) order.Add(prefix);
            }
        }

        // Connector nodes (registered prefixes that aren't skinned bones) default to an identity world in
        // BuildNodeTree — parked at the origin. When the scene rig supplied their true bind-space rests,
        // place them properly, composed with the same uprighting. Skinned bones always win.
        if (connectorRests is not null)
            foreach (var (prefix, rest) in connectorRests)
                if (seen.Contains(prefix) && !worldOf.ContainsKey(prefix))
                {
                    var w = rest;
                    if (uprighting is { } g) w *= g;
                    worldOf[prefix] = AxisConvention.Reflect(w);
                }
        return paths;
    }

    /// <summary>Materialize the node tree for the accumulated bones: one synthetic <c>armature</c> root over
    /// every path root. A node's local = <c>world · inverse(parentWorld)</c> so its world lands back on its
    /// assigned rest world regardless of connector prefixes. Bone nodes are
    /// <c>&lt;leaf&gt;_&lt;hash8&gt;</c> for hash-recovery on remap-import.</summary>
    private static (Dictionary<string, Node> nodeOf, Node armature) BuildNodeTree(
        Scene scene, Dictionary<string, Matrix4x4> worldOf, Dictionary<string, uint> hashOf, List<string> order)
    {
        // No node world may carry a REFLECTION (linear determinant < 0). Blender's bone rest holds only
        // translation + rotation — its glTF importer decomposes each bind local and discards the scale — so
        // a reflected rest (the game mirrors a left-hand weapon mount from the right) comes back with every
        // node under it point-reflected THROUGH it, and the mesh re-baked onto that displaced rest. Negating
        // the linear part is the nearest proper rotation with the same position; position is the only thing
        // the pipeline consumes from these worlds (children re-derive their locals from it below, weighted
        // joints' own worlds are proper corpus-wide, and the send-back matches bones by hash, not axes).
        // Deliberately QUIET: this is routine normalization that loses nothing, firing on every open of
        // every mirrored-mount subject, so it earns no line anywhere the user reads.
        foreach (var prefix in order)
            if (worldOf.TryGetValue(prefix, out var world) && LinearDeterminant(world) < 0)
                worldOf[prefix] = NegateLinear(world);
        var armature = scene.CreateNode("armature");
        var nodeOf = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (var prefix in order)
        {
            int slash = prefix.LastIndexOf('/');
            string parent = slash < 0 ? "" : prefix[..slash];
            string leaf = slash < 0 ? prefix : prefix[(slash + 1)..];
            string name = leaf;
            if (hashOf.TryGetValue(prefix, out var bh))
            {
                string suffix = $"_{bh:x8}";
                name = leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? leaf : leaf + suffix;
            }

            var node = (parent.Length == 0 ? armature : nodeOf[parent]).CreateNode(name);
            var w = worldOf.GetValueOrDefault(prefix, Matrix4x4.Identity);
            var pw = parent.Length != 0 && worldOf.TryGetValue(parent, out var pwv) ? pwv : Matrix4x4.Identity;
            Matrix4x4.Invert(pw, out var pwInv);
            node.LocalMatrix = SnapAffine(w * pwInv);
            nodeOf[prefix] = node;
        }
        return (nodeOf, armature);
    }

    /// <summary>Snap the projective (4th) column to an exact <c>(0,0,0,1)</c>. Rest transforms are affine by
    /// construction, but inverting/multiplying rotation matrices leaves ~1e-6 noise there, which SharpGLTF's
    /// <c>LocalMatrix</c> affine guard rejects.</summary>
    private static Matrix4x4 SnapAffine(Matrix4x4 m)
    {
        m.M14 = 0; m.M24 = 0; m.M34 = 0; m.M44 = 1;
        return m;
    }

    /// <summary>Determinant of the linear (upper-left 3×3) part — negative for a rest carrying a
    /// reflection.</summary>
    internal static float LinearDeterminant(Matrix4x4 m) =>
        m.M11 * (m.M22 * m.M33 - m.M23 * m.M32)
        - m.M12 * (m.M21 * m.M33 - m.M23 * m.M31)
        + m.M13 * (m.M21 * m.M32 - m.M22 * m.M31);

    /// <summary>The same placement with the reflection folded away: for an orthonormal linear part with
    /// determinant −1, the negation is the (proper) rotation reaching the same point set; translation is
    /// untouched.</summary>
    private static Matrix4x4 NegateLinear(Matrix4x4 m)
    {
        m.M11 = -m.M11; m.M12 = -m.M12; m.M13 = -m.M13;
        m.M21 = -m.M21; m.M22 = -m.M22; m.M23 = -m.M23;
        m.M31 = -m.M31; m.M32 = -m.M32; m.M33 = -m.M33;
        return m;
    }

    /// <summary>Per-vertex skin data in glTF's 4-influence form. A mesh with no BlendIndices (a rigid prop)
    /// binds every vertex to bone 0 at weight 1; BlendIndices without BlendWeight gets weight (1,0,0,0).
    /// <paramref name="localToCombined"/> remaps each bone index from this mesh's own bone order into a
    /// union skin's joint order.</summary>
    private static (ushort[] joints, Vector4[] weights) SkinAttributes(UnityMesh mesh, int[]? localToCombined = null)
    {
        int n = mesh.VertexCount;
        var joints = new ushort[n * 4];
        var weights = new Vector4[n];

        float[]? bi = mesh.Has("BlendIndices") ? mesh.Channels["BlendIndices"] : null;
        int biDim = bi is not null ? mesh.Dims["BlendIndices"] : 0;
        float[]? bw = mesh.Has("BlendWeight") ? mesh.Channels["BlendWeight"] : null;
        int bwDim = bw is not null ? mesh.Dims["BlendWeight"] : 0;

        // An out-of-range BlendIndex is a real mismatch — fail loudly rather than binding those verts to
        // joint 0, which would deform them to the root and pass every structural gate.
        int Remap(int local)
        {
            if (localToCombined is null) return local;
            if (local < 0 || local >= localToCombined.Length)
                throw new InvalidOperationException(
                    $"BlendIndex {local} is out of range for the {localToCombined.Length}-bone skin. " +
                    "The mesh references a bone the combined skeleton doesn't have.");
            return localToCombined[local];
        }

        for (int v = 0; v < n; v++)
        {
            if (bi is not null)
                for (int d = 0; d < 4 && d < biDim; d++)
                    joints[v * 4 + d] = (ushort)Remap((int)MathF.Round(bi[v * biDim + d]));

            if (bw is not null)
                weights[v] = new Vector4(
                    bwDim > 0 ? bw[v * bwDim + 0] : 0,
                    bwDim > 1 ? bw[v * bwDim + 1] : 0,
                    bwDim > 2 ? bw[v * bwDim + 2] : 0,
                    bwDim > 3 ? bw[v * bwDim + 3] : 0);
            else
                weights[v] = new Vector4(1, 0, 0, 0);   // single-bone / rigid: full weight on slot 0
        }
        return (joints, weights);
    }

    /// <summary>A context part's mesh POSED at its prefab scene rest: every directional channel
    /// linear-blends through each influence's <c>bindPose·sceneWorld</c>, the skinning the game applies at
    /// rest. Display-only bytes — a context part is Reference in every session and never round-trips, which
    /// is what buys the part sitting where the prefab puts it while the skin's IBMs stay
    /// inverse-of-rest-world (moving a joint in Blender then pivots the geometry that sits under it).
    /// Missing skin channels mirror <see cref="SkinAttributes"/>: no BlendIndices = everything on bone 0,
    /// no BlendWeight = full weight on the first influence.</summary>
    private static UnityMesh PoseAtRest(UnityMesh mesh, MeshSkin skin, IReadOnlyList<Matrix4x4> sceneWorlds)
    {
        var boneXf = new Matrix4x4[skin.BoneCount];
        for (int b = 0; b < skin.BoneCount; b++)
            boneXf[b] = b < sceneWorlds.Count ? skin.BindPoses[b] * sceneWorlds[b] : Matrix4x4.Identity;

        int n = mesh.VertexCount;
        float[]? bi = mesh.Has("BlendIndices") ? mesh.Channels["BlendIndices"] : null;
        int biDim = bi is not null ? mesh.Dims["BlendIndices"] : 0;
        float[]? bw = mesh.Has("BlendWeight") ? mesh.Channels["BlendWeight"] : null;
        int bwDim = bw is not null ? mesh.Dims["BlendWeight"] : 0;
        var blended = new Matrix4x4[n];
        for (int v = 0; v < n; v++)
        {
            var m = default(Matrix4x4);
            float total = 0;
            for (int d = 0; d < 4; d++)
            {
                float w = bw is not null ? (d < bwDim ? bw[v * bwDim + d] : 0) : (d == 0 ? 1 : 0);
                if (w <= 0) continue;
                int bone = bi is not null && d < biDim ? (int)MathF.Round(bi[v * biDim + d]) : 0;
                if (bone < 0 || bone >= boneXf.Length) continue;
                m += boneXf[bone] * w;
                total += w;
            }
            blended[v] = total > 0 ? m : Matrix4x4.Identity;
        }

        var channels = new Dictionary<string, float[]>(mesh.Channels);
        if (channels.TryGetValue("Vertex", out var pos) && mesh.Dims.GetValueOrDefault("Vertex") == 3)
            channels["Vertex"] = Blend(pos, 3, point: true);
        if (channels.TryGetValue("Normal", out _))
        {
            int nd = mesh.Dims.GetValueOrDefault("Normal");
            if (nd is 3 or 4) channels["Normal"] = Blend(channels["Normal"], nd, point: false);
        }
        if (channels.TryGetValue("Tangent", out _) && mesh.Dims.GetValueOrDefault("Tangent") == 4)
            channels["Tangent"] = Blend(channels["Tangent"], 4, point: false);

        return new UnityMesh
        {
            Name = mesh.Name,
            VertexCount = mesh.VertexCount,
            Channels = channels,
            Dims = new Dictionary<string, int>(mesh.Dims),
            Submeshes = mesh.Submeshes,
        };

        float[] Blend(float[] data, int dim, bool point)
        {
            var r = new float[data.Length];
            for (int v = 0; v * dim + dim <= data.Length; v++)
            {
                var m = blended[v];
                float x = data[v * dim], y = data[v * dim + 1], z = data[v * dim + 2];
                r[v * dim] = x * m.M11 + y * m.M21 + z * m.M31 + (point ? m.M41 : 0);
                r[v * dim + 1] = x * m.M12 + y * m.M22 + z * m.M32 + (point ? m.M42 : 0);
                r[v * dim + 2] = x * m.M13 + y * m.M23 + z * m.M33 + (point ? m.M43 : 0);
                if (dim == 4) r[v * dim + 3] = data[v * dim + 3];   // normal pad / tangent handedness
            }
            return r;
        }
    }

    /// <summary>A JOINTS_0 accessor (VEC4 of UNSIGNED_SHORT, un-normalized) — the integer encoding the spec
    /// demands (SharpGLTF's float <c>WithVertexAccessor</c> would write an invalid FLOAT accessor).</summary>
    private static Accessor JointAccessor(ModelRoot model, ushort[] joints)
    {
        var bytes = new byte[joints.Length * sizeof(ushort)];
        System.Buffer.BlockCopy(joints, 0, bytes, 0, bytes.Length);
        var view = model.UseBufferView(bytes, 0, null, 0, BufferMode.ARRAY_BUFFER);
        var acc = model.CreateAccessor("JOINTS_0");
        acc.SetVertexData(view, 0, joints.Length / 4, DimensionType.VEC4, EncodingType.UNSIGNED_SHORT, normalized: false);
        return acc;
    }

    /// <summary>A preview material per submesh from a (base-color, normal, RMO) PNG-path assignment. Returns
    /// null (caller uses the single material) when no assignment was supplied; an entry is null where that
    /// submesh has no maps.
    ///
    /// <para>Each submesh gets its OWN material (<c>gf2_submesh&lt;s&gt;</c>) even when several resolve to
    /// the same texture: Blender shows one material slot per glTF material, so a distinct material keeps the
    /// submesh boundary selectable in Edit Mode — the visual aid a modder needs for the manual
    /// submesh→texture assignment that can't be auto-derived. Collapsing to one slot would hide it. The
    /// materials still share one <c>Image</c> via <paramref name="imageCache"/>, so the texture embeds
    /// once.</para>
    ///
    /// <para>A submesh whose maps are ALL absent still gets its named material, carrying no texture. Material
    /// identity is the submesh boundary itself and belongs to the geometry, not to the pictures on it: a
    /// texture that couldn't be read must cost that submesh its picture and nothing else. Dropping the
    /// material instead collapsed every such submesh onto whatever material the writer fell back to, and the
    /// send back then re-split the whole part onto one output position.</para></summary>
    private static Material?[]? BuildSubmeshMaterials(ModelRoot model, string meshName, int submeshCount,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh, PreviewImageSet? imageCache)
    {
        if (perSubmesh is null) return null;
        var mats = new Material?[submeshCount];
        for (int s = 0; s < submeshCount; s++)
        {
            var key = s < perSubmesh.Count ? perSubmesh[s] : default;
            mats[s] = BuildPreviewMaterial(model, meshName, key.Base, key.Normal, key.Rmo,
                SubmeshMaterialName(s), imageCache);
            if (Embeddable(key.Rmo)) imageCache?.UsedRmo(meshName, s, key.Rmo!);
            // What THIS submesh was given, slot by slot: the answer that tells its own map from a sibling
            // submesh's on the way back. Recorded from the same paths the materials above were built over.
            imageCache?.UsedStock(meshName, s, MapKind.BaseColor, key.Base);
            imageCache?.UsedStock(meshName, s, MapKind.Normal, key.Normal);
            imageCache?.UsedStock(meshName, s, MapKind.Rmo, key.Rmo);
        }
        return mats;
    }

    /// <summary>What THIS app names the material of one exported submesh. A round trip that leaves the slot
    /// list alone brings this name back, so it is also the name a replacement's submesh is presented and
    /// recorded under when nothing else says otherwise — see
    /// <see cref="Project.AuthoredDonorRows.MaterialNames"/>, which re-derives that list from the
    /// replacement's own submesh layout.</summary>
    public static string SubmeshMaterialName(int submesh) => $"gf2_submesh{submesh}";

    /// <summary>A metallic-roughness PBR material over the part's base color (<c>_d</c>/<c>_da</c>, sRGB),
    /// normal (<c>_n</c>) and RMO. Images are embedded into the glb — a snapshot of the current PNGs.
    ///
    /// <para>The RMO travels as a standard glTF ORM texture: ONE image on both the metallic-roughness and
    /// the occlusion channel, its channels permuted into glTF's order by <see cref="PreviewMaps"/>. glTF
    /// multiplies each factor by its texture channel, so a material carrying an RMO leaves the metallic and
    /// roughness factors at their 1.0 default — the matte stand-in factors would zero the map.</para>
    ///
    /// <para>The base colour alone selects one of three alpha classes. When at least 0.1% of the image remains
    /// alpha 16..239 after a 5x5 erosion it declares BLEND (see <see cref="BlendMidAlphaCoreFraction"/>);
    /// otherwise at least 0.1% below half declares MASK at <see cref="PreviewMaps.GltfAlphaCutoff"/>; everything
    /// else is OPAQUE. The
    /// game's opaque alpha ceiling is 254, so merely being short of 255 is not transparency and endpoint-only
    /// maps remain OPAQUE. BLEND is safe for this double-sided preview only with the Blender bridge's
    /// post-import setup: it disables transparency overlap and remaps 254 to 1.0 without changing image
    /// pixels. An RMO's alpha is the emissive mask and a packed normal's is the X component, so neither may
    /// decide coverage.</para></summary>
    private static Material BuildPreviewMaterial(ModelRoot model, string owner, string? baseColorPng,
        string? normalPng, string? rmoPng = null, string name = "gf2_preview", PreviewImageSet? imageCache = null)
    {
        var material = model.CreateMaterial(name);
        material.WithPBRMetallicRoughness();
        material.DoubleSided = true;
        var mr = material.FindChannel("MetallicRoughness");
        // Exported PNGs are top-down and the glb's UVs are glTF-convention, so a base map embeds with its
        // rows as they are.
        if (Embeddable(baseColorPng)
            && UseImage(model, baseColorPng!, MapKind.BaseColor, owner, imageCache)
                is ({ } baseImage, var alpha))
        {
            material.FindChannel("BaseColor")?.SetTexture(0, baseImage);
            if (alpha.MidCore5Fraction >= BlendMidAlphaCoreFraction)
            {
                material.Alpha = AlphaMode.BLEND;
            }
            else if (alpha.FractionBelowHalf >= CutoutFraction)
            {
                material.Alpha = AlphaMode.MASK;
                material.AlphaCutoff = PreviewMaps.GltfAlphaCutoff;
            }
        }
        // The `_n` is a PACKED GFL2 normal (X in alpha, Y in green, R filler, B mirrors G) — NOT the RGB
        // tangent normal glTF expects. Unpacked for the preview only; the exported `_n` PNG is untouched.
        if (Embeddable(normalPng)
            && UseImage(model, normalPng!, MapKind.Normal, owner, imageCache).Image is { } normalImage)
            material.FindChannel("Normal")?.SetTexture(0, normalImage);
        // A map that would not decode leaves this material with no RMO, which is the same material an absent
        // one gives: the matte stand-in factors, not a metallic-roughness channel bound to nothing.
        var orm = Embeddable(rmoPng) ? UseImage(model, rmoPng!, MapKind.Rmo, owner, imageCache).Image : null;
        if (orm is null)
        {
            // matte, non-metallic — a flat stand-in for the toon shader (default metallic=1 reads as shiny)
            mr?.SetFactor("MetallicFactor", 0f);
            mr?.SetFactor("RoughnessFactor", 1f);
        }
        else
        {
            mr?.SetTexture(0, orm);
            material.FindChannel("Occlusion")?.SetTexture(0, orm);
        }
        return material;
    }

    /// <summary>The lowest alpha admitted to the BLEND measurement. Values 1..15 are near-cut endpoint
    /// noise, not evidence of a graded area.</summary>
    internal const byte BlendMidAlphaMin = 16;

    /// <summary>The highest alpha admitted to the BLEND measurement. The 240..254 near-ceiling band is
    /// deliberately not coverage: this prevents opaque endpoint noise from selecting BLEND. Consequently a
    /// veil living entirely in 240..254 classifies OPAQUE, the provisional classifier's recorded blind spot.</summary>
    internal const byte BlendMidAlphaMax = 239;

    /// <summary>How much of a base colour's full area must survive the 5x5 mid-alpha erosion before the
    /// material declares BLEND. Kept beside <see cref="CutoutFraction"/> and the band bounds as the single
    /// authority for the preview material's non-opaque class boundaries.</summary>
    private const double BlendMidAlphaCoreFraction = 0.001;

    /// <summary>How much of a base colour has to fall under <see cref="PreviewMaps.GltfAlphaCutoff"/> before
    /// the material declares MASK. Every sampled game texture whose only sub-opaque pixels are BC-compression
    /// quantization measures exactly 0.0% — the noise class sits at zero, so ANY positive threshold separates
    /// it, and the size of the margin is decided by the failure asymmetry rather than by the separation. A
    /// missed genuine cutout renders a solid sheet, which is the defect class this rule exists to remove; a
    /// false MASK on a near-opaque map only clips pixels already drawn at under half opacity, which is
    /// visually negligible. So the margin is set low enough to catch a cutout that is SMALL against its
    /// sheet: a 200×200 half-cut lace region on a 2048×2048 atlas is 0.48% of the pixels — a shape, and one
    /// that a one-percent threshold read as noise.</summary>
    private const double CutoutFraction = 0.001;

    /// <summary>Whether a map path is one the export can embed. The single answer, so what the sidecar
    /// records for a submesh cannot differ from what its material got.</summary>
    private static bool Embeddable(string? png) => png is not null && File.Exists(png);

    /// <summary>The preview images one glb embeds: the share-by-source cache plus the record
    /// <see cref="PreviewMaps.WriteSidecar"/> writes beside the glb — the images by content, and the stock
    /// RMO each submesh was given. One object, so an export path cannot embed an image without recording
    /// it.</summary>
    private sealed class PreviewImageSet
    {
        private readonly Dictionary<(string, MapKind),
            (GltfImage Image, string Hash, PreviewMaps.AlphaCoverage Alpha)> _cache = new();
        private readonly List<PreviewMaps.Entry> _entries = new();
        private readonly List<PreviewMaps.SubmeshSource> _submeshes = new();
        private readonly List<PreviewMaps.SlotSource> _slots = new();
        private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlySet<string>? _authored;
        private readonly Action<string>? _onUnreadable;
        private readonly PreviewBlobMemo _previewMemo;

        /// <param name="authoredSources">The embedded PNG paths that are the modder's OWN authored maps. Their
        /// entries are recorded under <see cref="MapOrigin.Authored"/>, which classifies nothing on the way
        /// back: an image reproducing one of these IS authored work, and reading it as stock would drop it.
        /// The entry still earns its place in the record — it is what keeps a part whose every map is authored
        /// from writing no record at all, and the record is where the submesh RMO sources live.</param>
        /// <param name="onUnreadable">Handed the path of every map that would not decode, once. The caller is
        /// what knows where the file came from and can act on it — this codec knows only that the picture is
        /// not a picture.</param>
        public PreviewImageSet(IReadOnlySet<string>? authoredSources = null, Action<string>? onUnreadable = null,
            PreviewBlobMemo? previewMemo = null)
        {
            _authored = authoredSources;
            _onUnreadable = onUnreadable;
            _previewMemo = previewMemo ?? new PreviewBlobMemo();
        }

        private MapOrigin OriginOf(string pngPath) =>
            _authored?.Contains(pngPath) == true ? MapOrigin.Authored : MapOrigin.Vanilla;

        /// <summary>What Blender should list a picture as, by its path: the modder's own pictures under their
        /// project labels. A path with no label takes the stock naming (<see cref="PreviewMaps.ImageName"/>).
        /// The transport's carrier rows share the embedded image where the bytes match, so the name here is
        /// the one the modder sees on the tagged node too.</summary>
        public IReadOnlyDictionary<string, string>? Labels { get; init; }

        public IReadOnlyList<PreviewMaps.Entry> Entries => _entries;
        public IReadOnlyList<PreviewMaps.SubmeshSource> Submeshes => _submeshes;
        public IReadOnlyList<PreviewMaps.SlotSource> Slots => _slots;

        /// <summary>Note which STOCK image one primitive's material was built over in one slot, so a return
        /// can tell that primitive's own map from a sibling primitive's (see
        /// <see cref="PreviewMaps.SlotSource"/>). Records nothing — which reads as "no stock map belongs
        /// here" — for a slot with no map, a map that would not decode, or a map of the MODDER's own: an
        /// authored file is not one of the part's stock images, and a stock map arriving over it is an
        /// ask.</summary>
        public void UsedStock(string meshName, int submesh, MapKind kind, string? pngPath)
        {
            if (pngPath is null || OriginOf(pngPath) != MapOrigin.Vanilla) return;
            if (!_cache.TryGetValue((pngPath, kind), out var hit)) return;
            _slots.Add(new PreviewMaps.SlotSource(meshName, submesh, kind, hit.Hash));
        }

        /// <summary>Note which stock RMO a submesh's material was built over, so the intake reads the alpha
        /// off the map this export actually embedded there. A map that would not decode records nothing: the
        /// export did not embed it, and a recorded source no material carries would hand an authored RMO an
        /// alpha channel out of a file that cannot be read.</summary>
        public void UsedRmo(string meshName, int submesh, string rmoPng)
        {
            if (_unreadable.Contains(rmoPng)) return;
            _submeshes.Add(new PreviewMaps.SubmeshSource(meshName, submesh, rmoPng));
        }

        /// <summary>Embed (or reuse) one image and record it for <paramref name="owner"/>. A cache hit still
        /// records: the image embeds once, but each owning mesh that binds it needs its own entry, or a map
        /// two parts share reads as belonging to only the first. The alpha-class answers are cached with the
        /// image, so a map several submeshes bind is measured once.
        ///
        /// <para>A picture that will not decode gives a null image and nothing else: no embed, no record
        /// entry, and the material keeps its own identity carrying no texture on that slot. The failure costs
        /// exactly this map — a session must not die over one bad file, and this is the seam every export
        /// route embeds through, so all of them get the same answer. The path is handed to
        /// <c>onUnreadable</c> once per set, however many materials bind it.</para></summary>
        public (GltfImage? Image, PreviewMaps.AlphaCoverage Alpha) Use(
            ModelRoot model, string pngPath, MapKind kind, string owner)
        {
            if (_cache.TryGetValue((pngPath, kind), out var hit))
            {
                _entries.Add(new PreviewMaps.Entry(hit.Hash, pngPath, kind, OriginOf(pngPath), owner));
                return (hit.Image, hit.Alpha);
            }
            if (_unreadable.Contains(pngPath)) return (null, default);
            PreviewBlobMemo.Blob blob;
            try { blob = _previewMemo.Get(pngPath, kind); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                _unreadable.Add(pngPath);
                _onUnreadable?.Invoke(pngPath);
                return (null, default);
            }
            var image = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(blob.Bytes));
            image.Name = Labels?.GetValueOrDefault(pngPath) ?? PreviewMaps.ImageName(pngPath, kind);
            _entries.Add(new PreviewMaps.Entry(blob.Hash, pngPath, kind, OriginOf(pngPath), owner));
            _cache[(pngPath, kind)] = (image, blob.Hash, blob.Alpha);
            return (image, blob.Alpha);
        }
    }

    /// <summary>Build (or reuse, via <paramref name="cache"/>) the embedded preview <see cref="Image"/> for a
    /// PNG path, and return its alpha-class measurements. Caching
    /// by (path, kind) hands the SAME <c>Image</c> node to every material, so a shared texture embeds once.
    /// Without a cache the image is embedded unrecorded. Null image ⇒ the file would not decode; that slot
    /// carries no texture (see <see cref="PreviewImageSet.Use"/>).</summary>
    private static (GltfImage? Image, PreviewMaps.AlphaCoverage Alpha) UseImage(
        ModelRoot model, string pngPath, MapKind kind,
        string owner, PreviewImageSet? cache)
    {
        if (cache is not null) return cache.Use(model, pngPath, kind, owner);
        byte[] bytes;
        PreviewMaps.AlphaCoverage alpha;
        try { bytes = PreviewMaps.ToPreviewWithAlphaCoverage(pngPath, kind, out alpha); }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        { return (null, default); }
        var image = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(bytes));
        image.Name = PreviewMaps.ImageName(pngPath, kind);
        return (image, alpha);
    }

    /// <summary>Force each normal to unit length (glTF requirement); a near-zero normal falls back to
    /// +Z.</summary>
    private static IReadOnlyList<Vector3> Normalize(IReadOnlyList<Vector3> v)
    {
        var r = new Vector3[v.Count];
        for (int i = 0; i < v.Count; i++)
        {
            float len = v[i].Length();
            r[i] = len > 1e-6f ? v[i] / len : new Vector3(0, 0, 1);
        }
        return r;
    }

    /// <summary>How far from unit length a tangent's xyz may sit and still ship as it arrived. Tighter than
    /// the reader's own tolerance, so nothing a validator would reject slips through untouched.</summary>
    private const float TangentUnitTolerance = 1e-4f;

    /// <summary>Tangents glTF accepts: xyz of unit length, w exactly ±1. Some game meshes ship neither, and
    /// the writer rejects the whole file over one such vertex ("Invalid Tangent"), so the part cannot
    /// materialize at all. A tangent already meeting both is passed through as it came; one whose xyz will
    /// not normalize (zero, infinite or NaN) is rebuilt perpendicular to the vertex normal with w = +1, and
    /// any other keeps its normalized xyz with w snapped to the nearer of ±1. Blender recomputes tangents on
    /// import, so a rebuilt one costs the edit nothing.</summary>
    /// <param name="normals">the same unit normals the export writes, in glTF space; null where the mesh
    /// carries no normal channel, which leaves the rebuilt tangents on +X.</param>
    private static IReadOnlyList<Vector4> SanitizeTangents(IReadOnlyList<Vector4> tangents,
        IReadOnlyList<Vector3>? normals)
    {
        Vector4[]? sanitized = null;
        for (int i = 0; i < tangents.Count; i++)
        {
            var t = tangents[i];
            var xyz = new Vector3(t.X, t.Y, t.Z);
            float len = xyz.Length();                                   // NaN/∞ propagates into the length
            bool usableXyz = float.IsFinite(len) && len > 1e-6f;
            if (usableXyz && Math.Abs(len - 1f) <= TangentUnitTolerance && (t.W == 1f || t.W == -1f)) continue;
            (sanitized ??= tangents.ToArray())[i] = usableXyz
                ? new Vector4(xyz / len, float.IsFinite(t.W) && t.W < 0f ? -1f : 1f)
                : new Vector4(Perpendicular(normals is not null && i < normals.Count ? normals[i] : default), 1f);
        }
        return sanitized ?? tangents;
    }

    /// <summary>A unit vector perpendicular to <paramref name="n"/>. The cross is taken against whichever
    /// axis the normal leans on least, so it stays well conditioned; a normal that is itself unusable gives
    /// +X, which is perpendicular to nothing in particular but is at least a legal tangent.</summary>
    private static Vector3 Perpendicular(Vector3 n)
    {
        float len = n.Length();
        if (!float.IsFinite(len) || len <= 1e-6f) return new Vector3(1, 0, 0);
        n /= len;
        var t = Vector3.Cross(n, Math.Abs(n.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0));
        float tl = t.Length();
        return tl > 1e-6f ? t / tl : new Vector3(1, 0, 0);
    }

    private static IReadOnlyList<Vector3> Map3(UnityMesh m, string ch, System.Func<Vector3, Vector3>? f) =>
        f is null ? m.AsVector3(ch) : m.AsVector3(ch).Select(f).ToArray();

    private static IReadOnlyList<Vector4> Map4(UnityMesh m, string ch, System.Func<Vector4, Vector4>? f) =>
        f is null ? m.AsVector4(ch) : m.AsVector4(ch).Select(f).ToArray();

    /// <summary>The consecutive UV prefix with at least two components in glTF convention. A <see cref="UnityMesh"/>
    /// always holds Unity-convention UVs; the V flip lives here alone, so every writer takes every
    /// TEXCOORD_n from here. Gaps and wider game channels stop the prefix rather than renumbering later
    /// channels. Wider channels are intentionally read at their own stride and transported as XY.</summary>
    private static IReadOnlyList<IReadOnlyList<Vector2>> TransportUvs(UnityMesh m)
    {
        var sets = new List<IReadOnlyList<Vector2>>();
        for (int i = 0; i < TransportedTexCoordCount(m); i++)
        {
            string channel = $"TexCoord{i}";
            sets.Add(m.AsVector2(channel).Select(AxisConvention.TexCoord).ToArray());
        }
        return sets;
    }

    /// <summary>
    /// Reads a (Blender-edited) <c>.glb</c> back into a <see cref="UnityMesh"/> in Unity space — the exact
    /// inverse of <see cref="ExportGlb"/>. Geometry channels only (Vertex/Normal/Tangent/TexCoord0..7); the
    /// skin is read by <see cref="ImportPayload"/>. Primitives sharing one vertex pool are read once with
    /// absolute indices; primitives Blender split per material into separate pools are concatenated with
    /// running index offsets. <paramref name="meshName"/> selects one mesh out of a multi-mesh glb and
    /// throws if absent; null reads the first mesh. <paramref name="lenient"/> skips schema validation for a
    /// file Blender wrote (see <see cref="LoadModel"/>).
    /// </summary>
    public static UnityMesh ImportGlb(string path, string? meshName = null, bool lenient = false) =>
        ImportCore(LoadModel(path, lenient), meshName, readSkin: false).Mesh;

    /// <summary>The mesh names a glb carries — what tells a part that came back from a part that was only
    /// context, a distinction the caller cannot make from its own target list. Validation is skipped:
    /// naming the meshes must not depend on the geometry accessors passing a schema check.</summary>
    public static IReadOnlyList<string> MeshNames(string path)
    {
        var model = ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
        return model.LogicalMeshes.Select(m => m.Name ?? "").ToList();
    }

    /// <summary>Resolve each submesh's preview map slots (base colour, normal, RMO) out of a returned glb, in
    /// primitive order. Origins
    /// come from the sidecar written beside the glb at export (<see cref="PreviewMaps"/>); a submesh whose
    /// material is missing or slot-less reads <see cref="MapOrigin.None"/> and inherits at build. Validation
    /// is skipped: a schema complaint about geometry accessors must not cost the modder their texture
    /// work.
    ///
    /// <para>The RMO rides an ORM pair — one image on both the metallic-roughness and the occlusion channel
    /// — which Blender re-imports as two texture nodes over one image. The metallic-roughness channel is
    /// authoritative for the slot; occlusion is read only where metallic-roughness carries no image, so a
    /// material whose two nodes disagree resolves to the metallic-roughness one.</para>
    ///
    /// <para><paramref name="recordGlb"/> names the glb the record was written beside, for a send that
    /// arrives under a name of its own — a combined session lands as its own file while the sidecar sits with
    /// the app-published combined, and reading the record off the arriving name finds nothing, which
    /// classifies every returned map as authored. Null reads the record beside the glb being read.</para>
    /// </summary>
    public static IReadOnlyList<IncomingMaps> ReadSubmeshMaps(string path, string? meshName = null,
        string? recordGlb = null, Action<string>? report = null) =>
        ReadSubmeshMaps(ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip }),
            recordGlb ?? path, meshName, new PreviewMaps.StockPixels(), GltfTextureTransport.Read(path), report,
            reportUnkeyed: true);

    /// <inheritdoc cref="ReadSubmeshMaps(string, string?, string?)"/>
    public static IReadOnlyList<IncomingMaps> ReadSubmeshMaps(ParsedGlb glb, string? meshName = null,
        string? recordGlb = null, Action<string>? report = null, bool reportUnkeyed = true) =>
        ReadSubmeshMaps(glb.Model, recordGlb ?? glb.Path, meshName, glb.Stock,
            glb.Transport, report, reportUnkeyed);

    /// <summary>The read over an ALREADY-loaded model, for a caller that has one. The IMAGES come from the
    /// model; the RECORD they are classified against is the sidecar beside <paramref name="recordGlb"/>, which
    /// is the same file only when the glb arrived under the name it was published as.
    /// <paramref name="stock"/> spans however many parts the caller reads: the parts of one session share
    /// their recorded stock maps, so a slot the hash cannot settle is decoded against candidates the part
    /// before it may already have measured.</summary>
    private static IReadOnlyList<IncomingMaps> ReadSubmeshMaps(ModelRoot model, string recordGlb, string? meshName,
        PreviewMaps.StockPixels stock, TextureTransportRead? transport = null, Action<string>? report = null,
        bool reportUnkeyed = true)
    {
        var glMesh = meshName is null
            ? model.LogicalMeshes.FirstOrDefault()
            : model.LogicalMeshes.FirstOrDefault(m => m.Name == meshName);
        if (glMesh is null) return Array.Empty<IncomingMaps>();

        var sidecar = PreviewMaps.ReadSidecar(recordGlb);
        var neutrals = sidecar.Values.Where(entry => entry.Origin == MapOrigin.Neutral).ToList();
        var owner = glMesh.Name ?? "";
        // What each of this part's slots was exported over, so a stock map coming back on a slot it never sat
        // on reads as the deliberate link it is — inside one part as much as across two. Null where the record
        // cannot say, which leaves every slot on the whole-record answer (see PreviewMaps.ReadSlotStock).
        var slotStock = PreviewMaps.ReadSlotStock(recordGlb, owner);
        IReadOnlyList<PreviewMaps.TransportBinding> sessionOutbound = PreviewMaps.ReadTransportBindings(recordGlb);
        // A hash-only row is Blender saying "the picture you sent, untouched" — it can only be honored
        // against the record that stamped it. When the sidecar record is gone, the outbound glb's own
        // carrier still names each row's identity (with no source path, so hash-only rows below still
        // refuse rather than resolve); byte-carrying parts of the same return keep their classification.
        // Scoped to THIS part: a sibling part's hash-only rows must not switch a byte-carrying part off
        // its recorded read.
        TextureTransportImage? hashOnly = transport?.Bindings
            .Where(binding => binding.Png is null && !string.IsNullOrWhiteSpace(binding.OutboundHash)
                && string.Equals(binding.Mesh, owner, StringComparison.Ordinal))
            .Select(binding => (TextureTransportImage?)binding).FirstOrDefault();
        if (sessionOutbound.Count == 0 && hashOnly is not null)
        {
            try
            {
                sessionOutbound = GltfTextureTransport.Read(recordGlb).Bindings.Select(binding =>
                    new PreviewMaps.TransportBinding(binding.Mesh, binding.MaterialIndex, binding.PrimitiveIndex,
                        binding.ShaderProperty, binding.Kind, "", binding.OutboundHash, binding.Stock,
                        binding.Srgb, binding.Origin, binding.Parameters, binding.TexCoord)).ToList();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            { /* refused below: with no record at all, an "unchanged" marker cannot be honored */ }
        }
        if (sessionOutbound.Count == 0 && hashOnly is { } unanswerable)
            throw new AuthoredRefusalException(
                $"The {Textures.TextureMap.PropertyLabel(unanswerable.ShaderProperty)} on {owner} came "
                + "back marked unchanged, but it isn't the picture this session sent. Open the part "
                + "again from the Lab and send it once more");
        // The part's rows, folded onto the primitives that CAME BACK: a replacement with more submeshes than
        // the record was projected over draws its extra ranges at the last drawable material, and its
        // pictures for them join that material's rows here exactly as the edit's cards slot them.
        var outbound = MaterialFold.FoldOntoPrimitives(sessionOutbound
            .Where(binding => string.Equals(binding.Mesh, owner, StringComparison.Ordinal)).ToList(),
            glMesh.Primitives.Count);
        var returned = new Dictionary<(int Material, int? Primitive, string Property), TextureTransportImage>();
        if (transport is not null)
        {
            if (reportUnkeyed)
                foreach (string image in transport.UnkeyedImages)
                    report?.Invoke($"Ignored {image} from Blender: it isn't linked to a texture slot.");
            var outboundKeys = outbound.Select(Key).ToHashSet();
            foreach (var binding in transport.Bindings.Where(binding =>
                         string.Equals(binding.Mesh, owner, StringComparison.Ordinal)))
            {
                var key = (binding.MaterialIndex, binding.PrimitiveIndex, binding.ShaderProperty);
                if (!outboundKeys.Contains(key))
                {
                    // An "unchanged" marker for a slot this session never sent asks for nothing and names
                    // no picture; a byte row on such a slot is a picture the modder is owed a word about.
                    if (binding.Png is null) continue;
                    report?.Invoke($"Ignored {binding.ImageName} "
                        + $"({Textures.TextureMap.PropertyLabel(binding.ShaderProperty)}) from Blender: "
                        + "that texture slot wasn't in the file this session opened.");
                    continue;
                }
                if (!returned.TryAdd(key, binding))
                    report?.Invoke($"Ignored a duplicate {binding.ImageName} from Blender: its texture "
                        + "slot already has an image.");
            }
        }

        // The outbound record selects the protocol. A carrier session joins every returned tagged node to the
        // exact property it left on, so returned metadata cannot relabel a slot. The standard glTF channels
        // are then read as well, per primitive and kind, against the ONE outbound slot of that kind: a
        // material built by hand in Blender, or a replacement mesh that arrived with its own textures, has
        // no tagged node at all, and its picture on the base colour, normal or ORM channel is the modder's
        // work for that slot unless it reproduces what the session sent there (see ReadStandardChannels).
        // Legacy glbs have no outbound bindings and continue through the fixed-channel path below.
        bool keyed = sessionOutbound.Count > 0;
        var resolvedByPrimitive = new Dictionary<int, List<IncomingTexture>>();
        if (keyed)
        {
            foreach (var binding in outbound)
            {
                var key = Key(binding);
                returned.TryGetValue(key, out var image);
                ResolvedMap resolved;
                if (image.Png is null && image.OutboundHash is { Length: > 0 } returnedHash)
                {
                    // The marker is honored only against the exact identity this session stamped; on any
                    // mismatch the send refuses whole rather than guess which picture "unchanged" meant.
                    if (!string.Equals(returnedHash, binding.OutboundHash, StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(binding.Source))
                        throw new AuthoredRefusalException(
                            $"The {Textures.TextureMap.PropertyLabel(binding.ShaderProperty)} on {owner} came "
                            + "back marked unchanged, but it isn't the picture this session sent. Open the part "
                            + "again from the Lab and send it once more");
                    resolved = new ResolvedMap(MapOrigin.Vanilla, StockPng: binding.Source);
                }
                else resolved = PreviewMaps.ResolveTransport(image.Png, binding, neutrals);
                if (binding.PrimitiveIndex is not { } primitive)
                {
                    if (resolved.Origin == MapOrigin.Authored)
                        report?.Invoke(
                            $"Ignored {Textures.TextureMap.PropertyLabel(binding.ShaderProperty)} from "
                            + $"Blender: material {binding.MaterialIndex + 1} is not on any submesh.");
                    continue;
                }
                if (!resolvedByPrimitive.TryGetValue(primitive, out var list))
                    resolvedByPrimitive[primitive] = list = new List<IncomingTexture>();
                list.Add(new IncomingTexture(binding.MaterialIndex, binding.PrimitiveIndex,
                    binding.ShaderProperty, binding.Kind, resolved, image.ImageName));
            }
            for (int p = 0; p < glMesh.Primitives.Count; p++)
                ReadStandardChannels(p, glMesh.Primitives[p].Material);
        }

        var maps = new List<IncomingMaps>(glMesh.Primitives.Count);
        for (int p = 0; p < glMesh.Primitives.Count; p++)
        {
            var prim = glMesh.Primitives[p];
            resolvedByPrimitive.TryGetValue(p, out var exact);
            maps.Add(keyed
                ? new IncomingMaps(Exact(MapKind.BaseColor), Exact(MapKind.Normal), Exact(MapKind.Rmo),
                    prim.Material?.Name ?? "", exact, ExactName(MapKind.BaseColor), ExactName(MapKind.Normal),
                    ExactName(MapKind.Rmo),
                    RmoStockSource: outbound.FirstOrDefault(binding => binding.PrimitiveIndex == p
                        && binding.Kind == MapKind.Rmo).Source)
                : new IncomingMaps(
                    PreviewMaps.Resolve(ChannelImage(prim.Material, "BaseColor"), MapKind.BaseColor, sidecar, owner,
                        stock, Expected(p, MapKind.BaseColor)),
                    PreviewMaps.Resolve(ChannelImage(prim.Material, "Normal"), MapKind.Normal, sidecar, owner,
                        stock, Expected(p, MapKind.Normal)),
                    PreviewMaps.Resolve(OrmImage(prim.Material), MapKind.Rmo, sidecar, owner, stock,
                        Expected(p, MapKind.Rmo)),
                    prim.Material?.Name ?? "", BaseColorName: ChannelImageName(prim.Material, "BaseColor"),
                    NormalName: ChannelImageName(prim.Material, "Normal"), RmoName: OrmImageName(prim.Material)));

            ResolvedMap Exact(MapKind kind) => exact?.FirstOrDefault(item => item.Kind == kind).Map ?? default;
            string? ExactName(MapKind kind) => exact?.FirstOrDefault(item => item.Kind == kind).ImageName;
        }
        return maps;

        static (int Material, int? Primitive, string Property) Key(PreviewMaps.TransportBinding binding) =>
            (binding.MaterialIndex, binding.PrimitiveIndex, binding.ShaderProperty);

        PreviewMaps.SlotStock? Expected(int primitive, MapKind kind) =>
            slotStock is null
                ? null
                : new PreviewMaps.SlotStock(slotStock.GetValueOrDefault((primitive, kind)));

        // A keyed session's second read: the picture on each standard glTF channel of one returned
        // primitive, joined to the outbound slot of the same kind. Exactly one such slot is the join; none
        // or several is a named ignore, since the picture cannot be placed without inventing a property.
        // The tagged node keeps its answer when it came back edited: a different picture on the standard
        // channel beside it is then reported, not silently dropped. Otherwise the channel's picture is
        // classified against the slot's own outbound bytes and its answer stands in for the tagged node's
        // untouched-or-absent one: a painted map, a link to another slot's picture or the neutral normal
        // is the ask it is, and the slot's own stock picture reads as untouched rather than as no image at
        // all (a missing normal beside an edited base builds flat; an untouched one keeps the game's).
        // Nothing is said about a channel still showing what the session sent.
        void ReadStandardChannels(int primitive, Material? material)
        {
            var channels = new (MapKind Kind, byte[]? Image, string? Name, string Label)[]
            {
                (MapKind.BaseColor, ChannelImage(material, "BaseColor"), ChannelImageName(material, "BaseColor"),
                    Textures.TextureMap.BaseColorLabel),
                (MapKind.Normal, ChannelImage(material, "Normal"), ChannelImageName(material, "Normal"),
                    Textures.TextureMap.NormalLabel),
                (MapKind.Rmo, OrmImage(material), OrmImageName(material), Textures.TextureMap.RmoLabel),
            };
            string materialName = string.IsNullOrEmpty(material?.Name) ? $"material {primitive + 1}" : $"'{material!.Name}'";
            foreach (var (kind, image, name, label) in channels)
            {
                if (image is null) continue;
                string imageName = string.IsNullOrEmpty(name) ? $"the {label.ToLowerInvariant()} image" : name;
                var slots = outbound.Where(b => b.PrimitiveIndex == primitive && b.Kind == kind).ToList();
                if (slots.Count == 0)
                {
                    report?.Invoke($"Ignored {imageName} from Blender: {materialName} has no {label} slot.");
                    continue;
                }
                if (slots.Count > 1)
                {
                    report?.Invoke($"Ignored {imageName} from Blender: {materialName} has {slots.Count} {label} "
                        + "slots and the image isn't linked to one of them.");
                    continue;
                }
                var binding = slots[0];
                if (!resolvedByPrimitive.TryGetValue(primitive, out var list)) continue;
                int index = list.FindIndex(item => item.MaterialIndex == binding.MaterialIndex
                    && string.Equals(item.ShaderProperty, binding.ShaderProperty, StringComparison.Ordinal));
                if (index < 0) continue;
                var tagged = list[index];
                // The base colour and normal images reach the standard channels as themselves, so a picture
                // there that differs from the tagged node's is a second picture. The ORM channel is
                // different in kind: the tagged RMO node feeds Principled through a Separate Color node,
                // and Blender's exporter composes a NEW metallic-roughness image from those links, so the
                // channel never matches what the session sent even when nothing was touched. With the
                // tagged node present, its answer is the RMO's whole answer.
                if (kind == MapKind.Rmo && returned.ContainsKey(Key(binding))) continue;
                if (tagged.Map.Origin == MapOrigin.Authored)
                {
                    if (!returned.TryGetValue(Key(binding), out var carried)
                        || carried.Png is not { } carriedPng || !carriedPng.AsSpan().SequenceEqual(image))
                        report?.Invoke($"Ignored {imageName} from Blender: {materialName} already has an edited "
                            + $"{Textures.TextureMap.PropertyLabel(binding.ShaderProperty)} image.");
                    continue;
                }
                var resolved = PreviewMaps.ResolveTransport(image, binding, neutrals);
                if (resolved.Origin == MapOrigin.None) continue;
                if (tagged.Map.Origin == MapOrigin.Neutral && resolved.Origin == MapOrigin.Vanilla) continue;
                list[index] = tagged with { Map = resolved, ImageName = name };
            }
        }
    }

    /// <summary>The map paths a re-split re-embeds per submesh, in primitive order — what makes the part open
    /// textured on its own. The AUTHORED file wins its slot where the send-back wrote one, so the part re-opens
    /// showing the modder's work rather than the game texture it covers. Otherwise only a slot that returned
    /// byte-identical to what the session embedded contributes its stock map: re-embedding the stock map an
    /// authored image replaced would make the next send read that work as untouched.</summary>
    private static List<(string?, string?, string?)> WorkspaceSubmeshMaps(IReadOnlyList<IncomingMaps> maps,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authored)
    {
        var perSubmesh = new List<(string?, string?, string?)>(maps.Count);
        for (int i = 0; i < maps.Count; i++)
        {
            var own = authored is not null && i < authored.Count ? authored[i] : default;
            perSubmesh.Add((own.Base ?? Stock(maps[i].BaseColor),
                            own.Normal ?? Stock(maps[i].Normal),
                            own.Rmo ?? Stock(maps[i].Rmo)));
        }
        return perSubmesh;

        static string? Stock(ResolvedMap m) => m.Origin == MapOrigin.Vanilla ? m.StockPng : null;
    }

    /// <summary>Every path in <paramref name="authored"/>, as the set the record write asks whether a source it
    /// embedded is the modder's own. Null where nothing is authored, which is what the plain stock export
    /// passes.</summary>
    internal static IReadOnlySet<string>? AuthoredSources(
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authored)
    {
        if (authored is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (b, n, r) in authored)
            foreach (var p in new[] { b, n, r })
                if (p is not null) set.Add(p);
        return set.Count > 0 ? set : null;
    }

    /// <summary>The ORM bytes for a material: the metallic-roughness image, or the occlusion one where that
    /// slot is empty.</summary>
    private static byte[]? OrmImage(Material? material) =>
        ChannelImage(material, "MetallicRoughness") ?? ChannelImage(material, "Occlusion");

    private static string? OrmImageName(Material? material) =>
        ChannelImageName(material, "MetallicRoughness") ?? ChannelImageName(material, "Occlusion");

    /// <summary>The encoded bytes behind one material channel's texture, or null when absent.</summary>
    private static byte[]? ChannelImage(Material? material, string channel)
    {
        var img = material?.FindChannel(channel)?.Texture?.PrimaryImage?.Content;
        return img is { IsValid: true } c ? c.Content.ToArray() : null;
    }

    private static string? ChannelImageName(Material? material, string channel) =>
        material?.FindChannel(channel)?.Texture?.PrimaryImage?.Name;

    /// <summary>
    /// Read a mesh AND its skin (JOINTS_0/WEIGHTS_0 + each joint's bone hash) into a
    /// <see cref="MeshApply.Payload"/> — the package-time compile input. Geometry is byte-identical to
    /// <see cref="ImportGlb"/> (shared core). Bone hashes are recovered from each joint node's
    /// <c>_&lt;hash8&gt;</c> suffix. The skin is axis-independent, so no conversion touches it; its arrays
    /// are null when the glb carries no skin.
    /// </summary>
    public static MeshApply.Payload ImportPayload(string path, string? meshName = null, bool lenient = false) =>
        ImportCorePayload(LoadModel(path, lenient), meshName);

    /// <inheritdoc cref="ImportPayload(string, string?, bool)"/>
    public static MeshApply.Payload ImportPayload(ParsedGlb glb, string? meshName = null) =>
        ImportCorePayload(glb.Model, meshName);

    private static MeshApply.Payload ImportCorePayload(ModelRoot model, string? meshName)
    {
        var (mesh, ji, jw, hashes, _) = ImportCore(model, meshName, readSkin: true);
        return new MeshApply.Payload { Mesh = mesh, JointIndices = ji, JointWeights = jw, SkinJointHashes = hashes };
    }

    /// <summary>
    /// A glb parsed ONCE, for a caller that reads several parts out of the same file (a combined send-back
    /// asks for the part list, each part's geometry and skin, its map slots, its re-split). Read-only — a
    /// re-split builds its own model — so one instance serves a whole receive; the parse is LENIENT, as
    /// every read of a Blender-written glb is. <see cref="Path"/> is kept because the map record is a
    /// sidecar FILE beside the glb: reads resolve origins against it unless the caller names another glb.
    /// </summary>
    public sealed class ParsedGlb
    {
        private readonly Lazy<TextureTransportRead> _transport;
        // The parse snapshot, held only until the transport read consumes it — the factory nulls the
        // field, so an instance that never asks for its transport is the only one still holding the
        // bytes, and only for its own (method-scoped) lifetime.
        private byte[]? _snapshot;

        private ParsedGlb(ModelRoot model, string path, byte[] snapshot)
        {
            Model = model;
            Path = path;
            _snapshot = snapshot;
            _transport = new Lazy<TextureTransportRead>(() =>
            {
                byte[] bytes = _snapshot!;
                _snapshot = null;
                return GltfTextureTransport.Read(bytes);
            });
        }

        internal ModelRoot Model { get; }

        /// <summary>The file the model was read from.</summary>
        public string Path { get; }

        /// <summary>Parse <paramref name="path"/>. Throws exactly as any other read of an unopenable glb
        /// does. Parses a byte SNAPSHOT rather than the file: a send file is Blender's to rewrite at any
        /// moment, and a parse that held it open for its own duration would fail the very Send that
        /// announces the next edit.</summary>
        public static ParsedGlb Open(string path)
        {
            byte[] snapshot = File.ReadAllBytes(path);
            return new ParsedGlb(ModelRoot.ReadGLB(new MemoryStream(snapshot),
                new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip }), path, snapshot);
        }

        /// <summary>The mesh names this glb carries — what tells a part that came back from a part that was
        /// only context.</summary>
        public IReadOnlyList<string> MeshNames => Model.LogicalMeshes.Select(m => m.Name ?? "").ToList();

        /// <summary>Images no exact transport binding owns, for one session-level diagnostic.</summary>
        public IReadOnlyList<string> UnkeyedTextureImages => Transport.UnkeyedImages;

        internal TextureTransportRead Transport => _transport.Value;

        /// <summary>What the map reads off this file have already measured about the recorded stock maps.
        /// One session's parts are classified against one set of stock maps, so the measurement is shared
        /// for as long as the parse is.</summary>
        internal PreviewMaps.StockPixels Stock { get; } = new();
    }

    /// <summary>
    /// Read an already-rigged glb back as a combinable part: geometry in Unity space carrying its authored
    /// skin as ordinary <c>BlendIndices</c>/<c>BlendWeight</c> channels, paired with the
    /// <see cref="MeshSkin"/> naming and posing those bones. Its own joints are the bone list, so every
    /// influence the modder painted rides through — including one on a bone the part's game mesh lacks.
    ///
    /// <para>That bone list is the FILE's, which since <see cref="ExtraBone"/> spans the whole subject: the
    /// zero-weighted tail comes back as bones like any other, at the worlds this file happened to bake. A
    /// caller placing this skin beside other parts' has to reduce it to what the geometry rides first
    /// (<see cref="MeshSkin.WeightedOnly"/>) — otherwise the tail claims the union's placement for bones
    /// those other parts pose.</para>
    ///
    /// <para>Bind poses are reconstructed as <c>inverse(Reflect(world))</c> from the joint nodes' rest
    /// worlds (<see cref="AxisConvention.Reflect"/> is self-inverse). Any rest bake or scene pose is already
    /// inside those worlds AND inside the geometry, so the result must be combined with no further
    /// uprighting.</para>
    ///
    /// <para>Returns null when nothing here can be placed in a union armature: no skin, a joint count that
    /// disagrees with the per-vertex data, an unrecoverable bone hash, or a bind pose that won't invert. The
    /// caller falls back to the game mesh and says so.</para>
    /// </summary>
    public static (UnityMesh Mesh, MeshSkin Skin)? ReadRiggedGlb(string path, string? meshName = null)
    {
        var model = LoadModel(path, lenient: true);
        var (mesh, ji, jw, hashes, skin) = ImportCore(model, meshName, readSkin: true);
        if (ji is null || jw is null || hashes is null || skin is null || hashes.Length == 0) return null;
        if (Array.IndexOf(hashes, 0u) >= 0) return null;              // an unnamed joint has no union identity
        if (skin.JointsCount != hashes.Length) return null;
        int n = mesh.VertexCount;
        if (n <= 0 || ji.Length != n * 4 || jw.Length != n * 4) return null;
        // An influence pointing outside the joint list can't be remapped into a union skin. Refusing here
        // degrades to the game copy with a named warning; letting it through throws out of the combined
        // write and takes the session's other parts down with it.
        foreach (var j in ji) if (j < 0 || j >= hashes.Length) return null;

        var binds = new Matrix4x4[hashes.Length];
        for (int i = 0; i < hashes.Length; i++)
            if (!Matrix4x4.Invert(AxisConvention.Reflect(skin.Joints[i].WorldMatrix), out binds[i]))
                return null;

        // The skin crosses over as plain vertex channels, 4 wide, exactly as a decoded game mesh carries it,
        // so the combined export reads an edited part and a stock one through the same path.
        var bi = new float[n * 4];
        for (int k = 0; k < ji.Length; k++) bi[k] = ji[k];
        mesh.Channels["BlendIndices"] = bi;
        mesh.Channels["BlendWeight"] = (float[])jw.Clone();
        mesh.Dims["BlendIndices"] = 4;
        mesh.Dims["BlendWeight"] = 4;
        return (mesh, new MeshSkin { BoneHashes = hashes, BindPoses = binds });
    }

    /// <summary>Lenient skips SharpGLTF's strict schema validation: a Blender export can emit morph-target
    /// accessors with no bufferView (shape keys) we never read, and Strict rejects the whole file.</summary>
    private static ModelRoot LoadModel(string path, bool lenient) => lenient
        ? ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip })
        : ModelRoot.Load(path);

    private static (UnityMesh Mesh, int[]? JointIndices, float[]? JointWeights, uint[]? SkinJointHashes, Skin? Skin)
        ImportCore(ModelRoot model, string? meshName, bool readSkin)
    {
        // A NAMED lookup that misses is an error, never a fallback: the combined send-back re-splits per
        // part by name, so reading the first mesh instead would write one part's geometry into another's
        // workspace glb.
        var glMesh = meshName is null
            ? model.LogicalMeshes.FirstOrDefault()
              ?? throw new InvalidOperationException(
                  "the glb carries no mesh data (armature or empty nodes only). Re-export from "
                  + "Blender with the mesh object itself selected and visible - selecting a parent "
                  + "empty exports only a transform node")
            : model.LogicalMeshes.FirstOrDefault(m => m.Name == meshName)
              ?? throw new InvalidOperationException(
                  $"mesh '{meshName}' not found in the glb (it has: " +
                  $"{string.Join(", ", model.LogicalMeshes.Select(m => $"'{m.Name}'"))}). " +
                  "Was the object renamed in Blender?");
        var prims = glMesh.Primitives.ToList();

        // Joints (in skin-joint order) carry their bone hash in the node name; read once for the mesh.
        var skin = readSkin ? FindSkin(model, glMesh) : null;
        uint[]? jointHashes = skin is null ? null : ReadJointHashes(skin);

        bool shared = prims.Select(p => p.GetVertexAccessor("POSITION").LogicalIndex).Distinct().Count() == 1;

        Dictionary<string, float[]> channels;
        var submeshes = new List<int[]>();
        int[]? jointIdx = null;
        float[]? jointW = null;

        if (shared)
        {
            channels = ReadPrimChannels(prims[0]);
            foreach (var p in prims)
                submeshes.Add(AxisConvention.ReverseWinding(p.GetIndices().Select(i => (int)i).ToArray()));
            // A skin can be bound at the model level while THIS primitive carries no per-vertex skin (a
            // multi-mesh glb where another mesh is skinned); ReadSkin would NRE on the missing accessor.
            if (skin is not null
                && prims[0].GetVertexAccessor("JOINTS_0") is not null
                && prims[0].GetVertexAccessor("WEIGHTS_0") is not null)
                (jointIdx, jointW) = ReadSkin(prims[0]);
        }
        else
        {
            var pools = prims.Select(ReadPrimChannels).ToList();
            // keep only channels present on every primitive, then concatenate the pools
            var common = pools[0].Keys.Where(k => pools.All(p => p.ContainsKey(k))).ToList();
            channels = common.ToDictionary(k => k, k => pools.SelectMany(p => p[k]).ToArray());
            var ji = new List<int>();
            var jw = new List<float>();
            bool haveSkin = skin is not null && prims.All(p => p.GetVertexAccessor("JOINTS_0") is not null
                                                            && p.GetVertexAccessor("WEIGHTS_0") is not null);
            int offset = 0;
            for (int i = 0; i < prims.Count; i++)
            {
                var idx = AxisConvention.ReverseWinding(prims[i].GetIndices().Select(x => (int)x).ToArray());
                int off = offset;
                submeshes.Add(idx.Select(x => x + off).ToArray());
                if (haveSkin) { var (pj, pw) = ReadSkin(prims[i]); ji.AddRange(pj); jw.AddRange(pw); }
                offset += pools[i]["Vertex"].Length / 3;
            }
            if (haveSkin) { jointIdx = ji.ToArray(); jointW = jw.ToArray(); }
        }

        // A skin with no per-vertex weights is unusable for remap; treat the payload as skinless.
        if (jointIdx is null || jointW is null) jointHashes = null;

        var dims = new Dictionary<string, int>
        {
            ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4,
        };
        for (int i = 0; i < MaxTexCoordSets; i++) dims[$"TexCoord{i}"] = 2;
        var mesh = new UnityMesh
        {
            Name = glMesh.Name ?? meshName ?? "",
            VertexCount = channels["Vertex"].Length / 3,
            Channels = channels,
            Dims = dims.Where(kv => channels.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
            Submeshes = submeshes,
        };
        return (mesh, jointIdx, jointW, jointHashes, skin);
    }

    // A joint node is named "<leaf>_<8 hex>" (BuildNodeTree's encoding); Blender may suffix ".001".
    private static readonly System.Text.RegularExpressions.Regex NameHash =
        new(@"_([0-9a-fA-F]{8})(?:\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The skin bound to <paramref name="glMesh"/> via a scene node, else the model's first skin;
    /// null if the glb is skinless.</summary>
    private static Skin? FindSkin(ModelRoot model, SharpGLTF.Schema2.Mesh glMesh)
    {
        foreach (var node in model.LogicalNodes)
            if (ReferenceEquals(node.Mesh, glMesh) && node.Skin is not null) return node.Skin;
        return model.LogicalSkins.FirstOrDefault();
    }

    /// <summary>Each skin joint's bone hash, recovered from its node name's <c>_&lt;hash8&gt;</c> suffix
    /// (0 where unrecoverable).</summary>
    private static uint[] ReadJointHashes(Skin skin)
    {
        var hashes = new uint[skin.JointsCount];
        for (int i = 0; i < skin.JointsCount; i++)
        {
            var m = NameHash.Match(skin.Joints[i].Name ?? "");
            hashes[i] = m.Success ? uint.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) : 0u;
        }
        return hashes;
    }

    /// <summary>One primitive's per-vertex skin (JOINTS_0 → <c>int[v*4]</c>, WEIGHTS_0 → <c>float[v*4]</c>).
    /// Axis-independent, so no coordinate conversion applies.</summary>
    private static (int[] jointIdx, float[] weights) ReadSkin(MeshPrimitive prim)
    {
        var j = prim.GetVertexAccessor("JOINTS_0")!.AsVector4Array();
        var w = prim.GetVertexAccessor("WEIGHTS_0")!.AsVector4Array();
        var ji = new int[j.Count * 4];
        var jw = new float[w.Count * 4];
        for (int i = 0; i < j.Count; i++)
        {
            ji[i * 4 + 0] = (int)j[i].X; ji[i * 4 + 1] = (int)j[i].Y; ji[i * 4 + 2] = (int)j[i].Z; ji[i * 4 + 3] = (int)j[i].W;
            jw[i * 4 + 0] = w[i].X; jw[i * 4 + 1] = w[i].Y; jw[i * 4 + 2] = w[i].Z; jw[i * 4 + 3] = w[i].W;
        }
        return (ji, jw);
    }

    /// <summary>
    /// Does a node that instances <paramref name="meshName"/> carry a non-identity WORLD transform — or,
    /// with no name, does ANY mesh in the file? <see cref="ImportGlb"/> reads POSITION straight from the
    /// accessor and ignores node transforms, so an Object-mode move would silently vanish on send-back.
    /// The send path uses this to WARN; it does not apply the transform. Tolerant of the ~1e-6 noise glTF
    /// round-trips leave in an "identity" matrix.
    /// </summary>
    public static bool HasNonIdentityNodeTransform(string path, string? meshName = null) =>
        meshName is null
            ? MeshesWithNodeTransform(path).Count > 0
            : MeshesWithNodeTransform(path).Contains(meshName, StringComparer.Ordinal);

    /// <summary>The names of the meshes in <paramref name="path"/> whose instancing node carries a
    /// transform the geometry read ignores. A whole-file answer, so a send carrying several parts can name
    /// the ones actually affected rather than reporting on the first mesh it happens to hold.</summary>
    public static IReadOnlyList<string> MeshesWithNodeTransform(string path)
    {
        var model = ModelRoot.Load(path);
        var moved = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in model.LogicalNodes)
            if (node.Mesh is { } mesh && !IsNearIdentity(node.WorldMatrix) && seen.Add(mesh.Name ?? ""))
                moved.Add(mesh.Name ?? "");
        return moved;
    }

    private static bool IsNearIdentity(Matrix4x4 m)
    {
        const float eps = 1e-4f;
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) < eps && MathF.Abs(d.M12) < eps && MathF.Abs(d.M13) < eps && MathF.Abs(d.M14) < eps
            && MathF.Abs(d.M21) < eps && MathF.Abs(d.M22) < eps && MathF.Abs(d.M23) < eps && MathF.Abs(d.M24) < eps
            && MathF.Abs(d.M31) < eps && MathF.Abs(d.M32) < eps && MathF.Abs(d.M33) < eps && MathF.Abs(d.M34) < eps
            && MathF.Abs(d.M41) < eps && MathF.Abs(d.M42) < eps && MathF.Abs(d.M43) < eps && MathF.Abs(d.M44) < eps;
    }

    /// <summary>Read one primitive's geometry channels into flattened Unity-space arrays: axis convention
    /// undone on the directional channels, V flip undone on every UV set. Every import route lands here, so this is
    /// the only place a glb's UVs cross back into Unity convention.</summary>
    private static Dictionary<string, float[]> ReadPrimChannels(MeshPrimitive prim)
    {
        var ch = new Dictionary<string, float[]>();

        var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
        if (pos is not null) ch["Vertex"] = Flatten3(pos.Select(AxisConvention.Position));

        var nrm = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
        if (nrm is not null) ch["Normal"] = Flatten3(nrm.Select(AxisConvention.Normal));

        var tan = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
        if (tan is not null) ch["Tangent"] = Flatten4(tan.Select(AxisConvention.Tangent));

        for (int i = 0; i < MaxTexCoordSets; i++)
        {
            var uv = prim.GetVertexAccessor($"TEXCOORD_{i}")?.AsVector2Array();
            if (uv is not null) ch[$"TexCoord{i}"] = Flatten2(uv.Select(AxisConvention.TexCoord));
        }

        return ch;
    }

    private static float[] Flatten2(IEnumerable<Vector2> v)
    {
        var list = v as IReadOnlyList<Vector2> ?? v.ToList();
        var a = new float[list.Count * 2];
        for (int i = 0; i < list.Count; i++) { a[i * 2] = list[i].X; a[i * 2 + 1] = list[i].Y; }
        return a;
    }

    private static float[] Flatten3(IEnumerable<Vector3> v)
    {
        var list = v as IReadOnlyList<Vector3> ?? v.ToList();
        var a = new float[list.Count * 3];
        for (int i = 0; i < list.Count; i++) { a[i * 3] = list[i].X; a[i * 3 + 1] = list[i].Y; a[i * 3 + 2] = list[i].Z; }
        return a;
    }

    private static float[] Flatten4(IEnumerable<Vector4> v)
    {
        var list = v as IReadOnlyList<Vector4> ?? v.ToList();
        var a = new float[list.Count * 4];
        for (int i = 0; i < list.Count; i++)
        {
            a[i * 4] = list[i].X; a[i * 4 + 1] = list[i].Y; a[i * 4 + 2] = list[i].Z; a[i * 4 + 3] = list[i].W;
        }
        return a;
    }
}
