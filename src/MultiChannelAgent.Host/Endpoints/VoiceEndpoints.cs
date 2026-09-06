using System.Security.Claims;
using System.Text.Json.Serialization;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Domain.Voice;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Security;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>Wire shape for the <c>POST /api/voice/admit</c> request.</summary>
public sealed record AdmitVoiceHttpRequest(string? SdpOffer);

/// <summary>
/// Wire shape for <c>POST /api/voice/admit</c> response. Never serializes
/// <c>ControlSessionId</c>, provider endpoints, Azure URLs, keys, tokens, or credential internals.
/// Null/omitted fields are consistent: denied admissions have no <c>VoiceSessionId</c> or
/// <c>SdpAnswer</c>; successful admissions have no <c>DenialReason</c>.
/// </summary>
public sealed record AdmitVoiceHttpResponse(
    bool Admitted,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? VoiceSessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SdpAnswer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DenialReason);

/// <summary>Wire shape for <c>POST /api/voice/heartbeat</c> and <c>POST /api/voice/release</c> requests.</summary>
public sealed record VoiceSessionIdHttpRequest(string? VoiceSessionId);

/// <summary>Wire shape for <c>POST /api/voice/heartbeat</c> response.</summary>
public sealed record HeartbeatHttpResponse(
    bool Renewed,
    string LifecycleState,
    int? RemainingSeconds,
    string? ForcedCloseReason);

/// <summary>Maps the voice session lifecycle HTTP endpoints.</summary>
public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/voice")
            .RequireAuthorization(AuthorizationPolicies.ActiveTenantMember);

        // ── Admit ────────────────────────────────────────────────────────────
        group.MapPost("/admit", async (
            AdmitVoiceHttpRequest request,
            ClaimsPrincipal user,
            HttpContext httpContext,
            VoiceAdmissionService admissionService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.SdpOffer))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["sdpOffer"] = ["sdpOffer is required and must not be blank."],
                });
            }

            var participantId = user.GetParticipantId();
            var channelConversationId = new Domain.Turns.ChannelConversationId(
                WebConversationCookie.EnsureId(httpContext));

            var result = await admissionService.AdmitAsync(
                participantId, channelConversationId, request.SdpOffer, cancellationToken);

            if (result.Admitted)
            {
                return Results.Ok(new AdmitVoiceHttpResponse(
                    Admitted: true,
                    VoiceSessionId: result.VoiceSessionId!.Value.Value,
                    SdpAnswer: result.SdpAnswer,
                    DenialReason: null));
            }

            return Results.Ok(new AdmitVoiceHttpResponse(
                Admitted: false,
                VoiceSessionId: null,
                SdpAnswer: null,
                DenialReason: result.DenialReason!.Value.ToString()));
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        // ── Heartbeat ────────────────────────────────────────────────────────
        group.MapPost("/heartbeat", async (
            VoiceSessionIdHttpRequest request,
            ClaimsPrincipal user,
            VoiceSessionReleaseService releaseService,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseVoiceSessionId(request.VoiceSessionId, out var sessionId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["voiceSessionId"] = ["voiceSessionId must be a valid GUID."],
                });
            }

            var participantId = user.GetParticipantId();
            var result = await releaseService.HeartbeatAsync(sessionId, participantId, cancellationToken);

            if (result.LifecycleState == HeartbeatLifecycleState.NotFound)
            {
                return Results.NotFound();
            }

            var lifecycleStateWire = result.LifecycleState switch
            {
                HeartbeatLifecycleState.Active => "active",
                HeartbeatLifecycleState.WarningDue => "warning_due",
                HeartbeatLifecycleState.Expired => "expired",
                HeartbeatLifecycleState.Idle => "idle",
                _ => throw new ArgumentOutOfRangeException(nameof(result.LifecycleState), result.LifecycleState, "Unexpected lifecycle state."),
            };

            return Results.Ok(new HeartbeatHttpResponse(
                Renewed: result.Renewed,
                LifecycleState: lifecycleStateWire,
                RemainingSeconds: result.RemainingSeconds,
                ForcedCloseReason: result.ForcedCloseReason));
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        // ── Release ──────────────────────────────────────────────────────────
        group.MapPost("/release", async (
            VoiceSessionIdHttpRequest request,
            ClaimsPrincipal user,
            IVoiceSessionStore store,
            VoiceSessionReleaseService releaseService,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseVoiceSessionId(request.VoiceSessionId, out var sessionId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["voiceSessionId"] = ["voiceSessionId must be a valid GUID."],
                });
            }

            var participantId = user.GetParticipantId();

            // Check ownership before releasing — return 404 for missing or wrong-owner without
            // disclosing whether the session exists at all.
            var session = await store.FindByIdAsync(sessionId, cancellationToken);
            if (session is null || session.ParticipantId != participantId)
            {
                return Results.NotFound();
            }

            if (session.Status == VoiceSessionStatus.Ended)
            {
                return Results.Ok();
            }

            await releaseService.ReleaseAsync(sessionId, participantId, cancellationToken);
            return Results.Ok();
        }).AddEndpointFilter<AntiforgeryEndpointFilter>();

        return endpoints;
    }

    private static bool TryParseVoiceSessionId(string? raw, out VoiceSessionId sessionId)
    {
        if (Guid.TryParse(raw, out var guid) && guid != Guid.Empty)
        {
            sessionId = new VoiceSessionId(guid);
            return true;
        }

        sessionId = default;
        return false;
    }
}
