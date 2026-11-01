using System.ComponentModel.DataAnnotations;

namespace Mode.Infrastructure.Directory;

public enum UserIdType { Guid, Int32, Int64, String }

/// <summary>Where and how to read the host application's users table. Bound from the "UserDirectory" section.</summary>
public sealed class UserDirectoryOptions
{
    public const string SectionName = "UserDirectory";

    [Required]
    public string ConnectionString { get; set; } = "";

    /// <summary>"table" or "schema.table". Must be plain identifiers; quoted by Mode.</summary>
    [Required]
    public string Table { get; set; } = "dbo.Users";

    public UserIdType IdType { get; set; } = UserIdType.Guid;

    [Required] public string IdColumn { get; set; } = "Id";
    [Required] public string DisplayNameColumn { get; set; } = "DisplayName";
    [Required] public string EmailColumn { get; set; } = "Email";

    /// <summary>Leave empty if the host has no avatar column.</summary>
    public string AvatarUrlColumn { get; set; } = "AvatarUrl";

    [Required] public string ActiveColumn { get; set; } = "IsActive";

    [Range(0, 60)]
    public int CacheMinutes { get; set; } = 5;

    [Range(1, 60)]
    public int CommandTimeoutSeconds { get; set; } = 5;

    internal bool HasValidIdentifiers() =>
        SqlIdentifier.IsValidTable(Table)
        && SqlIdentifier.IsValidColumn(IdColumn)
        && SqlIdentifier.IsValidColumn(DisplayNameColumn)
        && SqlIdentifier.IsValidColumn(EmailColumn)
        && (string.IsNullOrEmpty(AvatarUrlColumn) || SqlIdentifier.IsValidColumn(AvatarUrlColumn))
        && SqlIdentifier.IsValidColumn(ActiveColumn);
}
