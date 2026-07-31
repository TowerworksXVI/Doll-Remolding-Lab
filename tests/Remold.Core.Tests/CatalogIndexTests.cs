using Remold.Core.Bundles;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <c>CatalogIndex.KeyForAddress</c> — the game's address→primaryKey derivation (MD5 of the
/// UTF-16-LE address, dash-hex).
/// </summary>
public class CatalogIndexTests
{
    [Fact]
    public void KeyForAddress_DerivesTheCatalogKey()
    {
        Assert.Equal("7B-7E-73-2A-FE-89-EB-6C-3A-E3-F9-D0-84-82-6C-13",
            CatalogIndex.KeyForAddress(
                "Assets/ArtsResource/Player/Vesna/VesnaSSR01/Models/c_VesnaSSR01_slg_face_lod0.mesh"));
    }

    [Fact]
    public void KeyForAddress_AppendsFilenameForSceneAddresses()
    {
        var key = CatalogIndex.KeyForAddress("Assets/Scenes/Battle/Map01.unity");
        Assert.EndsWith("\\Map01.unity", key);
        Assert.Equal(16 * 3 - 1, key.IndexOf('\\'));   // dash-hex digest, then the scene suffix
    }
}
