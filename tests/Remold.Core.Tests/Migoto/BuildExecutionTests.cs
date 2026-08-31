using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>The execution package the runtime compiler is handed: work items minted from authored intent
/// and the settled plan, and nothing read off a projected released workspace.</summary>
public sealed class BuildExecutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-execution-" + Guid.NewGuid().ToString("N"));

    public BuildExecutionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Production_execution_is_compiled_from_the_settled_plan_not_legacy_verb_state()
    {
        var project = ProjectWithAlternative();
        project.RootDir = _root;
        File.WriteAllBytes(Path.Combine(_root, "active.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "alt.glb"), new byte[] { 2 });
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry>
            {
                new() { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
            Records = new List<AuthoredWorkspaceRecord>(),
        };
        var plan = AuthoredBuildPlanner.Plan(project, new AuthoredBuildPlannerTests.Backend());

        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var edit = Assert.Single(execution.Work);
        Assert.Equal(EditVerbs.Replace, edit.Verb);
        Assert.Equal("active.glb", edit.DonorFile);
        Assert.Empty(execution.StockRamps);
        // Each work item states its own gate and names the operation it came from, so nothing downstream
        // has to ask a side table which state a compiled item answers.
        Assert.Null(edit.Gate.Content);
        Assert.Same(plan.Parts[0].Operations[0], edit.Operation);
    }

    /// <summary>What the compiler receives, whole: authored intent, the settled plan, and work items minted
    /// from the two. No projected schema-1 workspace stands between them, so no compiled decision can be
    /// read off one — the projection is a compatibility reading for surfaces that still speak the released
    /// vocabulary, and the runtime compiler is not one of them.</summary>
    [Fact]
    public void The_execution_package_names_no_released_workspace()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var members = typeof(AuthoredBuildExecution).GetMembers(all);
        var carriers = members.OfType<PropertyInfo>().Select(property => property.PropertyType)
            .Concat(members.OfType<FieldInfo>().Select(field => field.FieldType))
            .Concat(members.OfType<MethodInfo>().SelectMany(method =>
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType)))
            .Concat(typeof(BuildWorkItem).GetMembers(all).OfType<PropertyInfo>()
                .Select(property => property.PropertyType))
            .SelectMany(type => type.IsGenericType
                ? type.GetGenericArguments().Append(type) : new[] { type })
            .ToList();

        Assert.DoesNotContain(typeof(ModProject), carriers);
        // and the compiler's production entry takes the package alone
        var production = typeof(ModBuilder).GetMethods()
            .Single(method => method.Name == nameof(ModBuilder.Build)
                && method.GetParameters()[0].ParameterType == typeof(AuthoredBuildExecution));
        Assert.DoesNotContain(typeof(ModProject),
            production.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Replacement_and_stock_ramps_are_independent_explicit_choices()
    {
        var project = ProjectWithAlternative();
        var alternative = project.EditDefinitions.Single(candidate => candidate.Id == "edit-alt");
        var slots = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var alternativeOutputs = alternative.Bindings.Where(binding =>
                slots[binding.SlotId].Domain == TargetSlotDomain.EditOutput)
            .Select(binding => binding.SlotId).ToHashSet(StringComparer.Ordinal);
        project.EditDefinitions.Remove(alternative);
        project.TargetSlots.RemoveAll(candidate => alternativeOutputs.Contains(candidate.Id));
        project.ProjectAssets.RemoveAll(candidate => candidate.Id == "asset-alt");
        project.RootDir = _root;
        File.WriteAllBytes(Path.Combine(_root, "active.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "alt.glb"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(_root, "donor-ramp.dds"), new byte[] { 3 });
        File.WriteAllBytes(Path.Combine(_root, "stock-ramp.dds"), new byte[] { 4 });
        var edit = project.EditDefinitions.Single(candidate => candidate.Id == "edit-active");
        var part = edit.Target;
        var renderer = project.TargetSlots[0].Renderer;
        var mesh = project.TargetSlots[0].Mesh!;
        var material = Game("material.bundle", 100, "body");
        project.ProjectAssets.AddRange(new[]
        {
            new ProjectAsset { Id = "donor-ramp", Kind = ProjectAssetKind.Ramp,
                Label = "Donor ramp", File = "donor-ramp.dds" },
            new ProjectAsset { Id = "stock-ramp", Kind = ProjectAssetKind.Ramp,
                Label = "Stock ramp", File = "stock-ramp.dds" },
        });
        project.TargetSlots.AddRange(new[]
        {
            new TargetSlot { Id = "slot-donor-ramp", Part = part,
                Domain = TargetSlotDomain.EditOutput, SubmeshIndex = 0, MaterialSlotIndex = 0,
                Input = TargetInputKind.Ramp, Renderer = renderer, Mesh = mesh },
            new TargetSlot { Id = "slot-stock-ramp", Part = part, SubmeshIndex = 0,
                MaterialSlotIndex = 0, Input = TargetInputKind.Ramp, Renderer = renderer,
                Mesh = mesh, Material = material },
        });
        edit.Bindings.AddRange(new[]
        {
            new Binding { SlotId = "slot-donor-ramp", Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "donor-ramp" },
            new Binding { SlotId = "slot-stock-ramp", Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "stock-ramp" },
        });
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Records = new List<AuthoredWorkspaceRecord>(),
        };
        var resolved = new LegacyResolvedPart(part, renderer, mesh,
            new[]
            {
                new LegacyResolvedMaterial(0, "body", material, new[]
                {
                    new LegacyResolvedTexture(TargetInputKind.BaseColor, "texture.bundle", "base", 200,
                        Game("texture.bundle", 200, "base")),
                    new LegacyResolvedTexture(TargetInputKind.Ramp, "texture.bundle", "ramp", 201,
                        Game("texture.bundle", 201, "ramp")),
                }),
            });

        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(_ => resolved));
        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        Assert.Equal("donor-ramp.dds", Assert.Single(Assert.Single(execution.Work).Textures!).Ramp);
        Assert.Equal("stock-ramp.dds", Assert.Single(execution.StockRamps).Ramp);
    }

    [Fact]
    public void An_authored_effect_map_projects_into_the_retexture_work_item()
    {
        // The Blend input rides the same game-domain picture lane as base colour/normal/RMO: an authored
        // picture lands in the work item's Blend slot, and an untouched sibling stays the game's own.
        var part = Part();
        var renderer = Game("prefab.bundle", 10, part.RendererSlot);
        var mesh = Game("mesh.bundle", 20, part.RendererSlot + "_mesh");
        var material = Game("material.bundle", 100, "body");
        var project = new AuthoredProject
        {
            Info = new ProjectInfo { Name = "Effect map", Author = "Tester" },
            ProjectAssets = new List<ProjectAsset>
            {
                new() { Id = "asset-spc", Kind = ProjectAssetKind.Picture,
                    Label = "Effect map", File = "spc.png" },
            },
            TargetSlots = new List<TargetSlot>
            {
                new() { Id = "slot-geometry", Part = part,
                    Tier = "lod0", Input = TargetInputKind.Geometry, Renderer = renderer, Mesh = mesh },
                new() { Id = "slot-base", Part = part, SubmeshIndex = 0, MaterialSlotIndex = 0,
                    Input = TargetInputKind.BaseColor, Renderer = renderer, Mesh = mesh,
                    Material = material },
                new() { Id = "slot-blend", Part = part, SubmeshIndex = 0, MaterialSlotIndex = 0,
                    Input = TargetInputKind.Blend, Renderer = renderer, Mesh = mesh,
                    Material = material },
            },
            EditDefinitions = new List<EditDefinition>
            {
                new() { Id = "edit-spc", Target = part, Label = "Effect", Bindings = new List<Binding>
                {
                    new() { SlotId = "slot-geometry", Kind = BindingKind.TargetGameValue },
                    new() { SlotId = "slot-base", Kind = BindingKind.TargetGameValue },
                    new() { SlotId = "slot-blend", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "asset-spc" },
                } },
            },
            Always = new List<string> { "edit-spc" },
        };
        project.RootDir = _root;
        File.WriteAllBytes(Path.Combine(_root, "spc.png"), new byte[] { 5 });
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Records = new List<AuthoredWorkspaceRecord>(),
        };
        var resolved = new LegacyResolvedPart(part, renderer, mesh,
            new[]
            {
                new LegacyResolvedMaterial(0, "body", material, new[]
                {
                    new LegacyResolvedTexture(TargetInputKind.BaseColor, "texture.bundle", "base", 200,
                        Game("texture.bundle", 200, "base")),
                    new LegacyResolvedTexture(TargetInputKind.Blend, "texture.bundle", "body_spc", 202,
                        Game("texture.bundle", 202, "body_spc")),
                }),
            });

        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(_ => resolved));
        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var work = Assert.Single(execution.Work);
        Assert.Equal(EditVerbs.Retexture, work.Verb);
        var row = Assert.Single(work.Textures!);
        Assert.Equal("spc.png", row.Blend);
        Assert.Equal(SlotOrigin.Authored, row.BlendAsk);
        Assert.Null(row.Albedo);
        Assert.Equal(SlotOrigin.VanillaOwn, row.AlbedoAsk);
    }

    private static AuthoredProject ProjectWithAlternative()
    {
        var part = Part();
        var renderer = Game("prefab.bundle", 10, part.RendererSlot);
        var mesh = Game("mesh.bundle", 20, part.RendererSlot + "_mesh");
        return new AuthoredProject
        {
            Info = new ProjectInfo { Name = "Alternatives", Author = "Tester" },
            ProjectAssets = new List<ProjectAsset>
            {
                new() { Id = "asset-active", Kind = ProjectAssetKind.Geometry,
                    Label = "Active", File = "active.glb" },
                new() { Id = "asset-alt", Kind = ProjectAssetKind.Geometry,
                    Label = "Alternative", File = "alt.glb" },
            },
            TargetSlots = new List<TargetSlot>
            {
                new() { Id = "slot-active", Part = part,
                    Tier = "lod0", Input = TargetInputKind.Geometry, Renderer = renderer, Mesh = mesh },
                new() { Id = "slot-alt", Part = part,
                    Tier = "lod0", Input = TargetInputKind.Geometry, Renderer = renderer, Mesh = mesh },
            },
            EditDefinitions = new List<EditDefinition>
            {
                new() { Id = "edit-active", Target = part, Label = "Active", Bindings = new List<Binding>
                    { new() { SlotId = "slot-active", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "asset-active" } } },
                new() { Id = "edit-alt", Target = part, Label = "Alternative", Bindings = new List<Binding>
                    { new() { SlotId = "slot-alt", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "asset-alt" } } },
            },
            Always = new List<string> { "edit-active" },
        };
    }

    private static TargetPart Part() => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna_body_lod0",
    };

    private static GameAssetRef Game(string bundle, long pathId, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = bundle, PathId = pathId, Name = name,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
