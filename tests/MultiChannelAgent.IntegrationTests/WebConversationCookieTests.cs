using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Host.Endpoints;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The web conversation cookie is the one piece of a Turn's trusted context that travels through the
/// client, so its value is only ever an identifier this application itself issued. Anything else - a
/// tampered, corrupted, or absurdly long value - is treated exactly like no cookie at all: a fresh
/// conversation is issued and the request carries on, disclosing nothing about why.
/// </summary>
public sealed class WebConversationCookieTests : IAsyncLifetime
{
    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return _factory.DisposeAsync().AsTask();
    }

    [Fact]
    public void An_identifier_this_application_issued_is_kept()
    {
        var issued = Guid.NewGuid().ToString();
        var httpContext = ContextCarrying(issued);

        var ensured = WebConversationCookie.EnsureId(httpContext);

        Assert.Equal(issued, ensured);
        Assert.Equal(0, httpContext.Response.Headers.SetCookie.Count);
    }

    // Anything that is not an identifier this application issued is not a conversation it knows, and
    // an over-long one would additionally be refused by the durable Turn contract - reaching it would
    // turn a hostile cookie into an unhandled failure.
    [Theory]
    [InlineData("not-a-conversation")]
    [InlineData("11111111-1111-1111-1111-11111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_value_this_application_never_issued_is_replaced(string tampered)
    {
        var httpContext = ContextCarrying(tampered);

        var ensured = WebConversationCookie.EnsureId(httpContext);

        Assert.True(Guid.TryParseExact(ensured, "D", out _));
        Assert.NotEqual(tampered, ensured);
        Assert.Contains(WebConversationCookie.Name, Assert.Single(httpContext.Response.Headers.SetCookie.ToArray())!);
    }

    [Fact]
    public void An_absurdly_long_value_is_replaced_rather_than_carried_into_a_turn()
    {
        var httpContext = ContextCarrying(new string('c', InboundTurn.MaxChannelConversationIdLength + 1));

        var ensured = WebConversationCookie.EnsureId(httpContext);

        Assert.True(Guid.TryParseExact(ensured, "D", out _));
        Assert.True(ensured.Length <= InboundTurn.MaxChannelConversationIdLength);
    }

    // End to end: a hostile cookie must not become a 500 on the way to durable acceptance, and the
    // Participant simply continues in a fresh conversation.
    [Fact]
    public async Task Submitting_a_turn_with_an_absurdly_long_conversation_cookie_still_succeeds()
    {
        var jar = await SignInAsync();
        var csrfToken = await BootstrapCsrfTokenAsync(jar);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new { nativeMessageId = "native-hostile-cookie", contentText = "list stock" }),
        };
        jar.Apply(request);
        OverrideConversationCookie(request, new string('c', InboundTurn.MaxChannelConversationIdLength + 1));
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            header => header.StartsWith($"{WebConversationCookie.Name}=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_conversation_cookie_is_retained_across_requests()
    {
        var jar = await SignInAsync();

        var first = await BootstrapAsync(jar);
        var second = await BootstrapAsync(jar);

        var firstConversationId = first.GetProperty("bootstrap").GetProperty("webConversationId").GetString();
        Assert.Equal(firstConversationId, second.GetProperty("bootstrap").GetProperty("webConversationId").GetString());
        Assert.True(Guid.TryParseExact(firstConversationId!, "D", out _));
    }

    private static DefaultHttpContext ContextCarrying(string cookieValue)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Cookie = $"{WebConversationCookie.Name}={cookieValue}";
        return httpContext;
    }

    private static void OverrideConversationCookie(HttpRequestMessage request, string value)
    {
        var existing = request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.First() : string.Empty;
        request.Headers.Remove("Cookie");
        request.Headers.Add("Cookie", $"{existing}; {WebConversationCookie.Name}={value}");
    }

    private async Task<CookieJar> SignInAsync()
    {
        var jar = new CookieJar();
        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName = "Cookie Sender", activeTenantMember = true }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await _client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        return jar;
    }

    private async Task<JsonElement> BootstrapAsync(CookieJar jar)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(request);
        var response = await _client.SendAsync(request);
        jar.Capture(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> BootstrapCsrfTokenAsync(CookieJar jar) =>
        (await BootstrapAsync(jar)).GetProperty("csrfToken").GetString()!;
}
