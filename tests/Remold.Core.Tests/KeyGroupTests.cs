using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

public sealed class KeyGroupTests
{
    [Fact]
    public void Two_states_are_the_floor_and_wording_points_to_group_delete()
    {
        var project = Project();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-1", Key = "F6", States = { new KeyGroupState { Id = "only" } },
        });

        Assert.Contains(AuthoredProjectValidator.Errors(project), error =>
            error.Contains("fewer than two states; delete the group instead", StringComparison.Ordinal));
    }

    [Fact]
    public void Placements_must_name_existing_edits()
    {
        var project = Project();
        project.Always.Add("missing");

        Assert.Contains(AuthoredProjectValidator.Errors(project), error =>
            error.Contains("names missing edit definition 'missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void Stable_state_ids_are_required_and_unique_within_the_group()
    {
        var project = Project();
        project.KeyGroups.Add(Group("F6", new("same"), new("same")));

        Assert.Contains(AuthoredProjectValidator.Errors(project), error =>
            error.Contains("duplicate state id 'same'", StringComparison.Ordinal));
    }

    [Fact]
    public void One_state_cannot_place_two_content_edits_for_one_part()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F6", new("one", "edit-long", "edit-short"), new("two")));

        Assert.Contains(AuthoredProjectValidator.Errors(project), error =>
            error.Contains("places content edits 'edit-long' and 'edit-short'", StringComparison.Ordinal));
    }

    [Fact]
    public void Different_states_may_place_the_parts_different_content_edits()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F6", new("one", "edit-long"), new("two", "edit-short")));

        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void Same_edit_may_be_placed_in_many_states_and_groups()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F6", new("one", "edit-long"), new("two", "edit-long")));
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-2", Key = "F7", States = { new KeyGroupState { Id = "a", ActiveEditIds = { "edit-long" } },
                new KeyGroupState { Id = "b" } },
        });

        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void Unplaced_edits_are_valid_library_entries()
    {
        var project = Project();
        project.Always.Clear();

        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void Identical_states_are_valid_and_warn_once_when_all_are_identical()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F6", new("one", "edit-long"), new("two", "edit-long"),
            new("three", "edit-long")));

        Assert.Empty(AuthoredProjectValidator.Errors(project));
        Assert.Equal("F6 switches nothing.", Assert.Single(AuthoredProjectValidator.Warnings(project)));
    }

    [Fact]
    public void An_unnamed_keyless_group_warning_never_exposes_its_internal_id()
    {
        var project = AuthoredEditFixtures.Golden();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-0042",
            States =
            {
                new KeyGroupState { Id = "state-0001" },
                new KeyGroupState { Id = "state-0002" },
            },
        });

        string warning = Assert.Single(AuthoredProjectValidator.Warnings(project));

        Assert.Equal("Unnamed key group switches nothing.", warning);
        Assert.DoesNotContain("key-0042", warning);
    }

    [Fact]
    public void Only_all_identical_states_warn()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group("F6", new("one", "edit-long"), new("two", "edit-long"), new("three")));

        Assert.Empty(AuthoredProjectValidator.Warnings(project));
    }

    [Fact]
    public void Keyless_groups_are_valid_authored_intent()
    {
        var project = Project();
        project.Always.Clear();
        project.KeyGroups.Add(Group(null, new("one", "edit-long"), new("two")));

        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void Non_null_keys_are_normalized_and_unique()
    {
        var project = Project();
        project.KeyGroups.Add(Group("f6", new("one"), new("two")));
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-2", Key = "F6", States = { new KeyGroupState { Id = "a" },
                new KeyGroupState { Id = "b" } },
        });

        var errors = AuthoredProjectValidator.Errors(project);
        Assert.Contains(errors, error => error.Contains("invalid key", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, error => error.Contains("shares key", StringComparison.Ordinal));
    }

    [Fact]
    public void Placement_order_is_not_part_ownership()
    {
        var project = Project();
        project.Always.Add("edit-short");

        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    private static AuthoredProject Project() => AuthoredEditFixtures.Golden();

    private static KeyGroup Group(string? key, params State[] states) => new()
    {
        Id = "key-1", Key = key,
        States = states.Select(state => new KeyGroupState
        {
            Id = state.Id, ActiveEditIds = state.EditIds.ToList(),
        }).ToList(),
    };

    private sealed record State(string Id, params string[] EditIds);
}
