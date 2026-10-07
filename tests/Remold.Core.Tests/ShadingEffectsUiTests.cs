using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using CommunityToolkit.Mvvm.Input;
using Remold.App.ViewModels.EditPage;
using Remold.App.Views;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

[Collection("Dispatcher")]
public sealed class ShadingEffectsUiTests
{
    [Fact]
    public async Task Checkbox_command_observes_the_changed_choice_and_refusal_restores_the_binding()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Remold.App.App));
        await session.Dispatch(() =>
        {
            var row = Row();
            row.SetEffects(VolumeEffects());
            var effect = row.Effects[0];
            bool? observed = null;
            var box = new ClickableCheckBox
            {
                DataContext = effect,
                Command = new RelayCommand(() => observed = effect.IsEnabled),
            };
            box.Bind(CheckBox.IsCheckedProperty, new Avalonia.Data.Binding(nameof(effect.IsEnabled))
                { Mode = BindingMode.TwoWay });
            var window = new Window { Content = box };
            try
            {
                window.Show();
                box.PerformClick();
                Assert.False(observed);
                Assert.False(effect.IsEnabled);
                effect.ResetEnabled();
                Assert.True(box.IsChecked);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void Disabling_a_parent_keeps_the_child_choice_and_restores_its_controls()
    {
        var row = Row();
        row.SetEffects(VolumeEffects());
        var volume = row.Effects.Single(effect => effect.Id == "internal-volume");
        var matcap = row.Effects.Single(effect => effect.Id == "matcap");

        volume.IsEnabled = false;

        Assert.False(matcap.IsAvailable);
        Assert.True(matcap.IsEnabled);
        Assert.True(matcap.OriginalIsEnabled);
        Assert.False(row.CanDisableAllEffects);
        volume.IsEnabled = true;
        Assert.True(matcap.IsAvailable);
        Assert.True(matcap.IsEnabled);

        matcap.IsEnabled = false;
        volume.IsEnabled = false;
        volume.IsEnabled = true;
        Assert.True(matcap.IsAvailable);
        Assert.False(matcap.IsEnabled);
    }

    [Fact]
    public void Busy_state_and_refused_toggle_restore_the_visible_choice()
    {
        var row = Row();
        row.SetEffects(VolumeEffects());
        var volume = row.Effects[0];
        volume.IsEnabled = false;
        row.IsBusy = true;

        Assert.All(row.Effects, effect => Assert.False(effect.IsAvailable));
        Assert.False(row.CanDisableAllEffects);
        volume.ResetEnabled();
        row.IsBusy = false;
        Assert.All(row.Effects, effect => Assert.True(effect.IsAvailable));
        Assert.True(row.CanDisableAllEffects);
    }

    [Fact]
    public void Disabled_effects_are_edited_state_even_without_numeric_overrides()
    {
        var row = Row(disabled: new[] { "detail" });
        row.SetEffects(new[] { new EditShadingEffect("detail", "Detail", false, true) });

        Assert.True(row.IsEdited);
        Assert.True(row.CanRevert);
        Assert.Equal("1 effect disabled", row.Summary);
        Assert.False(row.CanDisableAllEffects);
        Assert.False(row.Effects[0].IsEdited);

        row.Effects[0].IsEnabled = true;
        Assert.True(row.Effects[0].IsEdited);
    }

    [Fact]
    public void Advanced_dialog_returns_only_the_parent_change_and_keeps_child_values()
    {
        var selection = new ShadingValuesWindow.EffectSelection(VolumeEffects());
        selection.SetEnabled("internal-volume", false);

        Assert.True(selection.IsEnabled("matcap"));
        Assert.False(selection.IsActive("matcap"));
        Assert.False(selection.IsAvailable("matcap"));
        Assert.True(selection.IsActive(null));
        Assert.False(selection.MatchesOriginal);
        var change = Assert.Single(selection.Edits);
        Assert.Equal("internal-volume", change.EffectId);
        Assert.False(change.Enabled);

        selection.SetEnabled("internal-volume", true);
        Assert.True(selection.IsActive("matcap"));
        Assert.True(selection.MatchesOriginal);
        Assert.Empty(selection.Edits);
    }

    [Fact]
    public void Advanced_dialog_preserves_a_disabled_child_when_the_parent_changes()
    {
        var selection = new ShadingValuesWindow.EffectSelection(new[]
        {
            new EditShadingEffect("internal-volume", "Internal volume", false),
            new EditShadingEffect("matcap", "Matcap", false, ParentId: "internal-volume"),
        });
        selection.SetEnabled("internal-volume", true);

        Assert.True(selection.IsAvailable("matcap"));
        Assert.False(selection.IsEnabled("matcap"));
        Assert.False(selection.MatchesOriginal);
        var change = Assert.Single(selection.Edits);
        Assert.Equal("internal-volume", change.EffectId);
        Assert.True(change.Enabled);
    }

    [Fact]
    public void Advanced_dialog_rejects_an_incomplete_or_cyclic_effect_hierarchy()
    {
        Assert.Throws<ArgumentException>(() => new ShadingValuesWindow.EffectSelection(new[]
        {
            new EditShadingEffect("matcap", "Matcap", ParentId: "missing"),
        }));
        Assert.Throws<ArgumentException>(() => new ShadingValuesWindow.EffectSelection(new[]
        {
            new EditShadingEffect("internal-volume", "Internal volume", ParentId: "matcap"),
            new EditShadingEffect("matcap", "Matcap", ParentId: "internal-volume"),
        }));
    }

    private static EditShadingEffect[] VolumeEffects() => new[]
    {
        new EditShadingEffect("internal-volume", "Internal volume"),
        new EditShadingEffect("matcap", "Matcap", ParentId: "internal-volume"),
    };

    private sealed class ClickableCheckBox : CheckBox
    {
        internal void PerformClick() => OnClick();
    }

    private static EditShadingRowVm Row(IReadOnlyList<string>? disabled = null)
    {
        var part = new TargetPart { Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "body" };
        return new EditShadingRowVm
        {
            Edit = new EditRef(part, "coat-edit", "Long coat"),
            Part = part,
            MaterialSlotIndex = 0,
            MaterialLabel = "Clothing",
            AuthoredValues = new Dictionary<string, string>(),
            AuthoredSlotIds = Array.Empty<string>(),
            DisabledEffectIds = disabled ?? Array.Empty<string>(),
        };
    }
}
