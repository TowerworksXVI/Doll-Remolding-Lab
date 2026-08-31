using System;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Tests.Support;

/// <summary>Synthetic exact game identities for session tests that run without a mounted install.
/// <see cref="Resolve"/> supplies one renderer, mesh and material per part, with ids derived from the part's
/// name so two parts never collide. Nothing here touches a game folder or bundle.</summary>
internal static class AuthoredParts
{
    public static TargetPart Part(string character, string outfit, string mesh) => new()
    {
        Subject = character, Outfit = outfit, RendererSlot = mesh,
    };

    /// <summary>The synthetic install's answer for one part: <paramref name="materials"/> materials, each
    /// carrying one base-colour texture and one toon ramp, and no LOD tiers beyond the one asked for. The
    /// ramp is what gives the part's installed material a ramp slot to pick onto — a shader that binds none
    /// has no such place, and the session refuses a pick there by name.
    ///
    /// <para>More than one material is the multi-material part: submeshes that each draw with their own
    /// material, and therefore the shape a return has to land on its own output positions rather than
    /// collapse onto the first.</para></summary>
    public static LegacyResolvedPart Resolve(TargetPart part) => Resolve(part, materials: 1);

    /// <inheritdoc cref="Resolve(TargetPart)"/>
    public static LegacyResolvedPart Resolve(TargetPart part, int materials)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentOutOfRangeException.ThrowIfLessThan(materials, 1);
        string bundle = $"characters/{part.Outfit.ToLowerInvariant()}";
        long seed = 10_000 + Math.Abs(part.RendererSlot.GetHashCode(StringComparison.Ordinal)) % 100_000;
        return new LegacyResolvedPart(part,
            Game(seed, part.RendererSlot, bundle + "_prefab"),
            Game(seed + 1, part.RendererSlot + "_mesh", bundle + "_meshes"),
            Enumerable.Range(0, materials).Select(index =>
            {
                long own = seed + 10 * (index + 1);
                string name = index == 0 ? part.RendererSlot : $"{part.RendererSlot}_{index}";
                return new LegacyResolvedMaterial(index, name + "_material",
                    Game(own + 2, name + "_material", bundle + "_materials"),
                    new[]
                    {
                        new LegacyResolvedTexture(TargetInputKind.BaseColor, bundle + "_textures",
                            name + "_base", own + 3,
                            Game(own + 3, name + "_base", bundle + "_textures")),
                        new LegacyResolvedTexture(TargetInputKind.Ramp, bundle + "_textures",
                            name + "_ramp", own + 4,
                            Game(own + 4, name + "_ramp", bundle + "_textures")),
                    });
            }).ToList());
    }

    private static GameAssetRef Game(long pathId, string name, string bundle) => new()
    {
        GameBuild = "test-build",
        LogicalBundle = bundle,
        PathId = pathId,
        Name = name,
    };
}
