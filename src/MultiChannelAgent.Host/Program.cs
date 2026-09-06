using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Host.Authentication;
using MultiChannelAgent.Host.Authorization;
using MultiChannelAgent.Host.Endpoints;
using MultiChannelAgent.Host.HealthChecks;
using MultiChannelAgent.Host.Workers;
using MultiChannelAgent.Infrastructure;
using MultiChannelAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MultiChannelAgent")
    ?? throw new InvalidOperationException("Missing required 'ConnectionStrings:MultiChannelAgent' configuration value.");

builder.Services.AddMultiChannelAgentInfrastructure(connectionString, builder.Configuration);

var authenticationProvider = builder.Configuration["Authentication:Provider"] ?? "Entra";
var challengeScheme = string.Equals(authenticationProvider, "Test", StringComparison.OrdinalIgnoreCase)
    ? ProviderSchemes.Test
    : ProviderSchemes.Entra;

if (challengeScheme == ProviderSchemes.Test)
{
    // Overrides the production Microsoft Graph-backed directory adapter with a deterministic double
    // tests control entirely through HTTP (see TestAuthEndpoints) - never exercising the real
    // Microsoft Graph boundary (or requiring Graph credentials/network access) outside Production.
    builder.Services.AddSingleton<ITenantMemberDirectory, TestTenantMemberDirectory>();
}

builder.Services.AddMultiChannelAgentAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddMultiChannelAgentAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "mca_csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

builder.Services.AddHostedService<TurnProcessingWorker>();
builder.Services.AddHostedService<DeliveryDispatchWorker>();
builder.Services.AddHostedService<OutcomePayloadCleanupWorker>();
builder.Services.AddHostedService<TurnProgressEventCleanupWorker>();

// ── Voice ────────────────────────────────────────────────────────────────
// VoiceOptions is bound from configuration and validated when enabled. When disabled (the default),
// validation is a no-op and no Azure credentials are required, so the app starts cleanly in test
// environments. The stable process instance ID is registered once so every resolve shares the same
// identity — not a new ID per scope.
var voiceOptions = builder.Configuration.GetSection("Voice").Get<VoiceOptions>() ?? new VoiceOptions();
if (voiceOptions.Enabled)
{
    var errors = voiceOptions.Validate();
    if (errors.Count > 0)
    {
        throw new InvalidOperationException(
            $"Voice configuration is invalid: {string.Join("; ", errors)}");
    }
}
builder.Services.AddSingleton(voiceOptions);

var ownerInstanceId = $"host-{Environment.MachineName}-{Guid.NewGuid():N}";
builder.Services.AddScoped<VoiceAdmissionService>(sp => new VoiceAdmissionService(
    sp.GetRequiredService<IVoiceSessionStore>(),
    sp.GetRequiredService<IVoiceLiveGateway>(),
    sp.GetRequiredService<VoiceOptions>(),
    sp.GetRequiredService<TimeProvider>(),
    ownerInstanceId));

builder.Services.AddScoped<VoiceSessionReleaseService>(sp => new VoiceSessionReleaseService(
    sp.GetRequiredService<IVoiceSessionStore>(),
    sp.GetRequiredService<IVoiceLiveGateway>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<VoiceOptions>().IdleTimeout));

builder.Services.AddScoped<VoiceSessionCleanupCoordinator>(sp => new VoiceSessionCleanupCoordinator(
    sp.GetRequiredService<IVoiceSessionStore>(),
    sp.GetRequiredService<IVoiceLiveGateway>(),
    sp.GetRequiredService<TimeProvider>(),
    ownerInstanceId,
    sp.GetRequiredService<VoiceOptions>().IdleTimeout * 3));

builder.Services.AddHostedService<VoiceSessionCleanupWorker>();

// The production numbers, in one place. A test that must not wait fifteen real seconds for a
// heartbeat replaces this one registration and changes nothing else.
builder.Services.AddSingleton(new TurnStreamOptions());
builder.Services.AddSingleton(new InventoryStreamOptions());

builder.Services.AddHostedService<ConfirmationProposalCleanupWorker>();
builder.Services.AddHostedService<ImportCleanupWorker>();

builder.Services
    .AddHealthChecks()
    .AddCheck<LivenessHealthCheck>("self", tags: ["live"])
    .AddDbContextCheck<MultiChannelAgentDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.UseAuthentication();
app.UseAuthorization();

app.MapTurnEndpoints();
app.MapSessionEndpoints();
app.MapVoiceEndpoints();
app.MapInventoryEndpoints();
app.MapInventoryGovernanceEndpoints();
app.MapInventoryRecoveryEndpoints();
app.MapInventoryEventEndpoints();
app.MapConversationEndpoints();
app.MapStockEndpoints();
app.MapReferenceEndpoints();
app.MapImportEndpoints();
app.MapAuthEndpoints(challengeScheme);

if (challengeScheme == ProviderSchemes.Test)
{
    app.MapTestAuthEndpoints();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this Host in tests.</summary>
public partial class Program
{
}
