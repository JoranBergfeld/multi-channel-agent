using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Authentication;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Application.Voice;
using MultiChannelAgent.Infrastructure.Authentication;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Turns;
using MultiChannelAgent.Infrastructure.Voice;

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
        services.AddScoped<ITurnProgressEventStore, SqlTurnProgressEventStore>();
        services.AddScoped<ILeaseCoordinator, SqlLeaseCoordinator>();
        services.AddScoped<IDeliverySender, LoggingDeliverySender>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IModelBoundary, ScriptedModelBoundary>();

        // Voice infrastructure — the store is always available (it is just SQL); the gateway defaults
        // to a disabled stub that throws if unexpectedly invoked (VoiceAdmissionService fast-exits
        // before reaching the gateway when voice is disabled). A later task replaces this with the
        // real Azure adapter when voice is enabled.
        services.AddScoped<IVoiceSessionStore, SqlVoiceSessionStore>();
        services.AddSingleton<IVoiceLiveGateway, DisabledVoiceLiveGateway>();

        services.AddScoped<StockToolDispatcher>();
        services.AddScoped<ReferenceToolDispatcher>();

        // One registered dispatcher, which routes by an explicit closed set of tool names.
        services.AddScoped<IToolDispatcher>(sp => new InventoryToolRouter(
            sp.GetRequiredService<StockToolDispatcher>(), sp.GetRequiredService<ReferenceToolDispatcher>()));
        services.AddScoped<TurnAcceptanceService>();
        services.AddScoped<TurnProcessingCoordinator>();
        services.AddScoped<DeliveryDispatchCoordinator>();
        services.AddScoped<OutcomePayloadCleanupCoordinator>();
        services.AddScoped<TurnProgressEventCleanupCoordinator>();
        services.AddScoped<TurnOutcomeReader>();
        services.AddScoped<TurnEventReader>();
        services.AddScoped<IFoundryConversationBindingStore, SqlFoundryConversationBindingStore>();
        services.AddScoped<IConversationRotationStore, SqlConversationRotationStore>();
        services.AddScoped<ConversationRotationService>();
        services.AddScoped<TurnExecutionContextFactory>();

        services.AddScoped<IParticipantStore, SqlParticipantStore>();
        services.AddScoped<IInventoryStore, SqlInventoryStore>();
        services.AddScoped<IActiveInventorySelectionStore, SqlActiveInventorySelectionStore>();
        services.AddScoped<IInventoryAuthorizationAuditStore, SqlInventoryAuthorizationAuditStore>();
        services.AddScoped<IInventoryMembershipStore, SqlInventoryMembershipStore>();
        services.AddScoped<IInventoryOwnershipStore, SqlInventoryOwnershipStore>();
        services.AddScoped<IInventoryRecoveryStore, SqlInventoryRecoveryStore>();
        services.AddScoped<IStockStore, SqlStockStore>();
        services.AddScoped<IStockMutationStore, SqlStockMutationStore>();
        services.AddScoped<IInventoryReferenceStore, SqlInventoryReferenceStore>();
        services.AddScoped<IConfirmationProposalStore, SqlConfirmationProposalStore>();
        services.AddScoped<IStockChangeSetStore, SqlStockChangeSetStore>();
        services.AddScoped<IReferenceCatalogStore, SqlReferenceCatalogStore>();
        services.AddScoped<IReferenceAdministrationStore, SqlReferenceAdministrationStore>();
        services.AddScoped<IImportProposalStore, SqlImportProposalStore>();
        services.AddScoped<IImportExecutionStore, SqlImportExecutionStore>();
        services.AddScoped<IStockEmptyStateReader, SqlStockEmptyStateReader>();
        services.AddScoped<IInventoryAuditRetentionStore, SqlInventoryAuditRetentionStore>();
        services.AddScoped<IInventoryVersionStore, SqlInventoryVersionStore>();
        services.AddScoped<ReferenceChangeResolver>();
        services.AddScoped<ReferenceAdministrationService>();
        services.AddScoped<ReferenceListingService>();

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
        services.AddScoped<InventoryInvalidationReader>();
        services.AddScoped<InventoryAuthorizationService>();
        services.AddScoped<InventorySelectionService>();
        services.AddScoped<InventoryBootstrapService>();
        services.AddScoped<InventoryMembershipService>();
        services.AddScoped<InventoryOwnershipTransferService>();
        services.AddScoped<InventoryRecoveryService>();
        services.AddScoped<StockListingService>();
        services.AddScoped<StockFindingService>();
        services.AddScoped<StockMutationService>();
        services.AddScoped<StockChangeResolver>();
        services.AddScoped<StockChangeSetService>();
        services.AddScoped<InventoryConfirmationService>();
        services.AddScoped<ConfirmationProposalLifecycle>();
        services.AddScoped<ConfirmationProposalCleanupCoordinator>();
        services.AddScoped<ImportReferenceResolver>();
        services.AddScoped<InitialImportService>();
        services.AddScoped<ImportConfirmationService>();
        services.AddScoped<ImportCleanupCoordinator>();

        return services;
    }
}
