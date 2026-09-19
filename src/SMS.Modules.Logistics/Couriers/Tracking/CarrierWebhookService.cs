using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Data.Maps;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Couriers.Tracking;

/// <summary>What to answer the carrier with.</summary>
public sealed record WebhookReceipt(int StatusCode, string Message, int Recorded = 0, int Duplicates = 0, int Unmatched = 0);

public interface ICarrierWebhookService
{
    Task<WebhookReceipt> ReceiveAsync(
        string providerKey, Guid accountUuid, IReadOnlyDictionary<string, string> headers, byte[] body,
        DateTime? utcNow = null, CancellationToken ct = default);
}

/// <summary>
/// Receives carrier webhooks: verify, keep, apply, answer.
/// <para>
/// <b>Everything here runs with no tenant and no user</b> — a carrier cannot log in. The account in
/// the URL is only a pointer to which secret to verify with; it proves nothing. Every read is scoped
/// explicitly to that account's organization, and nothing is kept or changed until the adapter has
/// verified the signature with the account's own secret.
/// </para>
/// <para>
/// <b>Answers are chosen for how carriers react to them.</b> A 2xx stops resends, so it is only
/// given once a delivery is safely applied or already was. A 5xx asks for a resend, so a delivery
/// that failed to apply gets one — and its resend is let through the deduplication to try again.
/// Every way of not being a valid endpoint answers the same 404, so the endpoint cannot be used to
/// discover which accounts exist or which organizations run the module.
/// </para>
/// <para>
/// <b>Processed in the request, not queued.</b> Applying a handful of events is a few quick writes,
/// and answering 2xx only after they are made is what makes the carrier's own retry the recovery
/// mechanism. A queue would acknowledge first and then need a second retry system of its own.
/// </para>
/// </summary>
internal sealed class CarrierWebhookService : ICarrierWebhookService
{
    internal const string ModuleFeature = "MODULE_LOGISTICS";

    /// <summary>A delivery still RECEIVED after this long was abandoned mid-processing, and a resend may take it over.</summary>
    internal static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(5);

    private static readonly WebhookReceipt UnknownEndpoint = new(404, "Unknown webhook endpoint.");
    private static readonly WebhookReceipt NotVerified     = new(401, "Signature verification failed.");

    private readonly LogisticsDbContext             _db;
    private readonly ICourierProviderRegistry       _registry;
    private readonly ICarrierCredentialVault        _vault;
    private readonly ITrackingEventRecorder         _recorder;
    private readonly ITenantSnapshotProvider        _tenants;
    private readonly ILogger<CarrierWebhookService> _logger;

    public CarrierWebhookService(
        LogisticsDbContext db, ICourierProviderRegistry registry, ICarrierCredentialVault vault,
        ITrackingEventRecorder recorder, ITenantSnapshotProvider tenants, ILogger<CarrierWebhookService> logger)
    {
        _db       = db;
        _registry = registry;
        _vault    = vault;
        _recorder = recorder;
        _tenants  = tenants;
        _logger   = logger;
    }

