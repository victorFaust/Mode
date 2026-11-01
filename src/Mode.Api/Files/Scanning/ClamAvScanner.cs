using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Mode.Api.Files.Scanning;

public sealed record ScanResult(bool IsClean, string? Threat);

/// <summary>The scanner refused the file outright (e.g. above its size limit); retrying will not help.</summary>
public sealed class ScanRejectedException(string message) : Exception(message);

public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken);
}

public sealed class ClamAvOptions
{
    public const string SectionName = "ClamAv";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>Streams content to clamd with the INSTREAM command.</summary>
public sealed class ClamAvScanner(IOptions<ClamAvOptions> options) : IVirusScanner
{
    private const int ChunkSize = 64 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var ct = timeout.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(settings.Host, settings.Port, ct);
        await using var stream = client.GetStream();

        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);
        var buffer = new byte[ChunkSize];
        var prefix = new byte[4];
        int read;
        try
        {
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)read);
                await stream.WriteAsync(prefix, ct);
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            BinaryPrimitives.WriteUInt32BigEndian(prefix, 0);
            await stream.WriteAsync(prefix, ct);
        }
        catch (IOException)
        {
            // clamd closes the socket once its StreamMaxLength is exceeded; read the reason below.
        }

        var reply = await ReadReplyAsync(stream, ct);
        if (reply.EndsWith(" OK", StringComparison.Ordinal)) return new ScanResult(true, null);
        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
            return new ScanResult(false, reply["stream: ".Length..^" FOUND".Length]);
        if (reply.Contains("size limit exceeded", StringComparison.OrdinalIgnoreCase))
            throw new ScanRejectedException("The file is larger than the virus scanner accepts (raise StreamMaxLength in clamd.conf).");
        throw new InvalidOperationException($"Unexpected ClamAV reply: '{reply}'.");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var reply = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var end = Array.IndexOf(buffer, (byte)0, 0, read);
            reply.Write(buffer, 0, end >= 0 ? end : read);
            if (end >= 0 || reply.Length > 4096) break;
        }
        return Encoding.ASCII.GetString(reply.ToArray()).Trim();
    }
}
