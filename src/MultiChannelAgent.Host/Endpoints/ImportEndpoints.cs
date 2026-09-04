using System.Security.Claims;
using Microsoft.AspNetCore.Http.Metadata;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>The wire shape accepted at the import confirmation and cancellation endpoints.</summary>
public sealed record ImportDecisionHttpRequest(Guid ProposalId, string? Token);

/// <summary>
/// Maps the signed-in Initial Import workflow: the eligibility read, the bounded multipart
/// validation, and the confirmation and cancellation of the one pending import.
///
/// It is a workflow rather than a projection, so unlike the Stock and reference reads every mutating
/// route carries the shipped <see cref="AntiforgeryEndpointFilter"/>, and unlike a conversational
/// tool it never goes near a Turn. What it shares with both is the non-disclosure rule: whether the
/// Inventory does not exist or simply is not this Participant's, the answer is an identical 404.
/// </summary>
public static class ImportEndpoints
{
    /// <summary>
    /// The file bound plus a little multipart framing: what one whole upload request may weigh, what
    /// the server refuses beyond, and what this route is willing to hold in memory at once.
    /// </summary>
    private const long MaxRequestBodyBytes = ImportContract.MaxUploadBytes + (4 * 1024);

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/inventories/{inventoryId:guid}/import")
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        group.MapGet("", async (
            Guid inventoryId,
            ClaimsPrincipal user,
            InitialImportService importService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await importService.ReadEligibilityAsync(
                user.GetParticipantId(), new InventoryId(inventoryId), timeProvider.GetUtcNow(), cancellationToken);

            // Whether the Inventory does not exist or simply is not authorized for this Participant,
            // the response must be identical: a plain 404, never a distinct signal.
            return result.Kind switch
            {
                ImportResultKind.Completed => Results.Ok(result.View),
                ImportResultKind.NotFound or ImportResultKind.Forbidden => Results.NotFound(),
                _ => throw new InvalidOperationException($"Unhandled {nameof(ImportResultKind)}: {result.Kind}."),
            };
        });

        group.MapPost("/validate", async (
            Guid inventoryId,
            HttpRequest request,
            ClaimsPrincipal user,
            InitialImportService importService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType)
            {
                return MissingFile();
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(cancellationToken);
            }
            catch (InvalidDataException)
            {
                // A body that is not readable multipart is a malformed upload rather than a server
                // fault, and it is answered exactly like no file at all: naming the part expected.
                return MissingFile();
            }

            var file = form.Files.GetFile("file");

            if (file is null || file.Length == 0)
            {
                return MissingFile();
            }

            if (file.Length > ImportContract.MaxUploadBytes)
            {
                return TooLarge();
            }

            // Bounded before it is read, and read once: the whole file has to be in hand to digest it
            // and to validate it, and it is never written anywhere but the proposal's own row.
            using var buffer = new MemoryStream(capacity: (int)file.Length);
            await using (var stream = file.OpenReadStream())
            {
                await stream.CopyToAsync(buffer, cancellationToken);
            }

            var result = await importService.ValidateAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                buffer.ToArray(),
                timeProvider.GetUtcNow(),
                cancellationToken);

            return result.Kind switch
            {
                ImportResultKind.Completed => Results.Ok(result.View),
                ImportResultKind.NotFound or ImportResultKind.Forbidden => Results.NotFound(),
                ImportResultKind.NotEmpty => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "This Inventory already holds Stock, so there is nothing initial to import.",
                    extensions: new Dictionary<string, object?> { ["code"] = "inventory_not_empty" }),

                // Every actionable error at once, plus the exact number the bounded report left out.
                ImportResultKind.Invalid => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "That file could not be imported.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "invalid_file",
                        ["errors"] = result.Errors,
                        ["omittedErrorCount"] = result.OmittedErrorCount,
                    }),
                _ => throw new InvalidOperationException($"Unhandled {nameof(ImportResultKind)}: {result.Kind}."),
            };
        })
        .AddEndpointFilter<AntiforgeryEndpointFilter>()
        .WithMetadata(new ImportUploadSizeLimit(MaxRequestBodyBytes))

        // Whatever this route accepts, it holds in memory. ASP.NET Core buffers a multipart file
        // section through FileBufferingReadStream, which spools everything past its 64 KiB default
        // threshold to a temp file - so left at the defaults, every import worth importing would be
        // written to disk, when the promise is that the raw upload lives in memory for this request
        // and in SQL while its proposal is pending, and nowhere else. Bounding one part by the same
        // number that bounds the whole body makes that spill unreachable rather than merely unlikely:
        // a part is either buffered whole below the threshold or refused before it grows past it.
        //
        // It is stated as this route's own metadata rather than configured for the Host, both because
        // no other route accepts an upload and because every read of this form has to honor it -
        // including the one antiforgery performs when a request carries its token in the body rather
        // than in the header.
        .WithFormOptions(
            memoryBufferThreshold: (int)MaxRequestBodyBytes,
            multipartBodyLengthLimit: MaxRequestBodyBytes);

        group.MapPost("/confirm", async (
            Guid inventoryId,
            ImportDecisionHttpRequest body,
            ClaimsPrincipal user,
            ImportConfirmationService confirmationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await confirmationService.ConfirmAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new ImportProposalId(body.ProposalId),
                body.Token,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return ToResult(result);
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        group.MapPost("/reject", async (
            Guid inventoryId,
            ImportDecisionHttpRequest body,
            ClaimsPrincipal user,
            ImportConfirmationService confirmationService,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await confirmationService.RejectAsync(
                user.GetParticipantId(),
                new InventoryId(inventoryId),
                new ImportProposalId(body.ProposalId),
                body.Token,
                timeProvider.GetUtcNow(),
                cancellationToken);

            return ToResult(result);
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }

    private static IResult ToResult(ImportConfirmationResult result) => result.Kind switch
    {
        ImportConfirmationResultKind.Completed => Results.Ok(result.View),
        ImportConfirmationResultKind.Rejected => Results.Ok(new { rejected = true }),

        // A refusal and an absence look the same on purpose.
        ImportConfirmationResultKind.NotFound or ImportConfirmationResultKind.Forbidden => Results.NotFound(),
        ImportConfirmationResultKind.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "That import can no longer be applied.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        ImportConfirmationResultKind.Invalid => Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "That import could not be confirmed.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Code }),
        _ => throw new InvalidOperationException($"Unhandled {nameof(ImportConfirmationResultKind)}: {result.Kind}."),
    };

    private static IResult MissingFile() => Results.ValidationProblem(
        new Dictionary<string, string[]>
        {
            ["file"] = ["A single non-empty CSV file part named 'file' is required."],
        });

    private static IResult TooLarge() => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: $"An import file must not exceed {ImportContract.MaxUploadBytes} bytes.",
        extensions: new Dictionary<string, object?> { ["code"] = "file_too_large" });

    /// <summary>
    /// Applies the per-route body limit. The server's global limit stays where it is; only this route
    /// needs to accept a two-mebibyte body, and only up to that.
    /// </summary>
    private sealed class ImportUploadSizeLimit(long maxBytes) : IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => maxBytes;
    }
}
