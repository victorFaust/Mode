using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Mode.Api.Auth;

public static class AuthenticationSetup
{
    /// <summary>Asymmetric algorithms only; rules out "none" and HMAC key-confusion attacks.</summary>
    public static readonly string[] AllowedAlgorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384,
    ];

    public const string HubPathPrefix = "/hubs";

    public static IServiceCollection AddModeAuthentication(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.HasExactlyOneKeySource(), "Configure exactly one of Auth:MetadataAddress or Auth:PublicKeyPem.")
            .Validate(o => o.RoleMapIsValid(), $"Auth:RoleMap values must be '{ModeRoles.Admin}' or '{ModeRoles.Guest}'.")
            .ValidateOnStart();

        services.AddScoped<TokenValidatedHandler>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((jwt, auth) => Configure(jwt, auth.Value));

        return services;
    }

    private static void Configure(JwtBearerOptions jwt, AuthOptions auth)
    {
        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = auth.RequireHttpsMetadata;
        if (!string.IsNullOrWhiteSpace(auth.MetadataAddress))
            jwt.MetadataAddress = auth.MetadataAddress;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = auth.Issuer,
            ValidateAudience = true,
            ValidAudience = auth.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = AllowedAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(auth.ClockSkewSeconds),
            NameClaimType = auth.Claims.Name,
            RoleClaimType = auth.Claims.Roles,
        };

        if (!string.IsNullOrWhiteSpace(auth.PublicKeyPem))
            jwt.TokenValidationParameters.IssuerSigningKey = LoadPublicKey(auth.PublicKeyPem);

        jwt.Events = new JwtBearerEvents
        {
            // Browsers cannot set headers on WebSocket requests, so SignalR sends the token in the query string.
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"].ToString();
                if (token.Length > 0 && context.HttpContext.Request.Path.StartsWithSegments(HubPathPrefix))
                    context.Token = token;
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
                context.HttpContext.RequestServices.GetRequiredService<TokenValidatedHandler>().HandleAsync(context),
        };
    }

    private static SecurityKey LoadPublicKey(string pem)
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return new RsaSecurityKey(rsa);
        }
        catch (ArgumentException)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            return new ECDsaSecurityKey(ecdsa);
        }
    }
}
