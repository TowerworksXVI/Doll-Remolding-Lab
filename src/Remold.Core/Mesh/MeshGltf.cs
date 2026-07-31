using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using GltfImage = SharpGLTF.Schema2.Image;   // disambiguate from SixLabors.ImageSharp.Image

namespace Remold.Core.Mesh;

/// <summary>
/// Reads/writes a decoded <see cref="UnityMesh"/> as a Blender-facing <c>.glb</c>: geometry
/// (POSITION/NORMAL/TANGENT/TEXCOORD_0 + per-submesh indices) plus the named, posed armature and
/// JOINTS/WEIGHTS. Every glb here is glTF right-handed space, converted from Unity's left-handed space
/// via <see cref="AxisConvention"/>; the flip is applied on export and un-applied on import, never crossed.
/// The outline channel (Unity vertex COLOR) is computed, not transported — it is re-baked at package time
/// by <see cref="MeshApply.BakeOutline"/>, so this codec neither writes nor reads it.
/// </summary>
public static class MeshGltf
{
    /// <summary>Write a Blender-facing glb (axis-converted). Given base-color/normal PNGs, a preview PBR
    /// material referencing them is embedded — round-trip safe, since <see cref="ImportGlb"/> ignores
    /// materials. <paramref name="uprighting"/> bakes a prefab body's scene-rest rotation into the geometry
    /// (see <see cref="RestBake"/>) — recorded on the project target and undone at package build.</summary>
    /// <param name="authoredSources">The subset of the embedded PNG paths that are the MODDER's own authored
    /// maps rather than stock ones, so the record beside the glb tells the two apart. See
    /// <see cref="PreviewImageSet"/>.</param>
    /// <inheritdoc cref="ExportGlb(UnityMesh, string, string?, string?, IReadOnlyList{ValueTuple{string?, string?, string?}}?, Matrix4x4?)"/>
    public static void ExportGlb(UnityMesh mesh, string outPath, string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null, Matrix4x4? uprighting = null,
        IReadOnlySet<string>? authoredSources = null) =>
        Write(uprighting is { } g ? RestBake.Apply(mesh, g) : mesh, outPath, baseColorPng, normalPng, perSubmesh,
            authoredSources);

