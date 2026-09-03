using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Docker-free HTTP-boundary coverage (see <see cref="SqliteWebApplicationFactory"/>) for malformed
/// <c>POST /api/turns</c> requests: a missing, null, or blank required field (<c>nativeMessageId</c>,
/// <c>contentText</c>) must be rejected with a controlled <c>400</c> and useful validation details,
/// never surface as an unhandled <c>500</c> from
/// <see cref="MultiChannelAgent.Domain.Turns.InboundTurn.Create"/> throwing on a null/blank value.
/// Valid requests must still receive <c>202 Accepted</c> - the fix must not regress the happy path.
/// Every request here is signed in and CSRF-protected: Turn submission derives its Participant and
/// ChannelConversation from trusted context, so the endpoint requires the same authenticated,
/// antiforgery-protected shape as every other mutating request.
/// </summary>
public sealed class MalformedTurnSubmissionTests : IAsyncLifetime
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

    private async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync()
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName = "Turn Sender", activeTenantMember = true }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await _client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await _client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!);
    }

    private async Task<HttpResponseMessage> PostTurnAsync(CookieJar jar, string csrfToken, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(body) };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var response = await _client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("contentText")]
    public async Task Missing_required_field_is_rejected_with_400_instead_of_500(string missingField)
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync();
        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-missing-1",
            ["contentText"] = "hello",
        };
        body.Remove(missingField);

        var response = await PostTurnAsync(jar, csrfToken, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(missingField, out _));
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("contentText")]
    public async Task Null_required_field_is_rejected_with_400_instead_of_500(string nullField)
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync();
        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-null-1",
            ["contentText"] = "hello",
        };
        body[nullField] = null;

        var response = await PostTurnAsync(jar, csrfToken, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(nullField, out _));
    }

    [Theory]
    [InlineData("nativeMessageId")]
    [InlineData("contentText")]
    public async Task Blank_required_field_is_rejected_with_400_instead_of_500(string blankField)
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync();
        var body = new Dictionary<string, string?>
        {
            ["nativeMessageId"] = "native-blank-1",
            ["contentText"] = "hello",
        };
        body[blankField] = "   ";

        var response = await PostTurnAsync(jar, csrfToken, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(blankField, out _));
    }

    [Fact]
    public async Task Valid_request_still_receives_202_accepted()
    {
        var (jar, csrfToken) = await SignInAndBootstrapAsync();

        var response = await PostTurnAsync(jar, csrfToken, new
        {
            nativeMessageId = "native-valid-1",
            contentText = "hello valid",
            locale = "en-US",
            traceId = "trace-valid-1",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, accepted.GetProperty("turnId").GetGuid());
        Assert.False(accepted.GetProperty("alreadyAccepted").GetBoolean());
    }

    [Fact]
    public async Task Submitting_a_turn_without_signing_in_is_a_plain_401()
    {
        var response = await _client.PostAsJsonAsync("/api/turns", new
        {
            nativeMessageId = "native-unauthenticated-1",
            contentText = "hello",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Submitting_a_turn_without_a_csrf_token_is_rejected()
    {
        var (jar, _) = await SignInAndBootstrapAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns")
        {
            Content = JsonContent.Create(new { nativeMessageId = "native-no-csrf-1", contentText = "hello" }),
        };
        jar.Apply(request);
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
