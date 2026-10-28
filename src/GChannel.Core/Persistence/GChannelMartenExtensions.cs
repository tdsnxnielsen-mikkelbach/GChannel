using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Persistence.Documents;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Weasel.Core;
using Wolverine.Marten;

namespace GChannel.Core.Persistence;

public static class GChannelMartenExtensions
{
    /// <summary>Aspire connection name of the PostgreSQL database (must match the AppHost).</summary>
    public const string ConnectionName = "gchanneldb";

    /// <summary>PostgreSQL schema holding the Marten documents and the event store.</summary>
    public const string SchemaName = "gchannel";

    /// <summary>
    /// Registers the Aspire Npgsql data source (Microsoft Entra ID token auth against Azure Database for
    /// PostgreSQL, plain password auth against the local container) and Marten on top of it: the estate
    /// event store with inline snapshots for the customer / entitlement / link read models, plus the plain
    /// read-model documents. Schema changes are applied at startup (Marten takes an advisory lock, so the
    /// API and the worker can both do this safely).
    /// </summary>
    public static MartenServiceCollectionExtensions.MartenConfigurationExpression AddGChannelMarten(
        this IHostApplicationBuilder builder, Action<StoreOptions>? configure = null)
    {
        builder.AddAzureNpgsqlDataSource(ConnectionName);

        var marten = builder.Services.AddMarten(opts =>
            {
                opts.DatabaseSchemaName = SchemaName;
                opts.Events.DatabaseSchemaName = SchemaName;
                opts.Events.StreamIdentity = StreamIdentity.AsString;
                opts.UseSystemTextJsonForSerialization(EnumStorage.AsString, Casing.CamelCase);
                opts.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

                // Inline snapshots: the read-model documents are updated in the same transaction as the
                // events, so a query right after an append sees the new state.
                opts.Projections.Snapshot<CustomerRecord>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<EntitlementRecord>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<ResellerLinkRecord>(SnapshotLifecycle.Inline);

                RegisterEventTypes(opts);
                ConfigureDocuments(opts);
                configure?.Invoke(opts);
            })
            .UseNpgsqlDataSource()
            .UseLightweightSessions()
            // Wolverine inbox/outbox + saga storage in the same database (schema "wolverine"), and Marten
            // transactional middleware for message handlers / HTTP endpoints.
            .IntegrateWithWolverine(cfg => cfg.MessageStorageSchemaName = "wolverine")
            .ApplyAllDatabaseChangesOnStartup();

        return marten;
    }

    private static void RegisterEventTypes(StoreOptions opts)
    {
        var eventTypes = typeof(CustomerDiscovered).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(CustomerDiscovered).Namespace && t.IsClass && !t.IsAbstract && !t.IsNested);
        opts.Events.AddEventTypes(eventTypes);
    }

    private static void ConfigureDocuments(StoreOptions opts)
    {
        opts.Schema.For<CustomerRecord>()
            .Index(x => x.CustomerId, i => i.IsUnique = true)
            .Index(x => x.OwningLinkId)
            .Index(x => x.IsDeleted)
            .Index(x => x.Domain)
            .Index(x => x.CloudIdentityId);

        opts.Schema.For<EntitlementRecord>()
            .Index(x => x.EntitlementId, i => i.IsUnique = true)
            .Index(x => x.CustomerId)
            .Index(x => x.OwningLinkId)
            .Index(x => x.ProductId)
            .Index(x => x.IsDeleted)
            .Index(x => x.State)
            .Index(x => x.BillingAccount)
            .Index(x => x.ProvisioningId);

        opts.Schema.For<ResellerLinkRecord>()
            .Index(x => x.LinkId, i => i.IsUnique = true)
            .Index(x => x.LinkState)
            .Index(x => x.PrimaryDomain)
            .Index(x => x.ResellerCloudId);

        opts.Schema.For<CustomerSyncState>()
            .Index(x => x.LastSyncedUtc)
            .Index(x => x.OwningLinkId)
            .Index(x => x.IsDeleted);

        opts.Schema.For<LinkSyncState>()
            .Index(x => x.LastSyncedUtc);

        opts.Schema.For<IdentityCheckLog>()
            .Index(x => x.Domain)
            .Index(x => x.PerformedAt);

        opts.Schema.For<ChannelNotificationRecord>()
            .Index(x => x.ReceivedAt)
            .Index(x => x.CustomerId)
            .Index(x => x.EntitlementId);

        opts.Schema.For<CustomerSpendRecord>()
            .Index(x => x.CustomerId)
            .Index(x => x.OwningLinkId)
            .Index(x => x.InvoiceMonth)
            .Index(x => x.BillingSubAccountId);

        opts.Schema.For<SpendDailyRecord>()
            .Index(x => x.CustomerId)
            .Index(x => x.Day)
            .Index(x => x.BillingSubAccountId);

        opts.Schema.For<CustomerSpendBySellerRecord>()
            .Index(x => x.CustomerId)
            .Index(x => x.InvoiceMonth)
            .Index(x => x.BillingSubAccountId);

        opts.Schema.For<MarketplaceOrderRecord>()
            .Index(x => x.BillingAccountId)
            .Index(x => x.CustomerId);

        opts.Schema.For<BillingPostureAccountRecord>()
            .Index(x => x.CustomerId);

        opts.Schema.For<BillingPostureMemberRecord>()
            .Index(x => x.Email)
            .Index(x => x.BillingAccountId);

        opts.Schema.For<RecurringJobState>();
        opts.Schema.For<SyncCursor>();
        opts.Schema.For<DashboardRefreshState>();
        opts.Schema.For<DashboardSnapshot>();
    }
}
