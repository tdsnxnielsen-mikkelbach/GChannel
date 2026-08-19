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

    // Token-bucket-of-1 pacers so bursts (e.g. sub-account discovery, per-account budget lists) stay
    // under the Cloud Billing / Budget API per-minute quotas, mirroring the Channel client's RequestPacer.
    private readonly RequestPacer? _readPacer;
    private readonly RequestPacer? _writePacer;

    public BillingBudgetsService(
        IOptions<GoogleChannelOptions> channelOptions,
        IOptions<GoogleBillingOptions> billingOptions)
    {
        _credentialJson = channelOptions.Value.ServiceAccountKeyJson;
        _billingOptions = billingOptions.Value;
        _readPacer = _billingOptions.ReadRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _billingOptions.ReadRequestsPerMinute))
            : null;
        _writePacer = _billingOptions.WriteRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _billingOptions.WriteRequestsPerMinute))
            : null;
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

    private Task PaceReadAsync(CancellationToken ct) => _readPacer?.WaitAsync(ct) ?? Task.CompletedTask;
    private Task PaceWriteAsync(CancellationToken ct) => _writePacer?.WaitAsync(ct) ?? Task.CompletedTask;

    public async Task<BillingAccountsResult> ListBillingAccountsAsync(CancellationToken cancellationToken)
    {
        // Live discovery via the Cloud Billing API is best-effort: it needs cloudbilling.googleapis.com
        // enabled and billing.viewer. If it fails, fall back to the configured ids so budgets still work.
        try
        {
            var byId = new Dictionary<string, BillingAccountInfo>(StringComparer.OrdinalIgnoreCase);

            // 1) Top-level accounts the SA can access directly (reseller master accounts + any others).
            await PaceReadAsync(cancellationToken);
            await foreach (var account in BillingClient
                .ListBillingAccountsAsync(new ListBillingAccountsRequest())
                .WithCancellation(cancellationToken))
            {
                Add(byId, account);
            }

            // 2) A flat list only returns accounts the caller can access directly; reseller sub-accounts
            // are listed per master via the master_billing_account filter, so enumerate each master's subs.
            foreach (var masterId in byId.Values.Where(a => !a.IsSubaccount).Select(a => a.Id).ToList())
            {
                await PaceReadAsync(cancellationToken);
                try
                {
                    var request = new ListBillingAccountsRequest
                    {
                        Filter = $"master_billing_account=billingAccounts/{masterId}"
                    };
                    await foreach (var sub in BillingClient
                        .ListBillingAccountsAsync(request)
                        .WithCancellation(cancellationToken))
                    {
                        Add(byId, sub);
                    }
                }
                catch (Exception)
                {
                    // The master may not expose sub-accounts to this SA; keep what we already have.
                }
            }

            return new BillingAccountsResult
            {
                Accounts = byId.Values
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

    private static void Add(Dictionary<string, BillingAccountInfo> byId, BillingAccount account)
    {
        var id = StripPrefix(account.Name);
        byId[id] = new BillingAccountInfo
        {
            Id = id,
            DisplayName = account.DisplayName,
            Open = account.Open,
            MasterBillingAccountId = string.IsNullOrEmpty(account.MasterBillingAccount)
                ? null
                : StripPrefix(account.MasterBillingAccount)
        };
    }

    public async Task<BudgetsResult> ListBudgetsAsync(string billingAccountId, CancellationToken cancellationToken)
    {
        var budgets = new List<BudgetInfo>();
        await PaceReadAsync(cancellationToken);
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
            await PaceWriteAsync(cancellationToken);
            saved = await BudgetClient.CreateBudgetAsync(new CreateBudgetRequest
            {
                Parent = ParentName(request.BillingAccountId),
                Budget = budget
            }, cancellationToken);
        }
        else
        {
            budget.Name = BudgetName(request.BillingAccountId, request.BudgetId);
            await PaceWriteAsync(cancellationToken);
            saved = await BudgetClient.UpdateBudgetAsync(new UpdateBudgetRequest
            {
                Budget = budget,
                UpdateMask = new FieldMask { Paths = { "display_name", "amount", "threshold_rules", "budget_filter" } }
            }, cancellationToken);
        }

        return ToBudgetInfo(saved, request.BillingAccountId);
    }

    public async Task DeleteBudgetAsync(string billingAccountId, string budgetId, CancellationToken cancellationToken)
    {
        await PaceWriteAsync(cancellationToken);
        await BudgetClient.DeleteBudgetAsync(new DeleteBudgetRequest
        {
            Name = BudgetName(billingAccountId, budgetId)
        }, cancellationToken);
    }

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

    /// <summary>Token-bucket-of-1 that spaces calls to at most one per <c>interval</c>, mirroring the Channel client's pacer.</summary>
    private sealed class RequestPacer(TimeSpan interval)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset slot;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                slot = _nextSlot > now ? _nextSlot : now;
                _nextSlot = slot + interval;
            }

            var delay = slot - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
        }
    }
}
