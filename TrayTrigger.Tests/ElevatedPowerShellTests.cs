using System.IO;
using System.Text;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class ElevatedPowerShellTests
{
    [Theory]
    [InlineData(@"C:\Games\Plain\game.exe", @"'C:\Games\Plain\game.exe'")]
    [InlineData(@"C:\Games\Baldur's Gate\bg.exe", @"'C:\Games\Baldur''s Gate\bg.exe'")]
    // PowerShell closes a single-quoted string on the typographic quotes too.
    [InlineData("C:\\Games\\Assassin\u2019s Creed\\ac.exe", "'C:\\Games\\Assassin\u2019\u2019s Creed\\ac.exe'")]
    [InlineData("a\u2018b\u201Ac\u201Bd", "'a\u2018\u2018b\u201A\u201Ac\u201B\u201Bd'")]
    public void QuoteLiteral_DoublesEveryCharacterPowerShellTreatsAsASingleQuote(string value, string expected)
    {
        Assert.Equal(expected, ElevatedPowerShell.QuoteLiteral(value));
    }

    [Fact]
    public void QuoteLiteral_LeavesNoUnpairedQuote_ForAnInjectionAttempt()
    {
        string literal = ElevatedPowerShell.QuoteLiteral("C:\\x\u2019; Start-Process calc; \u2018");
        string inner = literal[1..^1];

        // Every quote-like character inside the literal is part of a doubled pair.
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
            {
                Assert.True(i + 1 < inner.Length && inner[i + 1] == inner[i], $"Unpaired quote at {i} in {inner}");
                i++;
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildStartInfo_UsesTheFullPowerShellPath_AndAnEncodedCommand(bool alreadyElevated)
    {
        const string script = @"Add-MpPreference -ExclusionPath 'C:\Games\a b\g.exe'";
        var psi = ElevatedPowerShell.BuildStartInfo(script, alreadyElevated);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), psi.FileName);
        Assert.Equal(!alreadyElevated, psi.UseShellExecute);
        Assert.Equal(alreadyElevated ? string.Empty : "runas", psi.Verb);

        int flag = psi.ArgumentList.IndexOf("-EncodedCommand");
        Assert.True(flag >= 0);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(psi.ArgumentList[flag + 1])));
        Assert.DoesNotContain("-Command", psi.ArgumentList);
    }
}
