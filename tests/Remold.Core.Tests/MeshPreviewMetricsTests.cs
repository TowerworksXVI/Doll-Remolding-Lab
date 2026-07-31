using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

public class MeshPreviewMetricsTests
{
    [Theory]
    [InlineData(null, 13684, "13,684 vertices")]
    [InlineData(12480, 13684, "13,684 vertices (+1,204 vs original)")]
    [InlineData(12480, 12000, "12,000 vertices (-480 vs original)")]
    [InlineData(12480, 12480, "12,480 vertices (+0 vs original)")]
    public void VertexCountLine_FormatsNullableOriginalAndEditedDelta(int? original, int current, string expected) =>
        Assert.Equal(expected, MeshPreviewMetrics.VertexCountLine(original, current));
}
