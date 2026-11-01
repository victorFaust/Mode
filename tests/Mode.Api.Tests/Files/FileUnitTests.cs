using System.Text;
using Microsoft.Extensions.Options;
using Mode.Api.Files;

namespace Mode.Api.Tests.Files;

public sealed class FileNamesTests
{
    private static readonly IReadOnlyList<string> Allowed = FilesOptions.DefaultExtensions;

    [Theory]
    [InlineData("report.pdf", "report.pdf", "pdf")]
    [InlineData("  Q3   plan .DOCX  ", "Q3 plan .DOCX", "docx")]
    [InlineData(@"C:\Users\me\secret\notes.txt", "notes.txt", "txt")]
    [InlineData("../../etc/passwd.txt", "passwd.txt", "txt")]
    [InlineData("in<voice>:2026?.pdf", "invoice2026.pdf", "pdf")]
    [InlineData("evil\u202Etxt.pdf", "eviltxt.pdf", "pdf")]
    [InlineData("tab\tand\nnewline.csv", "tab and newline.csv", "csv")]
    public void Names_are_cleaned(string raw, string expectedName, string expectedExtension)
    {
        Assert.True(FileNames.TrySanitize(raw, Allowed, out var name, out var extension, out var error), error);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedExtension, extension);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("noextension")]
    [InlineData("CON.txt")]
    [InlineData("lpt1.pdf")]
    [InlineData("setup.exe")]
    [InlineData("page.html")]
    [InlineData("logo.svg")]
    [InlineData("archive.zip")]
    [InlineData("report.pdf.exe")]
    public void Unsafe_or_disallowed_names_are_refused(string raw) =>
        Assert.False(FileNames.TrySanitize(raw, Allowed, out _, out _, out _));

    [Fact]
    public void Overlong_names_are_refused() =>
        Assert.False(FileNames.TrySanitize(new string('a', 300) + ".txt", Allowed, out _, out _, out _));

    [Fact]
    public void Configured_list_replaces_the_defaults()
    {
        var options = new FilesOptions { AllowedExtensions = [".PDF", "txt"] };

        Assert.Equal(["pdf", "txt"], options.Extensions);
        Assert.False(FileNames.TrySanitize("photo.png", options.Extensions, out _, out _, out _));
    }

    [Fact]
    public void Blocked_types_cannot_be_configured()
    {
        var options = new FilesOptions { LinkSigningKey = ApiFixtureKey, AllowedExtensions = ["pdf", "exe"] };

        Assert.False(options.IsValid(out var error));
        Assert.Contains("blocked by design", error);
    }

    private static readonly string ApiFixtureKey = Convert.ToBase64String(new byte[32]);
}

public sealed class FileTypeSnifferTests
{
    [Theory]
    [InlineData("png", "iVBORw0KGgo=", true)]
    [InlineData("pdf", "JVBERi0xLjQ=", true)]
    [InlineData("pdf", "iVBORw0KGgo=", false)]
    [InlineData("docx", "UEsDBA==", true)]
    [InlineData("jpg", "/9j/4A==", true)]
    [InlineData("doc", "0M8R4KGxGuE=", true)]
    [InlineData("xlsx", "JVBERi0xLjQ=", false)]
    public void Magic_bytes_must_match_the_extension(string extension, string base64Header, bool expected) =>
        Assert.Equal(expected, FileTypeSniffer.Matches(extension, Convert.FromBase64String(base64Header)));

    [Theory]
    [InlineData("hello, world", true)]
    [InlineData("\uFEFFname,amount\n1,2", true)]
    [InlineData("<!DOCTYPE html><script>alert(1)</script>", false)]
    [InlineData("  <svg onload=alert(1)>", false)]
    public void Text_files_must_not_be_markup(string content, bool expected) =>
        Assert.Equal(expected, FileTypeSniffer.Matches("txt", Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Binary_content_is_not_text() =>
        Assert.False(FileTypeSniffer.Matches("csv", [0x61, 0x00, 0x62]));
}

public sealed class SignedLinkRedactionTests
{
    [Theory]
    [InlineData("/api/v1/files/content/eyJQ.abc", "/api/v1/files/content/[redacted]")]
    [InlineData("/internal/files/eyJQ.abc", "/internal/files/[redacted]")]
    [InlineData("/API/V1/FILES/CONTENT/eyJQ.abc", "/API/V1/FILES/CONTENT/[redacted]")]
    [InlineData("/api/v1/files/3f2b/link", "/api/v1/files/3f2b/link")]
    [InlineData("/api/v1/channels/1/messages", "/api/v1/channels/1/messages")]
    public void Tokens_in_signed_link_paths_are_redacted(string path, string expected) =>
        Assert.Equal(expected, SignedLinkRedaction.Redact(path));
}

public sealed class FileLinkSignerTests
{
    private static FileLinkSigner Signer() =>
        new(Options.Create(new FilesOptions { LinkSigningKey = Convert.ToBase64String(new byte[32]) }), TimeProvider.System);

    [Fact]
    public void Valid_link_round_trips()
    {
        var signer = Signer();
        var versionId = Guid.NewGuid();

        var link = signer.Verify(signer.Create(LinkPurpose.Download, versionId, FileVariant.Thumbnail, TimeSpan.FromMinutes(5)), LinkPurpose.Download);

        Assert.Equal((versionId, FileVariant.Thumbnail), (link!.VersionId, link.Variant));
    }

    [Fact]
    public void Expired_tampered_or_repurposed_links_are_refused()
    {
        var signer = Signer();
        var expired = signer.Create(new FileLink(LinkPurpose.Download, Guid.NewGuid(), FileVariant.Original, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var valid = signer.Create(LinkPurpose.Download, Guid.NewGuid(), FileVariant.Original, TimeSpan.FromMinutes(5));
        var tampered = (valid[0] == 'a' ? 'b' : 'a') + valid[1..];
        var otherKey = new FileLinkSigner(
            Options.Create(new FilesOptions { LinkSigningKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) }),
            TimeProvider.System).Create(LinkPurpose.Download, Guid.NewGuid(), FileVariant.Original, TimeSpan.FromMinutes(5));

        Assert.Null(signer.Verify(expired, LinkPurpose.Download));
        Assert.Null(signer.Verify(tampered, LinkPurpose.Download));
        Assert.Null(signer.Verify(valid, LinkPurpose.ProcessorRead));
        Assert.Null(signer.Verify(otherKey, LinkPurpose.Download));
        Assert.Null(signer.Verify("garbage", LinkPurpose.Download));
    }
}
