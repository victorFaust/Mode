using System.Collections.Concurrent;
using Mode.Domain.Directory;

namespace Mode.Api.Tests.Infrastructure;

internal sealed class FakeUserDirectory : IUserDirectory
{
    private readonly ConcurrentDictionary<string, DirectoryUser> _users = new();

    public void Add(string id, string displayName, bool active = true) =>
        _users[id] = new DirectoryUser(id, displayName, $"{id}@test.example", null, active);

    public void SetActive(string id, bool active) => _users[id] = _users[id] with { IsActive = active };

    public bool TryNormalizeId(string? userId, out string normalized)
    {
        normalized = userId?.Trim().ToLowerInvariant() ?? "";
        return normalized.Length is > 0 and <= 64;
    }

    public Task<DirectoryUser?> GetByIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryNormalizeId(userId, out var id) ? _users.GetValueOrDefault(id) : null);

    public Task<IReadOnlyDictionary<string, DirectoryUser>> GetManyAsync(IEnumerable<string> userIds, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, DirectoryUser>();
        foreach (var raw in userIds)
            if (TryNormalizeId(raw, out var id) && _users.TryGetValue(id, out var user))
                found[id] = user;
        return Task.FromResult<IReadOnlyDictionary<string, DirectoryUser>>(found);
    }

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DirectoryUser>>(_users.Values
            .Where(u => u.IsActive && u.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList());

    public async Task<bool> IsActiveAsync(string userId, CancellationToken cancellationToken = default) =>
        (await GetByIdAsync(userId, cancellationToken))?.IsActive == true;
}
