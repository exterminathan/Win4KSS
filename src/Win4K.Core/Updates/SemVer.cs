using System.Globalization;

namespace Win4K.Core.Updates;

/// <summary>
/// A release version (<c>Major.Minor.Patch</c>, with an optional pre-release suffix), compared
/// numerically rather than as text. Lexicographic comparison gets some pairs right by accident
/// (e.g. "1.1.0" vs "1.10.0") and others wrong (e.g. "1.9.0" vs "1.10.0", where "9" &gt; "1" as
/// text but 9 &lt; 10 as a number) — SemVerTests covers both. GitHub tag names are accepted with
/// or without a leading "v" ("v1.2.3"); a trailing
/// build-metadata suffix ("+abc") is dropped (it never affects precedence); a 4th, assembly-style
/// component ("1.2.3.0", from .NET's AssemblyVersion) is accepted and ignored.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
            s = s[1..];

        // Build metadata never affects precedence, so it is dropped before anything else.
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
                return false;
        }

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        if (!TryParseNonNegative(parts[0], out var major) || !TryParseNonNegative(parts[1], out var minor))
            return false;

        var patch = 0;
        if (parts.Length >= 3 && !TryParseNonNegative(parts[2], out patch))
            return false;

        // parts[3], if present, is the assembly "revision" component (AssemblyVersion always
        // has four parts); it carries no release-version meaning and is intentionally ignored.
        if (parts.Length == 4 && !TryParseNonNegative(parts[3], out _))
            return false;

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    private static bool TryParseNonNegative(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(SemVer other)
    {
        var byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0) return byMajor;

        var byMinor = Minor.CompareTo(other.Minor);
        if (byMinor != 0) return byMinor;

        var byPatch = Patch.CompareTo(other.Patch);
        if (byPatch != 0) return byPatch;

        // Same Major.Minor.Patch: a release (no pre-release tag) always outranks a pre-release
        // of it (semver precedence rules); two pre-releases compare as plain text, which is
        // good enough here — GitHub's own `prerelease` flag is what actually gates these from
        // ever being compared in practice (see UpdateChecker.Evaluate).
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
