using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Mode.Api.Files;

public enum LinkPurpose { Download, ProcessorRead, ProcessorWrite }

public enum FileVariant { Original, Thumbnail, Preview }

public sealed record FileLink(LinkPurpose Purpose, Guid VersionId, FileVariant Variant, DateTimeOffset ExpiresAt);

/// <summary>
/// Short-lived, HMAC-signed links. Browsers use them where a bearer header cannot be sent (downloads, image tags);
/// the file processor uses them to read originals and store previews without any credentials of its own.
/// </summary>
public sealed class FileLinkSigner(IOptions<FilesOptions> options, TimeProvider clock)
{
    private readonly byte[] _key = Convert.FromBase64String(options.Value.LinkSigningKey);

    private sealed record Payload(LinkPurpose P, Guid V, FileVariant K, long E);

    public string Create(LinkPurpose purpose, Guid versionId, FileVariant variant, TimeSpan lifetime) =>
        Create(new FileLink(purpose, versionId, variant, clock.GetUtcNow() + lifetime));

    public string Create(FileLink link)
    {
        var payload = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(
            new Payload(link.Purpose, link.VersionId, link.Variant, link.ExpiresAt.ToUnixTimeSeconds())));
        return payload + "." + Base64UrlEncoder.Encode(Sign(payload));
    }

    /// <summary>Null when the token is malformed, tampered with, expired, or for another purpose.</summary>
    public FileLink? Verify(string token, LinkPurpose expectedPurpose)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1) return null;

        byte[] signature;
        Payload? payload;
        try
        {
            signature = Base64UrlEncoder.DecodeBytes(token[(dot + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(token[..dot]))) return null;
            payload = JsonSerializer.Deserialize<Payload>(Base64UrlEncoder.DecodeBytes(token[..dot]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }

        if (payload is null || payload.P != expectedPurpose) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.E);
        return expires <= clock.GetUtcNow() ? null : new FileLink(payload.P, payload.V, payload.K, expires);
    }

    private byte[] Sign(string payload) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload));
}
