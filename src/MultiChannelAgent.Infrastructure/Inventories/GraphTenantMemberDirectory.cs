using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// The production <see cref="ITenantMemberDirectory"/>: resolves an exact
/// <see cref="TenantMemberIdentifier"/> against Microsoft Graph v1.0 over a typed
/// <see cref="HttpClient"/>, authenticated with an app-only <c>https://graph.microsoft.com/.default</c>
/// token acquired lazily (only when a caller actually resolves someone - never at startup, never for
/// container liveness) from the injected <see cref="TokenCredential"/>. An object id resolves via
/// <c>GET /users/{id}</c>; an address resolves via a <c>$filter</c> query matching <c>mail</c> or
/// <c>userPrincipalName</c> exactly, safely OData-escaped and capped at two results so more than one
/// match is treated as ambiguous rather than guessed. Every candidate must be
/// <c>accountEnabled=true</c>, <c>userType=Member</c> (never a guest), and carry a valid id and
/// display name - anything else, plus a plain 404 or zero/ambiguous address matches, is an
/// authoritative "unresolved" (null), exactly like the real member does not exist. A 401/403 or any
/// other non-2xx/network/timeout failure is never treated as "unresolved": it throws
/// <see cref="TenantDirectoryUnavailableException"/> so a Graph outage surfaces as a visible failure
/// instead of silently orphaning every Inventory. Never logs the bearer token or a raw response body.
/// </summary>
public sealed class GraphTenantMemberDirectory(HttpClient httpClient, TokenCredential credential, ILogger<GraphTenantMemberDirectory> logger)
    : ITenantMemberDirectory
{
    private static readonly string[] GraphDefaultScope = ["https://graph.microsoft.com/.default"];
    private const string SelectFields = "id,displayName,userPrincipalName,mail,userType,accountEnabled";

    public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken) =>
        identifier.ObjectId is { } objectId
            ? ResolveByObjectIdAsync(objectId, cancellationToken)
            : ResolveByAddressAsync(identifier.Address!, cancellationToken);

    private async Task<ResolvedTenantMember?> ResolveByObjectIdAsync(Guid objectId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync($"users/{objectId:D}?$select={SelectFields}", cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureAuthoritative(response);

        var user = await response.Content.ReadFromJsonAsync<GraphUserDto>(cancellationToken);
        return ToResolvedMemberIfEligible(user, expectedAddress: null);
    }

    private async Task<ResolvedTenantMember?> ResolveByAddressAsync(string address, CancellationToken cancellationToken)
    {
        var escapedAddress = address.Replace("'", "''", StringComparison.Ordinal);
        var filter = $"mail eq '{escapedAddress}' or userPrincipalName eq '{escapedAddress}'";
        var path = $"users?$filter={Uri.EscapeDataString(filter)}&$select={SelectFields}&$top=2";

        using var response = await SendAsync(path, cancellationToken);
        EnsureAuthoritative(response);

        var body = await response.Content.ReadFromJsonAsync<GraphUserListDto>(cancellationToken);
        var candidates = body?.Value ?? [];

        // Zero matches: not found. Two matches (the query is capped at $top=2): ambiguous. Either
        // way, never a fuzzy/best-effort pick - only a single exact match resolves.
        return candidates.Count == 1 ? ToResolvedMemberIfEligible(candidates[0], address) : null;
    }

    private static ResolvedTenantMember? ToResolvedMemberIfEligible(GraphUserDto? user, string? expectedAddress)
    {
        if (user is null
            || user.AccountEnabled is not true
            || !string.Equals(user.UserType, "Member", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(user.DisplayName)
            || !Guid.TryParse(user.Id, out var objectId)
            || objectId == Guid.Empty)
        {
            return null;
        }

        if (expectedAddress is not null
            && !string.Equals(user.Mail, expectedAddress, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(user.UserPrincipalName, expectedAddress, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ResolvedTenantMember(new ParticipantId(objectId), user.DisplayName);
    }

    private async Task<HttpResponseMessage> SendAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(GraphDefaultScope), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to acquire a Microsoft Graph access token.");
            throw new TenantDirectoryUnavailableException("Failed to acquire a Microsoft Graph access token.", ex);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft Graph request failed (network/transport error).");
            throw new TenantDirectoryUnavailableException("Microsoft Graph request failed (network/transport error).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Microsoft Graph request timed out.");
            throw new TenantDirectoryUnavailableException("Microsoft Graph request timed out.", ex);
        }
    }

    private void EnsureAuthoritative(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Never logs the response body - only the status code - so a Graph error payload (which can
        // include tenant/user detail) is never written to logs.
        logger.LogWarning("Microsoft Graph request failed with status {StatusCode}.", (int)response.StatusCode);
        throw new TenantDirectoryUnavailableException($"Microsoft Graph request failed with status {(int)response.StatusCode}.");
    }

    private sealed record GraphUserDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
        [property: JsonPropertyName("mail")] string? Mail,
        [property: JsonPropertyName("userType")] string? UserType,
        [property: JsonPropertyName("accountEnabled")] bool? AccountEnabled);

    private sealed record GraphUserListDto([property: JsonPropertyName("value")] List<GraphUserDto>? Value);
}
