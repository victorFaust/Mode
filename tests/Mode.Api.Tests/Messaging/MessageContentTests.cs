using Mode.Api.Messaging;

namespace Mode.Api.Tests.Messaging;

public sealed class MessageContentTests
{
    private static SanitizedMessage Accept(string markdown)
    {
        Assert.True(MessageContent.TrySanitize(markdown, out var result, out var error), error);
        return result;
    }

    private static string Reject(string markdown)
    {
        Assert.False(MessageContent.TrySanitize(markdown, out _, out var error));
        return error;
    }

    [Fact]
    public void Plain_markdown_is_kept_as_is()
    {
        const string text = "**bold** _italic_ `code`\n\n- one\n- two\n\n```\nblock\n```\n[site](https://example.com) <https://example.com> mail@example.com";
        Assert.Equal(text, Accept(text).Markdown);
    }

    [Theory]
    [InlineData("hello <script>alert(1)</script>")]
    [InlineData("<div onclick=\"x()\">block</div>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    public void Raw_html_is_escaped_so_it_renders_as_text(string input)
    {
        var output = Accept(input).Markdown;

        Assert.DoesNotContain("<", output);
        Assert.Contains("&lt;", output);
    }

    [Fact]
    public void Angle_brackets_inside_code_are_left_alone() =>
        Assert.Equal("`<b>` and\n```\n<script>\n```", Accept("`<b>` and\n```\n<script>\n```").Markdown);

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JaVaScRiPt:alert(1))")]
    [InlineData("[x](data:text/html,hi)")]
    [InlineData("[x](/relative)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[x][r]\n\n[r]: vbscript:msgbox")]
    public void Unsafe_links_are_rejected(string input) =>
        Assert.Contains("http", Reject(input));

    [Fact]
    public void Images_are_rejected() =>
        Assert.Contains("attach", Reject("![x](https://example.com/a.png)"));

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Empty_messages_are_rejected(string input) => Reject(input);

    [Fact]
    public void Overlong_messages_are_rejected() => Reject(new string('a', MessageContent.MaxLength + 1));

    [Fact]
    public void Control_characters_are_stripped_and_newlines_normalized() =>
        Assert.Equal("a\nb\nc", Accept("a\r\nb\u0007\rc").Markdown);

    [Fact]
    public void Mentions_are_read_from_text_but_not_from_code_or_emails()
    {
        var mentions = Accept("hi @{u-1} and @{U-2} @channel\n`@{u-3}`\n```\n@{u-4} @workspace\n```\nmail me at a@{u-5}").Mentions;

        Assert.Equal(["u-1", "U-2"], mentions.UserIds);
        Assert.True(mentions.Channel);
        Assert.False(mentions.Workspace);
    }

    [Fact]
    public void Workspace_keyword_is_detected() =>
        Assert.True(Accept("**@workspace** please read").Mentions.Workspace);
}
