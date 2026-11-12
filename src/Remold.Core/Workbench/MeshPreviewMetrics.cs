using System;
using System.Globalization;
using Remold.Core.Text;

namespace Remold.Core.Workbench;

/// <summary>Pure formatting for the part inspector's vertex count and edited-topology delta, in the UI language with
/// the numbers grouped the same way in every language.</summary>
public static class MeshPreviewMetrics
{
    public static string VertexCountLine(int? originalVertexCount, int vertexCount)
    {
        if (originalVertexCount < 0) throw new ArgumentOutOfRangeException(nameof(originalVertexCount));
        if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        string count = vertexCount.ToString("N0", CultureInfo.InvariantCulture);
        if (originalVertexCount is null)
            return Catalog.Plural(vertexCount, CoreStrings.MeshVertexCount_One(count),
                CoreStrings.MeshVertexCount_Other(count));

        int delta = vertexCount - originalVertexCount.Value;
        string signed = delta >= 0
            ? "+" + delta.ToString("N0", CultureInfo.InvariantCulture)
            : delta.ToString("N0", CultureInfo.InvariantCulture);
        return Catalog.Plural(vertexCount, CoreStrings.MeshVertexCountChange_One(count, signed),
            CoreStrings.MeshVertexCountChange_Other(count, signed));
    }
}
