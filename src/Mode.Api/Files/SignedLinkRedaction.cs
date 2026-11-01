using System.Text.RegularExpressions;

namespace Mode.Api.Files;

/// <summary>Signed file links work like passwords until they expire, so request logs show them as [redacted].</summary>
internal static partial class SignedLinkRedaction
{
    [GeneratedRegex("(/api/v1/files/content|/internal/files)/[^/?]+", RegexOptions.IgnoreCase)]
    private static partial Regex SignedLinkPath();

    public static string Redact(string path) => SignedLinkPath().Replace(path, "$1/[redacted]");
}
