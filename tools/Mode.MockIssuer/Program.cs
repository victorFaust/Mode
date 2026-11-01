using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Stand-in for the host application's token issuer. Never deploy: it signs a token for any subject.
var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Mode.MockIssuer only runs with ASPNETCORE_ENVIRONMENT=Development.");

var issuer = builder.Configuration["MockIssuer:Issuer"] ?? "http://localhost:5100";
var audience = builder.Configuration["MockIssuer:Audience"] ?? "mode-api";
var defaultLifetime = builder.Configuration.GetValue("MockIssuer:TokenLifetimeMinutes", 60);

using var rsa = RSA.Create(2048);
var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
var signingCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
var publicKey = rsa.ExportParameters(includePrivateParameters: false);
var jwks = new
{
    keys = new[]
    {
        new
        {
            kty = "RSA",
            use = "sig",
            alg = SecurityAlgorithms.RsaSha256,
            kid = keyId,
            n = Base64UrlEncoder.Encode(publicKey.Modulus),
            e = Base64UrlEncoder.Encode(publicKey.Exponent),
        },
    },
};

var app = builder.Build();
var tokenHandler = new JsonWebTokenHandler();

app.MapGet("/.well-known/openid-configuration", () => Results.Json(new
{
    issuer,
    jwks_uri = $"{issuer}/.well-known/jwks.json",
    id_token_signing_alg_values_supported = new[] { SecurityAlgorithms.RsaSha256 },
}));

app.MapGet("/.well-known/jwks.json", () => Results.Json(jwks));

app.MapPost("/token", (TokenRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Sub))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["sub"] = ["Required."] });

    var lifetime = TimeSpan.FromMinutes(Math.Clamp(request.LifetimeMinutes ?? defaultLifetime, 1, 24 * 60));
    var now = DateTime.UtcNow;
    var claims = new Dictionary<string, object>
    {
        [JwtRegisteredClaimNames.Sub] = request.Sub,
        [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
    };
    if (!string.IsNullOrWhiteSpace(request.Name)) claims["name"] = request.Name;
    if (!string.IsNullOrWhiteSpace(request.Email)) claims["email"] = request.Email;
    if (request.Roles is { Length: > 0 }) claims["roles"] = request.Roles;

    var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
    {
        Issuer = issuer,
        Audience = audience,
        IssuedAt = now,
        NotBefore = now,
        Expires = now + lifetime,
        Claims = claims,
        SigningCredentials = signingCredentials,
    });

    return Results.Ok(new { access_token = token, token_type = "Bearer", expires_at = now + lifetime });
});

app.Run();

internal sealed record TokenRequest(string Sub, string? Name, string? Email, string[]? Roles, int? LifetimeMinutes);
