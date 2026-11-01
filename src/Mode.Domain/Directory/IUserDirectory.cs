namespace Mode.Domain.Directory;

/// <summary>A person from the host application's user store. <see cref="Id"/> is in normalized form.</summary>
public sealed record DirectoryUser(string Id, string DisplayName, string? Email, string? AvatarUrl, bool IsActive);

/// <summary>Read-only view of the host application's users. Mode never writes user records.</summary>
public interface IUserDirectory
{
    Task<DirectoryUser?> GetByIdAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Returns found users keyed by normalized id; unknown or malformed ids are omitted.</summary>
    Task<IReadOnlyDictionary<string, DirectoryUser>> GetManyAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default);

    /// <summary>Active users whose name or email contains <paramref name="query"/>.</summary>
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);

    Task<bool> IsActiveAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Canonical form of a host user id (e.g. lowercase GUID), without a database call.</summary>
    bool TryNormalizeId(string? userId, out string normalized);
}
