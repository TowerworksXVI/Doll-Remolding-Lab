using System;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Text;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Every on-screen name a table switches to the UI language has its own catalog entry: under the
/// pseudolocale each comes back bracketed, so a table entry the switch misses shows up as unbracketed
/// text.</summary>
[Collection("UiCulture")]
public class DisplayLabelLanguageTests
{
    private static void UnderThePseudolocale(Action check)
    {
        try
        {
            Catalog.Apply(Catalog.Pseudo);
            check();
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    private static void Bracketed(string label) =>
        Assert.True(label.StartsWith('[') && label.EndsWith(']'), $"'{label}' is not read from the catalog");

    [Fact]
    public void Every_shading_value_has_a_catalog_name()
    {
        UnderThePseudolocale(() =>
        {
            Assert.Equal(45, MaterialValueCatalog.Fields.Count);
            foreach (var field in MaterialValueCatalog.Fields) Bracketed(field.DisplayLabel);
        });
    }

    [Fact]
    public void Every_named_shader_property_and_every_map_kind_has_a_catalog_name()
    {
        UnderThePseudolocale(() =>
        {
            Assert.Equal(16, TextureMap.ShaderPropertyLabels.Count);
            foreach (string property in TextureMap.ShaderPropertyLabels.Keys)
            {
                Bracketed(TextureMap.DisplayPropertyLabel(property));
                Bracketed(TextureMap.DisplaySlotLabel(TargetInputKind.Texture, property));
            }
            foreach (var input in new[]
                     {
                         TargetInputKind.BaseColor, TargetInputKind.Normal, TargetInputKind.Rmo,
                         TargetInputKind.Blend, TargetInputKind.Ramp,
                     })
                Bracketed(TextureMap.DisplaySlotLabel(input, null));
            foreach (string suffix in new[] { "d", "da", "n", "rmo", "spc", "trans", "emi", "e" })
                Bracketed(TextureMap.DisplayLabel("x_" + suffix));
        });
    }

    [Fact]
    public void Every_map_kind_a_build_warning_names_has_a_catalog_name()
    {
        UnderThePseudolocale(() =>
        {
            foreach (string kind in new[] { "base color", "normal", "RMO", "effect map", "effect" })
                Bracketed(ModBuilder.ShownKind(kind));
            foreach (string kind in new[] { "a", "n", "r", "b" })
                Bracketed(ModBuilder.RetextureKindWord(kind));
        });
    }

    [Fact]
    public void Every_bundle_a_build_reads_names_itself_in_the_catalog_when_the_install_cannot_serve_it()
    {
        UnderThePseudolocale(() =>
        {
            var probe = new SubjectPoolProbe(new BuildEnv((_, _) => null, _ => null, _ => null, null, null));
            foreach (var use in Enum.GetValues<SubjectPoolProbe.BundleUse>())
                Bracketed(Assert.Throws<AuthoredRefusalException>(() => probe.Bundle("absent", use, "part")).Message);
        });
    }
}
