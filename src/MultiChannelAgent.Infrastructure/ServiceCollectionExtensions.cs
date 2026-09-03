using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Authentication;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Authentication;
using MultiChannelAgent.Infrastructure.Inventories;
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

        services.AddScoped<IParticipantStore, SqlParticipantStore>();
        services.AddScoped<IInventoryStore, SqlInventoryStore>();
        services.AddScoped<IActiveInventorySelectionStore, SqlActiveInventorySelectionStore>();
        services.AddScoped<IInventoryAuthorizationAuditStore, SqlInventoryAuthorizationAuditStore>();
        services.AddScoped<IInventoryMembershipStore, SqlInventoryMembershipStore>();
        services.AddScoped<IInventoryOwnershipStore, SqlInventoryOwnershipStore>();
        services.AddScoped<IInventoryRecoveryStore, SqlInventoryRecoveryStore>();
        services.AddSingleton<ITenantMemberDirectory, PlaceholderTenantMemberDirectory>();
        services.AddScoped<IAuthTicketRepository, SqlAuthTicketRepository>();
        services.AddScoped<ParticipantSessionService>();
        services.AddScoped<InventoryCreationService>();
        services.AddScoped<InventoryListingService>();
        services.AddScoped<InventoryAuthorizationService>();
        services.AddScoped<InventorySelectionService>();
        services.AddScoped<InventoryBootstrapService>();
        services.AddScoped<InventoryMembershipService>();
        services.AddScoped<InventoryOwnershipTransferService>();
        services.AddScoped<InventoryRecoveryService>();

        return services;
    }
}
