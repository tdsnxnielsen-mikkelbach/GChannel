using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Billing.Budgets.V1;
using Google.Cloud.Billing.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Money = Google.Type.Money;

namespace GChannel.ApiService.Services;

/// <summary>
/// Reads and writes Cloud Billing budgets (billingbudgets.googleapis.com) on the reseller's billing
/// accounts, plus best-effort discovery of those accounts (cloudbilling.googleapis.com). Authenticated
/// with the reseller service-account key reused from <c>GoogleChannel:ServiceAccountKeyJson</c> — this
/// is a separate data plane from the Channel API (no user token, no domain-wide delegation).
/// </summary>
public interface IBillingBudgetsService
{
    /// <summary>True when a service-account credential is available to call the billing APIs.</summary>
    bool IsConfigured { get; }

    Task<BillingAccountsResult> ListBillingAccountsAsync(CancellationToken cancellationToken);
    Task<BudgetsResult> ListBudgetsAsync(string billingAccountId, CancellationToken cancellationToken);
    Task<BudgetInfo> SaveBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken);
    Task DeleteBudgetAsync(string billingAccountId, string budgetId, CancellationToken cancellationToken);
}

public sealed class BillingBudgetsService : IBillingBudgetsService
{
    private static readonly double[] DefaultThresholds = [0.5, 0.9, 1.0];

    private readonly string _credentialJson;
    private readonly GoogleBillingOptions _billingOptions;

    // Built lazily on first use and reused (the gRPC clients are thread-safe).
    private BudgetServiceClient? _budgetClient;
    private CloudBillingClient? _billingClient;
    private GoogleCredential? _credential;

    public BillingBudgetsService(
        IOptions<GoogleChannelOptions> channelOptions,
        IOptions<GoogleBillingOptions> billingOptions)
    {
        _credentialJson = channelOptions.Value.ServiceAccountKeyJson;
        _billingOptions = billingOptions.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_credentialJson);

    private BudgetServiceClient BudgetClient =>
        _budgetClient ??= new BudgetServiceClientBuilder { GoogleCredential = Credential }.Build();

    private CloudBillingClient BillingClient =>
        _billingClient ??= new CloudBillingClientBuilder { GoogleCredential = Credential }.Build();

    private GoogleCredential Credential => _credential ??= IsConfigured
        ? CredentialFactory.FromJson(_credentialJson, "service_account").CreateScoped("https://www.googleapis.com/auth/cloud-platform")
        : throw new InvalidOperationException(
            "Billing budgets are not configured: set GoogleChannel:ServiceAccountKeyJson (reused for the Cloud Billing APIs).");

    public async Task<BillingAccountsResult> ListBillingAccountsAsync(CancellationToken cancellationToken)
    {
        // Live discovery via the Cloud Billing API is best-effort: it needs cloudbilling.googleapis.com
        // enabled and billing.viewer. If it fails, fall back to the configured ids so budgets still work.
        try
        {
            var accounts = new List<BillingAccountInfo>();
            var response = BillingClient.ListBillingAccountsAsync(new ListBillingAccountsRequest());
            await foreach (var account in response.WithCancellation(cancellationToken))
            {
                accounts.Add(new BillingAccountInfo
                {
                    Id = StripPrefix(account.Name),
                    DisplayName = account.DisplayName,
                    Open = account.Open,
                    MasterBillingAccountId = string.IsNullOrEmpty(account.MasterBillingAccount)
                        ? null
                        : StripPrefix(account.MasterBillingAccount)
                });
            }

            return new BillingAccountsResult
            {
                Accounts = accounts
                    .OrderBy(a => a.MasterBillingAccountId ?? a.Id)
                    .ThenBy(a => a.IsSubaccount)
                    .ThenBy(a => a.DisplayName)
                    .ToList(),
                DiscoveryAvailable = true
            };
        }
        catch (Exception ex)
        {
            return new BillingAccountsResult
            {
                Accounts = _billingOptions.BillingAccountIdList
                    .Select(id => new BillingAccountInfo { Id = id, Open = true })
                    .ToList(),
                DiscoveryAvailable = false,
                DiscoveryError = ex.Message
            };
        }
    }

    public async Task<BudgetsResult> ListBudgetsAsync(string billingAccountId, CancellationToken cancellationToken)
    {
        var budgets = new List<BudgetInfo>();
        var request = new ListBudgetsRequest { Parent = ParentName(billingAccountId) };
        var response = BudgetClient.ListBudgetsAsync(request);
        await foreach (var budget in response.WithCancellation(cancellationToken))
        {
            budgets.Add(ToBudgetInfo(budget, billingAccountId));
        }

        return new BudgetsResult
        {
            BillingAccountId = billingAccountId,
            Budgets = budgets.OrderBy(b => b.DisplayName).ToList()
        };
    }

