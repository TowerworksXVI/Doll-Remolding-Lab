using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Workbench;

namespace Remold.Core.Tests.Migoto;

/// <summary>The released workbench shape as a build, for the fixtures that still author one. It was
/// <c>ModBuilder.Build(ModProject, …)</c> until the legacy burn: production has one build entry now and it
/// takes authored intent, so the boundary that turns a schema-1 manifest into that intent lives here, with
/// the fixtures that need it.
///
/// <para>It is a BOUNDARY, not a route of its own — the same one production shipped: the project is swept
/// for blocked donor sources, adapted into authored intent, planned against the same install this build
/// reads, and compiled by the one production spine. The frozen golden pins exactly this sequence, so the
/// steps stay in this order and keep their semantics.</para></summary>
internal static class ReleasedBuild
{
    internal static ModBuilder.Result Build(ModProject project, BuildEnv env, string outRoot,
        Action<string>? log = null, bool zip = true, BuildCaches? caches = null,
        int? encoderCpuLimit = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        RefuseBlockedDonorSources(project, env);
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(project, resolver.ResolvePart, resolver.RosterSlots);
        if (!adaptation.Report.CanSave)
            throw new InvalidOperationException("this project can't be read against the current install: "
                + string.Join("; ", adaptation.Report.Items.Where(item => item.BlocksSave)
                    .Select(item => $"{item.Scope}: {item.Detail}")));
        var plan = AuthoredBuildPlanner.Plan(adaptation.Project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        return ModBuilder.Build(AuthoredBuildExecution.Create(adaptation.Project, plan), env, outRoot,
            log, zip, caches, encoderCpuLimit);
    }

    /// <summary>The content policy over the RELEASED shape's own inventory, before the project is read as
    /// authored intent. A donor row's maps name the materialized game textures they came off, and the
    /// subject behind those is one this build reaches — so it answers to the policy exactly as the subjects
    /// the change list names do. The released workspace is the only place that join is recorded, which is
    /// why the sweep belongs to this boundary rather than to the spine.
    ///
    /// <para>It sweeps EVERY target's donor rows, which is deliberately wider than the derivation the
    /// released build shipped: a row belonging to a change the Build pane unticked, or to a target the
    /// derivation drops, is swept here too, so such a project is refused where the released build compiled
    /// it. The direction is the safe one — this is content policy, and a blocked source refused is never a
    /// build wrongly shipped.</para></summary>
    private static void RefuseBlockedDonorSources(ModProject project, BuildEnv env)
    {
        foreach (var row in project.Targets
                     .SelectMany(target => target.DonorTextures ?? new List<SubmeshTextures>()))
        {
            if (RampConversion.RampSettled(row)) continue;
            if (DonorSourceOf(project, row) is not { } source) continue;
            // the recorded strings first, so a name this install can't resolve is still refused; then the
            // RESOLVED identity, the way the roster's own resolve does
            RefuseBlocked(source.SubjectCharacter, source.SubjectOutfit);
            SubjectModel? model = null;
            try { model = env.ResolveSubject(source.SubjectCharacter!, source.SubjectOutfit!); }
            catch (Exception ex) when (ex is not BlockedAssetException) { }
            if (model is not null) RefuseBlocked(model.Character, model.Stem);
        }
    }

    /// <summary>Where a donor submesh's maps came from on the RELEASED shape: whichever picture slot names a
    /// game texture this project materialized first, read off the project's own texture target. The schema-2
    /// twin is <see cref="RampConversion.DonorSourceOf(Remold.Core.Project.AuthoredWorkspaceFacts,
    /// SubmeshTextures)"/>, which asks the workspace index instead; this reading lives with the boundary
    /// because the released workspace is the only place that join is recorded.</summary>
    private static ProjectTarget? DonorSourceOf(ModProject project, SubmeshTextures row) =>
        StockPngTarget(project, row.Albedo)
        ?? StockPngTarget(project, row.Normal)
        ?? StockPngTarget(project, row.Rmo);

    /// <summary>Which materialized game texture a donor row's file is, or null when the file is authored
    /// bytes or nothing this project materialized.</summary>
    private static ProjectTarget? StockPngTarget(ModProject project, string? rel) =>
        rel is null ? null
        : project.Targets.FirstOrDefault(x => x.AssetType == "Texture2D"
            && !string.IsNullOrEmpty(x.ObjectName) && !string.IsNullOrEmpty(x.Bundle)
            && x.SubjectCharacter is not null && x.SubjectOutfit is not null
            && string.Equals(x.ReplaceFile?.Replace('\\', '/'), rel.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase));

    /// <summary>Fail the build on a blocked game asset. Don't weaken or drop the calls to this
    /// (<see cref="BuildBlacklist"/>).</summary>
    private static void RefuseBlocked(params string?[] names)
    {
        foreach (var n in names)
            if (BuildBlacklist.IsBlocked(n))
                throw new BlockedAssetException($"'{n}' is not a supported asset");
    }
}
