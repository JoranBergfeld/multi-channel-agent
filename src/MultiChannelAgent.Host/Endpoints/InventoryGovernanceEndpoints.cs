using System.Security.Claims;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted at the membership grant/change endpoint.</summary>
public sealed record GrantMembershipHttpRequest(string? TargetIdentifier, string? Role);

/// <summary>The wire shape accepted at the ownership transfer endpoint.</summary>
public sealed record TransferOwnershipHttpRequest(string? TargetIdentifier);

/// <summary>One member as shown only to the Inventory's Owner.</summary>
public sealed record MemberHttpView(string ParticipantId, string DisplayName, string Role);

/// <summary>
/// Maps Owner-only membership administration (grant/change role, remove, list) and ownership
/// transfer HTTP endpoints. Every requester check flows through <see cref="InventoryMembershipService"/>
/// / <see cref="InventoryOwnershipTransferService"/>, so a non-member and a non-owner member are both
/// refused without ever disclosing whether an Inventory exists to a caller who is not authorized on
/// it. Every mutation carries CSRF protection.
/// </summary>
public static class InventoryGovernanceEndpoints
{
    public static IEndpointRouteBuilder MapInventoryGovernanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/inventories/{inventoryId:guid}")
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        group.MapGet("/members", async (
            Guid inventoryId,
            ClaimsPrincipal user,
            InventoryMembershipService membershipService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await membershipService.ListMembersAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), timeProvider.GetUtcNow(), cancellationToken);

            return result.Outcome switch
            {
                MembershipListOutcome.Listed => Results.Ok(
                    result.Members!.Select(m => new MemberHttpView(m.ParticipantId, m.DisplayName, m.Role)).ToList()),
                MembershipListOutcome.RequesterNotAuthorized => Results.NotFound(),
                MembershipListOutcome.RequesterNotOwner => Results.Forbid(),
                _ => throw new InvalidOperationException($"Unhandled {nameof(MembershipListOutcome)}: {result.Outcome}"),
            };
        });

        group.MapPut("/members", async (
            Guid inventoryId,
            GrantMembershipHttpRequest request,
            ClaimsPrincipal user,
            InventoryMembershipService membershipService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<MembershipRole>(request.Role, ignoreCase: true, out var role))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["role"] = ["role must be 'Viewer' or 'Editor'."],
                });
            }

            var result = await membershipService.GrantOrChangeAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), request.TargetIdentifier, role, timeProvider.GetUtcNow(), cancellationToken);

            return result.Outcome switch
            {
                MembershipRequestOutcome.Granted or MembershipRequestOutcome.RoleChanged => Results.Ok(),
                MembershipRequestOutcome.RequesterNotAuthorized => Results.NotFound(),
                MembershipRequestOutcome.RequesterNotOwner => Results.Forbid(),
                MembershipRequestOutcome.InvalidRole => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["role"] = ["role must be 'Viewer' or 'Editor'."],
                }),
                MembershipRequestOutcome.TargetNotResolved => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["targetIdentifier"] = ["targetIdentifier must resolve to an exact, active, non-guest tenant member."],
                }),
                MembershipRequestOutcome.TargetIsOwner => Results.Conflict(
                    new { message = "The target already holds ownership; use ownership transfer instead." }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(MembershipRequestOutcome)}: {result.Outcome}"),
            };
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapDelete("/members/{participantId:guid}", async (
            Guid inventoryId,
            Guid participantId,
            ClaimsPrincipal user,
            InventoryMembershipService membershipService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await membershipService.RemoveAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), new ParticipantId(participantId), timeProvider.GetUtcNow(), cancellationToken);

            return result.Outcome switch
            {
                MembershipRequestOutcome.Removed => Results.Ok(),
                MembershipRequestOutcome.RequesterNotAuthorized => Results.NotFound(),
                MembershipRequestOutcome.RequesterNotOwner => Results.Forbid(),
                MembershipRequestOutcome.TargetNotAMember => Results.NotFound(),
                MembershipRequestOutcome.TargetIsOwner => Results.Conflict(
                    new { message = "The current Owner cannot be removed; use ownership transfer instead." }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(MembershipRequestOutcome)}: {result.Outcome}"),
            };
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/transfer-ownership", async (
            Guid inventoryId,
            TransferOwnershipHttpRequest request,
            ClaimsPrincipal user,
            InventoryOwnershipTransferService transferService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await transferService.TransferAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), request.TargetIdentifier, timeProvider.GetUtcNow(), cancellationToken);

            return result.Outcome switch
            {
                TransferRequestOutcome.Transferred => Results.Ok(),
                TransferRequestOutcome.RequesterNotAuthorized => Results.NotFound(),
                TransferRequestOutcome.RequesterNotOwner => Results.Forbid(),
                TransferRequestOutcome.SelfTransferRejected => Results.Conflict(
                    new { message = "Transferring ownership to the current Owner is a no-op." }),
                TransferRequestOutcome.TargetNotResolved => Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["targetIdentifier"] = ["targetIdentifier must resolve to an exact, active, non-guest tenant member."],
                }),
                TransferRequestOutcome.ConcurrentModification => Results.Conflict(
                    new { message = "Ownership changed concurrently; please retry." }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(TransferRequestOutcome)}: {result.Outcome}"),
            };
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }
}
