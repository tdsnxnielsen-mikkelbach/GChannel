using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Cloud Billing budget endpoints (billingbudgets.googleapis.com). Separate from the Channel API:
/// these read/write budgets on the reseller's billing accounts and per-customer sub-accounts using the
/// reseller service-account credential. See <see cref="IBillingBudgetsService"/>.
/// </summary>
public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Billing");

        group.MapGet("/accounts", async (
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Ok(new BillingAccountsResult
                    {
                        DiscoveryAvailable = false,
                        DiscoveryError = "No service-account credential configured (GoogleChannel:ServiceAccountKeyJson)."
                    });
                }

                return Results.Ok(await billing.ListBillingAccountsAsync(cancellationToken));
            })
            .WithName("ListBillingAccounts")
            .WithSummary("Lists the reseller's billing accounts and sub-accounts (discovered or configured).");

        group.MapGet("/accounts/{billingAccountId}/budgets", async (
                string billingAccountId,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(await billing.ListBudgetsAsync(billingAccountId, cancellationToken));
            })
            .WithName("ListBudgets")
            .WithSummary("Lists budgets for a billing account.");

        group.MapPost("/budgets", async (
                SaveBudgetRequest request,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var saved = await billing.SaveBudgetAsync(request, cancellationToken);
                return Results.Ok(saved);
            })
            .WithName("SaveBudget")
            .WithSummary("Creates a budget (no budget id) or updates an existing one (budget id set).");

        group.MapDelete("/accounts/{billingAccountId}/budgets/{budgetId}", async (
                string billingAccountId,
                string budgetId,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                await billing.DeleteBudgetAsync(billingAccountId, budgetId, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteBudget")
            .WithSummary("Deletes a budget.");

        return app;
    }
}
