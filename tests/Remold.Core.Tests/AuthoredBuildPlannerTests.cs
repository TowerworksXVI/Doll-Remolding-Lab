using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

public sealed class AuthoredBuildPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-authored-plan-" + Guid.NewGuid().ToString("N"));

    public AuthoredBuildPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Theory]
    [InlineData("c_vesna_body_lod0", "body")]
    [InlineData("c_vesna_cloth_lod0_Dorm", "cloth_dorm")]
    public void Plan_part_names_match_the_short_names_on_the_build_board(string rendererSlot, string expected)
    {
        Assert.Equal(expected, AuthoredBuildPlanner.PartName(new TargetPart { RendererSlot = rendererSlot }));
    }

    [Fact]
    public void An_empty_keyless_group_keeps_its_exact_structured_owner()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Clear();
        project.KeyGroups.Add(EmptyGroup("key-empty", "Body options", "state-a", "state-b"));
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        var plan = AuthoredBuildPlanner.Plan(project, new Backend());

        const string conflict = "Key group 'Body options' has no key. This blocks the build. "
            + "Give it a key, or delete the group.";
        Assert.Equal(conflict, Assert.Single(plan.Conflicts));
        Assert.Equal(new[] { "key-empty" }, plan.IssueGroupIds[conflict]);
        Assert.Empty(plan.IssueEditIds[conflict]);
    }

    [Fact]
    public void Duplicate_unnamed_keyless_groups_share_text_without_losing_either_owner()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Clear();
        project.KeyGroups.Add(EmptyGroup("key-a", null, "state-a1", "state-a2"));
        project.KeyGroups.Add(EmptyGroup("key-b", null, "state-b1", "state-b2"));
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        var plan = AuthoredBuildPlanner.Plan(project, new Backend());

        const string conflict = "Unnamed key group has no key. This blocks the build. "
            + "Give it a key, or delete the group.";
        Assert.Equal(conflict, Assert.Single(plan.Conflicts));
        Assert.Equal(new[] { "key-a", "key-b" }, plan.IssueGroupIds[conflict]);
    }

    private static KeyGroup EmptyGroup(string id, string? label, string firstState, string secondState) =>
        new()
        {
            Id = id,
            Label = label,
            States =
            {
                new KeyGroupState { Id = firstState },
                new KeyGroupState { Id = secondState },
            },
        };

    [Fact]
    public void The_active_edit_is_reanchored_and_only_its_assets_are_required()
    {
        var project = Fixture();
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        Assert.Equal(new[] { "edit-long:slot-geometry", "edit-long:slot-ramp" },
            plan.Bindings.Select(b => b.RowId).ToArray());
        Assert.All(plan.Bindings, b => Assert.Equal("current", b.CurrentSlot!.Renderer.GameBuild));
        Assert.All(project.TargetSlots, s => Assert.Equal("26109", s.Renderer.GameBuild));
        Assert.Equal(2, backend.BindingRequests.Count);
        Assert.All(plan.Bindings, b =>
        {
            Assert.NotNull(b.RenderPlan);
            Assert.Single(b.RenderPlan!.Roles,
                r => r.Kind == BuildRenderRoleKind.RenderCarrier && r.State == BuildCoverageState.Covered);
            Assert.Single(b.RenderPlan.Contracts);
        });
        Assert.NotNull(Assert.Single(plan.Parts).Lifecycle);
        Assert.Equal(2, plan.RuntimeEmissions.Count);
        Assert.Equal(2, plan.OutputArtifacts.Count);
        Assert.All(plan.OutputArtifacts, output =>
        {
            Assert.True(output.Artifact.Included);
            Assert.StartsWith("edit-long:", output.Consumer);
            Assert.Single(output.Artifact.EmissionIds);
        });
        Assert.Equal(new[] { "mesh-long", "ramp-warm" }, plan.ProjectArtifacts
            .Where(a => a.RequiredByActivePlan).Select(a => a.ProjectAssetId).ToArray());
        Assert.All(plan.ProjectArtifacts.Where(a => a.RequiredByActivePlan), a => Assert.True(a.Available));
        Assert.Equal(new[] { "mesh-short", "ramp-cool" }, plan.ProjectArtifacts
            .Where(a => !a.RequiredByActivePlan).Select(a => a.ProjectAssetId)
            .OrderBy(id => id, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_game_picture_on_a_geometry_edit_is_warning_dropped_without_changing_projection()
    {
        var project = Fixture();
        var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-old-stock-picture",
            Part = ramp.Part,
            Tier = ramp.Tier,
            SubmeshIndex = ramp.SubmeshIndex,
            MaterialSlotIndex = ramp.MaterialSlotIndex,
            Input = TargetInputKind.BaseColor,
            Domain = TargetSlotDomain.Game,
            Renderer = ramp.Renderer,
            Mesh = ramp.Mesh,
            Material = ramp.Material,
        });
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "old-stock-picture",
            Kind = ProjectAssetKind.Picture,
            Label = "Old stock picture",
            File = "textures/old-stock.png",
        });
        Directory.CreateDirectory(Path.Combine(_root, "textures"));
        File.WriteAllText(Path.Combine(_root, "textures", "old-stock.png"), "fixture");
        project.EditDefinitions.Single(edit => edit.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = "slot-old-stock-picture",
            Kind = BindingKind.ProjectAsset,
            ProjectAssetId = "old-stock-picture",
        });
        project.EditDefinitions.Single(edit => edit.Id == "edit-short").Bindings.Add(new Binding
        {
            SlotId = "slot-old-stock-picture",
            Kind = BindingKind.TargetGameValue,
        });

        var plan = AuthoredBuildPlanner.Plan(project, new Backend());

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        var dropped = plan.Bindings.Single(binding =>
            binding.AuthoredSlot.Id == "slot-old-stock-picture");
        Assert.Equal(BuildPlanVerdict.InheritedAsRequested, dropped.Decision.Verdict);
        Assert.Equal(BuildRuntimeAction.None, dropped.Decision.Action);
        Assert.Empty(dropped.Emissions);
        Assert.Empty(dropped.OutputArtifacts);
        Assert.Contains("Long body replaces the part's mesh, so its changes to the original textures "
            + "will not take effect. A replacement uses this edit's own maps instead.", plan.Warnings);

        var execution = AuthoredBuildExecution.Create(project, plan);
        var replacement = Assert.Single(execution.Work,
            edit => edit.Mesh == "c_vesna_body_lod0" && edit.Verb == EditVerbs.Replace);
        Assert.Null(replacement.Textures);
    }

    [Fact]
    public void Same_named_sources_and_alternative_edits_keep_exact_identity_through_save_and_build()
    {
        var project = Fixture();
        AuthoredProjectSerializer.Save(project, _root);

        var first = AuthoredProjectSerializer.Load(_root);
        var firstBackend = new Backend();
        var firstPlan = AuthoredBuildPlanner.Plan(first, firstBackend);
        var warm = firstBackend.BindingRequests.Single(request =>
            request.AuthoredSlot.Input == TargetInputKind.Ramp).EffectiveValue.ProjectAsset!.Source!.GameAsset!;

        Assert.True(firstPlan.CanBuild);
        Assert.Equal("RampMap_Linear_RGBAHalf", warm.Name);
        Assert.Equal(91001, warm.PathId);

        first.Always[0] = "edit-short";
        AuthoredProjectSerializer.Save(first, _root);

        var second = AuthoredProjectSerializer.Load(_root);
        var secondBackend = new Backend();
        var secondPlan = AuthoredBuildPlanner.Plan(second, secondBackend);
        var cool = secondBackend.BindingRequests.Single(request =>
            request.AuthoredSlot.Input == TargetInputKind.Ramp).EffectiveValue.ProjectAsset!.Source!.GameAsset!;

        Assert.True(secondPlan.CanBuild);
        Assert.Equal("RampMap_Linear_RGBAHalf", cool.Name);
        Assert.Equal(91002, cool.PathId);
        Assert.Equal(new[] { "edit-long", "edit-short" }, second.EditDefinitions.Select(edit => edit.Id));
        Assert.NotEqual(warm.PathId, cool.PathId);
    }

    [Fact]
    public void An_unplaced_edit_does_not_resolve_or_consume_anything()
    {
        var project = Fixture();
        project.Always.Clear();
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        Assert.Empty(plan.Parts);
        Assert.Empty(plan.Bindings);
        Assert.Empty(backend.SlotRequests);
        Assert.Empty(backend.BindingRequests);
        Assert.Empty(backend.LifecycleRequests);
        Assert.All(plan.ProjectArtifacts, a => Assert.False(a.RequiredByActivePlan));
    }

    [Fact]
    public void A_missing_active_project_asset_blocks_before_backend_capability()
    {
        var project = Fixture();
        File.Delete(Path.Combine(_root, "meshes", "long.glb"));
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var geometry = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Geometry);
        Assert.Equal(BuildPlanVerdict.Unresolved, geometry.Decision.Verdict);
        Assert.DoesNotContain(backend.BindingRequests, r => r.RowId == geometry.RowId);
        var artifact = plan.ProjectArtifacts.Single(a => a.ProjectAssetId == "mesh-long");
        Assert.True(artifact.RequestedByActivePlan);
        Assert.False(artifact.RequiredByActivePlan);
        Assert.Contains(geometry.RowId, artifact.BlockedConsumers);
        Assert.False(artifact.Available);
    }

    [Fact]
    public void An_unrooted_project_cannot_claim_that_its_active_files_are_available()
    {
        var project = Fixture();
        project.RootDir = null;
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding => Assert.Equal(
            BuildPlanVerdict.Unresolved, binding.Decision.Verdict));
        Assert.Empty(backend.BindingRequests);
        Assert.All(plan.ProjectArtifacts, artifact => Assert.Null(artifact.Available));
    }

    [Fact]
    public void A_slot_that_cannot_be_reanchored_blocks_before_capability_is_judged()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Slot = slot => slot.Input == TargetInputKind.Geometry
                ? new BuildSlotResolution(BuildPlanVerdict.NeedsRepair, null,
                    "the authored mesh route no longer exists in this install")
                : Backend.ResolvedSlot(slot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var geometry = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Geometry);
        Assert.Equal(BuildPlanVerdict.NeedsRepair, geometry.Decision.Verdict);
        Assert.Null(geometry.EffectiveValue);
        Assert.DoesNotContain(backend.BindingRequests, r => r.RowId == geometry.RowId);
    }

    [Fact]
    public void A_reanchor_for_a_different_structural_slot_is_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Slot = slot =>
            {
                var wrong = CopySlot(slot, slot.Id, slot.OwnerEditId);
                wrong.Part = new TargetPart
                {
                    Subject = "DifferentSubject",
                    Outfit = slot.Part.Outfit,
                    RendererSlot = slot.Part.RendererSlot,
                };
                return new BuildSlotResolution(BuildPlanVerdict.Resolved, wrong,
                    "fixture returned another subject's slot");
            },
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding =>
        {
            Assert.Equal(BuildPlanVerdict.Conflict, binding.Decision.Verdict);
            Assert.Contains("different structural slot", binding.Decision.Detail);
        });
        Assert.Empty(backend.BindingRequests);
    }

    [Fact]
    public void Restamped_owner_still_corresponds_to_same_structural_slot()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Slot = slot => new BuildSlotResolution(BuildPlanVerdict.Resolved,
                CopySlot(slot, slot.Id, "restamped-by-save"),
                "fixture returned the same structural route with transient filing metadata"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        Assert.All(plan.Bindings, binding =>
            Assert.Equal(BuildPlanVerdict.Resolved, binding.Decision.Verdict));
    }

    [Fact]
    public void An_unsupported_active_binding_blocks_without_hiding_the_other_rows()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Binding = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported,
                    "this material has no draw-local ramp discriminator")
                : Backend.Resolved(request),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Equal(2, plan.Bindings.Count);
        Assert.Equal(BuildPlanVerdict.Resolved,
            plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Geometry).Decision.Verdict);
        Assert.Equal(BuildPlanVerdict.Unsupported,
            plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp).Decision.Verdict);
        var rampAsset = plan.ProjectArtifacts.Single(a => a.ProjectAssetId == "ramp-warm");
        Assert.True(rampAsset.RequestedByActivePlan);
        Assert.False(rampAsset.RequiredByActivePlan);
        Assert.Single(rampAsset.BlockedConsumers);
    }

    [Fact]
    public void A_blocking_backend_verdict_may_return_a_partial_render_account()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Binding = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported,
                    "the ramp draw has no material-local discriminator")
                : Backend.Resolved(request),
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? new BuildRenderPlan(new[]
                {
                    new BuildRenderRole(BuildRenderRoleKind.RenderCarrier,
                        BuildCoverageState.Unsupported, request.CurrentSlot, null,
                        "the shared draw cannot identify this material"),
                }, Array.Empty<RenderContract>(), "the known carrier gap")
                : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Unsupported, ramp.Decision.Verdict);
        Assert.NotNull(ramp.RenderPlan);
        Assert.DoesNotContain("invalid render plan", ramp.Decision.Reason);
    }

    [Fact]
    public void A_resolved_runtime_action_without_targeting_proof_is_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Binding = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? new BuildPlanDecision(BuildPlanVerdict.Resolved, BuildRuntimeAction.BindProjectAsset,
                    null, "ramp bind")
                : Backend.Resolved(request),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("targeting proof", ramp.Decision.Detail);
    }

    [Fact]
    public void Whitespace_is_not_a_targeting_proof()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Binding = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? new BuildPlanDecision(BuildPlanVerdict.Resolved,
                    BuildRuntimeAction.BindProjectAsset, new BuildTargetingProof(" ", "\t"),
                    "ramp bind")
                : Backend.Resolved(request),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("targeting proof", ramp.Decision.Detail);
    }

    [Fact]
    public void An_excluded_output_can_explicitly_have_no_emission_consumers()
    {
        var resolution = new BuildOperationResolution(
            BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported, "no runtime discriminator"),
            null, Array.Empty<BuildRuntimeEmission>(), new[]
            {
                new BuildOutputArtifact("ramp-output", "generated ramp resource",
                    "decoded-ramp:fixture", "generated/ramp.dds", false,
                    Array.Empty<string>(),
                    "excluded because the binding is unsupported"),
            });

        var errors = AuthoredRenderPlanValidator.OperationErrors(resolution,
            BuildEmissionKind.ResourceBinding, BuildEmissionGate.Unconditional,
            requireComplete: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void A_blocking_operation_cannot_claim_an_included_output()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Operation = request => new BuildOperationResolution(
                BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported,
                    "no runtime discriminator"), null, Array.Empty<BuildRuntimeEmission>(), new[]
                {
                    new BuildOutputArtifact(request.RowId + ":output", "runtime-resource",
                        Backend.FunctionalIdentity(request), "generated/blocked.bin", true,
                        Array.Empty<string>(), "incorrect included output"),
                }),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding => Assert.Contains(
            "blocking operation claims an included output artifact", binding.Decision.Detail));
    }

    [Fact]
    public void An_unknown_backend_verdict_is_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Binding = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? new BuildPlanDecision((BuildPlanVerdict)999, BuildRuntimeAction.None, null,
                    "invented verdict")
                : Backend.Resolved(request),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("unknown capability verdict", ramp.Decision.Detail);
    }

    [Fact]
    public void A_resolved_runtime_action_without_a_render_plan_is_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? null : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("without a render plan", ramp.Decision.Detail);
    }

    [Fact]
    public void A_resolved_runtime_action_without_emissions_or_outputs_is_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            OmitEmission = request => request.AuthoredSlot.Input == TargetInputKind.Ramp,
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("runtime emissions were not accounted for", ramp.Decision.Detail);
        Assert.Contains("output artifacts were not accounted for", ramp.Decision.Detail);
    }

    [Fact]
    public void Missing_pass_coverage_makes_a_resolved_render_plan_a_conflict()
    {
        var project = Fixture();
        var backend = new Backend
        {
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? Backend.Render(request.CurrentSlot) with
                {
                    Contracts = new[]
                    {
                        Backend.Contract(request.CurrentSlot) with
                        {
                            Passes = Backend.Passes()
                                .Where(p => p.Pass != BuildRenderPass.Shadow).ToArray(),
                        },
                    },
                }
                : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("accounts for Shadow 0 times", ramp.Decision.Detail);
    }

    [Fact]
    public void A_runtime_action_and_its_carrier_must_share_one_targeting_proof()
    {
        var project = Fixture();
        var backend = new Backend
        {
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? Backend.Render(request.CurrentSlot) with
                {
                    Roles = Backend.Render(request.CurrentSlot).Roles.Select(r =>
                        r.Kind == BuildRenderRoleKind.RenderCarrier
                            ? r with { TargetingProof = new BuildTargetingProof("other", "draw") }
                            : r).ToArray(),
                }
                : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("not carried by its RenderCarrier", ramp.Decision.Detail);
    }

    [Fact]
    public void A_runtime_action_and_its_draw_contract_must_share_one_targeting_proof()
    {
        var project = Fixture();
        var backend = new Backend
        {
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? Backend.Render(request.CurrentSlot) with
                {
                    Contracts = new[]
                    {
                        Backend.Contract(request.CurrentSlot) with
                        {
                            TargetingProof = new BuildTargetingProof("other", "draw"),
                        },
                    },
                }
                : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Conflict, ramp.Decision.Verdict);
        Assert.Contains("not carried by its draw contract", ramp.Decision.Detail);
    }

    [Fact]
    public void Compatible_render_and_material_carriers_can_be_planned_per_draw()
    {
        var project = Fixture();
        var backend = new Backend
        {
            BindingRender = request => request.AuthoredSlot.Input == TargetInputKind.Ramp
                ? MultipleCarriers(request.CurrentSlot)
                : Backend.Render(request.CurrentSlot),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        var render = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp)
            .RenderPlan!;
        Assert.Equal(2, render.Roles.Count(r => r.Kind == BuildRenderRoleKind.RenderCarrier));
        Assert.Equal(2, render.Roles.Count(r => r.Kind == BuildRenderRoleKind.MaterialCarrier));
        Assert.Equal(2, render.Contracts.Count);

        static BuildRenderPlan MultipleCarriers(TargetSlot first)
        {
            var second = CopySlot(first, first.Id + "-second", first.OwnerEditId);
            var firstPlan = Backend.Render(first);
            var secondPlan = Backend.Render(second);
            return firstPlan with
            {
                Roles = firstPlan.Roles.Concat(secondPlan.Roles.Where(r => r.Kind is
                    BuildRenderRoleKind.RenderCarrier or BuildRenderRoleKind.MaterialCarrier)).ToArray(),
                Contracts = firstPlan.Contracts.Concat(secondPlan.Contracts).ToArray(),
                Reason = "two compatible draws carry this binding",
            };
        }
    }

    [Fact]
    public void Incompatible_answers_for_the_same_draw_block_the_plan()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Operation = request =>
            {
                var shared = CopySlot(request.CurrentSlot, "shared-draw", request.CurrentSlot.OwnerEditId);
                var proof = new BuildTargetingProof("fixture", "shared-draw");
                var decision = Backend.Resolved(request) with { TargetingProof = proof };
                var basePlan = Backend.Render(request.CurrentSlot);
                var roles = basePlan.Roles.Select(role => role.Kind switch
                {
                    BuildRenderRoleKind.RenderCarrier => role with
                    {
                        CurrentSlot = shared,
                        TargetingProof = proof,
                    },
                    BuildRenderRoleKind.MaterialCarrier => role with { CurrentSlot = shared },
                    _ => role,
                }).ToArray();
                var contract = Backend.Contract(request.CurrentSlot) with
                {
                    Id = request.RowId + ":draw",
                    CarrierSlot = shared,
                    MaterialCarrierSlot = shared,
                    TargetingProof = proof,
                    RenderQueue = request.AuthoredSlot.Input == TargetInputKind.Geometry ? 2000 : 2450,
                };
                var render = basePlan with { Roles = roles, Contracts = new[] { contract } };
                return Backend.Complete(decision, render, request.CurrentSlot, request.RowId,
                    request.Gate, Backend.FunctionalIdentity(request),
                    "runtime-resource");
            },
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Conflicts, conflict => conflict == AuthoredBuildPlanner.InternalGuard);
        Assert.Contains(plan.Diagnostics, line => line.Contains(
            "incompatible render contracts", StringComparison.Ordinal));
    }

    [Fact]
    public void Included_output_files_are_unique_across_the_whole_plan()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Operation = request => Backend.Complete(Backend.Resolved(request),
                Backend.Render(request.CurrentSlot), request.CurrentSlot, request.RowId,
                request.Gate, Backend.FunctionalIdentity(request),
                "runtime-resource", "generated/shared.bin"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Diagnostics, line => line.Contains(
            "Included output file", StringComparison.Ordinal));
        string conflict = Assert.Single(plan.Conflicts, line => line == AuthoredBuildPlanner.InternalGuard);
        // The line is about the edits whose bindings claim the file. A blocked build whose line marks no
        // row leaves the modder to find the edit by reading a file path.
        Assert.Equal(new[] { "edit-long" }, plan.IssueEditIds[conflict]);
    }

    /// <summary>An output no consumer can produce blocks the build, and the line says which edits it is
    /// about — so their rows are marked and the chips lead to where those edits are used.</summary>
    [Fact]
    public void A_blocked_output_names_the_edits_it_is_about()
    {
        var project = Fixture();
        var backend = new Backend
        {
            // An included output whose own consumer is blocked: nothing can produce the file the build
            // would ship.
            Operation = request => new BuildOperationResolution(
                BuildPlanDecision.Blocked(BuildPlanVerdict.Unsupported, "this route has no runtime action"),
                null, Array.Empty<BuildRuntimeEmission>(), new[]
                {
                    new BuildOutputArtifact(request.RowId + ":output", "runtime-resource",
                        Backend.FunctionalIdentity(request),
                        "generated/" + request.RowId.Replace(':', '_') + ".bin", true,
                        Array.Empty<string>(), "fixture output"),
                }),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Diagnostics, line => line.Contains("Blocked consumer",
            StringComparison.Ordinal));
        var blocked = plan.Conflicts.Where(line => line == AuthoredBuildPlanner.InternalGuard).ToList();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, line => Assert.Equal(new[] { "edit-long" }, plan.IssueEditIds[line]));
    }

    [Fact]
    public void Output_paths_use_the_same_project_relative_rule_as_inputs()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Operation = request => Backend.Complete(Backend.Resolved(request),
                Backend.Render(request.CurrentSlot), request.CurrentSlot, request.RowId,
                request.Gate, Backend.FunctionalIdentity(request),
                "runtime-resource", "generated/mod.ini:stream"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding =>
            Assert.Contains("invalid output path", binding.Decision.Detail));
    }

    [Fact]
    public void A_source_slot_can_reuse_an_inactive_edits_project_asset()
    {
        var project = Fixture();
        var sharedRamp = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        var sourceSlot = CopySlot(sharedRamp, "slot-ramp-short", "edit-short");
        project.TargetSlots.Add(sourceSlot);
        var shortEdit = project.EditDefinitions.Single(e => e.Id == "edit-short");
        var shortRamp = shortEdit.Bindings.Single(b => b.SlotId == sharedRamp.Id);
        shortRamp.SlotId = sourceSlot.Id;
        shortRamp.Kind = BindingKind.ProjectAsset;
        shortRamp.ProjectAssetId = "ramp-cool";
        var selected = project.EditDefinitions.Single(e => e.Id == "edit-long");
        var selectedRamp = selected.Bindings.Single(b => b.SlotId == "slot-ramp");
        selectedRamp.Kind = BindingKind.SourceSlot;
        selectedRamp.ProjectAssetId = null;
        selectedRamp.SourceSlot = new BindingSourceSlot
        {
            SlotId = sourceSlot.Id,
            EditDefinitionId = shortEdit.Id,
        };
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal("ramp-cool", ramp.EffectiveValue!.ProjectAsset!.Id);
        Assert.Equal(new[] { "edit-long:slot-ramp", "edit-short:slot-ramp-short" },
            ramp.EffectiveValue.SourceChain);
        Assert.True(plan.ProjectArtifacts.Single(a => a.ProjectAssetId == "ramp-cool")
            .RequiredByActivePlan);
        Assert.False(plan.ProjectArtifacts.Single(a => a.ProjectAssetId == "ramp-warm")
            .RequiredByActivePlan);
    }

    [Fact]
    public void A_source_slot_cannot_borrow_another_slots_live_inheritance()
    {
        var project = Fixture();
        var commonRamp = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        var sourceSlot = CopySlot(commonRamp, "slot-ramp-inherited", "edit-short");
        project.TargetSlots.Add(sourceSlot);
        var shortEdit = project.EditDefinitions.Single(e => e.Id == "edit-short");
        var shortRamp = shortEdit.Bindings.Single(b => b.SlotId == commonRamp.Id);
        shortRamp.SlotId = sourceSlot.Id;
        shortRamp.Kind = BindingKind.InheritedLiveCarrier;
        shortRamp.ProjectAssetId = null;
        var selected = project.EditDefinitions.Single(e => e.Id == "edit-long");
        var selectedRamp = selected.Bindings.Single(b => b.SlotId == commonRamp.Id);
        selectedRamp.Kind = BindingKind.SourceSlot;
        selectedRamp.ProjectAssetId = null;
        selectedRamp.SourceSlot = new BindingSourceSlot
        {
            SlotId = sourceSlot.Id,
            EditDefinitionId = shortEdit.Id,
        };
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(BuildPlanVerdict.Unsupported, ramp.Decision.Verdict);
        Assert.Contains("keeps the original, so there is nothing to copy", ramp.Decision.Reason);
        Assert.DoesNotContain(backend.BindingRequests, request => request.RowId == ramp.RowId);
    }

    [Fact]
    public void A_source_game_slot_is_independently_reanchored_before_it_is_bound()
    {
        var project = Fixture();
        var selected = project.EditDefinitions.Single(e => e.Id == "edit-long");
        var rampBinding = selected.Bindings.Single(b => b.SlotId == "slot-ramp");
        rampBinding.Kind = BindingKind.SourceSlot;
        rampBinding.ProjectAssetId = null;
        rampBinding.SourceSlot = new BindingSourceSlot { SlotId = "slot-ramp" };
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        var ramp = plan.Bindings.Single(b => b.AuthoredSlot.Input == TargetInputKind.Ramp);
        Assert.Equal(EffectiveValueKind.SourceGameSlot, ramp.EffectiveValue!.Kind);
        Assert.Equal("current", ramp.EffectiveValue.SourceGameSlot!.Renderer.GameBuild);
        Assert.Equal(BuildRuntimeAction.BindGameSource, ramp.Decision.Action);
        Assert.Equal(2, backend.SlotRequests.Count(s => s.Id == "slot-ramp"));
    }

    [Fact]
    public void A_source_slot_cycle_is_reported_as_a_conflict()
    {
        var project = Fixture();
        var sharedRamp = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        var selected = project.EditDefinitions.Single(e => e.Id == "edit-long");
        var cycleBinding = selected.Bindings.Single(binding => binding.SlotId == sharedRamp.Id);
        cycleBinding.Kind = BindingKind.SourceSlot;
        cycleBinding.ProjectAssetId = null;
        cycleBinding.SourceSlot = new BindingSourceSlot
        {
            SlotId = sharedRamp.Id,
            EditDefinitionId = selected.Id,
        };
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var cycle = plan.Bindings.Single(b => b.AuthoredSlot.Id == sharedRamp.Id);
        Assert.Equal(BuildPlanVerdict.Conflict, cycle.Decision.Verdict);
        Assert.Contains("form a cycle", cycle.Decision.Detail);
        Assert.DoesNotContain(backend.BindingRequests, r => r.RowId == cycle.RowId);
    }

    [Fact]
    public void A_hidden_toggle_off_state_gets_its_own_capability_proof()
    {
        var project = Fixture();
        project.KeyFirstPart("F7", offState: CompositionState.Hidden);
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        var part = Assert.Single(plan.Parts);
        var hideRequest = Assert.Single(backend.VisibilityRequests);
        Assert.Equal(1, Assert.Single(hideRequest.Gate.ActiveWhen).StateIndex);
        Assert.Equal(BuildRuntimeAction.Hide, part.ToggleOffDecision!.Action);
        Assert.NotNull(part.ToggleOffDecision.TargetingProof);
        Assert.All(plan.RuntimeEmissions, emission => Assert.All(emission.Emission.Gate.ActiveWhen,
            term => Assert.Equal("F7", term.Key)));
        Assert.All(plan.RuntimeEmissions.Where(e =>
            e.Emission.Kind != BuildEmissionKind.Suppression), emission =>
            Assert.Equal(0, Assert.Single(emission.Emission.Gate.ActiveWhen).StateIndex));
        var off = Assert.Single(plan.RuntimeEmissions,
            e => e.Emission.Kind == BuildEmissionKind.Suppression);
        Assert.Equal(1, Assert.Single(off.Emission.Gate.ActiveWhen).StateIndex);
    }

    [Fact]
    public void A_hide_can_reanchor_through_one_of_several_lod0_geometry_slots()
    {
        var project = Fixture();
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.Always.Clear();
        project.Always.Add(hide);
        var geometry = project.TargetSlots.Single(s => s.Id == "slot-geometry");
        var second = CopySlot(geometry, "slot-geometry-submesh-1", owner: null);
        second.SubmeshIndex = 1;
        project.TargetSlots.Add(second);
        foreach (var edit in project.EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Content))
            edit.Bindings.Add(new Binding
            {
                SlotId = second.Id,
                Kind = BindingKind.InheritedLiveCarrier,
            });
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        Assert.Equal(BuildPlanVerdict.Resolved, Assert.Single(plan.Parts).ActiveDecision!.Verdict);
        Assert.Single(backend.VisibilityRequests);
        Assert.Equal("slot-hide-0003", backend.VisibilityRequests[0].AuthoredSlot.Id);
    }

    [Fact]
    public void A_first_class_hide_edit_plans_as_hidden_while_content_edits_remain_available()
    {
        var project = Fixture();
        var source = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        var visibility = CopySlot(source, "slot-hide", "edit-hide");
        visibility.Input = TargetInputKind.Visibility;
        project.TargetSlots.Add(visibility);
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-hide",
            Kind = EditDefinitionKind.Hide,
            Target = AuthoredEditFixtures.Body,
            Label = "Hidden",
            Bindings = new List<Binding>
            {
                new() { SlotId = visibility.Id, Kind = BindingKind.Hidden },
            },
        });
        project.Always.Clear();
        project.Always.Add("edit-hide");
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        var part = Assert.Single(plan.Parts);
        Assert.Equal(PlannedPartDisposition.Hidden, part.Disposition);
        Assert.Equal(BuildRuntimeAction.Hide, part.Suppression!.Decision.Action);
        Assert.Single(backend.VisibilityRequests);
        Assert.Equal(2, project.EditDefinitions.Count(edit => edit.Kind == EditDefinitionKind.Content));
    }

    [Fact]
    public void A_hidden_toggle_without_targeting_proof_blocks_the_build()
    {
        var project = Fixture();
        project.KeyFirstPart("F7", offState: CompositionState.Hidden);
        var backend = new Backend
        {
            Visibility = _ => new BuildPlanDecision(BuildPlanVerdict.Resolved,
                BuildRuntimeAction.Hide, null, "renderer hide"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Equal(BuildPlanVerdict.Conflict, Assert.Single(plan.Parts).ToggleOffDecision!.Verdict);
    }

    [Fact]
    public void Every_emission_carries_the_condition_the_plan_gave_it()
    {
        var project = Fixture();
        project.KeyFirstPart("F7");
        var backend = new Backend
        {
            Operation = request => Backend.Complete(Backend.Resolved(request),
                Backend.Render(request.CurrentSlot), request.CurrentSlot, request.RowId,
                new BuildEmissionGate(new BuildGateTerm("key-0002", "F8", 0)),
                Backend.FunctionalIdentity(request), "runtime-resource"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding =>
            Assert.Contains("is gated by 'F8=0', expected 'F7=0'", binding.Decision.Detail));
    }

    [Fact]
    public void A_gate_term_naming_no_key_group_is_refused_rather_than_read_as_the_keyless_term()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Operation = request => Backend.Complete(Backend.Resolved(request),
                Backend.Render(request.CurrentSlot), request.CurrentSlot, request.RowId,
                new BuildEmissionGate(new BuildGateTerm("", "", 0)),
                Backend.FunctionalIdentity(request), "runtime-resource"),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        // A term naming an empty group is not the keyless term that holds in every session, however alike
        // the two look to a comparison that drops the difference between no group and an empty one.
        Assert.False(new BuildGateTerm("", "", 0).IsAlways);
        Assert.False(plan.CanBuild);
        Assert.All(plan.Bindings, binding =>
            Assert.Contains("is gated by a term naming no key group", binding.Decision.Detail));
    }

    [Fact]
    public void Runtime_actions_require_complete_lifecycle_coverage()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Lifecycle = request => Backend.ResolvedLifecycle(request) with
            {
                Plan = Backend.LifecyclePlan(request) with
                {
                    Coverage = Backend.LifecycleCoverage(request.LaunchCondition)
                        .Where(c => c.Event != BuildLifecycleEvent.LodChange).ToArray(),
                },
            },
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var lifecycle = Assert.Single(plan.Parts).Lifecycle!;
        Assert.Equal(BuildPlanVerdict.Conflict, lifecycle.Verdict);
        Assert.Contains("accounts for LodChange 0 times", lifecycle.Detail);
    }

    [Fact]
    public void Lifecycle_initial_state_must_match_the_first_state()
    {
        var project = Fixture();
        project.KeyFirstPart("F7", startsOff: true);
        var backend = new Backend
        {
            Lifecycle = request => Backend.ResolvedLifecycle(request) with
            {
                Plan = Backend.LifecyclePlan(request) with
                    { InitialCondition = PlanCondition.Always },
            },
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        var lifecycle = Assert.Single(plan.Parts).Lifecycle!;
        Assert.Equal(BuildPlanVerdict.Conflict, lifecycle.Verdict);
        Assert.Contains("starts in always, expected F7 state 0 of 2", lifecycle.Detail);
    }

    [Fact]
    public void Covered_lifecycle_rows_must_name_their_mechanism()
    {
        var project = Fixture();
        var backend = new Backend
        {
            Lifecycle = request => Backend.ResolvedLifecycle(request) with
            {
                Plan = Backend.LifecyclePlan(request) with
                {
                    Coverage = Backend.LifecycleCoverage(request.LaunchCondition).Select(row =>
                        row.Event == BuildLifecycleEvent.SceneChange
                            ? row with { Mechanism = BuildLifecycleMechanism.Unknown }
                            : row).ToArray(),
                },
            },
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.False(plan.CanBuild);
        Assert.Contains("SceneChange has no lifecycle mechanism",
            Assert.Single(plan.Parts).Lifecycle!.Detail);
    }

    [Fact]
    public void Absence_is_vanilla_and_never_asks_the_backend_to_emit()
    {
        var project = Fixture();
        project.Always.Clear();
        var backend = new Backend();

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild);
        Assert.Empty(plan.Parts);
        Assert.Empty(backend.SlotRequests);
        Assert.Empty(backend.BindingRequests);
        Assert.Empty(backend.VisibilityRequests);
        Assert.All(plan.ProjectArtifacts, a => Assert.False(a.RequiredByActivePlan));
    }

    private AuthoredProject Fixture()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Project", "golden", "authored_project_v2.json");
        var project = AuthoredProjectSerializer.Deserialize(File.ReadAllText(fixture));
        project.RootDir = _root;
        foreach (var asset in project.ProjectAssets)
        {
            string path = Path.Combine(_root, asset.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, asset.Id);
        }
        return project;
    }

    private static TargetSlot CopySlot(TargetSlot source, string id, string? owner) => new()
    {
        Id = id,
        OwnerEditId = owner,
        Part = source.Part,
        Tier = source.Tier,
        SubmeshIndex = source.SubmeshIndex,
        MaterialSlotIndex = source.MaterialSlotIndex,
        Input = source.Input,
        Domain = source.Domain,
        Semantic = source.Semantic,
        Renderer = source.Renderer,
        Mesh = source.Mesh,
        Material = source.Material,
    };

    internal sealed class Backend : IAuthoredBuildBackend
    {
        internal readonly List<TargetSlot> SlotRequests = new();
        internal readonly List<BuildBindingRequest> BindingRequests = new();
        internal readonly List<BuildVisibilityRequest> VisibilityRequests = new();
        internal readonly List<BuildLifecycleRequest> LifecycleRequests = new();

        internal Func<TargetSlot, BuildSlotResolution> Slot { get; init; } = ResolvedSlot;
        internal Func<BuildBindingRequest, BuildPlanDecision> Binding { get; init; } = Resolved;
        internal Func<BuildBindingRequest, BuildOperationResolution>? Operation { get; init; }
        internal Func<BuildBindingRequest, BuildRenderPlan?> BindingRender { get; init; }
            = request => Render(request.CurrentSlot);
        internal Func<BuildBindingRequest, bool> OmitEmission { get; init; } = _ => false;
        internal Func<BuildVisibilityRequest, BuildPlanDecision> Visibility { get; init; }
            = ResolvedVisibility;
        internal Func<BuildVisibilityRequest, BuildRenderPlan?> VisibilityRender { get; init; }
            = request => Suppression(request.CurrentSlot);
        internal Func<BuildLifecycleRequest, BuildLifecycleResolution> Lifecycle { get; init; }
            = ResolvedLifecycle;

        public BuildSlotResolution ResolveSlot(TargetSlot authoredSlot)
        {
            SlotRequests.Add(authoredSlot);
            return Slot(authoredSlot);
        }

        public BuildOperationResolution ResolveBinding(BuildBindingRequest request)
        {
            BindingRequests.Add(request);
            if (Operation is not null) return Operation(request);
            var decision = Binding(request);
            var render = BindingRender(request);
            return OmitEmission(request)
                ? new BuildOperationResolution(decision, render)
                : Complete(decision, render, request.CurrentSlot, request.RowId, request.Gate,
                    FunctionalIdentity(request), "runtime-resource");
        }

        /// <summary>Whether this fixture accounts for an emitted file on a part's suppression. The
        /// production backend does not — nothing ships to make a draw stop — but the instrument has to
        /// carry one if any backend ever states it, rather than dropping it where nobody sees it go.</summary>
        internal bool VisibilityShipsArtifact { get; init; }

        public BuildOperationResolution ResolveVisibility(BuildVisibilityRequest request)
        {
            VisibilityRequests.Add(request);
            var resolution = Complete(Visibility(request), VisibilityRender(request), request.CurrentSlot,
                request.Id, request.Gate, "suppression:" + request.CurrentSlot.Id,
                "runtime-state");
            if (!VisibilityShipsArtifact || resolution.Emissions is not { Count: > 0 } emissions)
                return resolution;
            return resolution with
            {
                OutputArtifacts = new[]
                {
                    new BuildOutputArtifact(request.Id + ":output", "runtime-state",
                        "suppression:" + request.CurrentSlot.Id,
                        "generated/" + request.Id.Replace(':', '_') + ".bin", true,
                        new[] { emissions[0].Id },
                        "fixture suppression accounts for an emitted file"),
                },
            };
        }

        public BuildLifecycleResolution ResolveLifecycle(BuildLifecycleRequest request)
        {
            LifecycleRequests.Add(request);
            return Lifecycle(request);
        }

        internal static BuildSlotResolution ResolvedSlot(TargetSlot slot) => new(
            BuildPlanVerdict.Resolved, Current(slot),
            "structural route resolved in the current install");

        internal static BuildPlanDecision Resolved(BuildBindingRequest request) => new(
            BuildPlanVerdict.Resolved,
            request.EffectiveValue.Kind switch
            {
                EffectiveValueKind.ProjectAsset => BuildRuntimeAction.BindProjectAsset,
                EffectiveValueKind.SourceGameSlot => BuildRuntimeAction.BindGameSource,
                EffectiveValueKind.Neutral => BuildRuntimeAction.GenerateNeutral,
                EffectiveValueKind.Hidden => BuildRuntimeAction.Hide,
                _ => BuildRuntimeAction.None,
            },
            new BuildTargetingProof("fixture", request.CurrentSlot.Id),
            "fixture backend can emit the requested value");

        internal static BuildRenderPlan Render(TargetSlot slot)
        {
            bool geometry = slot.Input == TargetInputKind.Geometry;
            var proof = Proof(slot);
            return new BuildRenderPlan(new[]
            {
                Role(BuildRenderRoleKind.PoseAnchor, geometry, slot),
                Role(BuildRenderRoleKind.LayoutTarget, geometry, slot),
                Role(BuildRenderRoleKind.RenderCarrier, true, slot, proof),
                Role(BuildRenderRoleKind.MaterialCarrier, true, slot),
                Role(BuildRenderRoleKind.SuppressionTarget, geometry, slot, geometry ? proof : null),
            }, new[] { Contract(slot) }, "fixture operation has a complete render account");
        }

        internal static BuildRenderPlan Suppression(TargetSlot slot)
        {
            var proof = Proof(slot);
            return new BuildRenderPlan(new[]
            {
                Role(BuildRenderRoleKind.PoseAnchor, false, slot),
                Role(BuildRenderRoleKind.LayoutTarget, false, slot),
                Role(BuildRenderRoleKind.RenderCarrier, false, slot),
                Role(BuildRenderRoleKind.MaterialCarrier, false, slot),
                Role(BuildRenderRoleKind.SuppressionTarget, true, slot, proof),
            }, Array.Empty<RenderContract>(), "fixture hide suppresses one target");
        }

        internal static RenderContract Contract(TargetSlot slot) => new(
            slot.Id + ":draw", slot, slot, Proof(slot), "fixture-layout", "fixture-draw-space",
            "fixture-shader", "fixture-material-layout", 2000, BuildTransparency.Opaque,
            "fixture-stencil", BuildCullMode.Back, Passes(),
            new BuildVisibilityDomain(new[] { "Fight", "Dorm" }, new[] { slot.Part.Outfit },
                new[] { slot.Tier ?? "all" }, slot.Part.Subject + "/" + slot.Part.Outfit,
                "fixture subject and outfit scope"),
            new BuildCarrierBounds(BuildBoundsBasis.Unavailable, null, null,
                "fixture carries no measured bounds"));

        internal static IReadOnlyList<BuildPassCoverage> Passes() => new[]
        {
            Pass(BuildRenderPass.Color, BuildCoverageState.Covered),
            Pass(BuildRenderPass.Outline, BuildCoverageState.Covered),
            Pass(BuildRenderPass.Shadow, BuildCoverageState.Covered),
            Pass(BuildRenderPass.Reflection, BuildCoverageState.NotApplicable),
            Pass(BuildRenderPass.Transparency, BuildCoverageState.NotApplicable),
            Pass(BuildRenderPass.SpecialView, BuildCoverageState.NotApplicable),
        };

        internal static BuildLifecycleResolution ResolvedLifecycle(BuildLifecycleRequest request) => new(
            BuildPlanVerdict.Resolved, LifecyclePlan(request),
            "fixture runtime action has complete lifecycle coverage");

        internal static BuildLifecyclePlan LifecyclePlan(BuildLifecycleRequest request) => new(
            request.LaunchCondition,
            LifecycleCoverage(request.LaunchCondition, request.ActingConditions),
            "fixture lifecycle account");

        /// <param name="acting">Every condition the part runs something under. A key acts on the part when
        /// any of them is keyed, which is not the same question as whether the part's own home is keyed:
        /// an always-on part another group takes off screen is switched by that group's key.</param>
        internal static IReadOnlyList<BuildLifecycleCoverage> LifecycleCoverage(
            PlanCondition launch, IReadOnlyList<PlanCondition>? acting = null)
        {
            bool keyed = !launch.IsAlways
                || (acting ?? Array.Empty<PlanCondition>()).Any(condition => !condition.IsAlways);
            return new[]
        {
            new BuildLifecycleCoverage(BuildLifecycleEvent.Toggle,
                keyed ? BuildCoverageState.Covered : BuildCoverageState.NotApplicable,
                keyed ? BuildLifecycleMechanism.KeyGate : BuildLifecycleMechanism.NotApplicable,
                keyed ? "the authored key gates this operation" : "no toggle is authored"),
            Life(BuildLifecycleEvent.Reload, BuildLifecycleMechanism.ConfigurationReload),
            Life(BuildLifecycleEvent.SceneChange, BuildLifecycleMechanism.PerDrawMatch),
            Life(BuildLifecycleEvent.OutfitChange, BuildLifecycleMechanism.PerDrawMatch),
            Life(BuildLifecycleEvent.LodChange, BuildLifecycleMechanism.PerDrawMatch),
        };
        }

        private static BuildRenderRole Role(BuildRenderRoleKind kind, bool covered, TargetSlot slot,
            BuildTargetingProof? proof = null) => covered
            ? new BuildRenderRole(kind, BuildCoverageState.Covered, slot, proof,
                "fixture assigns " + kind)
            : new BuildRenderRole(kind, BuildCoverageState.NotApplicable, null, null,
                kind + " is not needed for this operation");

        private static BuildPassCoverage Pass(BuildRenderPass pass, BuildCoverageState state) =>
            new(pass, state, state == BuildCoverageState.Covered
                ? "fixture carrier participates in this pass" : "fixture carrier does not use this pass");

        private static BuildLifecycleCoverage Life(BuildLifecycleEvent kind,
            BuildLifecycleMechanism mechanism) => new(kind, BuildCoverageState.Covered, mechanism,
                "fixture uses " + mechanism + " at " + kind);

        private static BuildTargetingProof Proof(TargetSlot slot) => new("fixture", slot.Id);

        internal static BuildOperationResolution Complete(BuildPlanDecision decision,
            BuildRenderPlan? render, TargetSlot slot, string id, BuildEmissionGate gate,
            string functionalIdentity, string purpose,
            string? outputFile = null)
        {
            if (decision.Verdict != BuildPlanVerdict.Resolved || decision.TargetingProof is null)
                return new BuildOperationResolution(decision, render);
            var emission = new BuildRuntimeEmission(id + ":emission", Emission(decision.Action, slot.Input),
                decision.TargetingProof, gate,
                render?.Contracts.Select(c => c.Id).ToArray()
                    ?? Array.Empty<string>(), "fixture runtime emission");
            IReadOnlyList<BuildOutputArtifact> outputs = decision.Action == BuildRuntimeAction.Hide
                ? Array.Empty<BuildOutputArtifact>()
                : new[]
                {
                    new BuildOutputArtifact(id + ":output", purpose, functionalIdentity,
                        outputFile ?? "generated/" + id.Replace(':', '_') + ".bin", true,
                        new[] { emission.Id }, "fixture output is consumed by the runtime emission"),
                };
            return new BuildOperationResolution(decision, render, new[] { emission }, outputs);
        }

        internal static string FunctionalIdentity(BuildBindingRequest request) =>
            request.EffectiveValue.ProjectAsset is { } asset
                ? "path:" + asset.File.Replace('\\', '/').TrimStart('/').ToLowerInvariant()
                : request.EffectiveValue.Kind == EffectiveValueKind.Neutral
                    ? "generated:GenerateNeutral:" + Purpose(request.AuthoredSlot)
                    : "game:" + request.CurrentSlot.Id;

        internal static string Purpose(TargetSlot slot) => slot.Input switch
        {
            TargetInputKind.Geometry => "geometry",
            TargetInputKind.BaseColor => "base-color:submesh:" + (slot.SubmeshIndex ?? 0),
            TargetInputKind.Normal => "normal:submesh:" + (slot.SubmeshIndex ?? 0),
            TargetInputKind.Rmo => "rmo:submesh:" + (slot.SubmeshIndex ?? 0),
            TargetInputKind.RmoAlpha => "rmo-alpha:submesh:" + (slot.SubmeshIndex ?? 0),
            TargetInputKind.Ramp when !string.IsNullOrWhiteSpace(slot.Material?.Name) =>
                "ramp:material:" + slot.Material!.Name!.Trim().ToLowerInvariant(),
            TargetInputKind.Ramp => "ramp:submesh:" + (slot.SubmeshIndex ?? 0),
            _ => slot.Input.ToString().ToLowerInvariant(),
        };

        private static BuildEmissionKind Emission(BuildRuntimeAction action, TargetInputKind input) =>
            action switch
            {
                BuildRuntimeAction.Hide => BuildEmissionKind.Suppression,
                BuildRuntimeAction.GenerateNeutral => BuildEmissionKind.NeutralBinding,
                BuildRuntimeAction.BindProjectAsset or BuildRuntimeAction.BindGameSource
                    when input == TargetInputKind.Geometry => BuildEmissionKind.GeometryReplacement,
                BuildRuntimeAction.BindProjectAsset or BuildRuntimeAction.BindGameSource
                    when input == TargetInputKind.MaterialValue => BuildEmissionKind.MaterialValuePatch,
                _ => BuildEmissionKind.ResourceBinding,
            };

        private static BuildPlanDecision ResolvedVisibility(BuildVisibilityRequest request) => new(
            BuildPlanVerdict.Resolved, BuildRuntimeAction.Hide,
            Proof(request.CurrentSlot), "renderer hide is targetable");

        private static TargetSlot Current(TargetSlot source) => new()
        {
            Id = source.Id,
            OwnerEditId = source.OwnerEditId,
            Part = source.Part,
            Tier = source.Tier,
            SubmeshIndex = source.SubmeshIndex,
            MaterialSlotIndex = source.MaterialSlotIndex,
            Input = source.Input,
            Domain = source.Domain,
            Semantic = source.Semantic,
            Renderer = Current(source.Renderer),
            Mesh = source.Mesh is null ? null : Current(source.Mesh),
            Material = source.Material is null ? null : Current(source.Material),
        };

        private static GameAssetRef Current(GameAssetRef source) => new()
        {
            GameBuild = "current",
            LogicalBundle = source.LogicalBundle,
            PathId = source.PathId,
            Name = source.Name,
        };
    }
    [Fact]
    public void Two_active_edits_cannot_claim_the_same_current_mesh()
    {
        var mesh = Game("mesh.bundle", 20, "shared_mesh");
        var first = new TargetPart { Subject = "A", Outfit = "A01", RendererSlot = "body-a" };
        var second = new TargetPart { Subject = "B", Outfit = "B01", RendererSlot = "body-b" };
        var project = new AuthoredProject
        {
            RootDir = _root,
            Info = new ProjectInfo { Name = "Collision", Author = "Tester" },
            ProjectAssets = new List<ProjectAsset>
            {
                new() { Id = "a", Kind = ProjectAssetKind.Geometry, Label = "A", File = "a.glb" },
                new() { Id = "b", Kind = ProjectAssetKind.Geometry, Label = "B", File = "b.glb" },
            },
            TargetSlots = new List<TargetSlot>
            {
                new() { Id = "slot-a", Part = first, Tier = "lod0",
                    Input = TargetInputKind.Geometry, Renderer = Game("prefab.bundle", 10, "body-a"),
                    Mesh = mesh },
                new() { Id = "slot-b", Part = second, Tier = "lod0",
                    Input = TargetInputKind.Geometry, Renderer = Game("prefab.bundle", 11, "body-b"),
                    Mesh = mesh },
            },
            EditDefinitions = new List<EditDefinition>
            {
                new() { Id = "edit-a", Target = first, Label = "A", Bindings = new List<Binding>
                    { new() { SlotId = "slot-a", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "a" } } },
                new() { Id = "edit-b", Target = second, Label = "B", Bindings = new List<Binding>
                    { new() { SlotId = "slot-b", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "b" } } },
            },
            Always = new List<string> { "edit-a", "edit-b" },
        };
        File.WriteAllBytes(Path.Combine(_root, "a.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "b.glb"), new byte[] { 2 });

        var plan = AuthoredBuildPlanner.Plan(project, new AuthoredBuildPlannerTests.Backend());

        Assert.False(plan.CanBuild);
        Assert.Contains(plan.Conflicts, conflict => conflict.Contains(
            "mesh replacements are active at once", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pure_hide_plans_through_the_production_backend()
    {
        var part = Part();
        var resolved = new LegacyResolvedPart(part,
            Game("prefab.bundle", 10, part.RendererSlot),
            Game("mesh.bundle", 20, part.RendererSlot + "_mesh"),
            Array.Empty<LegacyResolvedMaterial>());
        var legacy = new ModProject
        {
            RootDir = _root,
            Selection = new List<SelectionEntry>
            {
                new() { Character = part.Subject, Outfit = part.Outfit },
            },
        };
        legacy.SetHidden(part.Subject, part.Outfit, part.RendererSlot, true);

        var adaptation = LegacyProjectAdapter.Adapt(legacy, _ => resolved);
        Assert.True(adaptation.Report.CanSave, string.Join("; ",
            adaptation.Report.Items.Select(item => item.Detail)));
        var plan = AuthoredBuildPlanner.Plan(adaptation.Project,
            new ProductionAuthoredBuildBackend(_ => resolved));

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var planned = Assert.Single(plan.Parts);
        Assert.DoesNotContain(planned.Bindings, binding => binding.Decision.BlocksBuild);
        Assert.False(planned.ActiveDecision?.BlocksBuild ?? false,
            planned.ActiveDecision?.Reason);
        Assert.Contains(plan.RuntimeEmissions,
            item => item.Emission.Kind == BuildEmissionKind.Suppression);
    }

    [Fact]
    public void Alternate_tier_materials_never_borrow_lod0_bindings()
    {
        var part = Part();
        var resolved = new LegacyResolvedPart(part,
            Game("prefab.bundle", 10, part.RendererSlot), Game("mesh.bundle", 20, "lod0"),
            new[] { Material(0, "lod0-material", 100, Game("texture.bundle", 200, "base")) },
            new[] { new LegacyResolvedTier("body_lod1", "lod1",
                Game("prefab.bundle", 11, "body_lod1"), Game("mesh.bundle", 21, "lod1")) });
        var backend = new ProductionAuthoredBuildBackend(_ => resolved);
        var slot = new TargetSlot { Id = "lod1-ramp", Part = part, Tier = "lod1",
            SubmeshIndex = 0, MaterialSlotIndex = 0, Input = TargetInputKind.Ramp,
            Renderer = resolved.Renderer, Mesh = resolved.Mesh, Material = resolved.Materials[0].Material };

        var result = backend.ResolveSlot(slot);

        Assert.Equal(BuildPlanVerdict.Unresolved, result.Verdict);
        Assert.Contains("materials on the lod1 level of detail cannot be changed", result.Reason);
    }

    [Fact]
    public void Production_backend_builds_a_geometry_plan_with_logical_runtime_outputs()
    {
        var project = ProjectWithAlternative();
        project.RootDir = _root;
        File.WriteAllBytes(Path.Combine(_root, "active.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "alt.glb"), new byte[] { 2 });
        var part = Part();
        var resolved = new LegacyResolvedPart(part,
            Game("prefab.bundle", 10, part.RendererSlot),
            Game("mesh.bundle", 20, part.RendererSlot + "_mesh"),
            Array.Empty<LegacyResolvedMaterial>());

        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(_ => resolved));
        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var geometryEmission = Assert.Single(plan.RuntimeEmissions,
            item => item.Emission.Kind == BuildEmissionKind.GeometryReplacement);
        var output = Assert.Single(plan.OutputArtifacts,
            item => item.Artifact.Included && item.Artifact.EmissionIds.Contains(
                geometryEmission.Emission.Id, StringComparer.Ordinal));
        Assert.Null(output.Artifact.File);
        var contract = Assert.Single(Assert.Single(plan.Bindings).RenderPlan!.Contracts);
        Assert.Equal(BuildRenderStateOwnership.LiveCarrier, contract.RenderStateOwnership);
        Assert.Equal(BuildTransparency.Unknown, contract.Transparency);
        Assert.Equal(BuildCullMode.Unknown, contract.Cull);
        Assert.Single(execution.Work);
    }

    [Fact]
    public void Production_slot_resolution_carries_the_current_materials_zero_index_count()
    {
        var slot = MaterialSlot("materials.bundle");
        slot.SubmeshIndex = slot.MaterialSlotIndex = 1;
        var resolved = new LegacyResolvedPart(slot.Part, slot.Renderer, slot.Mesh!, new[]
        {
            new LegacyResolvedMaterial(0, "mat0", Game("materials.bundle", 43, "mat0"),
                Array.Empty<LegacyResolvedTexture>()),
            new LegacyResolvedMaterial(1, "mat1", slot.Material!,
                Array.Empty<LegacyResolvedTexture>()),
        }, MaterialIndexCounts: new[] { 3, 0 });

        var current = new ProductionAuthoredBuildBackend(_ => resolved).ResolveSlot(slot);

        Assert.Equal(BuildPlanVerdict.Resolved, current.Verdict);
        Assert.Equal(0, current.CurrentSlot!.DrawIndexCount);
    }

    [Fact]
    public void A_legacy_propertyless_picture_targets_the_first_coarse_binding_in_material_order()
    {
        var part = Part();
        var first = Game("textures.bundle", 201, "base-map");
        var second = Game("textures.bundle", 202, "main-tex");
        var material = new LegacyResolvedMaterial(0, "body", Game("materials.bundle", 100, "body"),
            new[]
            {
                new LegacyResolvedTexture(TargetInputKind.BaseColor, first.LogicalBundle, first.Name!,
                    first.PathId, first, "_BaseMap"),
                new LegacyResolvedTexture(TargetInputKind.BaseColor, second.LogicalBundle, second.Name!,
                    second.PathId, second, "_MainTex"),
            });
        var resolved = new LegacyResolvedPart(part, Game("prefab.bundle", 10, part.RendererSlot),
            Game("mesh.bundle", 20, part.RendererSlot + "_mesh"), new[] { material });
        var authored = new TargetSlot
        {
            Id = "legacy-base", Part = part, SubmeshIndex = 0, MaterialSlotIndex = 0,
            Input = TargetInputKind.BaseColor, Renderer = resolved.Renderer, Mesh = resolved.Mesh,
            Material = material.Material,
        };
        var backend = new ProductionAuthoredBuildBackend(_ => resolved);
        var current = Assert.IsType<TargetSlot>(backend.ResolveSlot(authored).CurrentSlot);
        var asset = new ProjectAsset
        {
            Id = "picture", Kind = ProjectAssetKind.Picture, Label = "Picture", File = "picture.png",
        };

        var operation = backend.ResolveBinding(new BuildBindingRequest("edit:legacy-base", "edit", authored,
            current, new Binding { SlotId = authored.Id, Kind = BindingKind.ProjectAsset,
                ProjectAssetId = asset.Id },
            new EffectiveBuildValue(EffectiveValueKind.ProjectAsset, asset, null, new[] { authored.Id }),
            BuildEmissionGate.Unconditional));

        Assert.Equal(BuildPlanVerdict.Resolved, operation.Decision.Verdict);
        Assert.Contains(":201", operation.Decision.TargetingProof!.Detail);
        Assert.DoesNotContain(":202", operation.Decision.TargetingProof.Detail);
    }

    [Fact]
    public void Stock_ramp_with_a_shared_ordinary_map_is_a_blocking_capability_verdict()
    {
        var part = Part();
        var shared = Game("textures.bundle", 300, "shared_base");
        var resolved = new LegacyResolvedPart(part,
            Game("prefab.bundle", 10, part.RendererSlot),
            Game("mesh.bundle", 20, part.RendererSlot + "_mesh"),
            new[]
            {
                Material(0, "body", 100, shared),
                Material(1, "trim", 101, shared),
            });
        var backend = new ProductionAuthoredBuildBackend(_ => resolved);
        var authoredSlot = new TargetSlot
        {
            Id = "slot-ramp", Part = part,
            SubmeshIndex = 0, MaterialSlotIndex = 0, Input = TargetInputKind.Ramp,
            Renderer = resolved.Renderer, Mesh = resolved.Mesh, Material = resolved.Materials[0].Material,
        };
        var current = Assert.IsType<TargetSlot>(backend.ResolveSlot(authoredSlot).CurrentSlot);
        var asset = new ProjectAsset
        {
            Id = "ramp", Kind = ProjectAssetKind.Ramp, Label = "Ramp", File = "ramp.dds",
        };

        var operation = backend.ResolveBinding(new BuildBindingRequest("edit-body:slot-ramp",
            "edit-body", authoredSlot, current,
            new Binding { SlotId = authoredSlot.Id, Kind = BindingKind.ProjectAsset,
                ProjectAssetId = asset.Id },
            new EffectiveBuildValue(EffectiveValueKind.ProjectAsset, asset, null,
                new[] { authoredSlot.Id }), BuildEmissionGate.Unconditional));

        Assert.Equal(BuildPlanVerdict.Unsupported, operation.Decision.Verdict);
        Assert.Contains("shares every one of its textures", operation.Decision.Reason);
        Assert.True(operation.Decision.BlocksBuild);
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

    private static TargetSlot MaterialSlot(string bundle) => new()
    {
        Id = "material-slot", Part = Part(), Input = TargetInputKind.MaterialValue,
        Semantic = MaterialValueSemantics.UseGiFlatten,
        Renderer = Game("prefab.bundle", 10, "renderer"),
        Mesh = Game("mesh.bundle", 20, "mesh"),
        Material = Game(bundle, 44, "same_material_name"),
    };

    private static LegacyResolvedMaterial Material(int index, string name, long pathId,
        GameAssetRef texture) => new(index, name,
        Game("materials.bundle", pathId, name), new[]
        {
            new LegacyResolvedTexture(TargetInputKind.BaseColor, texture.LogicalBundle,
                texture.Name!, texture.PathId, texture),
            new LegacyResolvedTexture(TargetInputKind.Ramp, "ramps.bundle", "ramp_" + index,
                500 + index, Game("ramps.bundle", 500 + index, "ramp_" + index)),
        });

    private static TargetPart Part() => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna_body_lod0",
    };

    private static GameAssetRef Game(string bundle, long pathId, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = bundle, PathId = pathId, Name = name,
    };
}
