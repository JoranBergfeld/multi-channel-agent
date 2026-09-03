using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Unit coverage for the pure redirect-target validator behind `/auth/sign-in?returnUrl=`: only a
/// same-origin absolute-path URL is ever trusted, so an attacker can never use the sign-in link
/// itself to redirect a victim to an external site after a legitimate login.
/// </summary>
public sealed class LocalReturnUrlTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_returnUrl_resolves_to_the_root_path(string? returnUrl)
    {
        Assert.Equal("/", LocalReturnUrl.Resolve(returnUrl));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("http://evil.example")]
    [InlineData("evil.example")]
    public void Absolute_external_url_falls_back_to_the_root_path(string returnUrl)
    {
        Assert.Equal("/", LocalReturnUrl.Resolve(returnUrl));
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("//evil.example/phish")]
    public void Protocol_relative_url_falls_back_to_the_root_path(string returnUrl)
    {
        Assert.Equal("/", LocalReturnUrl.Resolve(returnUrl));
    }

    [Theory]
    [InlineData(@"/\evil.example")]
    [InlineData(@"\\evil.example")]
    [InlineData(@"\/evil.example")]
    public void Backslash_variant_falls_back_to_the_root_path(string returnUrl)
    {
        Assert.Equal("/", LocalReturnUrl.Resolve(returnUrl));
    }

    [Theory]
    [InlineData("/inventories")]
    [InlineData("/inventories?highlight=abc123")]
    [InlineData("/")]
    public void Valid_local_absolute_path_with_optional_query_is_preserved(string returnUrl)
    {
        Assert.Equal(returnUrl, LocalReturnUrl.Resolve(returnUrl));
    }
}
