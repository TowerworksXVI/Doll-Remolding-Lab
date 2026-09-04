using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Remold.Core.Tests.Support;

/// <summary>
/// Builds a plain UnityFS v7 bundle from NOTHING — no game data, no <c>classdata.tpk</c> — so the REAL
/// <see cref="Bundles.BundleReader"/> path runs in the default suite. Every asset is solid-colour RGBA32
/// synthesised at test time, so nothing copyrighted is checked in or produced.
///
/// <para>The technique: register a hand-built type tree, <see cref="AssetFileInfo.Create"/> against it (the
/// type is already in the file's tree list, so a null ClassDatabase is accepted), populate a default value
/// field, and let <c>AssetsFile.Write</c> / <c>AssetBundleFile.Write</c> do the header bookkeeping.</para>
/// </summary>
internal static class SyntheticBundle
{
    public const int ClassTexture2D = 28;
    public const int ClassMesh = 43;
    public const int ClassAssetBundle = 142;
    // GFL2 = Unity 2019.4.29f1. Serialized-file format version for 2019.4 is 17; UnityFS bundle version 7.
    private const uint SerializedVersion = 17;
    private const string UnityVersion = "2019.4.29f1";

    /// <summary>One Texture2D: <c>m_Name</c>, dimensions, and the stored pixel bytes, row-major, in
    /// <paramref name="Format"/>'s layout — RGBA32 (<c>width*height*4</c>) unless another Unity texture
    /// format is named. <paramref name="ColorSpace"/> is Unity's <c>m_ColorSpace</c> (0 linear, 1 sRGB) — it
    /// picks the DXGI format, so it changes both the resource hash and the tag a replacement has to
    /// carry.</summary>
    /// <param name="Format">Unity's <c>m_TextureFormat</c>. 4 is RGBA32; 17 is RGBAHalf, the float layout a
    /// toon ramp is stored in, whose bytes travel raw rather than through any codec.</param>
    public readonly record struct TextureSpec(string Name, int Width, int Height, byte[] Pixels,
        int ColorSpace = 0, int Format = Rgba32);

    /// <summary>The serialized shading rows attached to one synthetic Material.</summary>
    public sealed record MaterialShadingSpec(int ShaderFileId, long ShaderPathId,
        IReadOnlyList<string> Keywords, IReadOnlyDictionary<string, float> Floats,
        IReadOnlyDictionary<string, float[]> Colors);

    /// <summary>Unity <c>m_TextureFormat</c> values these fixtures build.</summary>
    public const int Rgba32 = 4, RgbaHalf = 17;

    /// <summary>An fp16 image of <paramref name="width"/>×<paramref name="height"/>, four half-floats a
    /// texel, values walked from <paramref name="seed"/> so two of them never share bytes. The shape a toon
    /// ramp is stored in.</summary>
    public static byte[] RgbaHalfPixels(int width, int height, int seed)
    {
        var px = new byte[width * height * 8];
        for (int i = 0; i < width * height * 4; i++)
            BitConverter.TryWriteBytes(px.AsSpan(i * 2, 2), (Half)(((i + seed) % 41) / 40f));
        return px;
    }

