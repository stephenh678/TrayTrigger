using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class UpdateRepositoryTests
{
    [Theory]
    [InlineData("stephenh678/TrayTrigger")]
    [InlineData("  some-org/My.Repo_2  ")]
    public void WellFormedRepository_IsKept(string value)
    {
        Assert.Equal(value.Trim(), UpdateService.NormalizeRepository(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TrayTrigger")]
    [InlineData("a/b/c")]
    [InlineData("../../users/someone")]
    [InlineData("owner/..")]
    [InlineData("owner/repo?per_page=1")]
    [InlineData("owner/repo#frag")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("owner /repo")]
    public void AnythingElse_FallsBackToTheDefault(string? value)
    {
        Assert.Equal(UpdateService.DefaultRepository, UpdateService.NormalizeRepository(value));
    }
}
