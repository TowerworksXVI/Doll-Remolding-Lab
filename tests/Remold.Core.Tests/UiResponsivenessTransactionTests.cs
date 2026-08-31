using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public sealed class UiResponsivenessTransactionTests
{
    private const string CharacterName = "Vesna";

    private static CharacterVm PickRow(MainWindowViewModel window, params string[] stems)
    {
        var outfits = stems.Select((stem, index) =>
            new Outfit(index + 1, stem, OutfitKind.Alt)).ToList();
        var model = new Character(1, CharacterName, CharacterName, 1, 1, outfits);
        var row = new CharacterVm(model, window.AddSubject, window.OnCharacterToggled);
        row.Populate(outfits.Select(outfit =>
            (outfit, (IEnumerable<string>)new[] { "body" })));
        return row;
    }

    private static async Task<MainWindowViewModel> OpenAsync(string root, AuthoredProject project)
    {
        string author = LabSettings.Load().Author;
        project.Info.Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        Directory.CreateDirectory(root);
        AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
        var window = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await window.OpenModAsync(root));
        await WaitForPlansAsync(window, 1);
        return window;
    }

    private static async Task WaitForPlansAsync(MainWindowViewModel window, int minimum)
    {
        for (int i = 0; i < 500; i++)
        {
            if (window.BuildPlanRuns >= minimum && !window.BuildPage.IsPlanning) return;
            await Task.Delay(5);
        }
        throw new TimeoutException($"Only {window.BuildPlanRuns} build plans settled.");
    }

    private static AuthoredProject EmptyProject() => new()
    {
        Info = new ProjectInfo { Name = "Gesture test", Version = "1.0", IncludeRepairData = true },
        WorkspaceIndex = new AuthoredWorkspaceIndex(),
    };

    [Fact]
    public async Task Subject_checkbox_is_one_revision_one_save_and_one_replan()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        var project = EmptyProject();
        var window = await OpenAsync(temp.At(ModNaming.Slug(project.Info.Name)), project);
        var row = PickRow(window, "VesnaSSR01");
        var session = window.EditSession;
        long revision = session.Revision;
        int saves = window.ProjectSaves;
        int plans = window.BuildPlanRuns;
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);

        row.Outfits[0].IsInMod = true;
        await WaitForPlansAsync(window, plans + 1);
        await Task.Delay(30);

        Assert.True(session.Revision == revision + 1,
            string.Join(", ", changes.Select(change => $"{change.Revision}:{change.Invalidation}")));
        Assert.Single(changes);
        Assert.Equal(saves + 1, window.ProjectSaves);
        Assert.Equal(plans + 1, window.BuildPlanRuns);
    }

    [Fact]
    public async Task Character_checkbox_is_one_revision_one_save_and_one_replan()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        var project = EmptyProject();
        var window = await OpenAsync(temp.At(ModNaming.Slug(project.Info.Name)), project);
        var row = PickRow(window, "VesnaSSR01", "VesnaSSR02", "VesnaSSR03");
        var session = window.EditSession;
        long revision = session.Revision;
        int saves = window.ProjectSaves;
        int plans = window.BuildPlanRuns;
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);

        row.CharacterInMod = true;
        await WaitForPlansAsync(window, plans + 1);
        await Task.Delay(30);

        Assert.True(session.Revision == revision + 1,
            string.Join(", ", changes.Select(change => $"{change.Revision}:{change.Invalidation}")));
        Assert.Single(changes);
        Assert.Equal(3, session.Snapshot().WorkspaceIndex!.Selection.Count);
        Assert.Equal(saves + 1, window.ProjectSaves);
        Assert.Equal(plans + 1, window.BuildPlanRuns);
    }

    [Fact]
    public async Task Multi_slot_mesh_revert_is_one_revision_one_save_and_one_replan()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        var project = AuthoredEditFixtures.Golden();
        var geometry = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-geometry-lod1",
            Part = AuthoredEditFixtures.Body,
            Tier = "lod1",
            Input = TargetInputKind.Geometry,
            Renderer = geometry.Renderer,
            Mesh = geometry.Mesh,
        });
        foreach (var contentEdit in project.EditDefinitions.Where(candidate =>
                     candidate.Kind == EditDefinitionKind.Content))
        {
            contentEdit.Bindings.Add(new Binding
            {
                SlotId = "slot-geometry-lod1",
                Kind = BindingKind.ProjectAsset,
                ProjectAssetId = contentEdit.Bindings.Single(binding =>
                    binding.SlotId == "slot-geometry").ProjectAssetId,
            });
        }
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        var window = await OpenAsync(temp.At(ModNaming.Slug(project.Info.Name)), project);
        window.ConfirmForTests = (_, _, _, _) => Task.FromResult(true);
        var session = window.EditSession;
        var node = window.EditPage.Nodes.SelectMany(subject => subject.Children)
            .SelectMany(part => part.Children)
            .Single(candidate => candidate.EditDefinitionId == "edit-long");
        Assert.Equal(2, session.Slots("edit-long").Count(state =>
            state.Slot.Input == TargetInputKind.Geometry
            && state.Binding.Kind != BindingKind.TargetGameValue));
        long revision = session.Revision;
        int saves = window.ProjectSaves;
        int plans = window.BuildPlanRuns;
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);

        await window.EditPage.RevertMeshCommand.ExecuteAsync(node);
        await WaitForPlansAsync(window, plans + 1);
        await Task.Delay(30);

        Assert.Equal(revision + 1, session.Revision);
        Assert.Single(changes);
        Assert.All(session.Slots("edit-long").Where(state => state.Slot.Input == TargetInputKind.Geometry),
            state => Assert.Equal(BindingKind.TargetGameValue, state.Binding.Kind));
        Assert.Equal(saves + 1, window.ProjectSaves);
        Assert.Equal(plans + 1, window.BuildPlanRuns);
    }

    [Fact]
    public void Ledger_sync_matches_the_old_walk_on_a_mixed_roster_and_is_quiet_when_unchanged()
    {
        static CharacterVm Row(string name, params string[] stems)
        {
            var outfits = stems.Select((stem, index) =>
                new Outfit(index + 1, stem, OutfitKind.Alt)).ToList();
            var row = new CharacterVm(new Character(1, name, name, 1, 1, outfits), (_, _) => { }, (_, _) => { });
            row.Populate(outfits.Select(outfit =>
                (outfit, (IEnumerable<string>)Array.Empty<string>())));
            return row;
        }

        var alpha = Row("Alpha", "A1", "A2");
        var beta = Row("Beta", "B1");
        var gamma = Row("Gamma", "G1");
        var rows = new[] { alpha, beta, gamma };
        foreach (var outfit in rows.SelectMany(row => row.Outfits))
        {
            outfit.SetInModSilently(true);
            outfit.HasEdits = true;
        }
        var project = new AuthoredProject
        {
            WorkspaceIndex = new AuthoredWorkspaceIndex
            {
                Selection =
                {
                    new SelectionEntry { Character = "ALPHA", Outfit = "a2" },
                    new SelectionEntry { Character = "beta", Outfit = "B1" },
                },
            },
            EditDefinitions =
            {
                new EditDefinition
                {
                    Id = "content-alpha", Kind = EditDefinitionKind.Content,
                    Target = new TargetPart { Subject = "alpha", Outfit = "A2", RendererSlot = "body" },
                },
                new EditDefinition
                {
                    Id = "hide-beta", Kind = EditDefinitionKind.Hide,
                    Target = new TargetPart { Subject = "BETA", Outfit = "b1", RendererSlot = "body" },
                },
                new EditDefinition
                {
                    Id = "content-unselected", Kind = EditDefinitionKind.Content,
                    Target = new TargetPart { Subject = "gamma", Outfit = "g1", RendererSlot = "body" },
                },
            },
        };

        Assert.True(MainWindowViewModel.ApplySubjectLedger(project, rows));
        Assert.False(alpha.Outfits[0].IsInMod);
        Assert.False(alpha.Outfits[0].HasEdits);
        Assert.True(alpha.Outfits[1].IsInMod);
        Assert.True(alpha.Outfits[1].HasEdits);
        Assert.True(beta.Outfits[0].IsInMod);
        Assert.False(beta.Outfits[0].HasEdits);
        Assert.False(gamma.Outfits[0].IsInMod);
        Assert.False(gamma.Outfits[0].HasEdits);

        int notifications = 0;
        void Count(object? _, PropertyChangedEventArgs __) => notifications++;
        foreach (var row in rows)
        {
            row.PropertyChanged += Count;
            foreach (var outfit in row.Outfits) outfit.PropertyChanged += Count;
        }
        Assert.False(MainWindowViewModel.ApplySubjectLedger(project, rows));
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void Resolved_part_cache_survives_project_switch_and_dies_on_rescan_or_install_replacement()
    {
        var cache = new InstallResolvedPartCache();
        var install = new object();
        var nextInstall = new object();
        var part = AuthoredEditFixtures.Body;
        var resolved = new LegacyResolvedPart(part, new GameAssetRef(), new GameAssetRef(),
            Array.Empty<LegacyResolvedMaterial>());

        cache.Store(install, part, resolved);
        var switchedProject = new AuthoredProject();
        Assert.NotNull(switchedProject);
        Assert.True(cache.TryGet(install, part, out var afterSwitch));
        Assert.Same(resolved, afterSwitch);

        cache.Clear();
        Assert.False(cache.TryGet(install, part, out _));
        cache.Store(install, part, resolved);
        Assert.False(cache.TryGet(nextInstall, part, out _));
        Assert.Equal(0, cache.Count);
    }
}
