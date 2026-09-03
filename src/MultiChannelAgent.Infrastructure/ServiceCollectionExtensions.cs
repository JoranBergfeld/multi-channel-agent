using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    /// <summary>Microsoft Graph's base URL for the production tenant member directory adapter - <c>v1.0</c>, never <c>beta</c>.</summary>
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";

    public static IServiceCollection AddMultiChannelAgentInfrastructure(
        this IServiceCollection services, string connectionString, IConfiguration configuration)
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
        services.AddScoped<IToolDispatcher, StockToolDispatcher>();
        services.AddScoped<TurnAcceptanceService>();
        services.AddScoped<TurnProcessingCoordinator>();
        services.AddScoped<DeliveryDispatchCoordinator>();
        services.AddScoped<TurnOutcomeReader>();
        services.AddScoped<IFoundryConversationBindingStore, SqlFoundryConversationBindingStore>();
        services.AddScoped<TurnExecutionContextFactory>();

        services.AddScoped<IParticipantStore, SqlParticipantStore>();
        services.AddScoped<IInventoryStore, SqlInventoryStore>();
        services.AddScoped<IActiveInventorySelectionStore, SqlActiveInventorySelectionStore>();
        services.AddScoped<IInventoryAuthorizationAuditStore, SqlInventoryAuthorizationAuditStore>();
        services.AddScoped<IInventoryMembershipStore, SqlInventoryMembershipStore>();
        services.AddScoped<IInventoryOwnershipStore, SqlInventoryOwnershipStore>();
        services.AddScoped<IInventoryRecoveryStore, SqlInventoryRecoveryStore>();
        services.AddScoped<IStockStore, SqlStockStore>();
        services.AddScoped<IInventoryReferenceStore, SqlInventoryReferenceStore>();

        // Only ever constructed (and its TokenCredential only ever built/validated) the first time
        // something actually resolves ITenantMemberDirectory - which never happens for
        // Authentication:Provider=Test (Program.cs registers TestTenantMemberDirectory afterward, and
        // ASP.NET Core resolves the last registration) nor for /health/live, so a Test-mode or
        // liveness-only process never needs valid Graph configuration or network access. Scoped (not
        // Singleton) to match the typed HttpClient's own recommended Transient-per-resolution
        // lifetime, exactly like every other Scoped store above.
        services.AddSingleton<TokenCredential>(_ => GraphCredentialFactory.Create(configuration));
        services.AddHttpClient<GraphTenantMemberDirectory>(client => client.BaseAddress = new Uri(GraphBaseUrl));
        services.AddScoped<ITenantMemberDirectory>(sp => sp.GetRequiredService<GraphTenantMemberDirectory>());

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
        services.AddScoped<StockListingService>();
        services.AddScoped<StockFindingService>();

        return services;
    }
}
