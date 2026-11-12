using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>What a built mod's files hold does not depend on the UI language.</summary>
[Collection("UiCulture")]
public class BuiltRecordLanguageTests
{
    /// <summary>The edit's name is the modder's data, minted in the UI language as a typed name would be, so
    /// under the pseudolocale it is bracketed like one. Everything the build itself writes is checked: every
    /// value but the name, and the reason with the name taken out of it.</summary>
    [Fact]
    public void A_dropped_shading_values_record_reason_carries_no_catalog_text_under_the_pseudolocale()
    {
        using var builds = new ModBuilderTests();
        try
        {
            Catalog.Apply(Catalog.Pseudo);

            var (plan, repairJson, label) = builds.BuildShadingValueNoFaceUses();

            Assert.Contains(plan.Warnings, warning => warning.StartsWith('['));
            Assert.Equal(CoreStrings.DefaultEditName(1), label);
            using var record = JsonDocument.Parse(repairJson);
            var values = StringValues(record.RootElement, "").ToList();
            Assert.Contains(values, value => value.Property == "reason" && value.Text.Contains(label));
            foreach (var (_, text) in values.Where(value => value.Property != "label"))
                Assert.DoesNotContain('[', text.Replace(label, "", StringComparison.Ordinal));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    /// <summary>Every string value in <paramref name="element"/>, with the name of the property holding
    /// it.</summary>
    private static IEnumerable<(string Property, string Text)> StringValues(JsonElement element, string property)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return (property, element.GetString()!);
                break;
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                    foreach (var value in StringValues(member.Value, member.Name)) yield return value;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var value in StringValues(item, property)) yield return value;
                break;
        }
    }

    /// <summary>A finished build replays the lines it showed, in the language it ran in, so a rebuild under
    /// another language is not served from the record the first one left.</summary>
    [Fact]
    public void A_completed_build_is_served_again_only_under_the_language_it_ran_in()
    {
        using (var same = new ModBuilderTests())
            Assert.Equal(0, same.BundleReadsAcrossRebuild(() => { }).Rebuild);

        using var other = new ModBuilderTests();
        try
        {
            var (cold, rebuild) = other.BundleReadsAcrossRebuild(() => Catalog.Apply(Catalog.Pseudo));
            Assert.True(cold > 0);
            Assert.True(rebuild > 0, "the rebuild under another language was served from the first build's record");
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}
