using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Host.Endpoints;
using MultiChannelAgent.Host.HealthChecks;
using MultiChannelAgent.Host.Workers;
using MultiChannelAgent.Infrastructure;
using MultiChannelAgent.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MultiChannelAgent")
    ?? throw new InvalidOperationException("Missing required 'ConnectionStrings:MultiChannelAgent' configuration value.");

builder.Services.AddMultiChannelAgentInfrastructure(connectionString);

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

app.MapTurnEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this Host in tests.</summary>
public partial class Program
{
}
