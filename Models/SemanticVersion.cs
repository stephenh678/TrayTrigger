using System;
using System.Collections.Generic;

namespace TrayTrigger.Models;

/// <summary>
/// Minimal SemVer 2.0 version with correct pre-release ordering, so that
/// 1.3.0-beta.1 &lt; 1.3.0-beta.2 &lt; 1.3.0-rc.1 &lt; 1.3.0. Build metadata after '+' is ignored
/// for comparison (per spec). Accepts an optional leading 'v'/'V' and 1-3 numeric components.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public SemanticVersion(int major, int minor, int patch, string? prerelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease;
    }

    public static SemanticVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string s = text.Trim().TrimStart('v', 'V');

        int plus = s.IndexOf('+');
        if (plus >= 0) s = s.Substring(0, plus);

        string? pre = null;
        int hyphen = s.IndexOf('-');
        if (hyphen >= 0)
        {
            pre = s.Substring(hyphen + 1);
            s = s.Substring(0, hyphen);
            if (pre.Length == 0) return null;
        }

        string[] parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 4) return null;

        int[] nums = new int[3];
        for (int i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
        }
        // A 4th component (System.Version style "1.2.3.0") is tolerated and ignored.
        if (parts.Length == 4 && !int.TryParse(parts[3], out _)) return null;

        return new SemanticVersion(nums[0], nums[1], nums[2], pre);
    }

    public static SemanticVersion FromVersion(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        // Same core version: a release outranks any pre-release of it.
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        return ComparePrerelease(Prerelease!, other.Prerelease!);
    }

    private static int ComparePrerelease(string a, string b)
    {
        string[] ia = a.Split('.');
        string[] ib = b.Split('.');
        int n = Math.Min(ia.Length, ib.Length);

        for (int i = 0; i < n; i++)
        {
            bool na = int.TryParse(ia[i], out int va);
            bool nb = int.TryParse(ib[i], out int vb);

            int c;
            if (na && nb) c = va.CompareTo(vb);            // numeric vs numeric
            else if (na) c = -1;                            // numeric < alphanumeric
            else if (nb) c = 1;
            else c = string.CompareOrdinal(ia[i], ib[i]);   // alphanumeric lexical
            if (c != 0) return c;
        }

        // All shared identifiers equal: the longer list is higher (beta < beta.1).
        return ia.Length.CompareTo(ib.Length);
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>"v1.3.0-beta.1" style display string.</summary>
    public string ToDisplayString() => "v" + ToString();
}
