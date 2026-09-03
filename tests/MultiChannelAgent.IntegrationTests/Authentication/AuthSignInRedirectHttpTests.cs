using Microsoft.AspNetCore.Mvc.Testing;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Exercises the real `/auth/sign-in` endpoint mapping (the same code every authentication provider
/// shares) over HTTP against the deterministic Test challenge scheme, verifying that the RedirectUri
/// actually reaching <c>Results.Challenge</c> is always sanitized to a safe, same-origin path -
/// closing the open-redirect finding end to end rather than only at the pure-function seam covered by
/// <see cref="LocalReturnUrlTests"/>.
/// </summary>
public sealed class AuthSignInRedirectHttpTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    private async Task<string?> ChallengeRedirectUriForAsync(string? returnUrl)
    {
        var url = returnUrl is null ? "/auth/sign-in" : $"/auth/sign-in?returnUrl={Uri.EscapeDataString(returnUrl)}";
        var response = await _client.GetAsync(url);
        return response.Headers.TryGetValues("X-Test-Challenge-Redirect-Uri", out var values) ? values.Single() : null;
    }

    [Fact]
    public async Task No_returnUrl_challenges_with_the_root_path()
    {
        Assert.Equal("/", await ChallengeRedirectUriForAsync(null));
    }

    [Fact]
    public async Task Valid_local_returnUrl_is_passed_through_to_the_challenge()
    {
        Assert.Equal("/inventories", await ChallengeRedirectUriForAsync("/inventories"));
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example")]
    [InlineData(@"/\evil.example")]
    public async Task Unsafe_returnUrl_falls_back_to_the_root_path_at_the_real_endpoint(string returnUrl)
    {
        Assert.Equal("/", await ChallengeRedirectUriForAsync(returnUrl));
    }
}
