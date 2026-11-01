using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mode.Api.Files.Processing;
using Mode.Api.Files.Scanning;
using Mode.Api.Jobs;

namespace Mode.Api.Tests.Infrastructure;

internal static class TestFiles
{
    /// <summary>The standard antivirus test string; harmless, but every scanner reports it.</summary>
    public const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    public static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}

/// <summary>Flags content containing the EICAR string, like a real scanner would.</summary>
internal sealed class FakeVirusScanner : IVirusScanner
{
    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return Encoding.ASCII.GetString(buffer.ToArray()).Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE")
            ? new ScanResult(false, "Eicar-Test-Signature")
            : new ScanResult(true, null);
    }
}

/// <summary>Behaves like the Python processor: reads and writes only through the signed internal URLs.</summary>
internal sealed class FakePreviewProcessor : IPreviewProcessor
{
    public Func<HttpClient> ClientFactory { get; set; } = () => throw new InvalidOperationException("Not wired.");

    public bool IsEnabled => true;

    public async Task<PreviewResult> ProcessAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        using var client = ClientFactory();
        var source = await client.GetByteArrayAsync(request.SourceUrl, cancellationToken);

        if (request.Extension is "png" or "jpg" or "jpeg" or "gif" or "webp")
        {
            (await client.PutAsync(request.ThumbnailUploadUrl, new ByteArrayContent(TestFiles.TinyPng), cancellationToken)).EnsureSuccessStatusCode();
            return new PreviewResult(true, true, false, null);
        }
        return request.Extension is "txt" or "csv" or "md"
            ? new PreviewResult(true, false, false, Encoding.UTF8.GetString(source))
            : new PreviewResult(false, false, false, null);
    }
}

internal sealed class CountingJobHandler : IJobHandler
{
    public static readonly ConcurrentDictionary<int, int> Runs = new();

    public string Type => "test.count";

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        Runs.AddOrUpdate(payload.GetProperty("n").GetInt32(), 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
}

internal sealed class FailingJobHandler : IJobHandler
{
    public static readonly ConcurrentBag<string> GaveUp = [];

    public string Type => "test.fail";

    public Task HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("boom");

    public Task OnGaveUpAsync(JsonElement payload, string error, CancellationToken cancellationToken)
    {
        GaveUp.Add(payload.GetProperty("id").GetString()!);
        return Task.CompletedTask;
    }
}

internal static class FileTestExtensions
{
    /// <summary>Start, send and complete an upload; returns the FileDto.</summary>
    public static async Task<JsonElement> UploadFileAsync(this HttpClient client, string roomType, Guid roomId, string name, byte[] content)
    {
        var start = await client.PostJsonOkAsync("/api/v1/files/uploads", new { room = new { type = roomType, id = roomId }, name, size = content.Length });
        var put = await client.PutAsync(start.GetProperty("uploadUrl").GetString(), new ByteArrayContent(content));
        Assert.True(put.IsSuccessStatusCode, $"PUT content -> {(int)put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
        return await client.PostJsonOkAsync($"/api/v1/files/uploads/{start.GetProperty("uploadId").GetGuid()}/complete", new { });
    }

    public static Task<HttpResponseMessage> LinkAsync(this HttpClient client, Guid fileId, string? query = null) =>
        client.GetAsync($"/api/v1/files/{fileId}/link{(query is null ? "" : "?" + query)}");

    public static async Task<string> LinkUrlAsync(this HttpClient client, Guid fileId, string? query = null)
    {
        var response = await client.LinkAsync(fileId, query);
        Assert.True(response.IsSuccessStatusCode, $"link -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
    }
}
