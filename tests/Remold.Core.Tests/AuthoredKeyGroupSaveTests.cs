using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

public sealed class AuthoredKeyGroupSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remold-activation-save-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Round_trip_keeps_optional_key_stable_state_ids_and_placements()
    {
        var project = AuthoredEditFixtures.Golden();
        project.Always.Clear();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-1", Key = null, Label = "Options",
            States =
            {
                new KeyGroupState { Id = "long", Label = "Long", ActiveEditIds = { "edit-long" } },
                new KeyGroupState { Id = "short", Label = "Short", ActiveEditIds = { "edit-short" } },
            },
        });

        AuthoredProjectSerializer.Save(project, _dir);
        var loaded = AuthoredProjectSerializer.Load(_dir);

        var group = Assert.Single(loaded.KeyGroups);
        Assert.Null(group.Key);
        Assert.Equal(new[] { "long", "short" }, group.States.Select(state => state.Id));
        Assert.Equal("edit-short", Assert.Single(group.States[1].ActiveEditIds));
    }

    [Fact]
    public void Json_uses_snake_case_activation_fields_and_omits_old_schema_fields()
    {
        var project = AuthoredEditFixtures.Golden();
        string json = AuthoredProjectSerializer.Serialize(project);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("always", out _));
        Assert.False(root.TryGetProperty("composition", out _));
        Assert.DoesNotContain("build_enabled", json, StringComparison.Ordinal);
        Assert.DoesNotContain("start_state", json, StringComparison.Ordinal);
        Assert.DoesNotContain("also_hidden", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"entries\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Group_json_calls_state_lists_active_edit_ids()
    {
        var project = AuthoredEditFixtures.Golden();
        project.KeyFirstPart("F6");

        string json = AuthoredProjectSerializer.Serialize(project);

        Assert.Contains("\"active_edit_ids\"", json, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"state-0001\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_group_then_save_keeps_edits_unplaced()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string group = session.CreateKeyGroup("F6", "edit-long");
        session.DeleteKeyGroup(group);

        AuthoredProjectSerializer.Save(session.Snapshot(), _dir);
        var loaded = AuthoredProjectSerializer.Load(_dir);

        Assert.Empty(loaded.KeyGroups);
        Assert.Empty(loaded.Always);
        Assert.Equal(2, loaded.EditDefinitions.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
