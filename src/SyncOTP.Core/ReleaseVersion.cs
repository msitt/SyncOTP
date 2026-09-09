using System.Globalization;

namespace SyncOTP.Core;

/// <summary>
/// A semantic version, in either of the two shapes SyncOTP actually deals with: a release tag
/// ("v0.2.0") and <see cref="AppVersion.Display"/> ("0.1.1"). Hand-rolled because SyncOTP.Core
/// takes no dependencies, and the release process only ever produces a handful of shapes.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<ReleaseVersion>
{
    private string Pre => PreRelease ?? "";

    public bool IsPreRelease => Pre.Length > 0;

    /// <summary>
    /// Parses a version, tolerating a leading "v", a "+sha" build-metadata suffix, and a missing
    /// minor or patch component. Returns false for anything it cannot make sense of, which callers
    /// treat as "do not offer an update".
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var rest = text.Trim();

        if (rest[0] is 'v' or 'V') rest = rest[1..];

        // Build metadata never affects precedence, so it goes before anything else is decided.
        var plus = rest.IndexOf('+');
        if (plus >= 0) rest = rest[..plus];

        var pre = "";
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            pre = rest[(dash + 1)..];
            rest = rest[..dash];
            if (pre.Length == 0) return false;
        }

        if (rest.Length == 0) return false;

        var parts = rest.Split('.');
        if (parts.Length > 4) return false;

        var core = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseComponent(parts[i], out var value)) return false;

            // A fourth component only ever arrives from AppVersion's Version.ToString() fallback,
            // where it carries no meaning, so it is accepted and ignored.
            if (i < 3) core[i] = value;
        }

        version = new ReleaseVersion(core[0], core[1], core[2], pre);
        return true;
    }

    /// <summary>
    /// True only when both versions parse and <paramref name="candidate"/> sorts strictly after
    /// <paramref name="current"/>. Fails closed: an unreadable version is never an update.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var offered) &&
        TryParse(current, out var running) &&
        offered.CompareTo(running) > 0;

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        return ComparePreRelease(Pre, other.Pre);
    }

    public override string ToString() =>
        Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

    private static bool TryParseComponent(string part, out int value)
    {
        value = 0;
        if (part.Length == 0) return false;

        // int.TryParse would accept a sign, whitespace and digits the release process never emits.
        foreach (var c in part)
        {
            if (c is < '0' or > '9') return false;
        }

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0) return 0;

        // A release always outranks a prerelease of the same core version.
        if (left.Length == 0) return 1;
        if (right.Length == 0) return -1;

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);

        for (var i = 0; i < shared; i++)
        {
            var result = CompareIdentifier(leftParts[i], rightParts[i]);
            if (result != 0) return result;
        }

        // Where one list is a prefix of the other, the longer one wins: rc.1 outranks rc.
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = TryParseIdentifier(left, out var leftValue);
        var rightNumeric = TryParseIdentifier(right, out var rightValue);

        if (leftNumeric && rightNumeric) return leftValue.CompareTo(rightValue);

        // Numeric identifiers compare as numbers, so beta.10 outranks beta.2 instead of sorting
        // before it as text, and any alphanumeric identifier outranks a bare number.
        if (leftNumeric) return -1;
        if (rightNumeric) return 1;

        return string.CompareOrdinal(left, right);
    }

    private static bool TryParseIdentifier(string part, out long value)
    {
        value = 0;
        if (part.Length == 0) return false;

        foreach (var c in part)
        {
            if (c is < '0' or > '9') return false;
        }

        return long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
