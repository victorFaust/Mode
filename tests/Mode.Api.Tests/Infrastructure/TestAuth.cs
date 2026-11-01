using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Mode.Api.Tests.Infrastructure;

/// <summary>Plays the host token issuer in tests.</summary>
internal static class TestAuth
{
    public const string Issuer = "https://issuer.test";
    public const string Audience = "mode-api-test";

    public static readonly RSA SigningKey = RSA.Create(2048);
    public static readonly string PublicKeyPem = SigningKey.ExportSubjectPublicKeyInfoPem();

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public static SigningCredentials DefaultCredentials { get; } =
        new(new RsaSecurityKey(SigningKey), SecurityAlgorithms.RsaSha256);

    public static string CreateToken(
        string subject,
        string[]? roles = null,
        TimeSpan? lifetime = null,
        string issuer = Issuer,
        string audience = Audience,
        SigningCredentials? credentials = null,
        bool unsigned = false,
        bool withExpiry = true,
        string? tokenId = null)
    {
        var life = lifetime ?? TimeSpan.FromMinutes(10);
        var now = DateTime.UtcNow;
        var issuedAt = life < TimeSpan.Zero ? now + life - TimeSpan.FromMinutes(5) : now;

        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["jti"] = tokenId ?? Guid.NewGuid().ToString("N"),
        };
        if (roles is { Length: > 0 }) claims["roles"] = roles;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = withExpiry ? now + life : null,
            Claims = claims,
            SigningCredentials = unsigned ? null : credentials ?? DefaultCredentials,
        };
        return Handler.CreateToken(descriptor);
    }
}
