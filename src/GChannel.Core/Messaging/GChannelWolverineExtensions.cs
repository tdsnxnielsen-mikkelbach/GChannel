using JasperFx.Core;
using System.Reflection;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.ErrorHandling;

namespace GChannel.Core.Messaging;

/// <summary>Which host is being configured — decides which Service Bus entities are listened to.</summary>
public enum MessagingRole
{
    Api,
    Worker,
}

public static class GChannelWolverineExtensions
{
    /// <summary>
    /// Shared Wolverine configuration for both hosts: the Azure Service Bus transport over the Aspire
    /// connection (<c>ConnectionStrings:messaging</c> — always a real namespace: Aspire hands out the
    /// namespace host name and Wolverine authenticates with <c>DefaultAzureCredential</c>, i.e. the
    /// developer's Azure CLI / Visual Studio sign-in locally and the container app's managed identity in
    /// Azure), durable inbox/outbox backed by the Marten/PostgreSQL message store, automatic Marten
    /// transactions around handlers, message routing to the declared topology and the retry / dead-letter
    /// policies for Channel API throttling.
    /// </summary>
    public static WolverineOptions ConfigureGChannelMessaging(this WolverineOptions opts, string serviceName, MessagingRole role, Assembly applicationAssembly)
    {
        opts.ServiceName = serviceName;

        // Handler / endpoint discovery is pinned to the host's own assembly (Wolverine otherwise adopts the
        // process-wide entry assembly, which differs under test harnesses).
        opts.ApplicationAssembly = applicationAssembly;
        opts.Discovery.IncludeAssembly(applicationAssembly);

        // Handlers and HTTP endpoints are compiled at startup (WolverineFx.RuntimeCompilation).
        opts.UseRuntimeCompilation();

        // The topology is declared in the AppHost (infrastructure as code, provisioned by Aspire/azd), so no AutoProvision:
        //  - the shared dead-letter queue must be the declared one (Wolverine would otherwise create its own on first use);
        //  - Wolverine's per-node system queues (wolverine.response.* for remote request/reply, wolverine.retries.* for
        //    non-durable scheduled retries) are disabled — nothing here uses remote request/reply and every listener is
        //    durable, so scheduled retries live in the PostgreSQL inbox instead.
        opts.UseAzureServiceBusUsingNamedConnection(MessagingTopology.ConnectionName)
            .DefaultDeadLetterQueueName(MessagingTopology.DeadLetterQueue)
            .SystemQueuesAreEnabled(false);

        // Wolverine cancels a handler after DefaultExecutionTimeout (60s out of the box). Every handler here talks to
        // the quota-paced Channel API, where a single 429 cooldown already costs 30s+, so allow 10 minutes by default;
        // the recurring-job ticks carry their own, larger [MessageTimeout] budgets.
        opts.DefaultExecutionTimeout = 10.Minutes();

        // Durability: every incoming message is written to the PostgreSQL inbox and acknowledged on the
        // broker right away (long-running job ticks are not bound by the 5-minute Service Bus lock), and
        // every outgoing message goes through the outbox so it commits with the handler's Marten session.
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.AutoApplyTransactions();

        // Channel API quota (429) → back off generously before giving up to the dead-letter queue.
        opts.Policies.OnException<global::Google.GoogleApiException>(ex => ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .RetryWithCooldown(30.Seconds(), 60.Seconds(), 120.Seconds())
            .Then.MoveToErrorQueue();
        opts.Policies.OnAnyException()
            .RetryWithCooldown(2.Seconds(), 10.Seconds(), 30.Seconds())
            .Then.MoveToErrorQueue();

        // ---- Routing -------------------------------------------------------------------------------
        opts.PublishMessage<CustomerChanged>().ToAzureServiceBusTopic(MessagingTopology.EstateEventsTopic);
        opts.PublishMessage<EntitlementChanged>().ToAzureServiceBusTopic(MessagingTopology.EstateEventsTopic);
        opts.PublishMessage<ChannelNotificationArrived>().ToAzureServiceBusTopic(MessagingTopology.EstateEventsTopic);
        opts.PublishMessage<OperationSubmitted>().ToAzureServiceBusTopic(MessagingTopology.EstateEventsTopic);
        opts.PublishMessage<CustomerReadModelUpdated>().ToAzureServiceBusTopic(MessagingTopology.EstateEventsTopic);

        opts.PublishMessage<ProjectCustomer>().ToAzureServiceBusQueue(MessagingTopology.EstateSyncQueue);

        opts.PublishMessage<RunEstateSyncCycle>().ToAzureServiceBusQueue(MessagingTopology.JobsQueue);
        opts.PublishMessage<RefreshDashboardSnapshot>().ToAzureServiceBusQueue(MessagingTopology.JobsQueue);
        opts.PublishMessage<RunBillingSyncCycle>().ToAzureServiceBusQueue(MessagingTopology.JobsQueue);
        opts.PublishMessage<RunBillingPostureScan>().ToAzureServiceBusQueue(MessagingTopology.JobsQueue);

        opts.PublishMessage<CheckChannelOperation>().ToAzureServiceBusQueue(MessagingTopology.OperationsQueue);

        // ---- Listening -----------------------------------------------------------------------------
        switch (role)
        {
            case MessagingRole.Worker:
                opts.ListenToAzureServiceBusSubscription(MessagingTopology.WorkerEstateSubscription)
                    .FromTopic(MessagingTopology.EstateEventsTopic);
                // One message at a time: the targeted projections call the paced Channel API.
                opts.ListenToAzureServiceBusQueue(MessagingTopology.EstateSyncQueue).Sequential();
                opts.ListenToAzureServiceBusQueue(MessagingTopology.JobsQueue).Sequential();
                opts.ListenToAzureServiceBusQueue(MessagingTopology.OperationsQueue);
                break;
            case MessagingRole.Api:
                opts.ListenToAzureServiceBusSubscription(MessagingTopology.ApiCacheSubscription)
                    .FromTopic(MessagingTopology.EstateEventsTopic);
                break;
        }

        return opts;
    }
}
