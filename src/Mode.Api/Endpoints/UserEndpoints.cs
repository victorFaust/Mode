using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Domain.Directory;

namespace Mode.Api.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMe).WithTags("Users").Produces<MeDto>();
        var users = api.MapGroup("/users").WithTags("Users");
        users.MapGet("/search", Search).RequireRateLimiting(Security.RateLimits.Directory).Produces<UserDto[]>();
        users.MapGet("/{userId}", GetById).Produces<UserDto>();
        return api;
    }

    private static async Task<IResult> GetMe(CurrentUser user, IUserDirectory directory, CancellationToken ct)
    {
        var found = await directory.GetByIdAsync(user.Id, ct);
        var dto = found is null ? UserDto.Unknown(user.Id) : UserDto.From(found);
        return TypedResults.Ok(new MeDto(dto, user.IsAdmin, user.IsHostGuest));
    }

    private static async Task<IResult> Search(
        string? q, int? limit, CurrentUser user, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        if (!await policy.CanUseDirectoryAsync(user, ct)) return Problems.Forbidden("Guests cannot search the directory.");
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Problems.Invalid("q", "Enter at least 2 characters.");

        var results = await directory.SearchAsync(q, Math.Clamp(limit ?? 20, 1, Limits.SearchResults), ct);
        return TypedResults.Ok(results.Select(UserDto.From));
    }

    private static async Task<IResult> GetById(
        string userId, CurrentUser user, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        if (!await policy.CanUseDirectoryAsync(user, ct)) return Problems.Forbidden("Guests cannot browse the directory.");
        var found = await directory.GetByIdAsync(userId, ct);
        return found is null ? Problems.NotFound() : TypedResults.Ok(UserDto.From(found));
    }
}
