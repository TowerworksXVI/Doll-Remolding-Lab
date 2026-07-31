using System;
using System.Globalization;

namespace Remold.Core.Workbench;

/// <summary>Pure formatting for the part inspector's vertex count and edited-topology delta.</summary>
public static class MeshPreviewMetrics
{
    public static string VertexCountLine(int? originalVertexCount, int vertexCount)
    {
        if (originalVertexCount < 0) throw new ArgumentOutOfRangeException(nameof(originalVertexCount));
        if (vertexCount < 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (originalVertexCount is null)
            return $"{vertexCount.ToString("N0", CultureInfo.InvariantCulture)} vertices";

        int delta = vertexCount - originalVertexCount.Value;
        string signed = delta >= 0
            ? "+" + delta.ToString("N0", CultureInfo.InvariantCulture)
            : delta.ToString("N0", CultureInfo.InvariantCulture);
        return $"{vertexCount.ToString("N0", CultureInfo.InvariantCulture)} vertices " +
               $"({signed} vs original)";
    }
}
