using UrlShortener.Core.Validation;

namespace UrlShortener.UnitTests.Core;

public class AliasValidatorTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("my-link_2026")]
    [InlineData("ABCdef123")]
    public void Validate_AcceptsWellFormedAliases(string alias)
    {
        Assert.Null(AliasValidator.Validate(alias));
    }

    [Theory]
    [InlineData("ab", "alias_length")]
    [InlineData("this-alias-is-way-too-long-to-be-accepted", "alias_length")]
    [InlineData("has space", "alias_invalid_characters")]
    [InlineData("slash/es", "alias_invalid_characters")]
    [InlineData("émoji", "alias_invalid_characters")]
    [InlineData("api", "alias_reserved")]
    [InlineData("HEALTH", "alias_reserved")]
    public void Validate_RejectsBadAliases(string alias, string expectedCode)
    {
        Assert.Equal(expectedCode, AliasValidator.Validate(alias)?.Code);
    }

    [Theory]
    [InlineData("abc1234", true)]
    [InlineData("ab", false)]
    [InlineData(null, false)]
    [InlineData("../etc", false)]
    public void IsWellFormedCode_FiltersJunk(string? code, bool expected)
    {
        Assert.Equal(expected, AliasValidator.IsWellFormedCode(code));
    }
}
