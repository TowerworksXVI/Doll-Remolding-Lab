using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Skeleton;

namespace Remold.Core.Workbench;

/// <summary>Where a part's mesh space sits in its rig (<see cref="SceneRig.Placement"/>), read from the game
/// files: the one read the build's pool probe and the Blender export both place a part by.
///
/// <para>Accepted: no test builds a bundle holding an Animator and its saved pose, so this read, and the
/// renderer-rig and saved-pose decoders under it (<see cref="BundleReader.RendererRig"/>,
/// <see cref="BundleReader.RigRestPose"/>), run only against a real install. The offline install probes
/// cover them, placing every skinned part of every character, enemy, curated skin and support team.</para></summary>
public static class RigPlacement
{
    /// <summary>A part's placement, or the reason it can't be said, and the skeleton that drives it: the
    /// saved pose asset its rig names (<c>bundle:path id</c>), null for a rig with no saved pose.</summary>
    public sealed record Placed(Matrix4x4? Placement, string? Problem, string? Skeleton);

    /// <summary>The placement of the mesh <paramref name="skin"/> describes, drawn by the renderer at
    /// <paramref name="rendererPathId"/> in <paramref name="rendererBundle"/>, or the reason it can't be said.
    ///
    /// <para>The saved rest pose is the one the renderer's rig names, read off the renderer's own file here.
    /// <paramref name="pose"/> (<see cref="SubjectPart.Pose"/>) is where the subject model found that pose's
    /// file; one it couldn't find, or found under another pose, is looked up again among
    /// <paramref name="dependenciesOf"/> the renderer's file, so a file that couldn't be read when the
    /// subject was first opened is read now. <paramref name="poses"/> keeps each pose decoded once for the
    /// caller's run. The skeleton identity is what tells two independently animated rigs of one item apart
    /// (<see cref="Mesh.BindReference"/>).</para>
    ///
    /// <para><paramref name="read"/> answers null for a file it couldn't read, and the placement then can't be
    /// said. A read that throws a refusal or an I/O error propagates, as every other read of the caller's
    /// does: that is how the build names a file the game is holding.</para></summary>
    public static Placed Read(BundleReader reader, Func<string, byte[]?> read,
        Func<string, IReadOnlyList<string>>? dependenciesOf, string? rendererBundle, long rendererPathId,
        RigPose? pose, MeshSkin skin, IDictionary<(string, long), IReadOnlyDictionary<uint, Matrix4x4>> poses)
    {
        try
        {
            if (rendererBundle is null || read(rendererBundle) is not { } rendererBytes
                || reader.RendererRig(rendererBytes, rendererPathId) is not { } rig)
                return new Placed(null, "its skeleton can't be read", null);
            IReadOnlyDictionary<uint, Matrix4x4>? rest = null;
            string? skeleton = null;
            if (rig.Avatar is { } avatar)
            {
                string? poseBundle = avatar.Cab is null ? rendererBundle
                    : pose is { Bundle: { } known } && pose.PathId == avatar.PathId ? known
                    : BundleForCab(reader, read, dependenciesOf?.Invoke(rendererBundle), avatar.Cab);
                if (poseBundle is null || read(poseBundle) is not { } poseBytes)
                    return new Placed(null, "its skeleton's saved pose can't be read from this install", null);
                if (!poses.TryGetValue((poseBundle, avatar.PathId), out rest))
                    poses[(poseBundle, avatar.PathId)] = rest = reader.RigRestPose(poseBytes, avatar.PathId);
                skeleton = $"{poseBundle}:{avatar.PathId}";
            }
            return new Placed(SceneRig.Placement(rig.RootChain, skin, rest, rig.AvatarFrame, out var problem), problem,
                skeleton);
        }
        catch (Exception ex) when (ex is not IOException and not AuthoredRefusalException)
        {
            return new Placed(null, $"its skeleton can't be read ({ex.Message})", null);
        }
    }

    /// <summary>The file among <paramref name="bundles"/> whose asset file is <paramref name="cab"/>, or null.
    /// A file <paramref name="read"/> answers null for is passed over.</summary>
    static string? BundleForCab(BundleReader reader, Func<string, byte[]?> read, IReadOnlyList<string>? bundles,
        string cab)
    {
        foreach (var bundle in bundles ?? Array.Empty<string>())
            if (read(bundle) is { } bytes && string.Equals(reader.GetBundleCab(bytes), cab, StringComparison.Ordinal))
                return bundle;
        return null;
    }
}
