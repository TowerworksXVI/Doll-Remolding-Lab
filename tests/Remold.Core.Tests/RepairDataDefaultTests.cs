using System.IO;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The repair-data default as a stored value: what an old settings file reads as, and what a
/// caller that names nothing gets. Nothing here touches the shared settings.json, so it stays out of the
/// dispatcher collection.</summary>
public class RepairDataDefaultTests
{
    [Fact]
    public void A_settings_file_written_before_the_key_includes_repair_data()
    {
        // Every build that could have written such a file shipped the record, so absent must read as on.
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, """{ "Author": "TestAuthor" }""");

        Assert.True(LabSettings.Load(path).IncludeRepairData);
    }

    [Fact]
    public void The_default_round_trips_through_settings()
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        new LabSettings { IncludeRepairData = false }.Save(path);

        Assert.False(LabSettings.Load(path).IncludeRepairData);
    }

    [Fact]
    public void A_settings_save_that_names_nothing_leaves_the_record_shipping()
    {
        // Every existing caller builds a SettingsResult by naming the fields it cares about. Without a
        // default of its own the omitted flag would read false and silently stop shipping the record.
        Assert.True(new SettingsResult().IncludeRepairData);
        Assert.True(new SettingsInput().IncludeRepairData);
    }

}

