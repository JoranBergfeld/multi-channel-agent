using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMultiChannelAgentInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<MultiChannelAgentDbContext>(options => options.UseSqlServer(
            connectionString,
            sql => sql.MigrationsAssembly(typeof(MultiChannelAgentDbContext).Assembly.FullName)));

        services.AddScoped<IInboxStore, SqlInboxStore>();
        services.AddScoped<IOutcomeStore, SqlOutcomeStore>();
        services.AddScoped<IDeliveryStore, SqlDeliveryStore>();
        services.AddScoped<ITurnResultStore, SqlTurnResultStore>();
        services.AddScoped<ILeaseCoordinator, SqlLeaseCoordinator>();
        services.AddScoped<IDeliverySender, LoggingDeliverySender>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IModelBoundary, ScriptedModelBoundary>();
        services.AddScoped<TurnAcceptanceService>();
        services.AddScoped<TurnProcessingCoordinator>();
        services.AddScoped<DeliveryDispatchCoordinator>();
        services.AddScoped<TurnOutcomeReader>();

        return services;
    }
}
