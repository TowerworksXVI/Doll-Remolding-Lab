using System;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Migoto;

namespace Remold.Core.Tests.Support;

/// <summary>Shared builders for <see cref="PoolDerive"/> worlds: roster parts and a donor whose
/// vertices ride exactly the bone hashes a test names.</summary>
internal static class PoolFixtures
{
    public static PoolDerive.PartBones Part(string mesh, params uint[] hashes) =>
        new(mesh, hashes.ToHashSet());

    public static PoolDerive.PartBones Part(string mesh, PartPresence presence, params uint[] hashes) =>
        new(mesh, hashes.ToHashSet(), Presence: presence);

    /// <summary>A donor whose vertices ride exactly <paramref name="usedHashes"/>. The remaining
    /// influences carry weight 0 and point at joint 0 — zero-weight influences must never pull a bone
    /// into the pool.</summary>
    public static MeshApply.Payload Donor(params uint[] usedHashes)
    {
        int n = Math.Max(1, usedHashes.Length);
        var ji = new int[n * 4];
        var jw = new float[n * 4];
        for (int v = 0; v < usedHashes.Length; v++) { ji[v * 4] = v; jw[v * 4] = 1f; }
        return new MeshApply.Payload
        {
            Mesh = new UnityMesh { Name = "donor", VertexCount = n },
            JointIndices = ji, JointWeights = jw,
            SkinJointHashes = usedHashes.Length > 0 ? usedHashes : new uint[] { 1 },
        };
    }
}
