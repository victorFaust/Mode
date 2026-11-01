using System.Text.RegularExpressions;

namespace Mode.Infrastructure.Directory;

/// <summary>Table and column names come from configuration, so only plain identifiers are accepted.</summary>
internal static partial class SqlIdentifier
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex PartPattern();

    public static bool IsValidColumn(string? name) => name is not null && PartPattern().IsMatch(name);

    public static bool IsValidTable(string? name)
    {
        if (name is null) return false;
        var parts = name.Split('.');
        return parts.Length is 1 or 2 && parts.All(IsValidColumn);
    }

    public static string Quote(string name) => string.Join('.', name.Split('.').Select(p => $"[{p}]"));

    /// <summary>Escapes LIKE wildcards for use with <c>ESCAPE '\'</c>.</summary>
    public static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_").Replace("[", @"\[");
}
