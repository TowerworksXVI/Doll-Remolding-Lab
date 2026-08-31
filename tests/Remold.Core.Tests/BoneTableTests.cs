using System.Collections.Generic;
using Remold.Core.Skeleton;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The bone-name-hash rule and root-anchored path derivation. The hash fixtures are pinned body-bone pairs
/// (constants, not game data), so the CRC32 rule is asserted independently of any live bundle. The corpus
/// scan itself needs a real game dir and is not exercised here.
/// </summary>
public class BoneTableTests
{
    [Theory]
    [InlineData("root/Root_M", 0x20c78f46u)]
    [InlineData("root/Root_M/Spine1_M", 0xb0e35784u)]
    [InlineData("root/Root_M/Spine1_M/Spine2_M", 0xd7f8a476u)]
    [InlineData("root/Root_M/Spine1_M/Spine2_M/Chest_M", 0x81115162u)]
    [InlineData("root/Root_M/Spine1_M/Spine2_M/Chest_M/Neck_M", 0x4bd8ec42u)]
    [InlineData("root/Root_M/Spine1_M/Spine2_M/Chest_M/Neck_M/Head_M", 0xa8c83d16u)]
    [InlineData("root/Root_M/Hip_R", 0xbc42fa3fu)]
    [InlineData("root/Root_M/Hip_L", 0x464dc75cu)]
    [InlineData("root/Root_M/foot_R_Ctrl3_Jnt_skin", 0x07c1e6edu)]   // the 7-hex-digit one (leading zero)
    public void Hash_MatchesPinnedBoneHashes(string path, uint expected)
    {
        Assert.Equal(expected, BoneTable.Hash(path));
    }

    [Theory]
    [InlineData("Prefab/root/Root_M/Toes_L/Shoes01_L", "Shoes01_L")]
    [InlineData("Prefab/root/Root_M/Toes_L/Shoes01_L/Shoes02_L", "Shoes01_L/Shoes02_L")]
    [InlineData("root/Root_M/Spine1_M", "root/Root_M/Spine1_M")]
    public void MatchingSuffix_ReturnsTheFirstSuffixWhoseHashMatches(string fullPath, string suffix)
    {
        Assert.Equal(suffix, BoneTable.MatchingSuffix(BoneTable.Hash(suffix), fullPath));
    }

    [Fact]
    public void MatchingSuffix_ReturnsNullWhenThePathDoesNotNameTheHash()
    {
        Assert.Null(BoneTable.MatchingSuffix(BoneTable.Hash("Shoes01_R"), "Prefab/root/Root_M/Toes_L/Shoes01_L"));
        Assert.Null(BoneTable.MatchingSuffix(BoneTable.Hash("Shoes01_R"), null));
    }

    [Fact]
    public void MatchingLeaf_ReturnsNullWhenTheMatchingSuffixEndsInASeparator()
    {
        const string suffix = "Hair01_L/";
        Assert.Equal(suffix, BoneTable.MatchingSuffix(BoneTable.Hash(suffix), "Prefab/root/" + suffix));
        Assert.Null(BoneTable.MatchingLeaf(BoneTable.Hash(suffix), "Prefab/root/" + suffix));
    }

    [Theory]
    [InlineData("Root_M/Spring01", 0x05f0c65fu)]
    [InlineData("Root_M/Spring01/Spring02/Spring03/Spring04/Spring05/Spring06", 0x68bd228fu)]
    [InlineData("Root_M/SpringA01/SpringA02/SpringA03/SpringA04/SpringA05/SpringA06/SpringA07", 0x02ae5487u)]
    [InlineData("Root_M/SpringB01", 0x3663de6bu)]
    public void Hash_MatchesTheSpringChainMarkers(string path, uint expected)
    {
        // The spring-chain set is these paths' hashes; pinning the pairs here keeps the constant table
        // derivable from the hash rule alone.
        Assert.Equal(expected, BoneTable.Hash(path));
        Assert.True(BoneTable.HasSpringChain(new[] { expected }));
    }

