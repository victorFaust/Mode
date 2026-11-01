using System.Security.Claims;

namespace Mode.Api.Auth;

/// <summary>The authenticated caller. Built only from claims added by <see cref="TokenValidatedHandler"/>.</summary>
public sealed record CurrentUser(string Id, string DisplayName, string? Email, bool IsAdmin, bool IsHostGuest)
{
    public static CurrentUser FromPrincipal(ClaimsPrincipal principal)
    {
        var id = principal.FindFirst(ModeClaims.UserId)?.Value
            ?? throw new InvalidOperationException("The request is not authenticated.");
        return new CurrentUser(
            id,
            principal.FindFirst(ModeClaims.DisplayName)?.Value ?? id,
            principal.FindFirst(ModeClaims.Email)?.Value,
            principal.HasClaim(ModeClaims.Role, ModeRoles.Admin),
            principal.HasClaim(ModeClaims.Role, ModeRoles.Guest));
    }
}
