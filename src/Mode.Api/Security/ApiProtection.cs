using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Mode.Api.Auth;
using Mode.Api.Governance;

namespace Mode.Api.Security;

/// <summary>Per-person request limits for the endpoints that are cheap to call and expensive to serve.</summary>
public static class RateLimits
{
    public const string Send = "send";
    public const string Search = "search";
    public const string Directory = "directory";
    public const string Upload = "upload";
    public const string Export = "export";

    private static readonly (string Policy, int PerMinute)[] Defaults =
        [(Send, 60), (Search, 30), (Directory, 60), (Upload, 20), (Export, 5)];

    public static IServiceCollection AddModeRateLimits(this IServiceCollection services, IConfiguration config)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                return ValueTask.CompletedTask;
            };

            o.AddPolicy(IncomingWebhookEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Request.RouteValues["webhookId"]?.ToString() ?? "none",
                _ => Window(config.GetValue("Webhooks:PerMinute", 30))));

            foreach (var (policy, perMinute) in Defaults)
            {
                var limit = config.GetValue($"RateLimits:{policy}PerMinute", perMinute);
                o.AddPolicy(policy, http => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(http), _ => Window(limit)));
            }
        });
        return services;
    }

    // Authenticated callers are limited per person; the fallback policy means anonymous callers never reach these endpoints.
    private static string PartitionKey(HttpContext http) =>
        http.User.FindFirstValue(ModeClaims.UserId) ?? "ip:" + http.Connection.RemoteIpAddress;

    private static FixedWindowRateLimiterOptions Window(int perMinute) =>
        new() { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}

/// <summary>Response headers and request size limits applied to every API response.</summary>
public static class HttpHardening
{
    public const long MaxJsonBodyBytes = 1024 * 1024;

    public static IApplicationBuilder UseModeHardening(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        // The API serves JSON and files only; endpoints that need another policy set their own. Swagger UI is development-only.
        if (!http.Request.Path.StartsWithSegments("/swagger"))
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

        // File bytes have their own limit (Files:MaxSizeMb); everything else is small JSON.
        if (!IsFileContent(http.Request.Path))
        {
            if (http.Request.ContentLength > MaxJsonBodyBytes)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = MaxJsonBodyBytes;
        }
        await next();
    });

    private static bool IsFileContent(PathString path) =>
        path.StartsWithSegments("/internal/files") ||
        (path.StartsWithSegments("/api/v1/files/uploads") && path.Value!.EndsWith("/content", StringComparison.OrdinalIgnoreCase));
}
