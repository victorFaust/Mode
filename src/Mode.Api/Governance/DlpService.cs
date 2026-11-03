using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Governance;

public sealed record DlpResult(IReadOnlyList<string> Blocked, IReadOnlyList<string> Warnings)
{
    public static readonly DlpResult Clean = new([], []);
}

/// <summary>
/// Organization data-loss rules: blocked upload types and text patterns that warn or block before a message is sent.
/// Patterns run with the non-backtracking engine and a time limit, so an admin's pattern can't stall the server.
/// </summary>
public sealed class DlpService(ModeDbContext db)
{
    public const int MaxPatterns = 50;
    public const int MaxPatternLength = 500;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<DlpSettings> GetAsync(CancellationToken ct) =>
        await db.DlpSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == DlpSettings.SingletonId, ct)
        ?? new DlpSettings();

    public async Task<bool> IsBlockedExtensionAsync(string extension, CancellationToken ct)
    {
        var settings = await GetAsync(ct);
        return settings.BlockedExtensions.Contains(extension.TrimStart('.').ToLowerInvariant());
    }

    public async Task<DlpResult> CheckAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return DlpResult.Clean;
        var settings = await GetAsync(ct);
        if (settings.Patterns.Count == 0) return DlpResult.Clean;

        List<string> blocked = [], warnings = [];
        foreach (var pattern in settings.Patterns)
        {
            bool matched;
            try
            {
                matched = Compile(pattern.Pattern).IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail closed: a pattern that can't be evaluated is treated as a match.
                matched = true;
            }
            if (!matched) continue;
            (pattern.Action == DlpAction.Block ? blocked : warnings).Add(pattern.Name);
        }
        return new DlpResult(blocked, warnings);
    }

    public static bool TryValidate(string pattern, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength)
        {
            error = $"Each pattern must be 1 to {MaxPatternLength} characters.";
            return false;
        }
        try
        {
            Compile(pattern);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid pattern: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            error = $"Unsupported pattern (back-references and lookarounds are not allowed): {ex.Message}";
            return false;
        }
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
}
