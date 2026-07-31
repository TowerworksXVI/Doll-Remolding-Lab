using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The published package's name: what the built folder and its zip are called under the
/// published root.</summary>
public class ModNamingTests
{
    [Fact]
    public void A_package_reads_as_name_author_and_version()
    {
        Assert.Equal("vesna-newbody_anonymous_v1_0",
            ModNaming.PackageFolderName("Vesna NewBody", "Anonymous", "1.0"));
    }

    [Fact]
    public void A_blank_author_drops_its_segment()
    {
        Assert.Equal("vesna-newbody_v1_0", ModNaming.PackageFolderName("Vesna NewBody", null, "1.0"));
        Assert.Equal("vesna-newbody_v1_0", ModNaming.PackageFolderName("Vesna NewBody", "   ", "1.0"));
    }

    [Fact]
    public void A_version_keeps_only_letters_digits_and_its_dots_as_underscores()
    {
        Assert.Equal("mod_v2_1_3b", ModNaming.PackageFolderName("mod", null, "2.1.3b"));
        Assert.Equal("mod_v1_0", ModNaming.PackageFolderName("mod", null, " 1.0! "));
    }

    [Fact]
    public void A_version_that_sanitizes_to_nothing_drops_its_segment()
    {
        Assert.Equal("mod_tester", ModNaming.PackageFolderName("mod", "Tester", "--"));
        Assert.Equal("mod_tester", ModNaming.PackageFolderName("mod", "Tester", null));
    }

    [Fact]
    public void Names_and_authors_are_slugged_into_one_filesystem_token()
    {
        Assert.Equal("karst-jacket_test-author_v1_0",
            ModNaming.PackageFolderName("Karst: Jacket!", "Test  Author", "1.0"));
        // a name with nothing usable still names something
        Assert.Equal("mod_v1_0", ModNaming.PackageFolderName("***", "", "1.0"));
    }

    [Fact]
    public void A_typed_v_prefix_is_not_doubled()
    {
        Assert.Equal("mod_v1_0", ModNaming.PackageFolderName("mod", null, "v1.0"));
        Assert.Equal("mod_v2_1", ModNaming.PackageFolderName("mod", null, "V2.1"));
        // only a v-before-digit is a prefix; a version that is a word keeps its letters
        Assert.Equal("mod_vvista", ModNaming.PackageFolderName("mod", null, "vista"));
    }

    [Fact]
    public void A_projects_own_identity_names_its_package()
    {
        var info = new ProjectInfo { Name = "Vesna NewBody", Author = "TestAuthor", Version = "1.0" };
        Assert.Equal("vesna-newbody_testauthor_v1_0", ModNaming.PackageFolderName(info));
    }
}
