using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
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

builder.Services.AddMultiChannelAgentInfrastructure(connectionString);

var authenticationProvider = builder.Configuration["Authentication:Provider"] ?? "Entra";
var challengeScheme = string.Equals(authenticationProvider, "Test", StringComparison.OrdinalIgnoreCase)
    ? ProviderSchemes.Test
    : ProviderSchemes.Entra;

if (challengeScheme == ProviderSchemes.Test)
{
    // Overrides the production placeholder directory adapter with a deterministic double tests
    // control entirely through HTTP (see TestAuthEndpoints) - never exercising the real,
    // not-yet-wired Microsoft Graph boundary outside Production.
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
app.MapInventoryEndpoints();
app.MapInventoryGovernanceEndpoints();
app.MapInventoryRecoveryEndpoints();
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
