using System;
using System.Collections.Generic;
using System.Linq;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.App.Views;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

public sealed class ShadingValuesWindowTests
{
    [Fact]
    public void A_copied_value_is_shown_in_its_box_and_emptying_it_clears_the_copy()
    {
        var part = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "body",
        };
        var carrier = Slot("carrier", part, MaterialValueSemantics.UseGiFlatten, "body_skinuber");
        var source = Slot("source", new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "hair",
        }, MaterialValueSemantics.UseGiFlatten, "hair_faceuber");
        var project = new AuthoredProject
        {
            TargetSlots = new List<TargetSlot> { carrier, source },
            EditDefinitions = new List<EditDefinition>
            {
                new()
                {
                    Id = "edit", Target = part, Label = "Long coat",
                    Bindings = new List<Binding>
                    {
                        new()
                        {
                            SlotId = carrier.Id, Kind = BindingKind.SourceSlot,
                            SourceSlot = new BindingSourceSlot { SlotId = source.Id },
                        },
                    },
                },
            },
        };
        var opened = MainWindowViewModel.ReadShadingDialogValues(project, "edit", 0,
            new Dictionary<string, string>
            {
                [MaterialValueSemantics.UseGiFlatten] = "",
            }, new FixedReader("0"));
        var field = new EditShadingField(MaterialValueSemantics.UseGiFlatten, "Skin lighting",
            MaterialValueKind.Float, 0, 1, "1");

        var row = Assert.Single(ShadingValuesWindow.DialogRows(new[] { field },
            opened.Values, opened.Copied, opened.UnreadableCopies));
        Assert.Equal("0", row.Initial);
        Assert.True(row.Copied);
        Assert.Null(row.Problem);
        Assert.Empty(ShadingValuesWindow.ApplyRows(new[]
        {
            new ShadingValuesWindow.DialogInput(field, row.Initial, row.Copied, "0"),
        }).Edits);

        var cleared = ShadingValuesWindow.ApplyRows(new[]
        {
            new ShadingValuesWindow.DialogInput(field, row.Initial, row.Copied, ""),
        });
        var edit = Assert.Single(cleared.Edits);
        Assert.Equal(MaterialValueSemantics.UseGiFlatten, edit.Semantic);
        Assert.Null(edit.Value);
        Assert.False(cleared.Refused);
    }

    [Fact]
    public void A_broken_copy_marks_only_its_row_and_duplicate_legacy_slots_do_not_block_open()
    {
        var part = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "body",
        };
        var sourcePart = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "hair",
        };
        var good = Slot("carrier-good", part, MaterialValueSemantics.UseGiFlatten,
            "body_skinuber");
        var duplicate = Slot("carrier-legacy-fork", part, MaterialValueSemantics.UseGiFlatten,
            "body_skinuber");
        var broken = Slot("carrier-broken", part, "_StockingCenterColor", "body_skinuber");
        var source = Slot("source-good", sourcePart, MaterialValueSemantics.UseGiFlatten,
            "hair_faceuber");
        var project = new AuthoredProject
        {
            TargetSlots = new List<TargetSlot> { good, duplicate, broken, source },
            EditDefinitions = new List<EditDefinition>
            {
                new()
                {
                    Id = "edit", Target = part, Label = "Long coat",
                    Bindings = new List<Binding>
                    {
                        new()
                        {
                            SlotId = good.Id, Kind = BindingKind.SourceSlot,
                            SourceSlot = new BindingSourceSlot { SlotId = source.Id },
                        },
                        new()
                        {
                            SlotId = broken.Id, Kind = BindingKind.SourceSlot,
                            SourceSlot = new BindingSourceSlot { SlotId = "missing-source" },
                        },
                    },
                },
            },
        };

        var opened = MainWindowViewModel.ReadShadingDialogValues(project, "edit", 0,
            new Dictionary<string, string>
            {
                [MaterialValueSemantics.UseGiFlatten] = "",
                ["_StockingCenterColor"] = "",
            }, new FixedReader("0"));

        Assert.Equal("0", opened.Values[MaterialValueSemantics.UseGiFlatten]);
        Assert.Equal("", opened.Values["_StockingCenterColor"]);
        Assert.DoesNotContain(MaterialValueSemantics.UseGiFlatten, opened.UnreadableCopies);
        Assert.Contains("_StockingCenterColor", opened.UnreadableCopies);
    }

    [Fact]
    public void An_unreadable_copy_problem_is_visible_and_untouched_input_preserves_the_copy()
    {
        var field = new EditShadingField("_StockingCenterColor", "Stocking centre colour",
            MaterialValueKind.Color, 0, 1, "0 0 0 1");
        var row = Assert.Single(ShadingValuesWindow.DialogRows(new[] { field },
            new Dictionary<string, string> { [field.Semantic] = "" },
            new HashSet<string>(StringComparer.Ordinal) { field.Semantic },
            new HashSet<string>(StringComparer.Ordinal) { field.Semantic }));

        Assert.Equal(ShadingValuesWindow.CopiedValueUnreadable, row.Problem);
        Assert.Empty(ShadingValuesWindow.ApplyRows(new[]
        {
            new ShadingValuesWindow.DialogInput(field, row.Initial, row.Copied, row.Initial),
        }).Edits);

        var typed = Assert.Single(ShadingValuesWindow.ApplyRows(new[]
        {
            new ShadingValuesWindow.DialogInput(field, row.Initial, row.Copied, "0 0 0 1"),
        }).Edits);
        Assert.Equal("0 0 0 1", typed.Value);
    }

    [Fact]
    public void Apply_rows_ignores_unchanged_originals_and_refuses_mixed_invalid_input()
    {
        var color = new EditShadingField("_StockingCenterColor", "Stocking centre colour",
            MaterialValueKind.Color, 0, 1, "0 0 0 1");
        var toggle = new EditShadingField(MaterialValueSemantics.UseGiFlatten, "Skin lighting",
            MaterialValueKind.Float, 0, 1, "1");

        var result = ShadingValuesWindow.ApplyRows(new[]
        {
            new ShadingValuesWindow.DialogInput(color, "", false, "0 0 0 1"),
            new ShadingValuesWindow.DialogInput(toggle, "", false, "0.5"),
        });

        Assert.Empty(result.Edits);
        Assert.True(result.Refused);
        Assert.Equal("Not 0 or 1.", result.Problems[MaterialValueSemantics.UseGiFlatten]);
    }

    private sealed class FixedReader(string value) : IMaterialGameValueReader
    {
        public MaterialGameValueResolution Resolve(TargetSlot sourceSlot, TargetSlot carrierSlot,
            string semantic) => new(BuildPlanVerdict.Resolved, value,
            Array.Empty<MaterialCarrierState>(), "fixture value");
    }

    private static TargetSlot Slot(string id, TargetPart part, string semantic, string material) => new()
    {
        Id = id,
        Part = part,
        Tier = "lod0",
        SubmeshIndex = 0,
        MaterialSlotIndex = 0,
        Input = TargetInputKind.MaterialValue,
        Domain = TargetSlotDomain.Game,
        Semantic = semantic,
        Renderer = Asset(part.RendererSlot, 10),
        Mesh = Asset(part.RendererSlot + "_mesh", 11),
        Material = Asset(material, 12),
    };

    private static GameAssetRef Asset(string name, long pathId) => new()
    {
        GameBuild = "fixture", LogicalBundle = "fixture.bundle", PathId = pathId, Name = name,
    };
}
