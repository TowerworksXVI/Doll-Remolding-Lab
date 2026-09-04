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
    private readonly ConcurrentDictionary<(string Bundle, string Mesh, long PathId),
        bool> _collapsed = new();

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

    /// <summary>Both per-mesh answers the Blender-edit surfaces read
    /// (<see cref="PartSkinGate.TryBlenderEditAnswers"/>), settled together from one bundle read so the
    /// second question never costs a second deobfuscate. A settled read also settles
    /// <see cref="Blocked"/>'s memo, and a refusal <see cref="Blocked"/> already settled short-circuits
    /// here — including while the bundle cannot be read RIGHT NOW, since a refusal once read is a fact
    /// about the mesh. The not-memoized-while-unreadable contract otherwise holds.</summary>
    public (StreamDump.SkinRefusal? Refusal, bool CollapsedBillboard) BlenderEditAnswers(
        string bundle, string meshName, long pathId = 0)
    {
        var key = (bundle, meshName, pathId);
        if (_answers.TryGetValue(key, out var settledRefusal))
        {
            // a settled refusal answers the whole question — no consumer reads the billboard half past
            // one — and a settled clear answer waits only on the geometry half
            if (settledRefusal is not null) return (settledRefusal, false);
            if (_collapsed.TryGetValue(key, out var settledCollapsed))
                return (settledRefusal, settledCollapsed);
        }
        if (!PartSkinGate.TryBlenderEditAnswers(_tryDeobfuscate, bundle, meshName, pathId,
                out var refusal, out var collapsed))
            return (_answers.TryGetValue(key, out var prior) ? prior : null, false);
        _answers[key] = refusal;
        _collapsed[key] = collapsed;
        return (refusal, collapsed);
    }
}
