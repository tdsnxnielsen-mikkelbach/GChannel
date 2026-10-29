using GChannel.Core.Configuration;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService;

/// <summary>
/// Development-only convenience: seeds the notification feed with a couple of sample Channel events when it
/// is empty, so the Notifications page shows data without a live Pub/Sub subscription. Never runs in
/// production. The sample resource names resolve to real customers in dev so names display correctly.
/// </summary>
public static class DevelopmentSeeding
{
    public static async Task SeedNotificationsAsync(WebApplication app)
    {
        try
        {
            using var scope = app.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            await using var session = store.LightweightSession();
            if (await session.Query<ChannelNotificationRecord>().AnyAsync())
            {
                return; // Don't clobber a feed that already has (real or previously seeded) events.
            }

            var options = scope.ServiceProvider.GetRequiredService<IOptions<GoogleChannelOptions>>().Value;
            var account = string.IsNullOrWhiteSpace(options.AccountName) ? "accounts/C03r1rwb0" : options.AccountName;

            var samples = new[]
            {
                ("SV4YDIKAbIBzO8", "SvelaOSyAWM8Sz"),
                ("SV4YDIKAbIBzO8", "Slx8Y3wbAoWzOn")
            };

            var now = DateTimeOffset.UtcNow;
            var n = 0;
            foreach (var (customerId, entitlementId) in samples)
            {
                n++;
                var notification = new ChannelNotification
                {
                    Kind = "Entitlement",
                    EventType = "LICENSE_ASSIGNMENT_CHANGED",
                    ResourceName = $"{account}/customers/{customerId}/entitlements/{entitlementId}",
                    CustomerId = customerId,
                    EntitlementId = entitlementId,
                    MessageId = $"dev-seed-{n}",
                    ReceivedAt = now.AddMinutes(-n)
                };
                session.Store(new ChannelNotificationRecord
                {
                    Id = notification.MessageId!,
                    Notification = notification,
                    ReceivedAt = notification.ReceivedAt,
                    CustomerId = customerId,
                    EntitlementId = entitlementId,
                });
            }

            await session.SaveChangesAsync();
            app.Logger.LogInformation("Seeded {Count} sample notifications into the development feed.", samples.Length);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Development notification seeding was skipped.");
        }
    }
}