    [Fact]
    public void HasSpringChain_IgnoresEverythingOutsideTheChains()
    {
        // 0xfc90d5f9 = "Root_M/spring": a mechanically-named gun part, not a simulated chain.
        Assert.False(BoneTable.HasSpringChain(new uint[] { 0x20c78f46, 0xb0e35784, 0xfc90d5f9, 0 }));
        Assert.False(BoneTable.HasSpringChain(System.Array.Empty<uint>()));
    }

    [Fact]
    public void CanonicalBonePath_CharacterRigAnchorsOnRoot()
    {
        // a real chain: the skin-mesh root wraps "root", which holds the skeleton
        var chain = new[] { "c_TalviSSR01_slg_body", "root", "Root_M", "Spine1_M" };
        Assert.Equal("root/Root_M/Spine1_M", BoneTable.CanonicalBonePath(chain));
    }

    [Fact]
    public void CanonicalBonePath_AnchorsOnRootRegardlessOfWrapperDepth()
    {
        // deeper nesting under prefab/model wrappers must not change the hashed path
        var shallow = new[] { "model", "root", "Root_M", "Hip_L" };
        var deep = new[] { "Prefab", "Model", "Armature", "root", "Root_M", "Hip_L" };
        Assert.Equal("root/Root_M/Hip_L", BoneTable.CanonicalBonePath(shallow));
        Assert.Equal(BoneTable.CanonicalBonePath(shallow), BoneTable.CanonicalBonePath(deep));
        Assert.Equal(0x464dc75cu, BoneTable.Hash(BoneTable.CanonicalBonePath(deep)!));
    }

    [Fact]
    public void CanonicalBonePath_PropRigAnchorsOnRootM_WhenNoRootNode()
    {
        // a skinned prop (Solvig's beverage) has no "root" wrapper — its rig starts at Root_M
        var prop = new[] { "SolvigSSR01@c_SolvigSSR01_CommandCenterBack_Beverage1", "Root_M", "Beverage1", "ring" };
        Assert.Equal("Root_M/Beverage1/ring", BoneTable.CanonicalBonePath(prop));
        // a character chain (root present) must still prefer "root", not its inner Root_M
        var character = new[] { "c_X_slg_body", "root", "Root_M", "Hip_L" };
        Assert.Equal("root/Root_M/Hip_L", BoneTable.CanonicalBonePath(character));
    }

    [Fact]
    public void CanonicalBonePath_NullForNonSkeletonTransforms()
    {
        Assert.Null(BoneTable.CanonicalBonePath(new[] { "scene", "Camera", "lens" }));   // no entry node
        Assert.Null(BoneTable.CanonicalBonePath(new[] { "wrapper", "root" }));            // "root" is the leaf, no Root_M
    }

    [Fact]
    public void Resolved_CountsOnlyHashesInTheTable()
    {
        var table = new BoneTable
        {
            HashToPath = new Dictionary<uint, string>
            {
                [0x20c78f46u] = "root/Root_M",
                [0xb0e35784u] = "root/Root_M/Spine1_M",
            },
        };
        Assert.Equal("root/Root_M/Spine1_M", table.Path(0xb0e35784u));
        Assert.Null(table.Path(0xdeadbeefu));
        Assert.Equal(2, table.Resolved(new[] { 0x20c78f46u, 0xb0e35784u, 0xdeadbeefu }));
    }

    [Fact]
    public void HasUnsupportedRig_IgnoresAnOrdinaryBoneTable()
    {
        // hashes a supported subject's bone table actually carries
        Assert.False(BoneTable.HasUnsupportedRig(new[] { 0x5c6bc13du, 0x7a6134d1u, 0xf3ff2a11u, 0x3b4efd1fu }));
        Assert.False(BoneTable.HasUnsupportedRig(System.Array.Empty<uint>()));
    }

    [Fact]
    public void HasUnsupportedRig_OneListedBoneAnswersForTheTable()
    {
        Assert.True(BoneTable.HasUnsupportedRig(new uint[] { 0x5c6bc13d, 0x90c776f9, 0x7a6134d1 }));
    }
}
