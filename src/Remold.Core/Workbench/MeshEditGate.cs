using System;
using System.Collections.Concurrent;
using Remold.Core.Migoto;

namespace Remold.Core.Workbench;

/// <summary>
/// <see cref="PartSkinGate.Blocked"/> memoized per install: the ② Edit page's verbs, the Blender session's
/// writability and the Build plan's geometry verdict all ask the same question of the same mesh, and each
/// read costs a bundle deobfuscate plus a Mesh deserialize. One instance serves them all; a force rescan
/// swaps the install object, and the next ask builds a fresh gate.
///
/// <para>A bundle or mesh field that cannot be read RIGHT NOW answers clear and is not memoized — a held
/// file or throwing parse is not a fact about the mesh, and the next ask retries. Only a read and parse
/// that completed settles the answer for this install either way.</para>
/// </summary>
public sealed class MeshEditGate
{
    private readonly Func<string, byte[]?> _tryDeobfuscate;
    private readonly ConcurrentDictionary<(string Bundle, string Mesh, long PathId),
        StreamDump.SkinRefusal?> _answers = new();

    /// <param name="tryDeobfuscate">non-throwing logical-bundle → plain bytes (null when
    /// absent/unreadable).</param>
    public MeshEditGate(Func<string, byte[]?> tryDeobfuscate)
    {
        _tryDeobfuscate = tryDeobfuscate ?? throw new ArgumentNullException(nameof(tryDeobfuscate));
    }

    /// <summary>Why this mesh's geometry can't be replaced, or null when it can — the
    /// <see cref="PartSkinGate.Blocked"/> answer, settled once per mesh per install.</summary>
    public StreamDump.SkinRefusal? Blocked(string bundle, string meshName, long pathId = 0)
    {
        var key = (bundle, meshName, pathId);
        if (_answers.TryGetValue(key, out var settled)) return settled;
        if (!PartSkinGate.TryBlocked(_tryDeobfuscate, bundle, meshName, pathId, out var answer))
            return null;
        _answers[key] = answer;
        return answer;
    }
}
