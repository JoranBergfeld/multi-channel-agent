using Microsoft.AspNetCore.Antiforgery;

namespace MultiChannelAgent.Host.Security;

/// <summary>
/// Enforces CSRF protection on mutating minimal API endpoints by validating the antiforgery token
/// pair (cookie secret + request header) on every invocation. A missing or invalid token returns a
/// controlled 400 instead of letting <see cref="AntiforgeryValidationException"/> escape as a 500.
/// </summary>
public sealed class AntiforgeryEndpointFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "CSRF validation failed.");
        }

        return await next(context);
    }
}
