using System.Linq;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Texture identity is (subject, bundle, object name), not object name alone: the game carries
/// same-named textures in different bundles as distinct assets, so a second outfit's identically-named map
/// gets its OWN workspace file — and so does a second SUBJECT materializing the very same asset.</summary>
public class TextureBundleIdentityTests
{
    private const string BundleA = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6.bundle";
    private const string BundleB = "0f1e2d3c4b5a69788796a5b4c3d2e1f0.bundle";
    private const string Root = "/proj";
    private const string TexName = "c_body_d";
    private const string Char = "Vesna";
    private const string Stem = "VesnaSSR01";
    private const string OtherStem = "VesnaSSR02";

    [Fact]
    public void TextureWorkspacePath_IsDistinctPerBundle_ForTheSameObjectName()
    {
        Assert.NotEqual(
            Materializer.TextureWorkspacePath(Root, Char, Stem, BundleA, TexName),
            Materializer.TextureWorkspacePath(Root, Char, Stem, BundleB, TexName));
        // ...and the originals baseline is bundle-scoped too, so one doesn't overwrite the other's pristine copy.
        Assert.NotEqual(
            Materializer.TextureOriginalPath(Root, Char, Stem, BundleA, TexName),
            Materializer.TextureOriginalPath(Root, Char, Stem, BundleB, TexName));
    }

    [Fact]
    public void TextureWorkspacePath_IsDistinctPerSubject_ForTheSameBundleAndObjectName()
    {
        // the subject half of the identity: one asset materialized by two outfits is two files, so each
        // outfit's edit and revert are its own
        Assert.NotEqual(
            Materializer.TextureWorkspacePath(Root, Char, Stem, BundleA, TexName),
            Materializer.TextureWorkspacePath(Root, Char, OtherStem, BundleA, TexName));
        Assert.NotEqual(
            Materializer.TextureOriginalPath(Root, Char, Stem, BundleA, TexName),
            Materializer.TextureOriginalPath(Root, Char, OtherStem, BundleA, TexName));
    }

    [Fact]
    public void IdentityIsKeyedByBundle_SoASecondBundlesSameNameIsNotAlreadyPresent()
    {
        var proj = new ModProject { RootDir = Root };
        AddMaterializedTexture(proj, BundleA, Stem);   // outfit A materializes its body_d from bundle A

        // outfit B binds an identically-named map from a DIFFERENT bundle — it must NOT read as already present
        Assert.True(Materializer.IsTextureMaterialized(proj, Char, Stem, BundleA, TexName));
        Assert.False(Materializer.IsTextureMaterialized(proj, Char, Stem, BundleB, TexName));

        var a = Materializer.TextureTarget(proj, Char, Stem, BundleA, TexName);
        var b = Materializer.TextureTarget(proj, Char, Stem, BundleB, TexName);
        Assert.NotNull(a);
        Assert.Null(b);   // B has no target yet — the name-only lookup would have wrongly returned A's
    }

    [Fact]
    public void IdentityIsKeyedBySubject_SoASecondSubjectLandsItsOwnTarget()
    {
        var proj = new ModProject { RootDir = Root };
        AddMaterializedTexture(proj, BundleA, Stem);

        // the SAME bundle+object under a DIFFERENT subject is not already present — it is its own target
        Assert.False(Materializer.IsTextureMaterialized(proj, Char, OtherStem, BundleA, TexName));
        Assert.Null(Materializer.TextureTarget(proj, Char, OtherStem, BundleA, TexName));

        AddMaterializedTexture(proj, BundleA, OtherStem);
        Assert.Equal(2, proj.Targets.Count(t => t.AssetType == "Texture2D"));
        Assert.NotEqual(
            Materializer.TextureTarget(proj, Char, Stem, BundleA, TexName)!.ReplaceFile,
            Materializer.TextureTarget(proj, Char, OtherStem, BundleA, TexName)!.ReplaceFile);
    }

    /// <summary>One texture materialize landing, in the exact shape
    /// <see cref="Materializer.CommitTexture"/> commits.</summary>
    private static void AddMaterializedTexture(ModProject proj, string bundle, string stem)
    {
        // /proj/textures/<name>.<bundle>.<subject>.png
        var ws = Materializer.TextureWorkspacePath(Root, Char, stem, bundle, TexName);
        var report = new ExportReport { OutputDir = Root };
        report.Files.Add(new ExportedFile("texture", TexName, ws, true, null,
            Bundle: bundle,
            Users: new[] { "c_X_slg_body_lod0" }, Source: Materializer.MaterialSource));
        ProjectBuilder.AddExport(proj, report, Root, Char, stem);
    }
}
