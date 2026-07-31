using Remold.Core.Project;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The ONE bundle-scoped texture naming rule: every producer folds the source bundle AND the
/// owning subject into the workspace filename, so two same-named textures from DIFFERENT bundles never
/// collide on one file — and one texture materialized under two SUBJECTS is two files, one edit each.</summary>
public class TextureNamingTests
{
    private static readonly string Subject = ModNaming.SubjectSlug("Karst", "KarstSSR01");

    [Fact]
    public void BundleScopedName_FoldsBundleIntoTheFilename_ObjectNameLeads()
    {
        Assert.Equal($"c_KarstSSR01_slg_body_d.aabb.{Subject}.png",
            TextureExport.BundleScopedName("aabb", "c_KarstSSR01_slg_body_d", Subject));
    }

    [Fact]
    public void BundleScopedName_SameNameDifferentBundles_AreDistinctFiles()
    {
        // the exact cross-bundle collision this fixes: one texture name, two bundles → two files, never one.
        var a = TextureExport.BundleScopedName("bundleA", "body_d", Subject);
        var b = TextureExport.BundleScopedName("bundleB", "body_d", Subject);
        Assert.NotEqual(a, b);
        Assert.EndsWith(".png", a);
        Assert.EndsWith(".png", b);
    }

    [Fact]
    public void BundleScopedName_SameTextureUnderTwoSubjects_AreDistinctFiles()
    {
        // the subject half of the rule: one bundle, one texture name, two outfits → two files, so an edit
        // made on one outfit is that outfit's alone.
        var a = TextureExport.BundleScopedName("aabb", "body_d", ModNaming.SubjectSlug("Karst", "KarstSSR01"));
        var b = TextureExport.BundleScopedName("aabb", "body_d", ModNaming.SubjectSlug("Karst", "KarstSSR02"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BundleScopedName_SanitizesTheBundleSegment_AndIsStable()
    {
        // Any path-invalid char in the bundle segment is neutralised, so the name is always a valid
        // filename — and the same inputs always produce the same name, which dedup depends on.
        var n1 = TextureExport.BundleScopedName("a/b:c", "tex", Subject);
        var n2 = TextureExport.BundleScopedName("a/b:c", "tex", Subject);
        Assert.Equal(n1, n2);
        Assert.DoesNotContain('/', n1);
        Assert.DoesNotContain(':', n1);
        Assert.StartsWith("tex.", n1);
    }
}
