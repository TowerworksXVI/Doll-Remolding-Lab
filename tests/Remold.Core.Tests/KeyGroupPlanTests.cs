using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

public sealed class KeyGroupPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-activation-plan-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Three_state_group_maps_stable_ids_to_positions_and_launches_first()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "group", Key = "F7", States =
            {
                new KeyGroupState { Id = "long", ActiveEditIds = { "edit-long" } },
                new KeyGroupState { Id = "stock" },
                new KeyGroupState { Id = "short", ActiveEditIds = { "edit-short" } },
            },
        });

        var plan = Plan(project);
        var part = Assert.Single(plan.Parts);

        Assert.Equal(new[] { "edit-long", "edit-short" },
            part.Operations.Select(operation => operation.EditDefinitionId));
        Assert.Equal(new[] { 0, 2 }, part.Operations.Select(operation => operation.Condition.StateIndex));
        Assert.All(part.Operations, operation => Assert.Equal(0, operation.Condition.StartState));
        Assert.Equal(0, part.Lifecycle!.Plan!.InitialCondition.StateIndex);
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
    }

    [Fact]
    public void Head_inventories_content_placed_only_in_a_later_state()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("one", null), ("two", null), ("three", "edit-long")));

        var composed = AuthoredComposition.Head(project);

        Assert.Equal("edit-long", Assert.Single(composed).EditDefinitionId);
    }

    [Fact]
    public void One_edit_in_two_states_resolves_once_under_one_or_gate()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("one", "edit-long"), ("two", "edit-long"), ("three", null)));

        var plan = Plan(project);
        var operation = Assert.Single(Assert.Single(plan.Parts).Operations);

        Assert.Equal(2, operation.ActiveWhen.Count);
        Assert.All(operation.Bindings, binding => Assert.Equal(new[] { "F7=0", "F7=1" },
            binding.Gate.ActiveWhen.Select(term => term.ToString())));
        Assert.Equal(2, plan.Bindings.Count);
    }

    [Fact]
    public void Every_hide_placement_merges_into_one_suppression_and_content_exception_list()
    {
        var project = Fixture();
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.KeyGroups.Add(Group("F7", ("one", null), ("two", hide)));
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-2", Key = "F8", States =
            {
                new KeyGroupState { Id = "a" },
                new KeyGroupState { Id = "b", ActiveEditIds = { hide } },
            },
        });

        var plan = Plan(project);
        var part = Assert.Single(plan.Parts);

        Assert.NotNull(part.Suppression);
        var suppression = Assert.Single(part.Suppression!.Emissions!);
        Assert.Equal(new[] { "F7=1", "F8=1" }, suppression.Gate.ActiveWhen.Select(term => term.ToString()));
        Assert.All(part.Bindings, binding => Assert.Equal(new[] { "F7=1", "F8=1" },
            binding.Gate.UnlessAny.Select(term => term.ToString())));
    }

    [Fact]
    public void Content_edits_in_different_groups_block_with_both_placements_named()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("long", "edit-long"), ("off", null)));
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-2", Key = "F8", States =
            {
                new KeyGroupState { Id = "short", ActiveEditIds = { "edit-short" } },
                new KeyGroupState { Id = "off" },
            },
        });

        var conflict = Assert.Single(Plan(project).Conflicts, value =>
            value.Contains("can be active together", StringComparison.Ordinal));

        Assert.Contains("Long body at F7 · State 1", conflict, StringComparison.Ordinal);
        Assert.Contains("Short body at F8 · State 1", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("key-1", conflict, StringComparison.Ordinal);
        Assert.DoesNotContain("key-2", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void Always_plus_group_content_blocks_and_names_Always()
    {
        var project = Fixture();
        project.KeyGroups.Add(Group("F7", ("short", "edit-short"), ("off", null)));

        var conflict = Assert.Single(Plan(project).Conflicts, value =>
            value.Contains("can be active together", StringComparison.Ordinal));

        Assert.Contains("Long body at Always", conflict, StringComparison.Ordinal);
        Assert.Contains("Short body at F7 · State 1", conflict, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_states_of_one_group_prove_content_exclusivity()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("long", "edit-long"), ("short", "edit-short")));

        var plan = Plan(project);

        Assert.DoesNotContain(plan.Conflicts, conflict => conflict.Contains("can be active together",
            StringComparison.Ordinal));
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
    }

    [Fact]
    public void Always_plus_state_for_same_edit_warns_and_never_refuses()
    {
        var project = Fixture();
        project.KeyGroups.Add(Group("F7", ("long", "edit-long"), ("off", null)));

        var plan = Plan(project);

        // The part by the short name the rest of the app shows it under, never the renderer slot.
        string warning = Assert.Single(plan.Warnings, line =>
            line.Contains("Long body on body is in Always", StringComparison.Ordinal));
        // The part it is about, the place it is about, and what the modder actually gets.
        Assert.Contains("F7 · State 1", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("c_vesna_body_lod0", warning, StringComparison.Ordinal);
        Assert.Contains("changes nothing", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("edit-long", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("key-1", warning, StringComparison.Ordinal);
        // Attributed by id rather than by whatever name the text happens to contain.
        Assert.Equal(new[] { "edit-long" }, plan.IssueEditIds[warning]);
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
    }

    [Fact]
    public void Keyless_group_blocks_once_per_card()
    {
        var project = Fixture();
        project.KeyGroups.Add(Group(null, ("one", "edit-short"), ("two", null)));
        project.KeyGroups[^1].Label = "Body cycle";

        var plan = Plan(project);

        string conflict = Assert.Single(plan.Conflicts,
            value => value.Contains("has no key", StringComparison.Ordinal));
        Assert.Equal("Key group 'Body cycle' has no key. This blocks the build. "
            + "Give it a key, or delete the group.", conflict);
        Assert.DoesNotContain("key-1", conflict, StringComparison.Ordinal);
        // The edits used inside it are the rows the line marks: a group with no key blocks every one.
        Assert.Equal(new[] { "edit-short" }, plan.IssueEditIds[conflict]);
    }

    [Fact]
    public void All_identical_states_warn_but_build()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("one", "edit-long"), ("two", "edit-long")));

        var plan = Plan(project);

        Assert.Contains("F7 switches nothing.", plan.Warnings);
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
    }

    [Fact]
    public void Execution_uses_content_flag_for_or_placements_across_groups()
    {
        var project = Fixture();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F7", ("one", "edit-long"), ("two", null)));
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-2", Key = "F8", States =
            {
                new KeyGroupState { Id = "a" },
                new KeyGroupState { Id = "b", ActiveEditIds = { "edit-long" } },
            },
        });

        var execution = AuthoredBuildExecution.Create(project, Plan(project));

        Assert.Single(execution.ShownFlags);
        Assert.Equal(new[] { ("F7", 0), ("F8", 1) },
            execution.ShownFlags[0].WhenAny.Select(position => (position.Key, position.State)));
    }

    [Fact]
    public void Execution_cycles_always_launch_in_first_state()
    {
        var project = Fixture();
        project.KeyFirstPart("F7", startsOff: true);

        var execution = AuthoredBuildExecution.Create(project, Plan(project));

        Assert.Equal(0, Assert.Single(execution.KeyCycles).StartState);
    }

    [Fact]
    public void Single_group_replace_or_hide_still_collapses()
    {
        var project = Fixture();
        project.KeyFirstPart("F7", offState: CompositionState.Hidden);

        var execution = AuthoredBuildExecution.Create(project, Plan(project));
        var gate = Assert.Single(execution.Work).Gate;

        Assert.True(gate.SuppressesInEveryState);
        Assert.Empty(gate.HiddenWhen);
    }

    [Fact]
    public void Cross_group_hide_does_not_use_single_group_collapse()
    {
        var project = Fixture();
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.KeyGroups.Add(Group("F7", ("show", null), ("hide", hide)));

        var execution = AuthoredBuildExecution.Create(project, Plan(project));
        var gate = Assert.Single(execution.Work).Gate;

        Assert.False(gate.SuppressesInEveryState);
        Assert.Single(gate.HiddenWhen);
        Assert.Single(execution.HiddenFlags);
    }

    [Fact]
    public void Always_hide_warns_for_the_dead_content_placement_and_emits_only_hide()
    {
        var project = Fixture();
        project.Always.Add(project.Hide(AuthoredEditFixtures.Body));

        var plan = Plan(project);
        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.Contains(plan.Warnings, warning => warning.Contains("Long body on body never appears in Always",
                StringComparison.Ordinal)
            && warning.Contains("body is hidden there", StringComparison.Ordinal));
        Assert.Equal(EditVerbs.Hide, Assert.Single(execution.Work).Verb);
    }

    [Fact]
    public void Hide_in_every_state_warns_for_dead_group_content_and_emits_only_hide()
    {
        var project = Fixture();
        project.Always.Clear();
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-1", Key = "F7", Label = "Body cycle", States =
            {
                new KeyGroupState { Id = "shown", ActiveEditIds = { "edit-long", hide } },
                new KeyGroupState { Id = "off", ActiveEditIds = { hide } },
            },
        });

        var plan = Plan(project);
        var execution = AuthoredBuildExecution.Create(project, plan);

        Assert.Contains(plan.Warnings, warning => warning.Contains("Long body on body never appears in F7 · State 1",
                StringComparison.Ordinal)
            && warning.Contains("body is hidden there", StringComparison.Ordinal));
        Assert.Equal(EditVerbs.Hide, Assert.Single(execution.Work).Verb);
    }

    [Fact]
    public void Hide_and_content_in_one_state_warns_for_the_edit_suppression_will_hide()
    {
        var project = Fixture();
        project.Always.Clear();
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-1", Key = "F7", Label = "Body cycle", States =
            {
                new KeyGroupState
                {
                    Id = "layered", Label = "Layered", ActiveEditIds = { "edit-long", hide },
                },
                new KeyGroupState { Id = "plain", Label = "Plain" },
            },
        });

        var plan = Plan(project);

        const string warning =
            "Long body on body never appears in F7 · Layered because body is hidden there.";
        Assert.Contains(warning, plan.Warnings);
        Assert.Equal(new[] { "edit-long" }, plan.IssueEditIds[warning]);
        var part = Assert.Single(plan.Parts);
        var content = Assert.Single(part.Operations,
            operation => operation.Disposition == PlannedPartDisposition.Edit);
        Assert.Equal(new[] { "F7=0" }, content.Bindings[0].Gate.ActiveWhen.Select(term => term.ToString()));
        Assert.Equal(new[] { "F7=0" }, content.Bindings[0].Gate.UnlessAny.Select(term => term.ToString()));
        var suppression = Assert.Single(part.Suppression!.Emissions!);
        Assert.Equal(new[] { "F7=0" }, suppression.Gate.ActiveWhen.Select(term => term.ToString()));
    }

    private AuthoredProject Fixture()
    {
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = _root;
        foreach (var asset in project.ProjectAssets)
        {
            string file = Path.Combine(_root, asset.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, asset.Id);
        }
        return project;
    }

    private static AuthoredBuildPlan Plan(AuthoredProject project) =>
        AuthoredBuildPlanner.Plan(project, new AuthoredBuildPlannerTests.Backend());

    private static KeyGroup Group(string? key, params (string Id, string? EditId)[] states) => new()
    {
        Id = "key-1", Key = key,
        States = states.Select(state => new KeyGroupState
        {
            Id = state.Id,
            ActiveEditIds = state.EditId is null ? new() : new() { state.EditId },
        }).ToList(),
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
