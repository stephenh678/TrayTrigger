using TrayTrigger.Models;

namespace TrayTrigger.Tests;

/// <summary>
/// The operators compare versions, not references, and accept null on either side the way
/// CompareTo(null) does: null is below every version and equal only to null.
/// </summary>
public class SemanticVersionOperatorTests
{
    private static SemanticVersion V(string s) => SemanticVersion.TryParse(s)!;

    [Fact]
    public void EqualityOperators_CompareValues()
    {
        Assert.True(V("1.4.4-beta.1") == V("v1.4.4-beta.1"));
        Assert.False(V("1.4.4-beta.1") != V("v1.4.4-beta.1"));
        Assert.True(V("1.4.4") != V("1.4.4-beta.1"));
        Assert.False(V("1.4.4") == V("1.4.5"));
    }

    [Fact]
    public void Operators_TreatNullAsLowest()
    {
        SemanticVersion? none = null;
        var some = V("1.0.0");

        Assert.True(none == null);
        Assert.False(none == some);
        Assert.True(none != some);

        Assert.False(none > some);
        Assert.True(none < some);
        Assert.False(none >= some);
        Assert.True(none <= some);

        Assert.True(some > none);
        Assert.False(some < none);
        Assert.True(some >= none);
        Assert.False(some <= none);

        Assert.True(none >= null);
        Assert.True(none <= null);
        Assert.False(none > null);
        Assert.False(none < null);
    }
}
