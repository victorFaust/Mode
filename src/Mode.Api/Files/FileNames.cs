using System.Text;

namespace Mode.Api.Files;

public static class FileNames
{
    public const int MaxLength = 255;

    /// <summary>Refused even if an administrator adds them to the allowed list.</summary>
    public static readonly HashSet<string> NeverAllowed = new(StringComparer.Ordinal)
    {
        "exe", "dll", "com", "bat", "cmd", "msi", "msp", "scr", "pif", "cpl", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh",
        "hta", "jar", "sh", "app", "lnk", "reg", "html", "htm", "xhtml", "svg", "xml", "zip", "rar", "7z", "gz", "tar", "iso", "img",
    };

    private static readonly HashSet<string> ReservedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.Ordinal)
    {
        ["pdf"] = "application/pdf",
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["md"] = "text/markdown",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["doc"] = "application/msword",
        ["xls"] = "application/vnd.ms-excel",
        ["ppt"] = "application/vnd.ms-powerpoint",
    };

    /// <summary>
    /// Cleans a user-supplied file name: drops any path, control and reserved characters, trims trailing dots and spaces,
    /// and refuses device names and types that are not allowed.
    /// </summary>
    public static bool TrySanitize(string? raw, IReadOnlyCollection<string> allowedExtensions, out string name, out string extension, out string error)
    {
        name = "";
        extension = "";
        var text = (raw ?? "").Normalize(NormalizationForm.FormC);
        text = text[(Math.Max(text.LastIndexOf('/'), text.LastIndexOf('\\')) + 1)..];

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) builder.Append(' ');
            else if (!char.IsControl(c) && c is not ('<' or '>' or ':' or '"' or '|' or '?' or '*' or '\u202E' or '\u200E' or '\u200F'))
                builder.Append(c);
        }
        var cleaned = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ').TrimStart('.', ' ');

        if (cleaned.Length == 0)
        {
            error = "The file needs a name.";
            return false;
        }
        if (cleaned.Length > MaxLength)
        {
            error = $"File names can be at most {MaxLength} characters.";
            return false;
        }

        var dot = cleaned.LastIndexOf('.');
        var stem = dot > 0 ? cleaned[..dot] : cleaned;
        extension = dot > 0 ? cleaned[(dot + 1)..].ToLowerInvariant() : "";
        if (ReservedStems.Contains(stem.Split('.')[0].Trim()))
        {
            error = "This file name is reserved by Windows.";
            return false;
        }
        if (extension.Length == 0 || NeverAllowed.Contains(extension) || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            error = $"This file type is not allowed. Allowed: {string.Join(", ", allowedExtensions)}.";
            return false;
        }

        name = cleaned;
        error = "";
        return true;
    }

    public static string Normalize(string name) => name.ToLowerInvariant();

    public static string ContentTypeFor(string extension) =>
        ContentTypes.GetValueOrDefault(extension, "application/octet-stream");

    public static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }
}
