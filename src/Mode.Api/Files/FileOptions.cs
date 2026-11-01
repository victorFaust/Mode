using System.ComponentModel.DataAnnotations;

namespace Mode.Api.Files;

/// <summary>Bound from the "Files" section.</summary>
public sealed class FilesOptions
{
    public const string SectionName = "Files";

    /// <summary>Root folder for stored files. Back it up together with the database.</summary>
    [Required] public string StoragePath { get; set; } = "";

    /// <summary>HMAC key for download and processor links; at least 32 bytes, base64.</summary>
    [Required] public string LinkSigningKey { get; set; } = "";

    [Range(1, 2048)] public int MaxSizeMb { get; set; } = 100;

    /// <summary>Lower-case extensions without the dot. When set, replaces <see cref="DefaultExtensions"/> entirely.</summary>
    public string[] AllowedExtensions { get; set; } = [];

    public static readonly string[] DefaultExtensions =
        ["pdf", "png", "jpg", "jpeg", "gif", "webp", "txt", "csv", "md", "docx", "xlsx", "pptx", "doc", "xls", "ppt"];

    /// <summary>The configured list, or the defaults when none is configured (the binder would otherwise append to a default array).</summary>
    public IReadOnlyList<string> Extensions => AllowedExtensions.Length > 0
        ? AllowedExtensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToList()
        : DefaultExtensions;

    [Range(1, 365)] public int RecoveryDays { get; set; } = 30;
    [Range(1, 120)] public int UploadSessionMinutes { get; set; } = 15;
    [Range(1, 60)] public int DownloadLinkMinutes { get; set; } = 5;
    [Range(1, 20)] public int MaxAttachmentsPerMessage { get; set; } = 10;

    /// <summary>Base URL of the Python file processor, e.g. http://localhost:8090. Empty disables previews.</summary>
    public string ProcessorUrl { get; set; } = "";

    /// <summary>Shared key sent to the processor in X-Processor-Key.</summary>
    public string ProcessorKey { get; set; } = "";

    /// <summary>How the processor reaches this API to fetch originals and store previews.</summary>
    public string ProcessorCallbackBaseUrl { get; set; } = "";

    public long MaxSizeBytes => MaxSizeMb * 1024L * 1024L;

    internal bool IsValid(out string error)
    {
        error = "";
        byte[] key;
        try { key = Convert.FromBase64String(LinkSigningKey); }
        catch (FormatException) { key = []; }
        if (key.Length < 32) error = "Files:LinkSigningKey must be a base64 value of at least 32 bytes.";
        else if (Extensions.Any(e => e.Length == 0 || !e.All(char.IsAsciiLetterOrDigit)))
            error = "Files:AllowedExtensions must list extensions such as 'pdf' (letters and digits).";
        else if (Extensions.Any(FileNames.NeverAllowed.Contains))
            error = "Files:AllowedExtensions contains a type that is blocked by design (executables, scripts, HTML, SVG, archives).";
        else if (!string.IsNullOrEmpty(ProcessorUrl) && (string.IsNullOrEmpty(ProcessorKey) || string.IsNullOrEmpty(ProcessorCallbackBaseUrl)))
            error = "Files:ProcessorKey and Files:ProcessorCallbackBaseUrl are required when Files:ProcessorUrl is set.";
        return error.Length == 0;
    }
}