    public async Task<WebhookReceipt> ReceiveAsync(
        string providerKey, Guid accountUuid, IReadOnlyDictionary<string, string> headers, byte[] body,
        DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? DateTime.UtcNow;

        // ── Is this a real endpoint? ──────────────────────────────────────────

        var account = await _db.CarrierAccounts
            .IgnoreQueryFilters()
            .Include(a => a.Carrier)
            .FirstOrDefaultAsync(a => a.UUID == accountUuid && !a.IsDelete && a.IsActive, ct);

        if (account?.Carrier is not { IsDelete: false } carrier
            || !string.Equals(carrier.ProviderKey?.Trim(), providerKey?.Trim(), StringComparison.OrdinalIgnoreCase)
            || ConsignmentRepository.IntegrationModeOf(carrier) != CarrierIntegrationMode.Api
            || _registry.Find(providerKey) is not ICourierWebhookReceiver receiver)
            return UnknownEndpoint;

        // The feature filter cannot help here — an anonymous request bypasses it — so the
        // organization's module switch is checked directly.
        var tenant = await _tenants.GetSnapshotAsync(account.OrganizationId);
        if (tenant is not { IsActive: true } || !tenant.EnabledFeatureCodes.Contains(ModuleFeature))
            return UnknownEndpoint;

        // ── Verify ────────────────────────────────────────────────────────────

        IReadOnlyDictionary<string, string> credentials;
        try
        {
            credentials = await _vault.GetForAccountAsync(account.Id, account.OrganizationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Webhook for carrier account {AccountUuid}: credentials unreadable ({Reason}).", accountUuid, ex.Message);
            return new WebhookReceipt(500, "The delivery could not be verified right now.");
        }

        var result = receiver.Receive(
            new CourierWebhookRequest(new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase), body, now),
            credentials);

        if (result.Verdict is CourierWebhookVerdict.InvalidSignature or CourierWebhookVerdict.Stale)
        {
            // The reason stays in our log. Telling the caller which check failed helps a forger more
            // than it helps a carrier, who has our documentation.
            _logger.LogWarning("Webhook for carrier account {AccountUuid} refused: {Verdict} — {Reason}",
                accountUuid, result.Verdict, result.Reason);
            return NotVerified;
        }

        // ── Keep ──────────────────────────────────────────────────────────────

        var bodySha   = Hash(body);
        var dedupeKey = DedupeKeyFor(result.DeliveryId, bodySha);
        var rejected  = result.Verdict == CourierWebhookVerdict.Malformed;

        var (delivery, isNew) = await FindOrAddAsync(account, carrier, dedupeKey, bodySha, body, result, rejected, now, ct);

        if (!isNew)
        {
            switch (LogisticsCode.Parse<WebhookDeliveryStatus>(delivery.Status))
            {
                case WebhookDeliveryStatus.Processed:
                    return new WebhookReceipt(200, "Already received.", delivery.RecordedCount, delivery.DuplicateCount, delivery.UnmatchedCount);

                case WebhookDeliveryStatus.Rejected:
                    return new WebhookReceipt(400, delivery.Detail ?? "The delivery could not be read.");

                case WebhookDeliveryStatus.Received when delivery.ReceivedAt > now - AbandonedAfter:
                    return new WebhookReceipt(202, "Already being processed.");
            }

            // Failed, or abandoned mid-processing: this resend takes it over.
            if (!await TryTakeOverAsync(delivery, now, ct))
                return new WebhookReceipt(202, "Already being processed.");
        }

        if (rejected)
            return new WebhookReceipt(400, result.Reason ?? "The delivery could not be read.");

        // ── Apply ─────────────────────────────────────────────────────────────

        return await ApplyAsync(delivery, account, result.Events, now, ct);
    }

    private async Task<(CarrierWebhookDelivery delivery, bool isNew)> FindOrAddAsync(
        CarrierAccount account, Carrier carrier, string dedupeKey, string bodySha, byte[] body,
        CourierWebhookResult result, bool rejected, DateTime now, CancellationToken ct)
    {
        var existing = await FindAsync(account.Id, dedupeKey, ct);
        if (existing is not null) return (existing, false);

        var delivery = new CarrierWebhookDelivery
        {
            UUID             = Guid.NewGuid(),
            OrganizationId   = account.OrganizationId,
            CarrierAccountId = account.Id,
            ProviderKey      = carrier.ProviderKey!.Trim(),
            DedupeKey        = dedupeKey,
            BodySha256       = bodySha,
            Body             = Encoding.UTF8.GetString(body),
            Status           = LogisticsCode.Of(rejected ? WebhookDeliveryStatus.Rejected : WebhookDeliveryStatus.Received),
            Detail           = rejected ? Clip(result.Reason, CarrierWebhookDeliveryMap.DetailMax) : null,
            AttemptCount     = 1,
            EventCount       = result.Events.Count,
            ReceivedAt       = now
        };

        _db.CarrierWebhookDeliveries.Add(delivery);

        try
        {
            await _db.SaveChangesAsync(ct);
            return (delivery, true);
        }
        catch (DbUpdateException)
        {
            // The same delivery arrived twice at once and the other copy got in first.
            _db.Entry(delivery).State = EntityState.Detached;
            return ((await FindAsync(account.Id, dedupeKey, ct))!, false);
        }
    }

