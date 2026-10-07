using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Remold.Core.Mesh;

namespace Remold.Core.Migoto;

/// <summary>
/// The <c>channels</c> entry of a stream folder's <c>meta.json</c>, positional as <c>m_Channels</c> is.
/// It describes streams 0 and 1 as the folder holds them. It does NOT describe the skin stream of a mesh
/// dump, which is written widened while the table is the mesh's own; a compiled donor's table is read
/// after its widening. A draw reads a bound vertex buffer
/// through the input layout of the mesh the game issued it for, so a stream built for one mesh is only
/// readable at another mesh's draw when their tables agree on that stream — this is what lets a reader
/// of two folders ask.
/// </summary>
static class MetaChannels
{
    /// <summary>The <c>"channels": [...]</c> member, without a trailing separator.</summary>
    public static void Append(StringBuilder meta, IReadOnlyList<UnityMesh.ChannelDef> channels)
    {
        meta.Append("  \"channels\": [");
        for (int ci = 0; ci < channels.Count; ci++)
            meta.Append(ci > 0 ? ", " : "")
                .Append($"{{ \"stream\": {channels[ci].Stream}, \"offset\": {channels[ci].Offset}, ")
                .Append($"\"format\": {channels[ci].Format}, \"dimension\": {channels[ci].Dimension} }}");
        meta.Append(']');
    }

    /// <summary>The table <paramref name="dir"/>'s <c>meta.json</c> records, or null when it records
    /// none.</summary>
    public static IReadOnlyList<UnityMesh.ChannelDef>? Read(string dir)
    {
        string path = Path.Combine(dir, "meta.json");
        if (!File.Exists(path)) return null;
        using var meta = JsonDocument.Parse(File.ReadAllText(path));
        if (!meta.RootElement.TryGetProperty("channels", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        var channels = new List<UnityMesh.ChannelDef>();
        foreach (var c in arr.EnumerateArray())
            channels.Add(new UnityMesh.ChannelDef(c.GetProperty("stream").GetInt32(),
                c.GetProperty("offset").GetInt32(), c.GetProperty("format").GetInt32(),
                c.GetProperty("dimension").GetInt32()));
        return channels;
    }
}
