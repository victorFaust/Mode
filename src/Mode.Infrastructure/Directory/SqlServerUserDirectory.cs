using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mode.Domain.Directory;
using System.Data;

namespace Mode.Infrastructure.Directory;

/// <summary>Reads users from the host application's SQL Server database with a read-only login.</summary>
public sealed class SqlServerUserDirectory : IUserDirectory
{
    internal const int MaxIdsPerQuery = 200;
    internal const int MaxSearchResults = 50;

    private readonly UserDirectoryOptions _options;
    private readonly IMemoryCache _cache;
    private readonly string _selectColumns;
    private readonly string _table;

    public SqlServerUserDirectory(IOptions<UserDirectoryOptions> options, IMemoryCache cache)
    {
        _options = options.Value;
        if (!_options.HasValidIdentifiers())
            throw new InvalidOperationException("UserDirectory table or column names are not plain SQL identifiers.");

        _cache = cache;
        _table = SqlIdentifier.Quote(_options.Table);
        var avatar = string.IsNullOrEmpty(_options.AvatarUrlColumn) ? "NULL" : SqlIdentifier.Quote(_options.AvatarUrlColumn);
        _selectColumns = string.Join(", ",
            SqlIdentifier.Quote(_options.IdColumn),
            SqlIdentifier.Quote(_options.DisplayNameColumn),
            SqlIdentifier.Quote(_options.EmailColumn),
            avatar,
            SqlIdentifier.Quote(_options.ActiveColumn));
    }

    /// <summary>Selects zero rows; fails if the table or any configured column is missing or unreadable.</summary>
    internal string ProbeSql => $"SELECT TOP (0) {_selectColumns} FROM {_table}";

    public async Task<DirectoryUser?> GetByIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(userId, out var normalized, out _)) return null;
        var users = await GetManyAsync([userId], cancellationToken);
        return users.GetValueOrDefault(normalized);
    }

    public async Task<IReadOnlyDictionary<string, DirectoryUser>> GetManyAsync(
        IEnumerable<string> userIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, DirectoryUser>(StringComparer.Ordinal);
        var toFetch = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var id in userIds)
        {
            if (!TryNormalize(id, out var normalized, out var dbValue)) continue;
            if (_cache.TryGetValue(CacheKey(normalized), out DirectoryUser? cached) && cached is not null)
                result[normalized] = cached;
            else
                toFetch.TryAdd(normalized, dbValue);
        }

        foreach (var chunk in toFetch.Values.Chunk(MaxIdsPerQuery))
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var sql = new StringBuilder($"SELECT {_selectColumns} FROM {_table} WHERE {SqlIdentifier.Quote(_options.IdColumn)} IN (");
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append("@p").Append(i);
                command.Parameters.Add(CreateIdParameter($"@p{i}", chunk[i]));
            }
            sql.Append(')');
            command.CommandText = sql.ToString();
            command.CommandTimeout = _options.CommandTimeoutSeconds;

            foreach (var user in await ReadUsersAsync(command, cancellationToken))
                result[user.Id] = user;
        }

        return result;
    }

    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var term = query.Trim();
        if (term.Length == 0) return [];
        limit = Math.Clamp(limit, 1, MaxSearchResults);

        var name = SqlIdentifier.Quote(_options.DisplayNameColumn);
        var email = SqlIdentifier.Quote(_options.EmailColumn);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT TOP (@limit) {_selectColumns} FROM {_table} " +
            $"WHERE {SqlIdentifier.Quote(_options.ActiveColumn)} = 1 " +
            $"AND ({name} LIKE @pattern ESCAPE '\\' OR {email} LIKE @pattern ESCAPE '\\') " +
            $"ORDER BY {name}";
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
        command.Parameters.Add(new SqlParameter("@pattern", SqlDbType.NVarChar, 512) { Value = $"%{SqlIdentifier.EscapeLike(term)}%" });

        return await ReadUsersAsync(command, cancellationToken);
    }

    public async Task<bool> IsActiveAsync(string userId, CancellationToken cancellationToken = default) =>
        (await GetByIdAsync(userId, cancellationToken))?.IsActive == true;

    public bool TryNormalizeId(string? userId, out string normalized) => TryNormalize(userId, out normalized, out _);

    internal async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<List<DirectoryUser>> ReadUsersAsync(SqlCommand command, CancellationToken cancellationToken)
    {
        var users = new List<DirectoryUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = FormatId(reader.GetValue(0));
            var email = reader.IsDBNull(2) ? null : reader.GetString(2);
            var displayName = reader.IsDBNull(1) ? email ?? id : reader.GetString(1);
            var avatar = reader.IsDBNull(3) ? null : reader.GetString(3);
            var active = !reader.IsDBNull(4) && Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);

            var user = new DirectoryUser(id, displayName, email, avatar, active);
            if (_options.CacheMinutes > 0)
                _cache.Set(CacheKey(id), user, TimeSpan.FromMinutes(_options.CacheMinutes));
            users.Add(user);
        }
        return users;
    }

    internal bool TryNormalize(string? raw, out string normalized, out object dbValue)
    {
        normalized = "";
        dbValue = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();

        switch (_options.IdType)
        {
            case UserIdType.Guid when Guid.TryParse(raw, out var g):
                dbValue = g;
                break;
            case UserIdType.Int32 when int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i):
                dbValue = i;
                break;
            case UserIdType.Int64 when long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var l):
                dbValue = l;
                break;
            case UserIdType.String when raw.Length <= Domain.UserIds.MaxLength:
                dbValue = raw;
                break;
            default:
                return false;
        }

        normalized = FormatId(dbValue);
        return true;
    }

    private static string FormatId(object value) => value switch
    {
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private SqlParameter CreateIdParameter(string name, object value) => _options.IdType switch
    {
        UserIdType.Guid => new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = value },
        UserIdType.Int32 => new SqlParameter(name, SqlDbType.Int) { Value = value },
        UserIdType.Int64 => new SqlParameter(name, SqlDbType.BigInt) { Value = value },
        _ => new SqlParameter(name, SqlDbType.NVarChar, Domain.UserIds.MaxLength) { Value = value },
    };

    private static string CacheKey(string normalizedId) => "mode:dir:user:" + normalizedId;
}
