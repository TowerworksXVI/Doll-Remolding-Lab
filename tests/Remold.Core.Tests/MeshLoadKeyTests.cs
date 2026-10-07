using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A recipe address draws the one Mesh its owning bundle files under the address's load key. A bundle can
/// ship a second Mesh of the same name ahead of it in the file, which a read by name takes; the part a
/// modder opens, previews, measures and builds is the copy the game loads, chosen where the bundle is read.
/// </summary>
public class MeshLoadKeyTests
{
    private const string Logical = "dddddddddddddddddddddddddddddd01.bundle";
    private static readonly string Phys = new('d', 32);
    private const string Slot = "c_DollA01_dorm_hair_lod0";
    private const string Address = "Assets/X/hair_standalone.mesh";
    private const string LoadKey = "a4c293e69872ebb4caf23c603b72fd31";

    // the loaded copy is a triangle; the same-named copy ahead of it in the file is a quad
    private static readonly float[] Loaded = { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
    private static readonly float[] Copy = { 0, 0, 0, 2, 0, 0, 0, 2, 0, 2, 2, 0 };

    /// <summary>One install: a bundle shipping two Meshes named <see cref="Slot"/>, the loaded one filed under
    /// <see cref="LoadKey"/>, and a catalog whose row for <see cref="Address"/> states
    /// <paramref name="catalogKey"/>.</summary>
    private static (GameVfs Vfs, long LoadedId) Install(TempGame g, string catalogKey = LoadKey)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        long loaded = SyntheticBundle.BuildOneMesh(Path.Combine(abw, Phys + ".bundle"), Slot, Loaded,
            new[] { 0, 1, 2 }, bundleName: Logical, containerKey: LoadKey,
            sameNamedFirst: (Copy, new[] { 0, 1, 2, 2, 1, 3 }));
        var shell = TestVfs.Create(g.Root, new[] { (Address, Logical) }, null, (Logical, Phys));
        var catalog = CatalogIndex.ForTest(new[] { (Address, Logical) }, null,
            new[] { (Logical, Phys + ".bundle") }, new[] { (Address, catalogKey) });
        return (GameVfs.ForTest(abw, "test", catalog, shell.Manifest), loaded);
    }

    private static SubjectModel Model() => new("Doll", "DollA01", SubjectSource.Prefab, new[]
    {
        new SubjectPart("hair", Slot, Address, Array.Empty<SubjectMaterial>(),
            RendererBundle: Logical, RendererPathId: 9),
    }, Skeleton: null, Problems: Array.Empty<string>());

    private static BuildEnv Env(GameVfs vfs, SubjectModel model, Func<string, byte[]?>? deobfuscate = null) => new(
        (c, s) => c == "Doll" && s == "DollA01" ? model : null,
        vfs.Catalog.ResolveAddress, deobfuscate ?? vfs.TryDeobfuscateLogical, "26109", null,
        LoadKeyOf: vfs.Catalog.LoadKeyForAddress);

    private static int Vertices(AssetsTools.NET.AssetTypeValueField? mesh) =>
        (int)mesh!["m_VertexData"]["m_VertexCount"].AsUInt;

    [Fact]
    public void A_load_key_reads_the_copy_it_files_where_a_read_by_name_takes_the_first()
    {
        using var g = new TempGame();
        var (vfs, loadedId) = Install(g);
        var bytes = vfs.TryDeobfuscateLogical(Logical)!;
        var reader = new BundleReader();

        Assert.Equal(4, Vertices(reader.GetMeshField(bytes, Slot)));
        Assert.Equal(3, Vertices(reader.GetMeshField(bytes, Slot, MeshSelector.ByLoadKey(LoadKey))));
        // the bundle's own load compares keys case-insensitively, so this does too
        Assert.Equal(loadedId, reader.MeshPathId(bytes, Slot, MeshSelector.ByLoadKey(LoadKey.ToUpperInvariant())));
    }

