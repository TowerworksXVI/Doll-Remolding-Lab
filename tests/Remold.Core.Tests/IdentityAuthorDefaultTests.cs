using Remold.App.ViewModels;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Build pane's Author field IS the default-author setting. A modder types their handle once, on the mod
/// they are making, and every mod after it starts with it — there is no separate place to set it and no save
/// button to press. It rides the identity save, so the same coalesced tick that persists the mod persists
/// this.
/// </summary>
[Collection("Dispatcher")]
public class IdentityAuthorDefaultTests
{
    [Fact]
    public void EditingTheAuthor_BecomesTheDefaultTheNextNewModPrefills()
    {
        var vm = new MainWindowViewModel(startLoad: false);

        vm.PackageAuthor = "  Wren  ";
        vm.FlushIdentitySave();
        vm.NewMod();

        Assert.Equal("Wren", vm.PackageAuthor);   // trimmed, as the identity form reads it
    }

    [Fact]
    public void ClearingTheAuthor_ClearsTheDefaultToo()
    {
        // whatever the field says is the default — a modder who empties it means it, and a new mod must not
        // silently re-fill the handle they just removed
        var vm = new MainWindowViewModel(startLoad: false);
        vm.PackageAuthor = "Wren";
        vm.FlushIdentitySave();

        vm.PackageAuthor = "";
        vm.FlushIdentitySave();
        vm.NewMod();

        Assert.Equal("", vm.PackageAuthor);
    }
}
