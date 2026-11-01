using System.Text.RegularExpressions;
using Mode.Domain.Files;

namespace Mode.Infrastructure.Files;

/// <summary>Stores files under one root folder on local disk.</summary>
public sealed partial class LocalFileStore : IFileStore
{
    private readonly string _root;

    public LocalFileStore(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        System.IO.Directory.CreateDirectory(_root);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*(/[a-z0-9][a-z0-9_.-]*)*$")]
    private static partial Regex KeyPattern();

    public async Task WriteAsync(string key, Stream content, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        Stream? stream = File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken)
    {
        var info = new FileInfo(PathFor(key));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (!KeyPattern().IsMatch(key) || key.Contains(".."))
            throw new ArgumentException($"Invalid storage key '{key}'.", nameof(key));

        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Storage key '{key}' escapes the storage root.", nameof(key));
        return path;
    }
}