    /// <summary>A solid-colour RGBA32 fill — enough pixels for a real decode.</summary>
    public static byte[] SolidRgba32(int width, int height, byte r, byte g, byte b, byte a)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = r; px[i + 1] = g; px[i + 2] = b; px[i + 3] = a; }
        return px;
    }

    /// <summary>The stock-texture hash of <paramref name="textureName"/> in a built bundle, exactly as
    /// the build pipeline computes it — the value emitted inis carry in tags and hash lines.</summary>
    public static string StockTexHash(byte[] bundleBytes, string textureName)
    {
        var stock = new Remold.Core.Bundles.BundleReader().GetTextureHashSource(bundleBytes, textureName)!.Value;
        return Remold.Core.Migoto.TextureHash.Compute(stock.PictureData, stock.Width, stock.Height, stock.MipCount,
            Remold.Core.Migoto.TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)stock.Format, stock.Srgb)!.Value)
            .ToString("x8");
    }

    /// <summary>Returns each texture's assigned path id, index-aligned with <paramref name="textures"/>.</summary>
    public static IReadOnlyList<long> Build(string path, params TextureSpec[] textures)
        => Build(path, bundleName: null, textures);

    /// <summary>With <paramref name="bundleName"/> set, also embeds the self-identification object every
    /// real bundle carries: an AssetBundle (class 142) whose <c>m_Name</c> is the bundle's LOGICAL name.
    /// That is what exercises the VFS identity read.</summary>
    public static IReadOnlyList<long> Build(string path, string? bundleName, params TextureSpec[] textures)
    {
        var (bytes, ids) = BuildSerializedFile(textures, bundleName);
        WriteBundle(path, bytes);
        return ids;
    }

    /// <summary>One solid-colour texture; returns its path id.</summary>
    public static long BuildOneTexture(string path, string name, int width, int height,
        byte r = 0x20, byte g = 0x40, byte b = 0x60, byte a = 0xFF, string? bundleName = null, int colorSpace = 0)
        => Build(path, bundleName,
            new TextureSpec(name, width, height, SolidRgba32(width, height, r, g, b, a), colorSpace))[0];

    // ---- TextAsset (class 49) — the game's modern two-field layout -----------------------------------------
    // Real GFL2 bundles ship the TWO-field tree (m_Name + m_Script, no m_PathName); fixtures use that shape
    // to exercise type-REUSE paths.

    public const int ClassTextAsset = 49;

    /// <summary>One TextAsset in the game's two-field layout. Returns its path id.</summary>
    public static long BuildOneTextAsset(string path, string name, string script, string? bundleName = null)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        file.Metadata.TypeTreeTypes.Add(BuildTextAssetTwoFieldType());
        AddObject(file, 1, ClassTextAsset, bf =>
        {
            bf["m_Name"].AsString = name;
            bf["m_Script"].AsString = script;
        });
        if (bundleName is not null) AddAssetBundleObject(file, pathId: 2, bundleName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray());
        return 1;
    }

    /// <summary>The game's two-field TextAsset tree: <c>m_Name</c> + <c>m_Script</c>, no <c>m_PathName</c>.</summary>
    private static TypeTreeType BuildTextAssetTwoFieldType()
    {
        var b = new TreeBuilder(ClassTextAsset, "TextAsset");
        b.Str("m_Name", 1, align: true);
        b.Str("m_Script", 1, align: true);
        return b.Build();
    }

    // ---- Mesh (class 43) -----------------------------------------------------------------------------------
    // A minimal uncompressed inline Mesh: one Position channel (stream 0, Float32×3), one submesh of uint16
    // indices. Enough for the thin AssetsTools type-tree adapters; the byte codec beneath is MeshCodecTests'.

    /// <summary>One inline Mesh. <paramref name="positions"/> is a flat <c>x,y,z</c> list (vertex count =
    /// length/3); <paramref name="triangles"/> is index triples into it. <paramref name="indexFormat"/>:
    /// 0 = uint16, 1 = uint32, for exercising the u16-only guards. Returns the mesh's path id.</summary>
    /// <param name="sameNamedFirst">A DECOY Mesh of the SAME <c>m_Name</c>, written first at path id 1 with
    /// this geometry; the real mesh then lands at path id 3 and is what the returned id names. Enemy and
    /// prop bundles ship same-named copies, so a read that selects by name alone takes the decoy and a read
    /// that selects by path id takes the mesh the renderer pinned.</param>
    public static long BuildOneMesh(string path, string name, float[] positions, int[] triangles,
        string? bundleName = null, int indexFormat = 0,
        (float[] Positions, int[] Triangles)? sameNamedFirst = null)
    {
        var (bytes, id) = BuildSerializedMesh(name, positions, triangles, bundleName, indexFormat,
            sameNamedFirst);
        WriteBundle(path, bytes);
        return id;
    }

    private static (byte[] Bytes, long Id) BuildSerializedMesh(string name, float[] positions, int[] triangles,
        string? bundleName, int indexFormat = 0,
        (float[] Positions, int[] Triangles)? sameNamedFirst = null)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        file.Metadata.TypeTreeTypes.Add(BuildMeshType());

        // Objects are added in path-id order — decoy 1, the bundle object 2, the real mesh 3 — so a file
        // carrying a same-named decoy reads like any other.
        long meshPathId = sameNamedFirst is null ? 1 : 3;
        AddMesh(1, sameNamedFirst?.Positions ?? positions, sameNamedFirst?.Triangles ?? triangles);
        if (bundleName is not null) AddAssetBundleObject(file, pathId: 2, bundleName);
        if (sameNamedFirst is not null) AddMesh(meshPathId, positions, triangles);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        return (ms.ToArray(), meshPathId);

        void AddMesh(long pathId, float[] pos, int[] tris)
        {
            if (pos.Length % 3 != 0) throw new ArgumentException("positions must be a flat x,y,z list");
            int vertexCount = pos.Length / 3;
            var info = AssetFileInfo.Create(file, pathId, ClassMesh, classDatabase: null, preferEditor: false)
                ?? throw new InvalidOperationException("AssetFileInfo.Create returned null (Mesh type not registered)");
            var tpl = new AssetTypeTemplateField();
            tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
            var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);

            bf["m_Name"].AsString = name;
            bf["m_IndexFormat"].AsInt = indexFormat;

            // one Position channel: stream 0, offset 0, Float32 (format 0), dimension 3
            var vd = bf["m_VertexData"];
            vd["m_VertexCount"].AsUInt = (uint)vertexCount;
            var chArray = vd["m_Channels"]["Array"];
            chArray.Children = new List<AssetTypeValueField> { NewChannel(chArray, stream: 0, offset: 0, format: 0, dimension: 3) };
            // the vertex blob = tightly-packed Float32×3 per vertex (stride 12)
            var vbytes = new byte[vertexCount * 12];
            Buffer.BlockCopy(pos, 0, vbytes, 0, vbytes.Length);
            vd["m_DataSize"].AsByteArray = vbytes;

            // one submesh: all triangles, index width per indexFormat
            int step = indexFormat == 0 ? 2 : 4;
            var ibytes = new byte[tris.Length * step];
            for (int i = 0; i < tris.Length; i++)
            {
                if (indexFormat == 0) BitConverter.GetBytes((ushort)tris[i]).CopyTo(ibytes, i * 2);
                else BitConverter.GetBytes((uint)tris[i]).CopyTo(ibytes, i * 4);
            }
            bf["m_IndexBuffer"]["Array"].AsByteArray = ibytes;

            var smArray = bf["m_SubMeshes"]["Array"];
            smArray.Children = new List<AssetTypeValueField>
            {
                NewSubMesh(smArray, firstByte: 0, indexCount: (uint)tris.Length, baseVertex: 0,
                    firstVertex: 0, vertexCount: (uint)vertexCount),
            };

            // m_StreamData empty ⇒ inline mesh
            bf["m_StreamData"]["offset"].AsULong = 0;
            bf["m_StreamData"]["size"].AsUInt = 0;
            bf["m_StreamData"]["path"].AsString = "";

            info.SetNewData(bf);
            file.Metadata.AssetInfos.Add(info);
        }
    }

    // ---- a SKINNED Mesh ------------------------------------------------------------------------------
    // The layout palette recovery requires: the full 14-slot channel list with float4 weights + uint4
    // indices in stream 2, plus m_BoneNameHashes/m_BindPose. Strides are the corpus shape the emitter's
    // dump loaders read — 40 / 20 / 32.

    private const int SkinStream0Stride = 40;   // Vertex f3 @0, Normal f3 @12, Tangent f4 @24
    private const int SkinStream1Stride = 20;   // Color unorm8x4 @0, TexCoord0 f2 @4, TexCoord1 f2 @12
    // stream 2 is 8 bytes per stored influence: BlendWeight fW @0, BlendIndices u32xW @4W

    /// <summary>One inline SKINNED Mesh in the layout the swap pipeline accepts. At widths 1 and 4 every
    /// vertex sits fully on bone <c>v % boneHashes.Length</c>; bind poses are identity — identical across
    /// parts, which is what the pooled union requires of a bone two parts share. Returns the mesh's path
    /// id.</summary>
    /// <param name="blendShapes">how many blend shapes the mesh declares. Above 0 the type tree grows the
    /// <c>m_Shapes</c> struct, so a mesh without them stays byte-identical to what it was.</param>
    /// <param name="skinWidth">stored influences per vertex: 4 is the full float4 weights + uint4 indices
    /// pair, 2 the two-influence pair some game bodies ship, 1 the narrow one a part riding a single bone
    /// carries. Widths 2–3 store a genuine split (<see cref="SkinSplit"/>) with influence <c>k</c> on bone
    /// <c>(v + k) % boneHashes.Length</c>, so every declared slot carries weight; 1 and 4 keep the whole
    /// vertex on its first bone (fixtures pin those bytes).</param>
    /// <param name="implicitWeights">the OTHER one-influence spelling, the one the game's weapon and
    /// accessory parts actually ship: BlendIndices alone at stream-2 offset 0, no BlendWeight channel, each
    /// weight implicitly 1. Only meaningful at <paramref name="skinWidth"/> 1.</param>
    /// <param name="tabledOnlyBones">extra hashes appended to <c>m_BoneNameHashes</c> that NO vertex rides.
    /// Real meshes carry these, and a rule that reads the table where it means the pose reads them as
    /// posing something.</param>
    /// <param name="extraSkinChannel">puts a live TexCoord2 on the skin stream, past the influences. The
    /// stream is read and written whole at one stride, so a mesh whose skin channels otherwise read as a
    /// known layout still has bytes there that are neither weights nor indices.</param>
    /// <param name="unresolvableStream">empties the inline vertex blob and points <c>m_StreamData</c> at a
    /// <c>.resS</c> this bundle doesn't carry. The channel table, the bone hashes and the skin rule all
    /// still read exactly as a sound mesh's — only reading the vertex bytes themselves fails, which is
    /// the shape a caller that measures weights meets past every layout check.</param>
    /// <param name="submeshIndexCounts">How many of <paramref name="triangles"/>' indices each submesh takes,
    /// in order — the shape a MULTI-MATERIAL part has, where the renderer binds one material per submesh.
    /// Null (the default) writes one submesh over the whole index buffer.</param>
    public static long BuildOneSkinnedMesh(string path, string name, float[] positions, int[] triangles,
        uint[] boneHashes, string? bundleName = null, int blendShapes = 0, int skinWidth = 4,
        uint[]? tabledOnlyBones = null, bool implicitWeights = false, bool extraSkinChannel = false,
        int uvSeed = 0, bool unresolvableStream = false, int[]? submeshIndexCounts = null)
    {
        var file = NewMeshFile(blendShapes);
        AddSkinnedMesh(file, 1, name, positions, triangles, boneHashes, blendShapes, skinWidth, tabledOnlyBones,
            implicitWeights, extraSkinChannel, uvSeed, unresolvableStream, submeshIndexCounts);
        if (bundleName is not null) AddAssetBundleObject(file, pathId: 2, bundleName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray());
        return 1;
    }

    /// <summary>An assets file carrying only the Mesh type tree.</summary>
    private static AssetsFile NewMeshFile(int blendShapes)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        file.Metadata.TypeTreeTypes.Add(BuildMeshType(withShapes: blendShapes > 0));
        return file;
    }

    /// <summary>The skinned Mesh object itself, so a bundle that also ships a rig writes the same mesh the
    /// mesh-only fixture does.</summary>
    private static void AddSkinnedMesh(AssetsFile file, long pathId, string name, float[] positions,
        int[] triangles, uint[] boneHashes, int blendShapes, int skinWidth, uint[]? tabledOnlyBones = null,
        bool implicitWeights = false, bool extraSkinChannel = false, int uvSeed = 0,
        bool unresolvableStream = false, int[]? submeshIndexCounts = null)
    {
        if (positions.Length % 3 != 0) throw new ArgumentException("positions must be a flat x,y,z list");
        if (boneHashes.Length == 0) throw new ArgumentException("a skinned mesh needs at least one bone");
        int vertexCount = positions.Length / 3;

        AddObject(file, pathId, ClassMesh, bf =>
        {
            bf["m_Name"].AsString = name;
            bf["m_IndexFormat"].AsInt = 0;

            var vd = bf["m_VertexData"];
            vd["m_VertexCount"].AsUInt = (uint)vertexCount;
            var chArray = vd["m_Channels"]["Array"];
            chArray.Children = SkinnedChannels(chArray, skinWidth, implicitWeights, extraSkinChannel);
            vd["m_DataSize"].AsByteArray = SkinnedVertexBlob(positions, vertexCount, boneHashes.Length, skinWidth,
                implicitWeights, extraSkinChannel, uvSeed);

            var ibytes = new byte[triangles.Length * 2];
            for (int i = 0; i < triangles.Length; i++)
                BitConverter.GetBytes((ushort)triangles[i]).CopyTo(ibytes, i * 2);
            bf["m_IndexBuffer"]["Array"].AsByteArray = ibytes;

            var smArray = bf["m_SubMeshes"]["Array"];
            smArray.Children = SubMeshes(smArray, submeshIndexCounts, triangles.Length, vertexCount);

            var hashArray = bf["m_BoneNameHashes"]["Array"];
            var bindArray = bf["m_BindPose"]["Array"];
            var hashes = new List<AssetTypeValueField>();
            var binds = new List<AssetTypeValueField>();
            foreach (var h in boneHashes.Concat(tabledOnlyBones ?? Array.Empty<uint>()))
            {
                var he = ValueBuilder.DefaultValueFieldFromArrayTemplate(hashArray);
                he.AsUInt = h;
                hashes.Add(he);
                binds.Add(IdentityBindPose(bindArray));
            }
            hashArray.Children = hashes;
            bindArray.Children = binds;

            if (blendShapes > 0)
            {
                var shapeArray = bf["m_Shapes"]["shapes"]["Array"];
                var shapes = new List<AssetTypeValueField>();
                for (int i = 0; i < blendShapes; i++)
                {
                    // Only the shape COUNT is read; the per-shape vertex deltas the engine morphs with are
                    // not part of any rule under test.
                    var s = ValueBuilder.DefaultValueFieldFromArrayTemplate(shapeArray);
                    s["firstVertex"].AsUInt = 0;
                    s["vertexCount"].AsUInt = 0;
                    shapes.Add(s);
                }
                shapeArray.Children = shapes;
            }

            if (unresolvableStream)
            {
                // The shape of a streamed mesh whose .resS this bundle doesn't carry: the vertex blob is
                // gone, m_StreamData points at a resource nothing can load, and every channel still
                // declares the bytes that are no longer there.
                bf["m_VertexData"]["m_DataSize"].AsByteArray = Array.Empty<byte>();
                bf["m_StreamData"]["offset"].AsULong = 0;
                bf["m_StreamData"]["size"].AsUInt = (uint)(vertexCount * 32);
                bf["m_StreamData"]["path"].AsString = "archive:/CAB-absent/CAB-absent.resS";
            }
            else
            {
                bf["m_StreamData"]["offset"].AsULong = 0;
                bf["m_StreamData"]["size"].AsUInt = 0;
                bf["m_StreamData"]["path"].AsString = "";
            }
        });
    }

    /// <summary>One transform of a self-rigged prop's scene: its GameObject name, the index of its parent in
    /// the same array (-1 = a scene root) and its LOCAL position. Scale is one and <see cref="Rotation"/>
    /// defaults to identity, so the rest world a chain composes is the running sum of these — the shape a
    /// prefab mount offset takes; a fixture that ships a part lying down sets the root's rotation.
    /// </summary>
    public readonly record struct RigNode(string Name, int Parent, float X, float Y, float Z)
    {
        public System.Numerics.Quaternion Rotation { get; init; } = System.Numerics.Quaternion.Identity;
    }

    /// <summary>A skinned Mesh together with the rig that PLACES it: a SkinnedMeshRenderer pointing at the
    /// mesh and naming its bone Transforms in mesh bone order, plus a GameObject + Transform per node. This
    /// is what a self-rigged prop's own bundle carries and what <c>SceneRig.TryRead</c> reads out of it; a
    /// mesh alone has no scene rest at all, and the export then falls back to the corpus bone table. Bind
    /// poses are identity (as in <see cref="BuildOneSkinnedMesh"/>), so each bone's scene rest world IS the
    /// bind→scene relation the rig measures. Returns the mesh's path id.</summary>
    /// <param name="skinBones">Indices into <paramref name="nodes"/>, in mesh bone order — one per entry of
    /// <paramref name="boneHashes"/>. Nodes not listed are the connectors above them.</param>
    public static long BuildSelfRiggedMesh(string path, string name, float[] positions, int[] triangles,
        uint[] boneHashes, RigNode[] nodes, int[] skinBones, string? bundleName = null)
    {
        if (skinBones.Length != boneHashes.Length)
            throw new ArgumentException("one skin bone per bone hash");
        var file = NewMeshFile(blendShapes: 0);
        file.Metadata.TypeTreeTypes.Add(BuildGameObjectType());
        file.Metadata.TypeTreeTypes.Add(BuildSmrType(withBones: true));
        file.Metadata.TypeTreeTypes.Add(BuildTransformType());

        const long meshPid = 1;
        AddSkinnedMesh(file, meshPid, name, positions, triangles, boneHashes, blendShapes: 0, skinWidth: 4);

        // pathIds: the mesh takes 1, then a GameObject + Transform per node, then the SMR and its carrier
        long pid = 2;
        var goPid = new long[nodes.Length];
        var trPid = new long[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) { goPid[i] = pid++; trPid[i] = pid++; }
        for (int i = 0; i < nodes.Length; i++)
        {
            int bi = i;
            AddObject(file, goPid[bi], ClassGameObject, bf => bf["m_Name"].AsString = nodes[bi].Name);
            AddObject(file, trPid[bi], ClassTransform, bf =>
            {
                bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = goPid[bi];
                bf["m_Father"]["m_FileID"].AsInt = 0;
                bf["m_Father"]["m_PathID"].AsLong = nodes[bi].Parent >= 0 ? trPid[nodes[bi].Parent] : 0;
                var rotation = nodes[bi].Rotation;
                bf["m_LocalRotation"]["x"].AsFloat = rotation.X; bf["m_LocalRotation"]["y"].AsFloat = rotation.Y;
                bf["m_LocalRotation"]["z"].AsFloat = rotation.Z; bf["m_LocalRotation"]["w"].AsFloat = rotation.W;
                bf["m_LocalPosition"]["x"].AsFloat = nodes[bi].X;
                bf["m_LocalPosition"]["y"].AsFloat = nodes[bi].Y;
                bf["m_LocalPosition"]["z"].AsFloat = nodes[bi].Z;
                bf["m_LocalScale"]["x"].AsFloat = 1; bf["m_LocalScale"]["y"].AsFloat = 1;
                bf["m_LocalScale"]["z"].AsFloat = 1;
            });
        }
        long smrGoPid = pid++;
        long smrPid = pid++;
        AddObject(file, smrGoPid, ClassGameObject, bf => bf["m_Name"].AsString = name + "_renderer");
        AddObject(file, smrPid, ClassSkinnedMeshRenderer, bf =>
        {
            bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = smrGoPid;
            bf["m_Materials"]["Array"].Children = new List<AssetTypeValueField>();
            bf["m_Mesh"]["m_FileID"].AsInt = 0; bf["m_Mesh"]["m_PathID"].AsLong = meshPid;
            var arr = bf["m_Bones"]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var b in skinBones)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["m_FileID"].AsInt = 0; el["m_PathID"].AsLong = trPid[b];
                els.Add(el);
            }
            arr.Children = els;
        });

        if (bundleName is not null) AddAssetBundleObject(file, pid, bundleName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray());
        return meshPid;
    }

    /// <summary>The 14 positional channel slots, unused ones at dimension 0. <paramref name="skinWidth"/> is
    /// the stored influence count the two skin channels declare; <paramref name="implicitWeights"/> drops
    /// BlendWeight entirely and puts BlendIndices at the head of the stream;
    /// <paramref name="extraSkinChannel"/> gives TexCoord2 storage on the skin stream past them.</summary>
    private static List<AssetTypeValueField> SkinnedChannels(AssetTypeValueField array, int skinWidth = 4,
        bool implicitWeights = false, bool extraSkinChannel = false)
    {
        var c = new List<AssetTypeValueField>
        {
            NewChannel(array, 0, 0, 0, 3),      // Vertex     Float32 x3
            NewChannel(array, 0, 12, 0, 3),     // Normal     Float32 x3
            NewChannel(array, 0, 24, 0, 4),     // Tangent    Float32 x4
            NewChannel(array, 1, 0, 2, 4),      // Color      UNorm8  x4
            NewChannel(array, 1, 4, 0, 2),      // TexCoord0  Float32 x2
            NewChannel(array, 1, 12, 0, 2),     // TexCoord1  Float32 x2
        };
        for (int i = 6; i < 12; i++)
            c.Add(i == 6 && extraSkinChannel
                ? NewChannel(array, 2, (byte)SkinBase(skinWidth, implicitWeights), 0, 2)   // TexCoord2 Float32 x2
                : NewChannel(array, 0, 0, 0, 0));                                          // TexCoord2..7 unused
        if (implicitWeights)
        {
            c.Add(NewChannel(array, 0, 0, 0, 0));                                 // BlendWeight  absent
            c.Add(NewChannel(array, 2, 0, 10, (byte)skinWidth));                  // BlendIndices UInt32 xW @0
        }
        else
        {
            c.Add(NewChannel(array, 2, 0, 0, (byte)skinWidth));                       // BlendWeight  Float32 xW
            c.Add(NewChannel(array, 2, (byte)(4 * skinWidth), 10, (byte)skinWidth));   // BlendIndices UInt32  xW
        }
        return c;
    }

    /// <summary>Bytes the influences themselves take on the skin stream, which is where anything else on
    /// that stream starts.</summary>
    private static int SkinBase(int skinWidth, bool implicitWeights) =>
        (implicitWeights ? 4 : 8) * skinWidth;

    /// <summary>The per-vertex weight values a width stores, summing to 1. Public so a test asserting
    /// widened bytes states the same values the blob wrote.</summary>
    public static float[] SkinSplit(int skinWidth) => skinWidth switch
    {
        2 => new[] { 0.75f, 0.25f },
        3 => new[] { 0.5f, 0.375f, 0.125f },
        _ => new[] { 1f },
    };

    /// <summary>The stream-interleaved blob for <see cref="SkinnedChannels"/>: intermediate streams padded
    /// up to 16 bytes, the last one not, exactly as the engine lays them out.</summary>
    private static byte[] SkinnedVertexBlob(float[] positions, int vertexCount, int boneCount,
        int skinWidth = 4, bool implicitWeights = false, bool extraSkinChannel = false, int uvSeed = 0)
    {
        int skinStride = SkinBase(skinWidth, implicitWeights) + (extraSkinChannel ? 8 : 0);
        int s0 = (vertexCount * SkinStream0Stride + 15) & ~15;
        int s1 = (vertexCount * SkinStream1Stride + 15) & ~15;
        var blob = new byte[s0 + s1 + vertexCount * skinStride];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = v * SkinStream0Stride;
            for (int c = 0; c < 3; c++) BitConverter.GetBytes(positions[v * 3 + c]).CopyTo(blob, p + c * 4);
            foreach (var (off, value) in new[]
                     {
                         (12, 0f), (16, 1f), (20, 0f),                  // normal  (0, 1, 0)
                         (24, 1f), (28, 0f), (32, 0f), (36, 1f),        // tangent (1, 0, 0), w = 1
                     })
                BitConverter.GetBytes(value).CopyTo(blob, p + off);

            int q = s0 + v * SkinStream1Stride;
            blob[q] = blob[q + 1] = blob[q + 2] = blob[q + 3] = 0xFF;   // white vertex colour
            // a nonzero seed gives the mesh a UV set of its own, so two same-topology meshes can differ
            // in their stream-1 bytes exactly the way remodel twins with a re-unwrap do
            if (uvSeed != 0) BitConverter.GetBytes((float)(uvSeed + v)).CopyTo(blob, q + 4);

            int r = s0 + s1 + v * skinStride;
            var split = SkinSplit(skinWidth);
            if (!implicitWeights)
                for (int k = 0; k < split.Length; k++)
                    BitConverter.GetBytes(split[k]).CopyTo(blob, r + k * 4);
            for (int k = 0; k < split.Length; k++)
                BitConverter.GetBytes((uint)((v + k) % boneCount))
                    .CopyTo(blob, r + (implicitWeights ? 0 : 4 * skinWidth) + k * 4);
        }
        return blob;
    }

    private static AssetTypeValueField IdentityBindPose(AssetTypeValueField array)
    {
        var m = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                m[$"e{row}{col}"].AsFloat = row == col ? 1f : 0f;
        return m;
    }

    private static AssetTypeValueField NewChannel(AssetTypeValueField array, byte stream, byte offset, byte format, byte dimension)
    {
        var c = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        c["stream"].AsByte = stream;
        c["offset"].AsByte = offset;
        c["format"].AsByte = format;
        c["dimension"].AsByte = dimension;
        return c;
    }

    /// <summary>The submesh rows for an index buffer: one per entry of <paramref name="indexCounts"/>, taken
    /// in order out of the buffer, or one row over the whole buffer when none is given. Every row spans the
    /// whole vertex pool, which is how the game's own parts are laid out — submeshes are ranges of the index
    /// buffer, not separate vertex sets.</summary>
    private static List<AssetTypeValueField> SubMeshes(AssetTypeValueField array, int[]? indexCounts,
        int totalIndices, int vertexCount)
    {
        var rows = new List<AssetTypeValueField>();
        if (indexCounts is not { Length: > 0 })
        {
            rows.Add(NewSubMesh(array, firstByte: 0, indexCount: (uint)totalIndices, baseVertex: 0,
                firstVertex: 0, vertexCount: (uint)vertexCount));
            return rows;
        }
        uint firstIndex = 0;
        foreach (int count in indexCounts)
        {
            rows.Add(NewSubMesh(array, firstByte: firstIndex * 2, indexCount: (uint)count, baseVertex: 0,
                firstVertex: 0, vertexCount: (uint)vertexCount));
            firstIndex += (uint)count;
        }
        if (firstIndex != totalIndices)
            throw new ArgumentException("submeshIndexCounts must add up to the index count");
        return rows;
    }

    private static AssetTypeValueField NewSubMesh(AssetTypeValueField array, uint firstByte, uint indexCount,
        uint baseVertex, uint firstVertex, uint vertexCount)
    {
        var s = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        s["firstByte"].AsUInt = firstByte;
        s["indexCount"].AsUInt = indexCount;
        s["topology"].AsInt = 0;   // Triangles
        s["baseVertex"].AsUInt = baseVertex;
        s["firstVertex"].AsUInt = firstVertex;
        s["vertexCount"].AsUInt = vertexCount;
        return s;
    }

    /// <summary>Only the fields the read/apply adapters touch. The other real-Mesh fields (compression, the
    /// bone-weight tables the runtime rebuilds…) are omitted — nothing under test reads them. The bone table
    /// is here but left EMPTY by the rigid builder, which is how a rigid mesh reads anyway.
    /// <paramref name="withShapes"/> adds the <c>m_Shapes</c> struct, which only a blend-shape fixture needs:
    /// a mesh without it serializes exactly as it did before the field existed.</summary>
    private static TypeTreeType BuildMeshType(bool withShapes = false)
    {
        var b = new TreeBuilder(ClassMesh, "Mesh");
        b.Str("m_Name", 1, align: true);
        if (withShapes)
        {
            b.Struct("BlendShapeData", "m_Shapes", 1);
            b.VectorOfStruct("shapes", "MeshBlendShape", 2, s =>
            {
                s.Value("unsigned int", "firstVertex", 5, 4);
                s.Value("unsigned int", "vertexCount", 5, 4);
            });
        }
        b.VectorOfValue("m_BoneNameHashes", "unsigned int", 4, 1);
        b.VectorOfStruct("m_BindPose", "Matrix4x4f", 1, m =>
        {
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    m.Value("float", $"e{row}{col}", 4, 4);
        });
        b.VectorOfStruct("m_SubMeshes", "SubMesh", 1, sub =>
        {
            // element fields sit at level+3 (the "data" element node is at level+2 = 3, so its children are 4)
            sub.Value("unsigned int", "firstByte", 4, 4);
            sub.Value("unsigned int", "indexCount", 4, 4);
            sub.Value("int", "topology", 4, 4);
            sub.Value("unsigned int", "baseVertex", 4, 4);
            sub.Value("unsigned int", "firstVertex", 4, 4);
            sub.Value("unsigned int", "vertexCount", 4, 4);
            sub.Aabb("localAABB", 4);
        });
        b.Value("int", "m_IndexFormat", 1, 4);
        b.ByteArrayVector("m_IndexBuffer", 1, align: true);
        b.Struct("VertexData", "m_VertexData", 1);
        b.Value("unsigned int", "m_VertexCount", 2, 4);
        b.VectorOfStruct("m_Channels", "ChannelInfo", 2, ch =>
        {
            // element fields sit at level+3 (data element node at level+2 = 4, so its children are 5)
            ch.Value("UInt8", "stream", 5, 1);
            ch.Value("UInt8", "offset", 5, 1);
            ch.Value("UInt8", "format", 5, 1);
            ch.Value("UInt8", "dimension", 5, 1, align: true);
        });
        b.ByteArray("m_DataSize", 2, align: true);   // TypelessData
        b.Aabb("m_LocalAABB", 1);
        b.Struct("StreamingInfo", "m_StreamData", 1);
        b.Value("UInt64", "offset", 2, 8);
        b.Value("unsigned int", "size", 2, 4);
        b.Str("path", 2, align: true);
        return b.Build();
    }

    private static (byte[] Bytes, List<long> Ids) BuildSerializedFile(TextureSpec[] textures, string? bundleName)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader
            {
                Version = SerializedVersion,
                Endianness = false, // little-endian (PC)
                MetadataSize = 0, FileSize = 0, DataOffset = 0,
            },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion,
                TargetPlatform = 5, // StandaloneWindows64
                TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(),
                AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(),
                Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(),
                UserInformation = "",
            },
        };

        // Register the hand-built Texture2D type tree once; every texture asset points at it.
        file.Metadata.TypeTreeTypes.Add(BuildTexture2DType());

        var ids = new List<long>(textures.Length);
        long pathId = 0;
        foreach (var spec in textures)
        {
            pathId++;
            var info = AssetFileInfo.Create(file, pathId, ClassTexture2D, classDatabase: null, preferEditor: false)
                ?? throw new InvalidOperationException("AssetFileInfo.Create returned null (type not registered)");

            var tpl = new AssetTypeTemplateField();
            tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
            var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);

            int texelBytes = spec.Format == RgbaHalf ? 8 : 4;
            if (spec.Pixels.Length != spec.Width * spec.Height * texelBytes)
                throw new ArgumentException(
                    $"texture '{spec.Name}': {spec.Pixels.Length} pixel bytes, expected "
                    + $"{spec.Width * spec.Height * texelBytes} for {spec.Width}x{spec.Height} "
                    + $"in Unity format {spec.Format}");

            bf["m_Name"].AsString = spec.Name;
            bf["m_Width"].AsInt = spec.Width;
            bf["m_Height"].AsInt = spec.Height;
            bf["m_TextureFormat"].AsInt = spec.Format;
            bf["m_MipCount"].AsInt = 1;
            bf["m_MipMap"].AsBool = false;
            bf["m_IsReadable"].AsBool = false;
            bf["m_ImageCount"].AsInt = 1;
            bf["m_TextureDimension"].AsInt = 2; // Tex2D
            bf["m_TextureSettings"]["m_FilterMode"].AsInt = 1;
            bf["m_TextureSettings"]["m_Aniso"].AsInt = 1;
            bf["m_TextureSettings"]["m_MipBias"].AsFloat = 0f;
            bf["m_TextureSettings"]["m_WrapU"].AsInt = 0;
            bf["m_TextureSettings"]["m_WrapV"].AsInt = 0;
            bf["m_LightmapFormat"].AsInt = 0;
            bf["m_ColorSpace"].AsInt = spec.ColorSpace;
            bf["m_CompleteImageSize"].AsInt = spec.Pixels.Length;
            bf["image data"].AsByteArray = spec.Pixels;

            // m_StreamData present but empty ⇒ inline texture (size 0).
            bf["m_StreamData"]["offset"].AsULong = 0;
            bf["m_StreamData"]["size"].AsUInt = 0;
            bf["m_StreamData"]["path"].AsString = "";

            info.SetNewData(bf);
            file.Metadata.AssetInfos.Add(info);
            ids.Add(pathId);
        }

        if (bundleName is not null) AddAssetBundleObject(file, pathId + 1, bundleName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        return (ms.ToArray(), ids);
    }

    // ---- Assembly prefab --------------------------------------------------------------------------
    // Root GameObject with a RoleMeshRes-shaped MonoBehaviour, one renderer-slot GameObject with an SMR, and
    // an AssetBundle whose m_Container maps the root. Exercises prefab-root capture and PrefabReader.

    public const int ClassGameObject = 1;
    public const int ClassTransform = 4;
    public const int ClassMonoBehaviour = 114;
    public const int ClassSkinnedMeshRenderer = 137;

    /// <summary>Root GameObject (pathId 1) + recipe MonoBehaviour (3), slot GameObject (2) + its SMR (4,
    /// materials as (fileId, pathId) PPtrs and no serialized mesh), and the AssetBundle object (5) with an
    /// m_Container entry for the root. <paramref name="externalCabs"/> is the external list, 1-based by
    /// fileId.</summary>
    public static void BuildPrefab(string path, string bundleName, string rootName, string slotName,
        (string SlotPath, string MeshAddress)[] recipe, (int FileId, long PathId)[] slotMaterials,
        string[] externalCabs)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        foreach (var cab in externalCabs)
            file.Metadata.Externals.Add(new AssetsFileExternal
            {
                PathName = $"archive:/{cab}/{cab}", VirtualAssetPathName = "", Guid = default,
                Type = AssetsFileExternalType.Normal, OriginalPathName = "",
            });

        file.Metadata.TypeTreeTypes.Add(BuildGameObjectType());
        file.Metadata.TypeTreeTypes.Add(BuildRecipeMonoBehaviourType());
        file.Metadata.TypeTreeTypes.Add(BuildSmrType());

        // root GameObject (1): components = [MB 3]
        AddObject(file, 1, ClassGameObject, bf =>
        {
            bf["m_Name"].AsString = rootName;
            var arr = bf["m_Component"]["Array"];
            var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            el["m_FileID"].AsInt = 0; el["m_PathID"].AsLong = 3;
            arr.Children = new List<AssetTypeValueField> { el };
        });
        // slot GameObject (2): no components
        AddObject(file, 2, ClassGameObject, bf => bf["m_Name"].AsString = slotName);
        // recipe MonoBehaviour (3) on the root
        AddObject(file, 3, ClassMonoBehaviour, bf =>
        {
            bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = 1;
            bf["m_Name"].AsString = "RoleMeshRes";
            var arr = bf["MeshResList"]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var (slotPath, meshAddress) in recipe)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["TransfromPath"].AsString = slotPath;    // the game's own (misspelled) field name
                el["MeshResPath"].AsString = meshAddress;
                els.Add(el);
            }
            arr.Children = els;
        });
        // SkinnedMeshRenderer (4) on the slot GameObject
        AddObject(file, 4, ClassSkinnedMeshRenderer, bf =>
        {
            bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = 2;
            var arr = bf["m_Materials"]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var (fid, pid) in slotMaterials)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["m_FileID"].AsInt = fid; el["m_PathID"].AsLong = pid;
                els.Add(el);
            }
            arr.Children = els;
            bf["m_Mesh"]["m_FileID"].AsInt = 0; bf["m_Mesh"]["m_PathID"].AsLong = 0;   // slot ships no mesh
        });
        AddAssetBundleObject(file, pathId: 5, bundleName, containerRootPathId: 1, containerKey: rootName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray());
    }

    /// <summary>One Material (class 21) with the given <c>m_TexEnvs</c> slots, plus optionally one inline
    /// Texture2D (pathId 2) for local (fileId 0) references — the shape the renderer-first tier walks.</summary>
    /// <param name="localTexture">One Texture2D beside the material, at path id 2.</param>
    /// <param name="localTextures">Several Texture2Ds beside the material, at path ids 2, 3, … in order —
    /// for the shape where one bundle ships SAME-NAMED textures and only the path id tells them apart.
    /// Overrides <paramref name="localTexture"/> when both are given.</param>
    public static void BuildOneMaterial(string path, string bundleName, string materialName, long materialPathId,
        (string Slot, int FileId, long PathId)[] texEnvs, string[] externalCabs,
        TextureSpec? localTexture = null, string? cabName = null,
        MaterialShadingSpec? shading = null, IReadOnlyList<TextureSpec>? localTextures = null)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        foreach (var cab in externalCabs)
            file.Metadata.Externals.Add(new AssetsFileExternal
            {
                PathName = $"archive:/{cab}/{cab}", VirtualAssetPathName = "", Guid = default,
                Type = AssetsFileExternalType.Normal, OriginalPathName = "",
            });

        file.Metadata.TypeTreeTypes.Add(BuildMaterialType());
        AddObject(file, materialPathId, ClassMaterial, bf =>
        {
            bf["m_Name"].AsString = materialName;
            bf["m_Shader"]["m_FileID"].AsInt = shading?.ShaderFileId ?? 0;
            bf["m_Shader"]["m_PathID"].AsLong = shading?.ShaderPathId ?? 0;
            var keywordArray = bf["m_ValidKeywords"]["Array"];
            keywordArray.Children = (shading?.Keywords ?? Array.Empty<string>()).Select(keyword =>
            {
                var value = ValueBuilder.DefaultValueFieldFromArrayTemplate(keywordArray);
                value.AsString = keyword;
                return value;
            }).ToList();
            var arr = bf["m_SavedProperties"]["m_TexEnvs"]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var (slot, fid, pid) in texEnvs)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["first"].AsString = slot;
                el["second"]["m_Texture"]["m_FileID"].AsInt = fid;
                el["second"]["m_Texture"]["m_PathID"].AsLong = pid;
                els.Add(el);
            }
            arr.Children = els;
            var floatArray = bf["m_SavedProperties"]["m_Floats"]["Array"];
            floatArray.Children = (shading?.Floats
                ?? new Dictionary<string, float>(StringComparer.Ordinal)).Select(pair =>
            {
                var value = ValueBuilder.DefaultValueFieldFromArrayTemplate(floatArray);
                value["first"].AsString = pair.Key;
                value["second"].AsFloat = pair.Value;
                return value;
            }).ToList();
            var colorArray = bf["m_SavedProperties"]["m_Colors"]["Array"];
            colorArray.Children = (shading?.Colors
                ?? new Dictionary<string, float[]>(StringComparer.Ordinal)).Select(pair =>
            {
                if (pair.Value.Length != 4)
                    throw new ArgumentException("a synthetic material color needs four components");
                var value = ValueBuilder.DefaultValueFieldFromArrayTemplate(colorArray);
                value["first"].AsString = pair.Key;
                var color = value["second"];
                color["r"].AsFloat = pair.Value[0];
                color["g"].AsFloat = pair.Value[1];
                color["b"].AsFloat = pair.Value[2];
                color["a"].AsFloat = pair.Value[3];
                return value;
            }).ToList();
        });

        var locals = localTextures ?? (localTexture is { } one ? new[] { one } : Array.Empty<TextureSpec>());
        if (locals.Count > 0)
        {
            file.Metadata.TypeTreeTypes.Add(BuildTexture2DType());
            for (int i = 0; i < locals.Count; i++)
            {
                var spec = locals[i];
                AddObject(file, 2 + i, ClassTexture2D, bf =>
                {
                    bf["m_Name"].AsString = spec.Name;
                    bf["m_Width"].AsInt = spec.Width;
                    bf["m_Height"].AsInt = spec.Height;
                    bf["m_TextureFormat"].AsInt = spec.Format;
                    bf["m_MipCount"].AsInt = 1;
                    bf["m_IsReadable"].AsBool = true;
                    bf["m_ImageCount"].AsInt = 1;
                    bf["m_TextureDimension"].AsInt = 2;
                    bf["m_CompleteImageSize"].AsInt = spec.Pixels.Length;
                    bf["image data"].AsByteArray = spec.Pixels;
                    bf["m_StreamData"]["offset"].AsULong = 0;
                    bf["m_StreamData"]["size"].AsUInt = 0;
                    bf["m_StreamData"]["path"].AsString = "";
                });
            }
        }

        AddAssetBundleObject(file, pathId: 90, bundleName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray(), cabName);
    }

    /// <summary>The fields the TexEnvs readers walk. Scale/offset omitted — nothing under test reads them.</summary>
    private static TypeTreeType BuildMaterialType()
    {
        var b = new TreeBuilder(ClassMaterial, "Material");
        b.Str("m_Name", 1, align: true);
        b.Struct("PPtr<Shader>", "m_Shader", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.VectorOfString("m_ValidKeywords", 1);
        b.Struct("UnityPropertySheet", "m_SavedProperties", 1);
        b.VectorOfStruct("m_TexEnvs", "pair", 2, e =>
        {
            e.Str("first", 5, align: true);
            e.Struct("UnityTexEnv", "second", 5);
            e.Struct("PPtr<Texture>", "m_Texture", 6);
            e.Value("int", "m_FileID", 7, 4);
            e.Value("SInt64", "m_PathID", 7, 8);
        });
        b.VectorOfStruct("m_Floats", "pair", 2, e =>
        {
            e.Str("first", 5, align: true);
            e.Value("float", "second", 5, 4);
        });
        b.VectorOfStruct("m_Colors", "pair", 2, e =>
        {
            e.Str("first", 5, align: true);
            e.Struct("ColorRGBA", "second", 5);
            e.Value("float", "r", 6, 4);
            e.Value("float", "g", 6, 4);
            e.Value("float", "b", 6, 4);
            e.Value("float", "a", 6, 4);
        });
        return b.Build();
    }

    public const int ClassMaterial = 21;

    /// <summary>Create an object from its registered tree, populate it, add it to the file.</summary>
    private static void AddObject(AssetsFile file, long pathId, int classId, Action<AssetTypeValueField> fill)
    {
        var info = AssetFileInfo.Create(file, pathId, classId, classDatabase: null, preferEditor: false)
            ?? throw new InvalidOperationException($"AssetFileInfo.Create returned null (class {classId} not registered)");
        var tpl = new AssetTypeTemplateField();
        tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
        var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);
        fill(bf);
        info.SetNewData(bf);
        file.Metadata.AssetInfos.Add(info);
    }

    /// <summary>The two fields prefab-root capture and PrefabReader read.</summary>
    private static TypeTreeType BuildGameObjectType()
    {
        var b = new TreeBuilder(ClassGameObject, "GameObject");
        b.VectorOfStruct("m_Component", "PPtr<Component>", 1, e => e
            .Value("int", "m_FileID", 4, 4)
            .Value("SInt64", "m_PathID", 4, 8));
        b.Str("m_Name", 1, align: true);
        return b.Build();
    }

    /// <summary>The standard MB header fields plus <c>MeshResList</c> (vector of {TransfromPath,
    /// MeshResPath} — the game's own misspelling).</summary>
    private static TypeTreeType BuildRecipeMonoBehaviourType()
    {
        var b = new TreeBuilder(ClassMonoBehaviour, "MonoBehaviour");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Value("UInt8", "m_Enabled", 1, 1, align: true);
        b.Struct("PPtr<MonoScript>", "m_Script", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Str("m_Name", 1, align: true);
        b.VectorOfStruct("MeshResList", "MeshRes", 1, e =>
        {
            e.Str("TransfromPath", 4, align: true);
            e.Str("MeshResPath", 4, align: true);
        });
        return b.Build();
    }

    /// <summary>The three fields PrefabReader reads off a slot. <paramref name="withBones"/> adds the
    /// ordered <c>m_Bones</c> a scene rig is read through — a slot that ships no rig stays as it was.</summary>
    private static TypeTreeType BuildSmrType(bool withBones = false)
    {
        var b = new TreeBuilder(ClassSkinnedMeshRenderer, "SkinnedMeshRenderer");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.VectorOfStruct("m_Materials", "PPtr<Material>", 1, e => e
            .Value("int", "m_FileID", 4, 4)
            .Value("SInt64", "m_PathID", 4, 8));
        if (withBones)
            b.VectorOfStruct("m_Bones", "PPtr<Transform>", 1, e => e
                .Value("int", "m_FileID", 4, 4)
                .Value("SInt64", "m_PathID", 4, 8));
        b.Struct("PPtr<Mesh>", "m_Mesh", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        return b.Build();
    }

    /// <summary>Parent link plus the local TRS a rest world composes from — what a scene rig reads to place
    /// a bone, and what the hierarchy walk reads to name and parent it.</summary>
    private static TypeTreeType BuildTransformType()
    {
        var b = new TreeBuilder(ClassTransform, "Transform");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Quaternion("m_LocalRotation", 1);
        b.Vector3f("m_LocalPosition", 1);
        b.Vector3f("m_LocalScale", 1);
        b.Struct("PPtr<Transform>", "m_Father", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        return b.Build();
    }

    /// <summary>The self-identification object real bundles carry: <c>m_Name</c> = the bundle's LOGICAL
    /// name. An empty name is written AS GIVEN, so fixtures can prove the reader refuses it. With
    /// <paramref name="containerRootPathId"/> it also carries <c>m_Container</c> — the prefab shape.</summary>
    private static void AddAssetBundleObject(AssetsFile file, long pathId, string bundleName,
        long? containerRootPathId = null, string? containerKey = null)
    {
        file.Metadata.TypeTreeTypes.Add(BuildAssetBundleType(withContainer: containerRootPathId is not null));
        var info = AssetFileInfo.Create(file, pathId, ClassAssetBundle, classDatabase: null, preferEditor: false)
            ?? throw new InvalidOperationException("AssetFileInfo.Create returned null (AssetBundle type not registered)");
        var tpl = new AssetTypeTemplateField();
        tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
        var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);
        bf["m_Name"].AsString = bundleName;
        if (containerRootPathId is not null)
        {
            var arr = bf["m_Container"]["Array"];
            var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            el["first"].AsString = containerKey ?? "";
            el["second"]["asset"]["m_FileID"].AsInt = 0;
            el["second"]["asset"]["m_PathID"].AsLong = containerRootPathId.Value;
            arr.Children = new List<AssetTypeValueField> { el };
        }
        info.SetNewData(bf);
        file.Metadata.AssetInfos.Add(info);
    }

    /// <summary><c>m_Name</c> (the one field the VFS identity read touches), plus optionally
    /// <c>m_Container</c> in the {first, second.asset} shape prefab-root capture walks.</summary>
    private static TypeTreeType BuildAssetBundleType(bool withContainer = false)
    {
        var b = new TreeBuilder(ClassAssetBundle, "AssetBundle");
        b.Str("m_Name", 1, align: true);
        if (withContainer)
        {
            b.VectorOfStruct("m_Container", "pair", 1, e =>
            {
                e.Str("first", 4, align: true);
                e.Struct("AssetInfo", "second", 4);
                e.Value("int", "preloadIndex", 5, 4);
                e.Value("int", "preloadSize", 5, 4);
                e.Struct("PPtr<Object>", "asset", 5);
                e.Value("int", "m_FileID", 6, 4);
                e.Value("SInt64", "m_PathID", 6, 8);
            });
        }
        return b.Build();
    }

    /// <summary>What AssetsTools.NET's <c>TextureFile</c> requires, plus the optional fields the read/apply
    /// paths touch. m_PlatformBlob is omitted — absent ⇒ None, an unswizzled PC texture.</summary>
    private static TypeTreeType BuildTexture2DType()
    {
        var b = new TreeBuilder(ClassTexture2D, "Texture2D");
        b.Str("m_Name", 1, align: true);
        b.Value("int", "m_ForcedFallbackFormat", 1, 4);
        b.Value("bool", "m_DownscaleFallback", 1, 1, align: true);
        b.Value("int", "m_Width", 1, 4);
        b.Value("int", "m_Height", 1, 4);
        b.Value("int", "m_CompleteImageSize", 1, 4);
        b.Value("int", "m_TextureFormat", 1, 4);
        b.Value("int", "m_MipCount", 1, 4);
        b.Value("bool", "m_MipMap", 1, 1);
        b.Value("bool", "m_IsReadable", 1, 1);
        b.Value("bool", "m_ReadAllowed", 1, 1, align: true);
        b.Value("int", "m_ImageCount", 1, 4);
        b.Value("int", "m_TextureDimension", 1, 4);
        b.Struct("GLTextureSettings", "m_TextureSettings", 1);
        b.Value("int", "m_FilterMode", 2, 4);
        b.Value("int", "m_Aniso", 2, 4);
        b.Value("float", "m_MipBias", 2, 4);
        b.Value("int", "m_WrapU", 2, 4);
        b.Value("int", "m_WrapV", 2, 4);
        b.Value("int", "m_WrapW", 2, 4);
        b.Value("int", "m_LightmapFormat", 1, 4);
        b.Value("int", "m_ColorSpace", 1, 4);
        b.ByteArray("image data", 1, align: true);
        b.Struct("StreamingInfo", "m_StreamData", 1);
        b.Value("UInt64", "offset", 2, 8);
        b.Value("unsigned int", "size", 2, 4);
        b.Str("path", 2, align: true);
        return b.Build();
    }

    private static void WriteBundle(string outPath, byte[] assetsBytes, string? cabName = null)
    {
        var bun = new AssetBundleFile
        {
            Header = new AssetBundleHeader
            {
                Signature = "UnityFS",
                Version = 7,   // real GFL2 bundles are UnityFS v7
                GenerationVersion = "5.x.x",
                EngineVersion = UnityVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0, CompressedSize = 0, DecompressedSize = 0,
                    Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo, // 0x40
                },
            },
            BlockAndDirInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = default,
                BlockInfos = Array.Empty<AssetBundleBlockInfo>(),
                DirectoryInfos = new List<AssetBundleDirectoryInfo>(),
            },
        };

        var dir = new AssetBundleDirectoryInfo
        {
            Offset = 0, DecompressedSize = 0, Flags = 4, // 4 = SerializedFile flag
            Name = cabName ?? "CAB-gf2synthetic",
        };
        dir.SetNewData(assetsBytes);
        bun.BlockAndDirInfo.DirectoryInfos.Add(dir);

        using var w = new AssetsFileWriter(outPath);
        bun.Write(w);
    }

    /// <summary>Hand-authors Unity type trees from scratch, on a custom string buffer.</summary>
    private sealed class TreeBuilder
    {
        private readonly List<TypeTreeNode> _nodes = new();
        private readonly StringBuf _strings = new();
        private readonly int _typeId;

        public TreeBuilder(int typeId, string baseTypeName)
        {
            _typeId = typeId;
            Add(baseTypeName, "Base", 0, byteSize: -1, TypeTreeNodeFlags.None, meta: 0);
        }

        public TreeBuilder Value(string type, string name, byte level, int byteSize, bool align = false)
        {
            Add(type, name, level, byteSize, TypeTreeNodeFlags.None, meta: align ? 0x4000u : 0u);
            return this;
        }

        public TreeBuilder Struct(string type, string name, byte level, bool align = false)
        {
            Add(type, name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: align ? 0x4000u : 0u);
            return this;
        }

        // A Unity string (Array of char) — 4-byte aligned like real m_Name.
        public TreeBuilder Str(string name, byte level, bool align = true)
        {
            Add("string", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: align ? 0x4000u : 0u);
            Add("Array", "Array", (byte)(level + 1), byteSize: -1, TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 2), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("char", "data", (byte)(level + 2), byteSize: 1, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        // A TypelessData byte array: the FIELD node itself carries the Array flag, with an {int size, UInt8
        // data} child — the shape AssetsTools.NET detects as a ByteArray on the field. Cf. ByteArrayVector.
        public TreeBuilder ByteArray(string name, byte level, bool align = true)
        {
            Add("TypelessData", name, level, byteSize: -1,
                TypeTreeNodeFlags.Array, meta: align ? 0x4000u : 0u);
            Add("int", "size", (byte)(level + 1), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("UInt8", "data", (byte)(level + 1), byteSize: 1, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        // A vector<UInt8> (m_IndexBuffer). Unlike ByteArray above, the data hangs off the field's "Array"
        // CHILD, not the field node — the read path is f["Array"].AsByteArray.
        public TreeBuilder ByteArrayVector(string name, byte level, bool align = true)
        {
            Add("vector", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: align ? 0x4000u : 0u);
            Add("Array", "Array", (byte)(level + 1), byteSize: -1, TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 2), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("UInt8", "data", (byte)(level + 2), byteSize: 1, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        // A vector<T> of a SCALAR: the element node itself carries the value, so there is no child to add.
        public TreeBuilder VectorOfValue(string name, string elemType, int elemSize, byte level)
        {
            Add("vector", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0x4000u);
            Add("Array", "Array", (byte)(level + 1), byteSize: -1, TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 2), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add(elemType, "data", (byte)(level + 2), byteSize: elemSize, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        public TreeBuilder VectorOfString(string name, byte level)
        {
            Add("vector", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0x4000u);
            Add("Array", "Array", (byte)(level + 1), byteSize: -1,
                TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 2), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("string", "data", (byte)(level + 2), byteSize: -1,
                TypeTreeNodeFlags.None, meta: 0x4000u);
            Add("Array", "Array", (byte)(level + 3), byteSize: -1,
                TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 4), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("char", "data", (byte)(level + 4), byteSize: 1, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        // A vector<T> of a struct: its fields sit at level+3, under the Array's element node at level+2.
        public TreeBuilder VectorOfStruct(string name, string elemType, byte level, Action<TreeBuilder> element)
        {
            Add("vector", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0x4000u);
            Add("Array", "Array", (byte)(level + 1), byteSize: -1, TypeTreeNodeFlags.Array, meta: 0x4000u);
            Add("int", "size", (byte)(level + 2), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add(elemType, "data", (byte)(level + 2), byteSize: -1, TypeTreeNodeFlags.None, meta: 0);
            element(this);
            return this;
        }

        // {Vector3 m_Center, Vector3 m_Extent}: children at level+1, their x/y/z floats at level+2.
        public TreeBuilder Aabb(string name, byte level)
        {
            Add("AABB", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0);
            Vector3("m_Center", (byte)(level + 1));
            Vector3("m_Extent", (byte)(level + 1));
            return this;
        }

        private void Vector3(string name, byte level)
        {
            Add("Vector3f", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0);
            Add("float", "x", (byte)(level + 1), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("float", "y", (byte)(level + 1), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            Add("float", "z", (byte)(level + 1), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
        }

        public TreeBuilder Vector3f(string name, byte level)
        {
            Vector3(name, level);
            return this;
        }

        public TreeBuilder Quaternion(string name, byte level)
        {
            Add("Quaternionf", name, level, byteSize: -1, TypeTreeNodeFlags.None, meta: 0);
            foreach (var c in new[] { "x", "y", "z", "w" })
                Add("float", c, (byte)(level + 1), byteSize: 4, TypeTreeNodeFlags.None, meta: 0);
            return this;
        }

        private void Add(string type, string name, byte level, int byteSize, TypeTreeNodeFlags flags, uint meta)
        {
            _nodes.Add(new TypeTreeNode
            {
                Version = 1,
                Level = level,
                TypeFlags = flags,
                TypeStrOffset = _strings.Offset(type),
                NameStrOffset = _strings.Offset(name),
                ByteSize = byteSize,
                Index = 0,
                MetaFlags = meta,
                RefTypeHash = 0,
            });
        }

        public TypeTreeType Build()
        {
            for (int i = 0; i < _nodes.Count; i++) _nodes[i].Index = (uint)i;
            return new TypeTreeType
            {
                TypeId = _typeId,
                IsStrippedType = false,
                ScriptTypeIndex = ushort.MaxValue,
                Nodes = _nodes,
                StringBufferBytes = _strings.ToBytes(),
                TypeDependencies = Array.Empty<int>(),
                TypeHash = new Hash128(new byte[16]),
                ScriptIdHash = new Hash128(new byte[16]),
            };
        }

        private sealed class StringBuf
        {
            private readonly StringBuilder _s = new();
            private readonly Dictionary<string, uint> _off = new();
            public uint Offset(string name)
            {
                if (_off.TryGetValue(name, out var have)) return have;
                uint off = (uint)_s.Length;
                _s.Append(name).Append('\0');
                _off[name] = off;
                return off;
            }
            public byte[] ToBytes() => Encoding.ASCII.GetBytes(_s.ToString());
        }
    }
}