    /// <summary>The route the reported part refused on: New edit records the loaded copy of a mesh its bundle
    /// ships twice, instead of reporting the mesh missing.</summary>
    [Fact]
    public void A_new_edit_records_the_loaded_copy_of_a_mesh_its_bundle_ships_twice()
    {
        using var g = new TempGame();
        var (vfs, loadedId) = Install(g);

        var resolved = new LegacyProjectResolver(Env(vfs, Model()))
            .ResolvePart(new TargetPart { Subject = "Doll", Outfit = "DollA01", RendererSlot = Slot })!;

        Assert.Equal((Logical, loadedId), (resolved.Mesh.LogicalBundle, resolved.Mesh.PathId));
    }

    /// <summary>The build's tiers name the mesh off the catalog alone — no bundle is opened to resolve one — and
    /// the read the build then makes takes the loaded copy.</summary>
    [Fact]
    public void The_build_resolves_tiers_without_reading_and_reads_the_loaded_copy()
    {
        using var g = new TempGame();
        var (vfs, _) = Install(g);
        var model = Model();
        int reads = 0;
        var env = Env(vfs, model, logical => { reads++; return vfs.TryDeobfuscateLogical(logical); });

        var tier = Assert.Single(new SubjectPoolProbe(env).Tiers(model.Parts[0]));

        Assert.Equal(0, reads);
        Assert.Equal((Logical, MeshSelector.ByLoadKey(LoadKey)), (tier.BundleId, tier.Mesh));
        var bytes = vfs.TryDeobfuscateLogical(Logical)!;
        Assert.Equal(BufferHash.Compute(bytes, Slot, MeshSelector.ByLoadKey(LoadKey)).Ib,
            BufferHash.Compute(bytes, Slot, tier.Mesh).Ib);
        Assert.NotEqual(BufferHash.Compute(bytes, Slot).Ib, BufferHash.Compute(bytes, Slot, tier.Mesh).Ib);
    }

    /// <summary>The app's own reads: the preview's vertex count is the loaded copy's.</summary>
    [Fact]
    public void The_preview_counts_the_loaded_copy()
    {
        using var g = new TempGame();
        var (vfs, _) = Install(g);
        var service = new EditPreviewService(() => vfs, () => vfs.Catalog, vfs.TryDeobfuscateLogical,
            new ThumbnailCache(g.At("thumbs")));

        int? count = service.GameMeshVertexCount(new RecipePart("hair", Slot, Address,
            Array.Empty<RecipeTierSlot>()));

        Assert.Equal(3, count);
    }

    /// <summary>A load key the bundle files no mesh under selects nothing: every read comes back empty and
    /// New edit's mesh stays unidentified — which it refuses out loud — rather than any read falling back to
    /// the first mesh of the name.</summary>
    [Fact]
    public void A_key_that_files_no_mesh_reads_nothing_and_never_falls_back_to_the_name()
    {
        using var g = new TempGame();
        var (vfs, _) = Install(g, catalogKey: "a key this bundle files nothing under");
        var bytes = vfs.TryDeobfuscateLogical(Logical)!;
        var (_, which) = vfs.Catalog.TierMesh(Address, null, 0);

        Assert.Null(new BundleReader().GetMeshField(bytes, Slot, which));
        var resolved = new LegacyProjectResolver(Env(vfs, Model()))
            .ResolvePart(new TargetPart { Subject = "Doll", Outfit = "DollA01", RendererSlot = Slot })!;
        Assert.Equal(0L, resolved.Mesh.PathId);
    }

    /// <summary>A saved sharing row states the load key each address resolved with, so a catalog that points
    /// an address at another copy inside an unchanged bundle re-measures the row instead of reusing it.</summary>
    [Fact]
    public void A_sharing_row_is_not_current_once_an_address_load_key_changes()
    {
        var rows = new[] { (Address, Logical) };
        var before = CatalogIndex.ForTest(rows, loadKeyRows: new[] { (Address, LoadKey) });
        var after = CatalogIndex.ForTest(rows, loadKeyRows: new[] { (Address, "another key") });
        string recorded = PartAddressResolutions.Of(new[] { (Address, Logical, (string?)LoadKey) });

        Assert.True(PartAddressResolutions.StillCurrent(PartAddressResolutions.CurrentKeys(before), recorded));
        Assert.False(PartAddressResolutions.StillCurrent(PartAddressResolutions.CurrentKeys(after), recorded));
    }
}
