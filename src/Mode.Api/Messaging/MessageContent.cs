using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mode.Api.Messaging;

public sealed record ParsedMentions(IReadOnlyList<string> UserIds, bool Channel, bool Workspace)
{
    public static readonly ParsedMentions None = new([], false, false);
}

public sealed record SanitizedMessage(string Markdown, ParsedMentions Mentions);

/// <summary>
/// Server-side guard for message Markdown. Raw HTML is escaped so it renders as text, links must be http, https or
/// mailto, images are refused, and mentions are read only from plain text (never from code).
/// </summary>
public static partial class MessageContent
{
    public const int MaxLength = 20_000;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();
    private static readonly string[] AllowedSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto];

    /// <summary>@{user-id}, @channel, @workspace; not preceded by a word character, so emails are not mentions.</summary>
    [GeneratedRegex(@"(?<![\w@])@(?:\{(?<id>[^{}\s]{1,64})\}|(?<keyword>channel|workspace)\b)")]
    private static partial Regex MentionPattern();

    public static bool TrySanitize(string? raw, out SanitizedMessage result, out string error)
    {
        result = new SanitizedMessage("", ParsedMentions.None);
        var text = Normalize(raw);
        if (text.Trim().Length == 0)
        {
            error = "Message cannot be empty.";
            return false;
        }
        if (text.Length > MaxLength)
        {
            error = $"Message must be at most {MaxLength} characters.";
            return false;
        }

        var document = Markdown.Parse(text, Pipeline);
        var htmlSpans = new List<SourceSpan>();
        var userIds = new List<string>();
        bool channel = false, workspace = false;

        foreach (var node in document.Descendants())
        {
            switch (node)
            {
                case HtmlBlock block:
                    htmlSpans.Add(block.Span);
                    break;
                case HtmlInline inline:
                    htmlSpans.Add(inline.Span);
                    break;
                case LinkInline { IsImage: true }:
                    error = "Images cannot be embedded; attach the file instead.";
                    return false;
                case LinkInline link when !IsAllowedUrl(link.Url):
                    error = "Links must use http, https or mailto.";
                    return false;
                case AutolinkInline autolink when !autolink.IsEmail && !IsAllowedUrl(autolink.Url):
                    error = "Links must use http, https or mailto.";
                    return false;
                case LinkReferenceDefinition definition when !IsAllowedUrl(definition.Url):
                    error = "Links must use http, https or mailto.";
                    return false;
                case LiteralInline literal:
                    foreach (Match match in MentionPattern().Matches(literal.Content.ToString()))
                    {
                        if (match.Groups["id"].Success) userIds.Add(match.Groups["id"].Value);
                        else if (match.Groups["keyword"].Value == "channel") channel = true;
                        else workspace = true;
                    }
                    break;
            }
        }

        result = new SanitizedMessage(EscapeHtml(text, htmlSpans), new ParsedMentions(userIds.Distinct().ToList(), channel, workspace));
        error = "";
        return true;
    }

    private static string Normalize(string? raw)
    {
        if (raw is null) return "";
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw.Replace("\r\n", "\n").Replace('\r', '\n'))
            if (c is '\n' or '\t' || !char.IsControl(c))
                builder.Append(c);
        return builder.ToString();
    }

    private static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && AllowedSchemes.Contains(uri.Scheme);

    /// <summary>Replaces '&lt;' inside raw HTML so Markdown renders it as literal text.</summary>
    private static string EscapeHtml(string text, List<SourceSpan> spans)
    {
        if (spans.Count == 0) return text;
        var builder = new StringBuilder(text);
        foreach (var span in spans.OrderByDescending(s => s.Start))
        {
            var end = Math.Min(span.End, text.Length - 1);
            for (var i = end; i >= span.Start; i--)
                if (builder[i] == '<')
                    builder.Remove(i, 1).Insert(i, "&lt;");
        }
        return builder.ToString();
    }
}
