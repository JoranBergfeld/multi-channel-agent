using Microsoft.Agents.Hosting.AspNetCore;
using MultiChannelAgent.Host.Channels.Teams;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>Maps the Teams activity receiver endpoint handled by the Microsoft Agents SDK adapter.</summary>
public static class TeamsActivityEndpoints
{
    public static IEndpointRouteBuilder MapTeamsActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/messages", async (
            HttpRequest request,
            HttpResponse response,
            IAgentHttpAdapter adapter,
            InventoryAgentTeamsHandler handler,
            CancellationToken cancellationToken) =>
        {
            await adapter.ProcessAsync(request, response, handler, cancellationToken);
        }).AllowAnonymous();

        return endpoints;
    }
}
