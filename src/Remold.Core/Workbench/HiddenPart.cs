using System;
using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Mesh;

namespace Remold.Core.Workbench;

/// <summary>Where the Blender export shows a part the game starts shrunk out of sight
/// (<see cref="BindReference.Hidden"/>): centred at the origin at full size, its own joints moved with it.
///
/// <para>The centre <c>c</c> is the middle of the bounding box of the part's STOCK lod0 vertices in the
/// space the export writes them in, after the part's uprighting <c>U</c>. It is never taken from an edit, so
/// every tier of the part and every edit of it share one shift. The display placement is
/// <c>Q = U · T(−c)</c> (row-vector): a vertex <c>v</c> of the part's own mesh space shows at
/// <c>v · Q</c>. The export and the build's probe compute <c>c</c> here, from the same floats in the same
/// order, so both read one value. The export records it beside the file it writes; the prepare step reads
/// that record, and the build takes back off the centre the edit's own record states.</para></summary>
public static class HiddenPart
{
    /// <summary>The centre of <paramref name="stock"/>'s vertices once <paramref name="uprighting"/> is
    /// applied: the middle of their bounding box, per axis. The origin for a mesh with no positions.</summary>
    public static Vector3 Centre(UnityMesh stock, Matrix4x4? uprighting)
    {
        var mesh = uprighting is { } u ? RestBake.Apply(stock, u) : stock;
        if (!mesh.Channels.TryGetValue("Vertex", out var pos) || mesh.Dims.GetValueOrDefault("Vertex") != 3
            || pos.Length < 3)
            return Vector3.Zero;
        float minX = pos[0], minY = pos[1], minZ = pos[2], maxX = minX, maxY = minY, maxZ = minZ;
        for (int i = 3; i + 3 <= pos.Length; i += 3)
        {
            minX = MathF.Min(minX, pos[i]); maxX = MathF.Max(maxX, pos[i]);
            minY = MathF.Min(minY, pos[i + 1]); maxY = MathF.Max(maxY, pos[i + 1]);
            minZ = MathF.Min(minZ, pos[i + 2]); maxZ = MathF.Max(maxZ, pos[i + 2]);
        }
        return new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
    }

    /// <summary>The display placement <c>Q = U · T(−c)</c>: the part's uprighting, where it has one, then
    /// the shift that puts <paramref name="centre"/> at the origin.</summary>
    public static Matrix4x4 Display(Matrix4x4? uprighting, Vector3 centre) =>
        (uprighting ?? Matrix4x4.Identity) * Matrix4x4.CreateTranslation(-centre);

    /// <summary>The display placement of a part whose rig placement is <paramref name="placement"/>, or null
    /// when that placement does not hide it (or can't be read): the part's own stock mesh, read only for a
    /// hidden part, is what fixes the centre.</summary>
    public static (Matrix4x4 Display, Vector3 Centre)? For(Matrix4x4? placement, Func<UnityMesh> stock,
        Matrix4x4? uprighting)
    {
        if (placement is not { } g || !BindReference.Hidden(g)) return null;
        var centre = Centre(stock(), uprighting);
        return (Display(uprighting, centre), centre);
    }

    /// <summary>The shift record as it is persisted: the three floats of the centre the geometry was
    /// moved by.</summary>
    public static List<float> ToList(Vector3 centre) => new() { centre.X, centre.Y, centre.Z };

    /// <summary>The centre a persisted shift record states, or null where it states none (absent, or not
    /// three floats), which is geometry left where it was modelled.</summary>
    public static Vector3? FromList(IReadOnlyList<float>? record) =>
        record is { Count: 3 } ? new Vector3(record[0], record[1], record[2]) : null;
}
