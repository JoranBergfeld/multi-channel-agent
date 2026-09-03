using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Unit coverage for <see cref="GraphTenantMemberDirectory"/> against a fake
/// <see cref="TokenCredential"/> and a fake <see cref="HttpMessageHandler"/> - no real network, no
/// real Graph tenant, no Docker. Covers: resolving an exact active tenant member by object id and by
/// verified address, refusing a guest/disabled account/ambiguous address match exactly like an
/// authoritative "not found", surfacing 404 as unresolved (null) but 401/403/5xx/transport failures as
/// a typed <see cref="TenantDirectoryUnavailableException"/> rather than a false "not found", the
/// exact OData escaping applied to an address containing a single quote, that every request carries
/// the credential's bearer token, and that nothing sensitive (the token, a raw response body) is ever
/// logged.
/// </summary>
public sealed class GraphTenantMemberDirectoryTests
{
    private static readonly Guid MemberObjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class StubTokenCredential(string token) : TokenCredential
    {
        public int CallCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    /// <summary>Captures every outgoing request (so a test can assert on the URL/headers) and replies with a canned response built from the request.</summary>
    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>Records every formatted log message so a test can assert none of them ever contain a secret.</summary>
    private sealed class RecordingLogger : ILogger<GraphTenantMemberDirectory>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static (GraphTenantMemberDirectory Directory, StubHttpMessageHandler Handler, StubTokenCredential Credential, RecordingLogger Logger) CreateDirectory(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string token = "fake-access-token")
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var credential = new StubTokenCredential(token);
        var logger = new RecordingLogger();
        var directory = new GraphTenantMemberDirectory(httpClient, credential, logger);
        return (directory, handler, credential, logger);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string GraphUserJson(Guid id, string displayName, string? mail, string? upn, string userType, bool accountEnabled) =>
        $$"""
        {
            "id": "{{id}}",
            "displayName": "{{displayName}}",
            "mail": {{(mail is null ? "null" : $"\"{mail}\"")}},
            "userPrincipalName": {{(upn is null ? "null" : $"\"{upn}\"")}},
            "userType": "{{userType}}",
            "accountEnabled": {{(accountEnabled ? "true" : "false")}}
        }
        """;

    [Fact]
    public async Task An_active_non_guest_member_is_resolved_by_object_id()
    {
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(
            HttpStatusCode.OK, GraphUserJson(MemberObjectId, "Member Person", "member@contoso.com", "member@contoso.com", "Member", accountEnabled: true)));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MemberObjectId, result!.ParticipantId.Value);
        Assert.Equal("Member Person", result.DisplayName);
    }

    [Fact]
    public async Task A_guest_account_is_not_resolved()
    {
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(
            HttpStatusCode.OK, GraphUserJson(MemberObjectId, "Guest Person", "guest@partner.com", "guest_partner.com#EXT#@contoso.onmicrosoft.com", "Guest", accountEnabled: true)));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_disabled_account_is_not_resolved()
    {
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(
            HttpStatusCode.OK, GraphUserJson(MemberObjectId, "Disabled Person", "disabled@contoso.com", "disabled@contoso.com", "Member", accountEnabled: false)));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_404_by_object_id_is_unresolved_not_an_exception()
    {
        var (directory, _, _, _) = CreateDirectory(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task An_exact_verified_address_is_resolved()
    {
        const string address = "member@contoso.com";
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(HttpStatusCode.OK, $$"""
        { "value": [ {{GraphUserJson(MemberObjectId, "Member Person", address, address, "Member", accountEnabled: true)}} ] }
        """));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(address)!, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(MemberObjectId, result!.ParticipantId.Value);
    }

    [Fact]
    public async Task An_ambiguous_address_match_is_not_resolved()
    {
        const string address = "shared@contoso.com";
        var otherId = Guid.NewGuid();
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(HttpStatusCode.OK, $$"""
        {
            "value": [
                {{GraphUserJson(MemberObjectId, "Member One", address, address, "Member", accountEnabled: true)}},
                {{GraphUserJson(otherId, "Member Two", address, address, "Member", accountEnabled: true)}}
            ]
        }
        """));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse(address)!, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_zero_result_address_lookup_is_unresolved()
    {
        var (directory, _, _, _) = CreateDirectory(_ => JsonResponse(HttpStatusCode.OK, """{ "value": [] }"""));

        var result = await directory.ResolveAsync(TenantMemberIdentifier.Parse("nobody@contoso.com")!, CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_authorization_failure_throws_a_typed_transient_failure_rather_than_reporting_not_found(HttpStatusCode status)
    {
        var (directory, _, _, _) = CreateDirectory(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<TenantDirectoryUnavailableException>(
            () => directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None));
    }

    [Fact]
    public async Task A_5xx_response_throws_a_typed_transient_failure_rather_than_reporting_not_found()
    {
        var (directory, _, _, _) = CreateDirectory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<TenantDirectoryUnavailableException>(
            () => directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None));
    }

    [Fact]
    public async Task A_transport_level_failure_throws_a_typed_transient_failure()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var directory = new GraphTenantMemberDirectory(httpClient, new StubTokenCredential("t"), NullLogger<GraphTenantMemberDirectory>.Instance);

        await Assert.ThrowsAsync<TenantDirectoryUnavailableException>(
            () => directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None));
    }

    [Fact]
    public async Task An_address_containing_a_single_quote_is_escaped_in_the_OData_filter_and_url_encoded()
    {
        const string address = "o'brien@contoso.com";
        var (directory, handler, _, _) = CreateDirectory(_ => JsonResponse(HttpStatusCode.OK, """{ "value": [] }"""));

        await directory.ResolveAsync(TenantMemberIdentifier.Parse(address)!, CancellationToken.None);

        var sentUrl = handler.Requests.Single().RequestUri!.ToString();
        // The raw single quote must never appear un-escaped in the filter (OData escapes ' as '');
        // Uri escaping then percent-encodes the whole filter expression into the query string.
        Assert.Contains("o%27%27brien%40contoso.com", sentUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("o'brien@contoso.com", sentUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_request_carries_the_credentials_bearer_token()
    {
        var (directory, handler, credential, _) = CreateDirectory(_ => JsonResponse(
            HttpStatusCode.OK, GraphUserJson(MemberObjectId, "Member Person", "member@contoso.com", "member@contoso.com", "Member", accountEnabled: true)),
            token: "super-secret-token-value");

        await directory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        var authHeader = handler.Requests.Single().Headers.Authorization;
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "super-secret-token-value"), authHeader);
        Assert.True(credential.CallCount > 0);
    }

    [Fact]
    public async Task Nothing_sensitive_is_ever_logged_across_success_notfound_and_failure_paths()
    {
        const string secretToken = "super-secret-token-value";

        var (successDirectory, _, _, successLogger) = CreateDirectory(_ => JsonResponse(
            HttpStatusCode.OK, GraphUserJson(MemberObjectId, "Member Person", "member@contoso.com", "member@contoso.com", "Member", accountEnabled: true)),
            token: secretToken);
        await successDirectory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None);

        var (failureDirectory, _, _, failureLogger) = CreateDirectory(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), token: secretToken);
        await Assert.ThrowsAsync<TenantDirectoryUnavailableException>(
            () => failureDirectory.ResolveAsync(TenantMemberIdentifier.Parse(MemberObjectId.ToString())!, CancellationToken.None));

        foreach (var message in successLogger.Messages.Concat(failureLogger.Messages))
        {
            Assert.DoesNotContain(secretToken, message, StringComparison.Ordinal);
            Assert.DoesNotContain("Member Person", message, StringComparison.Ordinal);
            Assert.DoesNotContain("member@contoso.com", message, StringComparison.Ordinal);
        }
    }
}
