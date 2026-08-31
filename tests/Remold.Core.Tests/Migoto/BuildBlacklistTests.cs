using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Export;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The build-phase content policy (<see cref="BuildBlacklist"/>) and its enforcement in
/// <see cref="ModBuilder"/>. Load-bearing: a failure here means an enforcement point was removed, and
/// that removal is the bug. Each route test blocks at a DIFFERENT funnel, so no funnel can be dropped
/// without a red test.
/// </summary>
public class BuildBlacklistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-bl-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public BuildBlacklistTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Theory]
    [InlineData("c_Helena_body_lod0", true)]
    [InlineData("C_HELENA_BODY", true)]      // case-insensitive
    [InlineData("Helena", true)]
    [InlineData("Melanie", true)]
    [InlineData("c_Helen_body", false)]      // a different character, not a prefix of this one
    [InlineData("c_Vesna01_body_lod0", false)]
    [InlineData(null, false)]
    public void IsBlocked_MatchesTheNameFragmentAnywhere(string? name, bool expected) =>
        Assert.Equal(expected, BuildBlacklist.IsBlocked(name));

    // Every check fires before the build reads a bundle, so these worlds carry no bytes.

    private static BuildEnv EnvFor(SubjectModel model) => new BuildEnv(
        (c, s) => string.Equals(c, model.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s, model.Stem, StringComparison.OrdinalIgnoreCase) ? model : null,
        _ => "bundle0", _ => null, CatalogVersion: "12345", AppVersion: "test-1.0").Exact();

    private ModProject NewProject(string character, string stem, string name)
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = character, Outfit = stem });
        return p;
    }

    [Fact]
    public void A_blocked_subject_refuses_the_build()
    {
        var model = new SubjectModel("Helena", "HelenaNPC01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_Helena_body_lod0", "addr_body", Array.Empty<SubjectMaterial>()),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var p = NewProject("Helena", "HelenaNPC01", "Blocked Subject");
        p.SetHidden("Helena", "HelenaNPC01", "c_Helena_body_lod0", true);

        var ex = Assert.Throws<BlockedAssetException>(() => ReleasedBuild.Build(p, EnvFor(model), _out));

        Assert.Contains("not a supported asset", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_out));   // no folder, no zip, no work dirs
    }

    [Fact]
    public void A_blocked_name_on_a_sibling_tier_refuses_a_clean_subjects_build()
    {
        // subject and lod0 slot are clean; only the lod1 tier names the blocked model
        var model = new SubjectModel("Ilse", "IlseSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_ilse01_body_lod0", "addr_body", Array.Empty<SubjectMaterial>(),
                SiblingTiers: new[] { new RecipeTierSlot("c_Helena_body_lod1", "addr_blocked_l1") }),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var p = NewProject("Ilse", "IlseSSR01", "Blocked Tier");
        p.SetHidden("Ilse", "IlseSSR01", "c_ilse01_body_lod0", true);

        var ex = Assert.Throws<BlockedAssetException>(() => ReleasedBuild.Build(p, EnvFor(model), _out));

        Assert.Contains("c_Helena_body_lod1", ex.Message);
        Assert.Contains("not a supported asset", ex.Message);
    }

    [Fact]
    public void A_blocked_stock_texture_refuses_a_clean_meshs_retexture()
    {
        // the mesh is clean; only the stock texture the retexture would override is blocked
        var materials = new[]
        {
            new SubjectMaterial("m_body", 1, "cab-body",
                new[] { new SubjectMap("_BaseMap", "c_Helena_body_d", "bundleT") }),
        };
        var model = new SubjectModel("Ilse", "IlseSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_ilse01_body_lod0", "addr_body", materials),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var p = NewProject("Ilse", "IlseSSR01", "Blocked Texture");
        FlatDds.Write(Path.Combine(_proj, "skin.dds"), (1, 2, 3, 255));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundleT", ObjectName = "c_Helena_body_d",
            ReplaceFile = "skin.dds", Users = new List<string> { "c_ilse01_body_lod0" },
            SubjectCharacter = "Ilse", SubjectOutfit = "IlseSSR01",
        });

        var ex = Assert.Throws<BlockedAssetException>(() => ReleasedBuild.Build(p, EnvFor(model), _out));

        Assert.Contains("c_Helena_body_d", ex.Message);
        Assert.Contains("not a supported asset", ex.Message);
    }
}
