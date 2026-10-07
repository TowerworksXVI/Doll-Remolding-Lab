using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Bundles;
using Remold.Core.Mesh;

namespace Remold.Core.Workbench;

/// <summary>
/// Whether the game starts a part shrunk out of sight (<see cref="BindReference.Hidden"/>), memoized per
/// install and per renderer and pose: the ② Edit page says so on the part's rows, and the answer costs a mesh
/// read and a placement read (<see cref="RigPlacement.Read"/>). A force rescan swaps the install object, and
/// the next ask builds a fresh gate.
///
/// <para>A mesh or placement that cannot be read RIGHT NOW answers no and is not memoized: a held file is
/// not a fact about the part, and the next ask retries. Only a placement that was read settles the answer,
/// either way.</para>
/// </summary>
public sealed class HiddenPartGate
{
    private readonly Func<string, byte[]?> _tryDeobfuscate;
    private readonly Func<string, IReadOnlyList<string>>? _dependenciesOf;
    private readonly Func<string?, long, RigPose?, MeshSkin, RigPlacement.Placed>? _placementOf;
    private readonly ConcurrentDictionary<(string?, long, string?, long, string, string, MeshSelector), bool> _answers =
        new();
    // the placement read shares one reader and one pose memo, neither of which is thread-safe
    private readonly object _read = new();
    private readonly BundleReader _reader = new();
    private readonly Dictionary<(string, long), IReadOnlyDictionary<uint, Matrix4x4>> _poses = new();

    /// <param name="tryDeobfuscate">non-throwing logical-bundle → plain bytes (null when
    /// absent/unreadable).</param>
    /// <param name="dependenciesOf">the catalog's load dependencies of a bundle, where a placement read
    /// looks for a skeleton's saved pose the subject model didn't find; null looks nowhere.</param>
    public HiddenPartGate(Func<string, byte[]?> tryDeobfuscate,
        Func<string, IReadOnlyList<string>>? dependenciesOf = null)
        : this(tryDeobfuscate, dependenciesOf, placementOf: null) { }

    /// <param name="placementOf">states where a part's mesh space sits in its rig, by renderer, pose and
    /// skin, for a synthetic install whose renderers carry no skeleton; null reads it
    /// (<see cref="RigPlacement.Read"/>).</param>
    internal HiddenPartGate(Func<string, byte[]?> tryDeobfuscate,
        Func<string, IReadOnlyList<string>>? dependenciesOf,
        Func<string?, long, RigPose?, MeshSkin, RigPlacement.Placed>? placementOf)
    {
        _tryDeobfuscate = tryDeobfuscate ?? throw new ArgumentNullException(nameof(tryDeobfuscate));
        _dependenciesOf = dependenciesOf;
        _placementOf = placementOf;
    }

    /// <summary>Whether the part drawn by the renderer at <paramref name="rendererPathId"/> in
    /// <paramref name="rendererBundle"/>, whose mesh is <paramref name="meshName"/> in
    /// <paramref name="meshBundle"/>, is one the game starts hidden. No for a part with no skin, and for a
    /// part whose placement can't be read.</summary>
    public bool StartsHidden(string meshBundle, string meshName, MeshSelector mesh, string? rendererBundle,
        long rendererPathId, RigPose? pose)
    {
        var key = (rendererBundle, rendererPathId, pose?.Bundle, pose?.PathId ?? 0, meshBundle, meshName,
            mesh);
        if (_answers.TryGetValue(key, out bool settled)) return settled;
        lock (_read)
        {
            try
            {
                if (_tryDeobfuscate(meshBundle) is not { } bytes
                    || _reader.GetMeshField(bytes, meshName, mesh) is not { } field)
                    return false;
                var skin = MeshSkin.Decode(field);
                if (skin is not { IsSkinned: true }) return _answers[key] = false;
                var placed = _placementOf?.Invoke(rendererBundle, rendererPathId, pose, skin)
                    ?? RigPlacement.Read(_reader, _tryDeobfuscate, _dependenciesOf, rendererBundle,
                        rendererPathId, pose, skin, _poses);
                if (placed.Placement is not { } placement) return false;
                return _answers[key] = BindReference.Hidden(placement);
            }
            // a read that fails now is not a fact about the part; the next ask retries
            catch (Exception) { return false; }
        }
    }
}