    public async Task<BudgetInfo> SaveBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken)
    {
        var thresholds = request.ThresholdPercents.Count > 0 ? request.ThresholdPercents : DefaultThresholds;
        var budget = new Budget
        {
            DisplayName = request.DisplayName,
            Amount = new BudgetAmount
            {
                SpecifiedAmount = ToMoney(request.Amount, request.CurrencyCode)
            },
            BudgetFilter = new Filter { CalendarPeriod = ToCalendarPeriod(request.CalendarPeriod) }
        };
        budget.ThresholdRules.AddRange(thresholds.Select(p => new ThresholdRule
        {
            ThresholdPercent = p,
            SpendBasis = ThresholdRule.Types.Basis.CurrentSpend
        }));

        Budget saved;
        if (string.IsNullOrWhiteSpace(request.BudgetId))
        {
            saved = await BudgetClient.CreateBudgetAsync(new CreateBudgetRequest
            {
                Parent = ParentName(request.BillingAccountId),
                Budget = budget
            }, cancellationToken);
        }
        else
        {
            budget.Name = BudgetName(request.BillingAccountId, request.BudgetId);
            saved = await BudgetClient.UpdateBudgetAsync(new UpdateBudgetRequest
            {
                Budget = budget,
                UpdateMask = new FieldMask { Paths = { "display_name", "amount", "threshold_rules", "budget_filter" } }
            }, cancellationToken);
        }

        return ToBudgetInfo(saved, request.BillingAccountId);
    }

    public Task DeleteBudgetAsync(string billingAccountId, string budgetId, CancellationToken cancellationToken) =>
        BudgetClient.DeleteBudgetAsync(new DeleteBudgetRequest
        {
            Name = BudgetName(billingAccountId, budgetId)
        }, cancellationToken);

    private static BudgetInfo ToBudgetInfo(Budget budget, string billingAccountId)
    {
        var usesLastPeriod = budget.Amount?.LastPeriodAmount is not null;
        var money = budget.Amount?.SpecifiedAmount;
        return new BudgetInfo
        {
            Name = budget.Name,
            BudgetId = StripPrefix(budget.Name, "budgets/"),
            BillingAccountId = billingAccountId,
            DisplayName = budget.DisplayName,
            Amount = money is null ? 0m : FromMoney(money),
            UsesLastPeriodAmount = usesLastPeriod,
            CurrencyCode = money?.CurrencyCode,
            ThresholdPercents = budget.ThresholdRules.Select(r => r.ThresholdPercent).ToList(),
            CalendarPeriod = budget.BudgetFilter?.CalendarPeriod is { } cp && cp != CalendarPeriod.Unspecified
                ? cp.ToString().ToUpperInvariant()
                : null
        };
    }

    private static string ParentName(string billingAccountId) => $"billingAccounts/{billingAccountId}";

    private static string BudgetName(string billingAccountId, string budgetId) =>
        $"billingAccounts/{billingAccountId}/budgets/{budgetId}";

    // "billingAccounts/012ABC" -> "012ABC"; "billingAccounts/012ABC/budgets/xyz" -> "xyz" (with prefix).
    private static string StripPrefix(string resourceName, string? afterSegment = null)
    {
        if (afterSegment is not null)
        {
            var idx = resourceName.IndexOf(afterSegment, StringComparison.Ordinal);
            return idx >= 0 ? resourceName[(idx + afterSegment.Length)..] : resourceName;
        }

        var slash = resourceName.LastIndexOf('/');
        return slash >= 0 ? resourceName[(slash + 1)..] : resourceName;
    }

    private static Money ToMoney(decimal amount, string currencyCode)
    {
        var units = decimal.Truncate(amount);
        var nanos = (int)decimal.Round((amount - units) * 1_000_000_000m);
        return new Money { CurrencyCode = currencyCode, Units = (long)units, Nanos = nanos };
    }

    private static decimal FromMoney(Money money) => money.Units + money.Nanos / 1_000_000_000m;

    private static CalendarPeriod ToCalendarPeriod(string? period) => period?.ToUpperInvariant() switch
    {
        "QUARTER" => CalendarPeriod.Quarter,
        "YEAR" => CalendarPeriod.Year,
        _ => CalendarPeriod.Month
    };
}
