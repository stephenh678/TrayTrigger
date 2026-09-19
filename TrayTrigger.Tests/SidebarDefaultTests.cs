using System.Text.Json;
using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>UX-14i: a new install starts with the sidebar expanded; a saved choice is kept.</summary>
public class SidebarDefaultTests
{
    [Fact]
    public void NewInstall_StartsExpanded() => Assert.True(new AppSettings().IsSidebarExpanded);

    [Fact]
    public void SavedCollapsedSidebar_StaysCollapsed()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{\"IsSidebarExpanded\": false}");
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsSidebarExpanded);
    }
}
