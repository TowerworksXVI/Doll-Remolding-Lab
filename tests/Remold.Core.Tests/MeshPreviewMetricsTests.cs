using Remold.Core.Text;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

[Collection("UiCulture")]
public class MeshPreviewMetricsTests
{
    [Theory]
    [InlineData(null, 13684, "13,684 vertices")]
    [InlineData(12480, 13684, "13,684 vertices (+1,204 vs original)")]
    [InlineData(12480, 12000, "12,000 vertices (-480 vs original)")]
    [InlineData(12480, 12480, "12,480 vertices (+0 vs original)")]
    [InlineData(null, 1, "1 vertex")]
    [InlineData(4, 1, "1 vertex (-3 vs original)")]
    [InlineData(null, 0, "0 vertices")]
    public void VertexCountLine_FormatsNullableOriginalAndEditedDelta(int? original, int current, string expected) =>
        Assert.Equal(expected, MeshPreviewMetrics.VertexCountLine(original, current));

    [Theory]
    [InlineData(null, 2495, "2,495个顶点")]
    [InlineData(null, 1, "1个顶点")]
    [InlineData(4, 1, "1个顶点（相比原始-3）")]
    [InlineData(2495, 2495, "2,495个顶点（相比原始+0）")]
    [InlineData(12480, 12000, "12,000个顶点（相比原始-480）")]
    public void In_chinese_the_vertex_count_reads_in_chinese_with_the_same_number_grouping(int? original,
        int current, string expected)
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            Assert.Equal(expected, MeshPreviewMetrics.VertexCountLine(original, current));
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}
