using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mode.Api.Endpoints;

internal static class Problems
{
    /// <summary>Also used when the caller may not see the resource, so ids cannot be probed.</summary>
    public static IResult NotFound() => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found");

    public static IResult Forbidden(string? detail = null) =>
        TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: detail);

    public static IResult Conflict(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: detail);

    public static IResult Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// 422 with code "dlp_blocked" when a blocking rule matched, or 409 with code "dlp_warning" when only warning rules
    /// matched and the sender has not confirmed. The client shows the rule names and can resend with the confirmation.
    /// </summary>
    public static async Task<IResult?> DlpGateAsync(Governance.DlpService dlp, string text, bool acknowledged, CancellationToken ct)
    {
        var result = await dlp.CheckAsync(text, ct);
        if (result.Blocked.Count > 0)
            return TypedResults.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Blocked by policy",
                detail: "This message matches your organization's data-loss rules and can't be sent.",
                extensions: new Dictionary<string, object?> { ["code"] = "dlp_blocked", ["rules"] = result.Blocked });
        if (result.Warnings.Count > 0 && !acknowledged)
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Check before sending",
                detail: "This message may contain sensitive information.",
                extensions: new Dictionary<string, object?> { ["code"] = "dlp_warning", ["rules"] = result.Warnings });
        return null;
    }
}

internal static class Input
{
    public static bool TryRequired(string? value, string field, int maxLength, out string result, [NotNullWhen(false)] out IResult? error)
    {
        result = value?.Trim() ?? "";
        error = result.Length == 0 ? Problems.Invalid(field, "Required.")
            : result.Length > maxLength ? Problems.Invalid(field, $"Must be at most {maxLength} characters.")
            : null;
        return error is null;
    }

    /// <summary>Null stays null (no change); whitespace becomes null (cleared).</summary>
    public static bool TryOptional(string? value, string field, int maxLength, out string? result, [NotNullWhen(false)] out IResult? error)
    {
        result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        error = result?.Length > maxLength ? Problems.Invalid(field, $"Must be at most {maxLength} characters.") : null;
        return error is null;
    }
}

internal static class Limits
{
    public const int WorkspaceName = 100;
    public const int ChannelName = 80;
    public const int Description = 1000;
    public const int SearchResults = 50;
}
