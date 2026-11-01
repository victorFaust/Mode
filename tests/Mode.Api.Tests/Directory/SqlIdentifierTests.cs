using Mode.Infrastructure.Directory;

namespace Mode.Api.Tests.Directory;

public sealed class SqlIdentifierTests
{
    [Theory]
    [InlineData("Users", true)]
    [InlineData("dbo.Users", true)]
    [InlineData("_t1", true)]
    [InlineData("dbo.Users; DROP TABLE x", false)]
    [InlineData("[dbo].[Users]", false)]
    [InlineData("a.b.c", false)]
    [InlineData("1Users", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Table_names_must_be_plain_identifiers(string? name, bool valid) =>
        Assert.Equal(valid, SqlIdentifier.IsValidTable(name));

    [Fact]
    public void Quote_brackets_each_part() =>
        Assert.Equal("[dbo].[Users]", SqlIdentifier.Quote("dbo.Users"));

    [Theory]
    [InlineData("50%", @"50\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData("[x]", @"\[x]")]
    [InlineData(@"c:\", @"c:\\")]
    public void EscapeLike_escapes_wildcards(string input, string expected) =>
        Assert.Equal(expected, SqlIdentifier.EscapeLike(input));
}
