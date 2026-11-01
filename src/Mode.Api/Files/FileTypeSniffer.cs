using System.Text;

namespace Mode.Api.Files;

/// <summary>Checks that a file's first bytes match its extension, so a renamed file cannot pass as another type.</summary>
public static class FileTypeSniffer
{
    public const int HeaderLength = 8192;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] OleCompound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly string[] MarkupStarts = ["<!doctype", "<html", "<script", "<svg", "<?xml", "<iframe", "<body", "<head"];

    /// <summary>Unknown extensions (added by configuration) pass; their content type falls back to octet-stream.</summary>
    public static bool Matches(string extension, ReadOnlySpan<byte> header) => extension switch
    {
        "png" => header.StartsWith(Png),
        "jpg" or "jpeg" => header.StartsWith(Jpeg),
        "gif" => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8),
        "webp" => header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
        "pdf" => header.StartsWith(Pdf),
        "docx" or "xlsx" or "pptx" => header.StartsWith(Zip),
        "doc" or "xls" or "ppt" => header.StartsWith(OleCompound),
        "txt" or "csv" or "md" => IsPlainText(header),
        _ => true,
    };

    private static bool IsPlainText(ReadOnlySpan<byte> header)
    {
        if (header.Contains((byte)0)) return false;
        var start = Encoding.UTF8.GetString(header[..Math.Min(header.Length, 512)]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return !MarkupStarts.Any(m => start.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }
}