    private async Task<bool> TryTakeOverAsync(CarrierWebhookDelivery delivery, DateTime now, CancellationToken ct)
    {
        delivery.Status       = LogisticsCode.Of(WebhookDeliveryStatus.Received);
        delivery.AttemptCount += 1;
        delivery.ReceivedAt   = now;
        delivery.Detail       = null;

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another resend took it over between our read and our write.
            _db.Entry(delivery).State = EntityState.Detached;
            return false;
        }
    }

    private async Task<WebhookReceipt> ApplyAsync(
        CarrierWebhookDelivery delivery, CarrierAccount account, IReadOnlyList<CourierWebhookEvent> events,
        DateTime now, CancellationToken ct)
    {
        int recorded = 0, duplicates = 0, unmatched = 0, invalid = 0;

        try
        {
            foreach (var group in events.GroupBy(e => e.AwbNumber.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var awb = group.Key;

                // This carrier's consignment in this organization — never another carrier's parcel
                // that happens to share an airway bill number.
                var consignment = await _db.Consignments
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.OrganizationId == account.OrganizationId
                                           && c.CarrierId == account.CarrierId
                                           && c.MasterAwb == awb
                                           && !c.IsDelete, ct);

                if (consignment is null)
                {
                    // Booked outside the system, or not yet recorded here. Not an error — the
                    // carrier will keep sending, and the poll will catch up once it is.
                    unmatched += group.Count();
                    continue;
                }

                var result = await _recorder.RecordAsync(
                    consignment, group.Select(e => e.Event), TrackingEventSource.Webhook, now, ct);

                recorded   += result.Recorded;
                duplicates += result.Duplicates;
                invalid    += result.Invalid;
            }

            delivery.Status         = LogisticsCode.Of(WebhookDeliveryStatus.Processed);
            delivery.RecordedCount  = recorded;
            delivery.DuplicateCount = duplicates;
            delivery.UnmatchedCount = unmatched;
            delivery.ProcessedAt    = now;
            delivery.Detail         = invalid > 0
                ? $"{invalid} event(s) ignored: dated in the future, or with a milestone this system does not know."
                : null;

            await _db.SaveChangesAsync(ct);

            return new WebhookReceipt(200, "Received.", recorded, duplicates, unmatched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Webhook delivery {DeliveryUuid} for carrier account {AccountUuid} failed to apply: {Reason}",
                delivery.UUID, account.UUID, ex.Message);

            // Whatever half-applied state is tracked goes; events already saved are keyed, so the
            // resend applies the rest without doubling any.
            _db.ChangeTracker.Clear();

            var failed = await _db.CarrierWebhookDeliveries.IgnoreQueryFilters().FirstAsync(d => d.Id == delivery.Id, CancellationToken.None);
            failed.Status = LogisticsCode.Of(WebhookDeliveryStatus.Failed);
            failed.Detail = Clip($"{ex.GetType().Name}: {ex.Message}", CarrierWebhookDeliveryMap.DetailMax);
            await _db.SaveChangesAsync(CancellationToken.None);

            return new WebhookReceipt(500, "The delivery could not be applied. Resend it and it will be retried.");
        }
    }

    private Task<CarrierWebhookDelivery?> FindAsync(int accountId, string dedupeKey, CancellationToken ct) =>
        _db.CarrierWebhookDeliveries
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.CarrierAccountId == accountId && d.DedupeKey == dedupeKey, ct);

    /// <summary>The carrier's delivery id when it sends one, else the body's hash.</summary>
    internal static string DedupeKeyFor(string? deliveryId, string bodySha)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return "sha256:" + bodySha;

        var id = deliveryId.Trim();
        return id.Length <= CarrierWebhookDeliveryMap.DedupeKeyMax - 3
            ? "id:" + id
            : "id-sha256:" + Hash(Encoding.UTF8.GetBytes(id));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? Clip(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= max ? value.Trim() : value.Trim()[..max];
}
