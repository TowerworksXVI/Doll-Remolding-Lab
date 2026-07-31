using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The pure submesh-assignment step: ordered texture groups (one per renderer material slot) onto a mesh's
/// submeshes. The renderer PPtr follow that produces the groups needs live bundles.
/// </summary>
public class PartTextureResolverTests
{
    private static SubmeshMaps G(string b) => new(b, b + "_n");

    [Fact]
    public void AssignGroups_IndexAligned_WhenGroupPerSubmesh()
    {
        var groups = new[] { G("a"), G("b"), G("c") };
        var r = PartTextureResolver.AssignGroups(groups, 3);
        Assert.Equal("a", r[0].BaseColor);
        Assert.Equal("b", r[1].BaseColor);
        Assert.Equal("c", r[2].BaseColor);
    }

    [Fact]
    public void AssignGroups_SingleGroup_FillsEverySubmesh()
    {
        var r = PartTextureResolver.AssignGroups(new[] { G("skin") }, 4);
        Assert.All(r, sm => Assert.Equal("skin", sm.BaseColor));
        Assert.Equal(4, r.Length);
    }

    [Fact]
    public void AssignGroups_MultiGroup_RepeatsLastForExtraSubmeshes()
    {
        // [a, b]; a 3-submesh part becomes a, b, b (extra submeshes repeat the last group)
        var r = PartTextureResolver.AssignGroups(new[] { G("a"), G("b") }, 3);
        Assert.Equal("a", r[0].BaseColor);
        Assert.Equal("b", r[1].BaseColor);
        Assert.Equal("b", r[2].BaseColor);
    }

    [Fact]
    public void AssignGroups_TruncatesSurplusGroups()
    {
        var r = PartTextureResolver.AssignGroups(new[] { G("a"), G("b") }, 1);
        Assert.Single(r);
        Assert.Equal("a", r[0].BaseColor);
    }

    [Fact]
    public void AssignGroups_NoGroups_YieldsEmptyAssignments()
    {
        var r = PartTextureResolver.AssignGroups(System.Array.Empty<SubmeshMaps>(), 2);
        Assert.Equal(2, r.Length);
        Assert.Null(r[0].BaseColor);
        Assert.Null(r[1].Normal);
    }

    [Fact]
    public void AssignGroups_ClampsSubmeshCountToAtLeastOne()
    {
        var r = PartTextureResolver.AssignGroups(new[] { G("a") }, 0);
        Assert.Single(r);
    }
}
