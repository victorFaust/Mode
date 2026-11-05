using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Mode.Api.Auditing;
using Mode.Domain;
using Mode.Domain.Directory;
using Mode.Infrastructure.Data;

namespace Mode.Api.Auth;

/// <summary>Runs after the token signature and lifetime checks: confirms the user with the host directory and adds Mode claims.</summary>
internal sealed class TokenValidatedHandler(
    IUserDirectory directory,
    IOptions<AuthOptions> options,
    IMemoryCache cache,
    IAuditLog audit,
    ModeDbContext db,
    TimeProvider clock)
{
    public async Task HandleAsync(TokenValidatedContext context)
    {
        var auth = options.Value;
        var principal = context.Principal!;
        if (auth.MaxTokenLifetimeMinutes > 0 && context.SecurityToken is JsonWebToken jwt)
        {
            var issued = jwt.IssuedAt != DateTime.MinValue ? jwt.IssuedAt : jwt.ValidFrom;
            if (issued == DateTime.MinValue || jwt.ValidTo - issued > TimeSpan.FromMinutes(auth.MaxTokenLifetimeMinutes) + TimeSpan.FromSeconds(auth.ClockSkewSeconds))
            {
                context.Fail($"Token lifetime is unknown or longer than {auth.MaxTokenLifetimeMinutes} minutes.");
                return;
            }
        }
        var subject = principal.FindFirst(auth.Claims.Subject)?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > UserIds.MaxLength)
        {
            context.Fail("Token has no usable subject claim.");
            return;
        }

        var user = await directory.GetByIdAsync(subject, context.HttpContext.RequestAborted);
        if (user is null || !user.IsActive)
        {
            context.Fail("User is unknown or inactive in the host directory.");
            return;
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(ModeClaims.UserId, user.Id));
        identity.AddClaim(new Claim(ModeClaims.DisplayName, user.DisplayName));
        if (user.Email is not null)
            identity.AddClaim(new Claim(ModeClaims.Email, user.Email));

        var modeRoles = principal.FindAll(auth.Claims.Roles)
            .Select(c => auth.RoleMap.FirstOrDefault(m => string.Equals(m.Key, c.Value, StringComparison.OrdinalIgnoreCase)).Value)
            .Where(r => r is not null)
            .Distinct()
            .ToList();
        foreach (var role in modeRoles)
            identity.AddClaim(new Claim(ModeClaims.Role, role!));

        await RecordSignInAsync(context, user.Id);
    }

    /// <summary>A JWT has no sign-in event, so the first use of each token is recorded as one.</summary>
    private async Task RecordSignInAsync(TokenValidatedContext context, string userId)
    {
        if (context.SecurityToken is not JsonWebToken token) return;

        var tokenKey = string.IsNullOrEmpty(token.Id) ? token.EncodedSignature : token.Id;
        var cacheKey = "mode:auth:seen:" + tokenKey;
        if (cache.TryGetValue(cacheKey, out _)) return;

        var expires = new DateTimeOffset(token.ValidTo, TimeSpan.Zero) + TimeSpan.FromSeconds(options.Value.ClockSkewSeconds);
        cache.Set(cacheKey, true, expires > clock.GetUtcNow() ? expires : clock.GetUtcNow().AddMinutes(1));

        audit.Record(
            AuditActions.SignIn, "user", userId,
            data: new { tokenId = string.IsNullOrEmpty(token.Id) ? null : token.Id, expiresAt = token.ValidTo },
            actorUserId: userId);
        await db.SaveChangesAsync(context.HttpContext.RequestAborted);
    }
}
