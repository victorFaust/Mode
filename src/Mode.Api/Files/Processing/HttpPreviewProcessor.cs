using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Mode.Api.Files.Processing;

/// <summary>Contract with the Python file processor (services/file-processor).</summary>
public sealed record PreviewRequest(
    Guid VersionId, string Extension, string ContentType, string SourceUrl, string ThumbnailUploadUrl, string PreviewUploadUrl);

public sealed record PreviewResult(bool Supported, bool Thumbnail, bool Preview, string? Text);

public interface IPreviewProcessor
{
    bool IsEnabled { get; }

    Task<PreviewResult> ProcessAsync(PreviewRequest request, CancellationToken cancellationToken);
}

public sealed class HttpPreviewProcessor(HttpClient http, IOptions<FilesOptions> options) : IPreviewProcessor
{
    public const string KeyHeader = "X-Processor-Key";

    public bool IsEnabled => !string.IsNullOrEmpty(options.Value.ProcessorUrl);

    public async Task<PreviewResult> ProcessAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.Value.ProcessorUrl), "/v1/process"))
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add(KeyHeader, options.Value.ProcessorKey);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PreviewResult>(cancellationToken)
            ?? throw new InvalidOperationException("The file processor returned an empty response.");
    }
}