    private static void Write(UnityMesh mesh, string outPath,
        string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null,
        IReadOnlySet<string>? authoredSources = null)
    {
        var model = ModelRoot.CreateModel();
        var glMesh = model.CreateMesh(mesh.Name);

        // A preview PBR material so the part shows textured in Blender — an approximation, not the in-game
        // toon/uber shader. A per-submesh assignment overrides the single material where given. One image
        // cache spans every material, so a shared texture embeds ONCE.
        var imgCache = new PreviewImageSet(authoredSources);
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
        var uv0 = Uv0(mesh);

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
                if (uv0 is not null) prim.WithVertexAccessor("TEXCOORD_0", uv0);
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
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes);
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
    public static void ExportRiggedGlb(UnityMesh mesh, MeshSkin skin, Func<uint, string?> resolveBone,
        string outPath, string? baseColorPng = null, string? normalPng = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh = null,
        IReadOnlyList<string>? scenePaths = null, Matrix4x4? uprighting = null,
        IReadOnlyDictionary<string, Matrix4x4>? connectorRests = null)
    {
        // Uprighting is the ONLY placement this export applies, and it moves geometry and joints together.
        // A part whose prefab mounts it by an unbakeable offset gets no placement at all: its bytes have to
        // stay raw bind space for the compile to round-trip, so mesh and armature sit together at the part's
        // own origin rather than the joints alone standing at the mount.
        if (uprighting is { } g) mesh = RestBake.Apply(mesh, g);
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        var (jointNodes, armature) = BuildArmature(scene, skin, resolveBone, scenePaths,
            uprighting, connectorRests);
        var glSkin = BindSkin(model, mesh.Name + "_skin", jointNodes, armature);

        var (joints, weights) = SkinAttributes(mesh);
        var imgCache = new PreviewImageSet();
        Material? material = baseColorPng is not null || normalPng is not null
            ? BuildPreviewMaterial(model, mesh.Name, baseColorPng, normalPng, imageCache: imgCache)
            : null;
        var submeshMats = BuildSubmeshMaterials(model, mesh.Name, mesh.Submeshes.Count, perSubmesh, imgCache);
        AddSkinnedMeshNode(model, scene, mesh, joints, weights, glSkin, material, submeshMats);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        model.SaveGLB(outPath);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes);
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
        IReadOnlyList<Matrix4x4>? ContextPose = null);

    /// <summary>
    /// Write several skinned parts into ONE glb sharing ONE armature = the <b>union</b> of all their bones.
    /// Each part keeps its own geometry + preview material and binds to the shared skin, its per-vertex
    /// JOINTS remapped from its own bone order into the union order. <c>ImportGlb(path, meshName)</c> reads
    /// any one part back by name, so the round-trip and package payload stay per-part.
    /// </summary>
    public static void ExportCombinedRiggedGlb(IReadOnlyList<RiggedPart> parts, Func<uint, string?> resolveBone, string outPath)
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");

        // 1. union of bones across every part (first part to use a bone fixes its pose)
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var partPaths = parts.Select(p => CollectBones(p.Skin, resolveBone, worldOf, hashOf, order, seen,
            p.ScenePaths, p.Uprighting, p.ConnectorRests, p.ContextPose)).ToList();
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);

        // 2. union skin: the actual bones in hierarchy order; combined joint index per bone path.
        // Keyed on hashOf, not worldOf — a connector prefix gains a world when the scene rig supplies its
        // rest (see CollectBones), but only hash-named bones may be skin joints: a hash-less joint in a
        // send-back would poison the per-part re-split and every later open of that part.
        var unionBones = order.Where(hashOf.ContainsKey).ToList();
        var combinedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < unionBones.Count; i++) combinedIndex[unionBones[i]] = i;
        var glSkin = BindSkin(model, "combined_skin", unionBones.Select(p => nodeOf[p]).ToList(), armature);

        // 3. each part: own mesh, JOINTS remapped local→combined, bound to the shared skin
        var imgCache = new PreviewImageSet();   // dedup an image shared across parts
        for (int pi = 0; pi < parts.Count; pi++)
        {
            var part = parts[pi];
            var l2c = partPaths[pi].Select(p => combinedIndex[p]).ToArray();
            var (joints, weights) = SkinAttributes(part.Mesh, l2c);
            Material? material = part.BaseColorPng is not null || part.NormalPng is not null
                ? BuildPreviewMaterial(model, part.Mesh.Name, part.BaseColorPng, part.NormalPng, imageCache: imgCache)
                : null;
            var submeshMats = BuildSubmeshMaterials(model, part.Mesh.Name, part.Mesh.Submeshes.Count,
                part.PerSubmesh, imgCache);
            var partMesh = part.Uprighting is { } g ? RestBake.Apply(part.Mesh, g)
                : part.ContextPose is { } pose ? PoseAtRest(part.Mesh, part.Skin, pose)
                : part.Mesh;
            AddSkinnedMeshNode(model, scene, partMesh, joints, weights, glSkin, material, submeshMats);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        model.SaveGLB(outPath);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes);
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
    /// </summary>
    public static MeshApply.Payload ReexportPartGlb(string sourcePath, string? meshName, string outPath,
        Action<string>? beforeWrite = null, string? recordGlb = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authoredMaps = null) =>
        // Lenient: the source is a Blender send-back (see LoadModel).
        ReexportPartGlb(ParsedGlb.Open(sourcePath), meshName, outPath, beforeWrite, recordGlb, authoredMaps);

    /// <inheritdoc cref="ReexportPartGlb(string, string?, string, Action{string}?, string?, IReadOnlyList{ValueTuple{string?, string?, string?}}?)"/>
    public static MeshApply.Payload ReexportPartGlb(ParsedGlb source, string? meshName, string outPath,
        Action<string>? beforeWrite = null, string? recordGlb = null,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authoredMaps = null)
    {
        var model = source.Model;
        var record = recordGlb ?? source.Path;
        var payload = ImportCorePayload(model, meshName);
        var authoredSources = AuthoredSources(authoredMaps);
        if (!payload.HasSkin)
        {
            var maps = WorkspaceSubmeshMaps(ReadSubmeshMaps(model, record, meshName, source.Stock), authoredMaps);
            beforeWrite?.Invoke(outPath);
            ExportGlb(payload.Mesh, outPath, perSubmesh: maps, authoredSources: authoredSources);
            return payload;
        }

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
        var oldToNew = new int[jointNodes.Count];
        var keep = new List<int>();
        for (int i = 0; i < jointNodes.Count; i++)
        {
            if (srcHashes[i] != 0) { oldToNew[i] = keep.Count; keep.Add(i); }
            else oldToNew[i] = -1;
        }
        if (keep.Count == 0)
            throw new InvalidOperationException(
                $"none of \"{payload.Mesh.Name}\"'s skin joints carry a bone hash in their names - "
                + "the armature doesn't look like one this app exported, so the edit can't be read back");
        if (keep.Count < jointNodes.Count)
        {
            var offenders = new SortedSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < payload.JointIndices!.Length; k++)
                if (payload.JointWeights![k] != 0 && oldToNew[payload.JointIndices[k]] < 0)
                    offenders.Add(jointNodes[payload.JointIndices[k]].Name ?? "(unnamed)");
            if (offenders.Count > 0)
                throw new InvalidOperationException(
                    $"\"{payload.Mesh.Name}\" carries weights on {string.Join(", ", offenders)} - "
                    + "not game bones, so those weights can't ship. Move them onto the session "
                    + "armature's own bones (or delete those vertex groups) and send again.");
        }

        static string NodeKey(Node n) =>
            string.IsNullOrEmpty(n.Name) ? $"node{n.LogicalIndex}" : n.Name.Replace('/', '_');

        // Every joint's ancestor chain (below the wrapper) becomes a path; every node on it — connectors
        // too — records its ACTUAL world from the source, so nothing snaps back to the origin. Those worlds
        // are already glTF-space (no Reflect: they came from a glb, not from Unity bind poses).
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var paths = new string[keep.Count];
        for (int ki = 0; ki < keep.Count; ki++)
        {
            var chain = new List<Node>();
            for (var n = jointNodes[keep[ki]]; n is not null && !wrapper.Contains(n); n = n.VisualParent) chain.Add(n);
            chain.Reverse();
            string path = "";
            foreach (var n in chain)
            {
                path = path.Length == 0 ? NodeKey(n) : path + "/" + NodeKey(n);
                if (seen.Add(path)) order.Add(path);
                worldOf.TryAdd(path, n.WorldMatrix);
            }
            paths[ki] = path;
            hashOf.TryAdd(path, srcHashes[keep[ki]]);
        }

        var outModel = ModelRoot.CreateModel();
        var scene = outModel.UseScene("scene");
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);
        var glSkin = BindSkin(outModel, payload.Mesh.Name + "_skin", paths.Select(p => nodeOf[p]).ToList(), armature);

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
        // The part's own preview materials + map sidecar, rebuilt over the maps it came back on, so a re-open
        // ALONE opens textured and its next send-back classifies those maps against the same record the
        // session's own read used.
        var imgCache = new PreviewImageSet(authoredSources);
        var submeshMats = BuildSubmeshMaterials(outModel, payload.Mesh.Name, payload.Mesh.Submeshes.Count,
            WorkspaceSubmeshMaps(ReadSubmeshMaps(model, record, meshName, source.Stock), authoredMaps), imgCache);
        AddSkinnedMeshNode(outModel, scene, payload.Mesh, joints, weights, glSkin, material: null, submeshMats);

        beforeWrite?.Invoke(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        outModel.SaveGLB(outPath);
        PreviewMaps.WriteSidecar(outPath, imgCache.Entries, imgCache.Submeshes);
        return payload;
    }

    /// <summary>Create a skin over the given joint nodes (in joint order), deriving each inverse-bind matrix
    /// from the node's exact rest world, and anchor it on the armature root.</summary>
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
        var uv0 = Uv0(mesh);

        MeshPrimitive? shared = null;
        for (int s = 0; s < mesh.Submeshes.Count; s++)
        {
            var prim = glMesh.CreatePrimitive();
            if (shared is null)
            {
                prim.WithVertexAccessor("POSITION", positions);
                if (normals is not null) prim.WithVertexAccessor("NORMAL", normals);
                if (tangents is not null) prim.WithVertexAccessor("TANGENT", tangents);
                if (uv0 is not null) prim.WithVertexAccessor("TEXCOORD_0", uv0);
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
    /// BlendIndices index straight into <c>skin.Joints</c> with no remap) plus the skeleton root. Each
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
        IReadOnlyDictionary<string, Matrix4x4>? connectorRests = null)
    {
        var (worldOf, hashOf, order) = NewBoneAccumulators();
        var paths = CollectBones(skin, resolveBone, worldOf, hashOf, order, new HashSet<string>(StringComparer.Ordinal),
            scenePaths, uprighting, connectorRests);
        var (nodeOf, armature) = BuildNodeTree(scene, worldOf, hashOf, order);
        return (paths.Select(p => nodeOf[p]).ToList(), armature);   // joints in this skin's bone order
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
    /// once.</para></summary>
    private static Material?[]? BuildSubmeshMaterials(ModelRoot model, string meshName, int submeshCount,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? perSubmesh, PreviewImageSet? imageCache)
    {
        if (perSubmesh is null) return null;
        var mats = new Material?[submeshCount];
        for (int s = 0; s < submeshCount; s++)
        {
            var key = s < perSubmesh.Count ? perSubmesh[s] : default;
            if (key.Base is null && key.Normal is null && key.Rmo is null) continue;   // no maps for this submesh
            mats[s] = BuildPreviewMaterial(model, meshName, key.Base, key.Normal, key.Rmo, $"gf2_submesh{s}", imageCache);
            if (Embeddable(key.Rmo)) imageCache?.UsedRmo(meshName, s, key.Rmo!);
        }
        return mats;
    }

    /// <summary>A metallic-roughness PBR material over the part's base color (<c>_d</c>/<c>_da</c>, sRGB),
    /// normal (<c>_n</c>) and RMO. Images are embedded into the glb — a snapshot of the current PNGs.
    ///
    /// <para>The RMO travels as a standard glTF ORM texture: ONE image on both the metallic-roughness and
    /// the occlusion channel, its channels permuted into glTF's order by <see cref="PreviewMaps"/>. glTF
    /// multiplies each factor by its texture channel, so a material carrying an RMO leaves the metallic and
    /// roughness factors at their 1.0 default — the matte stand-in factors would zero the map.</para></summary>
    private static Material BuildPreviewMaterial(ModelRoot model, string owner, string? baseColorPng,
        string? normalPng, string? rmoPng = null, string name = "gf2_preview", PreviewImageSet? imageCache = null)
    {
        var material = model.CreateMaterial(name);
        material.WithPBRMetallicRoughness();
        material.DoubleSided = true;
        var mr = material.FindChannel("MetallicRoughness");
        bool hasRmo = Embeddable(rmoPng);
        if (!hasRmo)
        {
            // matte, non-metallic — a flat stand-in for the toon shader (default metallic=1 reads as shiny)
            mr?.SetFactor("MetallicFactor", 0f);
            mr?.SetFactor("RoughnessFactor", 1f);
        }
        // Exported PNGs are top-down and the glb's UVs are glTF-convention, so a base map embeds with its
        // rows as they are.
        if (Embeddable(baseColorPng))
            material.FindChannel("BaseColor")?.SetTexture(0, UseImage(model, baseColorPng!, MapKind.BaseColor, owner, imageCache));
        // The `_n` is a PACKED GFL2 normal (X in alpha, Y in green, R filler, B mirrors G) — NOT the RGB
        // tangent normal glTF expects. Unpacked for the preview only; the exported `_n` PNG is untouched.
        if (Embeddable(normalPng))
            material.FindChannel("Normal")?.SetTexture(0, UseImage(model, normalPng!, MapKind.Normal, owner, imageCache));
        if (hasRmo)
        {
            var orm = UseImage(model, rmoPng!, MapKind.Rmo, owner, imageCache);
            mr?.SetTexture(0, orm);
            material.FindChannel("Occlusion")?.SetTexture(0, orm);
        }
        return material;
    }

    /// <summary>Whether a map path is one the export can embed. The single answer, so what the sidecar
    /// records for a submesh cannot differ from what its material got.</summary>
    private static bool Embeddable(string? png) => png is not null && File.Exists(png);

    /// <summary>The preview images one glb embeds: the share-by-source cache plus the record
    /// <see cref="PreviewMaps.WriteSidecar"/> writes beside the glb — the images by content, and the stock
    /// RMO each submesh was given. One object, so an export path cannot embed an image without recording
    /// it.</summary>
    private sealed class PreviewImageSet
    {
        private readonly Dictionary<(string, MapKind), (GltfImage Image, string Hash)> _cache = new();
        private readonly List<PreviewMaps.Entry> _entries = new();
        private readonly List<PreviewMaps.SubmeshSource> _submeshes = new();
        private readonly IReadOnlySet<string>? _authored;

        /// <param name="authoredSources">The embedded PNG paths that are the modder's OWN authored maps. Their
        /// entries are recorded under <see cref="MapOrigin.Authored"/>, which classifies nothing on the way
        /// back: an image reproducing one of these IS authored work, and reading it as stock would drop it.
        /// The entry still earns its place in the record — it is what keeps a part whose every map is authored
        /// from writing no record at all, and the record is where the submesh RMO sources live.</param>
        public PreviewImageSet(IReadOnlySet<string>? authoredSources = null) => _authored = authoredSources;

        private MapOrigin OriginOf(string pngPath) =>
            _authored?.Contains(pngPath) == true ? MapOrigin.Authored : MapOrigin.Vanilla;

        public IReadOnlyList<PreviewMaps.Entry> Entries => _entries;
        public IReadOnlyList<PreviewMaps.SubmeshSource> Submeshes => _submeshes;

        /// <summary>Note which stock RMO a submesh's material was built over, so the intake reads the alpha
        /// off the map this export actually embedded there.</summary>
        public void UsedRmo(string meshName, int submesh, string rmoPng) =>
            _submeshes.Add(new PreviewMaps.SubmeshSource(meshName, submesh, rmoPng));

        /// <summary>Embed (or reuse) one image and record it for <paramref name="owner"/>. A cache hit still
        /// records: the image embeds once, but each owning mesh that binds it needs its own entry, or a map
        /// two parts share reads as belonging to only the first.</summary>
        public GltfImage Use(ModelRoot model, string pngPath, MapKind kind, string owner)
        {
            if (_cache.TryGetValue((pngPath, kind), out var hit))
            {
                _entries.Add(new PreviewMaps.Entry(hit.Hash, pngPath, kind, OriginOf(pngPath), owner));
                return hit.Image;
            }
            var bytes = PreviewMaps.ToPreview(pngPath, kind);
            var image = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(bytes));
            image.Name = PreviewMaps.ImageName(pngPath, kind);
            var hash = PreviewMaps.Hash(bytes);
            _entries.Add(new PreviewMaps.Entry(hash, pngPath, kind, OriginOf(pngPath), owner));
            _cache[(pngPath, kind)] = (image, hash);
            return image;
        }
    }

    /// <summary>Build (or reuse, via <paramref name="cache"/>) the embedded preview <see cref="Image"/> for a
    /// PNG path. Caching by (path, kind) hands the SAME <c>Image</c> node to every material, so a shared
    /// texture embeds once. Without a cache the image is embedded unrecorded.</summary>
    private static GltfImage UseImage(ModelRoot model, string pngPath, MapKind kind, string owner,
        PreviewImageSet? cache)
    {
        if (cache is not null) return cache.Use(model, pngPath, kind, owner);
        var image = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(PreviewMaps.ToPreview(pngPath, kind)));
        image.Name = PreviewMaps.ImageName(pngPath, kind);
        return image;
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

    /// <summary>UV0 in glTF convention, or null when the mesh carries none. A <see cref="UnityMesh"/> always
    /// holds Unity-convention UVs; the V flip lives here alone, so every writer takes TEXCOORD_0 from
    /// here.</summary>
    private static IReadOnlyList<Vector2>? Uv0(UnityMesh m) =>
        m.Has("TexCoord0") ? m.AsVector2("TexCoord0").Select(AxisConvention.TexCoord).ToArray() : null;

    /// <summary>
    /// Reads a (Blender-edited) <c>.glb</c> back into a <see cref="UnityMesh"/> in Unity space — the exact
    /// inverse of <see cref="ExportGlb"/>. Geometry channels only (Vertex/Normal/Tangent/TexCoord0); the
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
        string? recordGlb = null) =>
        ReadSubmeshMaps(ModelRoot.Load(path, new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip }),
            recordGlb ?? path, meshName, new PreviewMaps.StockPixels());

    /// <inheritdoc cref="ReadSubmeshMaps(string, string?, string?)"/>
    public static IReadOnlyList<IncomingMaps> ReadSubmeshMaps(ParsedGlb glb, string? meshName = null,
        string? recordGlb = null) =>
        ReadSubmeshMaps(glb.Model, recordGlb ?? glb.Path, meshName, glb.Stock);

    /// <summary>The read over an ALREADY-loaded model, for a caller that has one. The IMAGES come from the
    /// model; the RECORD they are classified against is the sidecar beside <paramref name="recordGlb"/>, which
    /// is the same file only when the glb arrived under the name it was published as.
    /// <paramref name="stock"/> spans however many parts the caller reads: the parts of one session share
    /// their recorded stock maps, so a slot the hash cannot settle is decoded against candidates the part
    /// before it may already have measured.</summary>
    private static IReadOnlyList<IncomingMaps> ReadSubmeshMaps(ModelRoot model, string recordGlb, string? meshName,
        PreviewMaps.StockPixels stock)
    {
        var glMesh = meshName is null
            ? model.LogicalMeshes.FirstOrDefault()
            : model.LogicalMeshes.FirstOrDefault(m => m.Name == meshName);
        if (glMesh is null) return Array.Empty<IncomingMaps>();

        var sidecar = PreviewMaps.ReadSidecar(recordGlb);
        var owner = glMesh.Name ?? "";
        var maps = new List<IncomingMaps>(glMesh.Primitives.Count);
        foreach (var prim in glMesh.Primitives)
            maps.Add(new IncomingMaps(
                PreviewMaps.Resolve(ChannelImage(prim.Material, "BaseColor"), MapKind.BaseColor, sidecar, owner, stock),
                PreviewMaps.Resolve(ChannelImage(prim.Material, "Normal"), MapKind.Normal, sidecar, owner, stock),
                PreviewMaps.Resolve(OrmImage(prim.Material), MapKind.Rmo, sidecar, owner, stock),
                prim.Material?.Name ?? ""));
        return maps;
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
    private static IReadOnlySet<string>? AuthoredSources(
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

    /// <summary>The encoded bytes behind one material channel's texture, or null when absent.</summary>
    private static byte[]? ChannelImage(Material? material, string channel)
    {
        var img = material?.FindChannel(channel)?.Texture?.PrimaryImage?.Content;
        return img is { IsValid: true } c ? c.Content.ToArray() : null;
    }

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
        private ParsedGlb(ModelRoot model, string path) { Model = model; Path = path; }

        internal ModelRoot Model { get; }

        /// <summary>The file the model was read from.</summary>
        public string Path { get; }

        /// <summary>Parse <paramref name="path"/>. Throws exactly as any other read of an unopenable glb
        /// does. Parses a byte SNAPSHOT rather than the file: a send file is Blender's to rewrite at any
        /// moment, and a parse that held it open for its own duration would fail the very Send that
        /// announces the next edit.</summary>
        public static ParsedGlb Open(string path) => new(ModelRoot.ReadGLB(
            new MemoryStream(File.ReadAllBytes(path)),
            new ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip }), path);

        /// <summary>The mesh names this glb carries — what tells a part that came back from a part that was
        /// only context.</summary>
        public IReadOnlyList<string> MeshNames => Model.LogicalMeshes.Select(m => m.Name ?? "").ToList();

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
                  $"mesh \"{meshName}\" not found in the glb (it has: " +
                  $"{string.Join(", ", model.LogicalMeshes.Select(m => $"\"{m.Name}\""))}). " +
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
            ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
        };
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
    /// Does a node that instances <paramref name="meshName"/> (or the first mesh) carry a non-identity WORLD
    /// transform? <see cref="ImportGlb"/> reads POSITION straight from the accessor and ignores node
    /// transforms, so an Object-mode move would silently vanish on send-back. The send path uses this to
    /// WARN; it does not apply the transform. Tolerant of the ~1e-6 noise glTF round-trips leave in an
    /// "identity" matrix.
    /// </summary>
    public static bool HasNonIdentityNodeTransform(string path, string? meshName = null)
    {
        var model = ModelRoot.Load(path);
        var mesh = meshName is null
            ? model.LogicalMeshes.FirstOrDefault()
            : model.LogicalMeshes.FirstOrDefault(m => m.Name == meshName);
        if (mesh is null) return false;
        foreach (var node in model.LogicalNodes)
            if (node.Mesh == mesh && !IsNearIdentity(node.WorldMatrix))
                return true;
        return false;
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
    /// undone on the directional channels, V flip undone on UV0. Every import route lands here, so this is
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

        var uv0 = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
        if (uv0 is not null) ch["TexCoord0"] = Flatten2(uv0.Select(AxisConvention.TexCoord));

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
