using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers.Labels;

/// <summary>A label ready to send to a browser or printer.</summary>
public sealed record ConsignmentLabelFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// The carrier could not be reached, or sent something that is not a usable label. Distinct from
/// a refusal: trying again later may well work. The controller answers it with 502.
/// </summary>
public sealed class CarrierUnavailableException(string message) : Exception(message);

/// <summary>The label surface a request gets. Public because the controller is.</summary>
public interface IConsignmentLabelService
{
    /// <summary>
    /// The label for the consignment's current airway bill — from storage, or fetched from the
    /// carrier and stored on first request. Null when the consignment does not exist.
    /// </summary>
    Task<ConsignmentLabelFile?> GetLabelAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);
}

internal interface IConsignmentLabelStore
{
    /// <summary>
    /// Validates and stores a label the carrier returned, and moves a BOOKED consignment to
    /// LABEL_READY. Saves. The consignment must be tracked by the same context.
    /// </summary>
    Task<ConsignmentLabelFile> StoreAsync(
        Consignment consignment, CourierLabel label, ConsignmentLabelSource source, string providerKey,
        int userId, DateTime now, CancellationToken ct = default);
}

/// <summary>
/// Fetches, stores and serves carrier labels.
/// <para>
/// <b>Nothing a carrier sends is trusted as-is.</b> Its content type must be one a label printer or
/// browser should receive, and the bytes must actually be that type. A carrier API that answers an
/// error with an HTML page and a <c>200</c> labelled <c>application/pdf</c> is common in the wild —
/// printed, it is a blank label on a real parcel; served inline from our own origin, it is script
/// running in a user's session. The file name is built here, never taken from the carrier.
/// </para>
/// </summary>
internal sealed class ConsignmentLabelService : IConsignmentLabelService, IConsignmentLabelStore
{
    /// <summary>A 4×6 PDF is well under a megabyte; anything past this is not a label.</summary>
    internal const int MaxLabelBytes = 5 * 1024 * 1024;

    internal static readonly TimeSpan DefaultFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Media types a label may be stored and served as, with their file extension. Deliberately
    /// excludes anything a browser would render as a page.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> AllowedTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"]   = "pdf",
            ["image/png"]         = "png",
            ["image/gif"]         = "gif",
            ["image/jpeg"]        = "jpg",
            ["application/x-zpl"] = "zpl",
            ["application/zpl"]   = "zpl"
        };

    private static readonly ShipmentStateMachine Machine = ShipmentStateMachine.Instance;

    private readonly LogisticsDbContext      _db;
    private readonly ICarrierAccountResolver _resolver;
    private readonly TimeSpan                _fetchTimeout;

    public ConsignmentLabelService(LogisticsDbContext db, ICarrierAccountResolver resolver, IConfiguration configuration)
    {
        _db       = db;
        _resolver = resolver;

        var seconds   = configuration.GetValue<int?>("Logistics:Labels:FetchTimeoutSeconds");
        _fetchTimeout = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultFetchTimeout;
    }

    // ── Serving ───────────────────────────────────────────────────────────────

    public async Task<ConsignmentLabelFile?> GetLabelAsync(Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        if (string.IsNullOrWhiteSpace(consignment.MasterAwb))
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is not booked yet, so there is no label. The carrier " +
                "issues one when it accepts the booking.");

        if (consignment.Status == LogisticsCode.Of(ShipmentStatus.Cancelled))
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is cancelled. Its label must not be printed — a parcel " +
                "carrying it would travel on a booking that no longer exists.");

        var awb = consignment.MasterAwb.Trim();

        // Only a label for the current airway bill. One kept from an earlier booking of this
        // consignment would send the parcel on a booking that no longer exists.
        var stored = await _db.ConsignmentLabels
            .AsNoTracking()
            .Where(l => l.ConsignmentId == consignment.Id && l.AwbNumber == awb)
            .OrderByDescending(l => l.CreatedDate)
            .ThenByDescending(l => l.Id)
            .Select(l => new ConsignmentLabelFile(l.Content, l.ContentType, l.FileName))
            .FirstOrDefaultAsync(ct);

        if (stored is not null) return stored;

        var carrier = consignment.Carrier
            ?? throw new ConflictException("This consignment has no carrier to ask for a label.");

        var account = await _resolver.ResolveAsync(carrier.UUID, consignment.CarrierAccount?.UUID, ct);

        if (!account.Capabilities.SupportsLabels)
            throw new ConflictException(
                account.Provider.Capabilities.SupportsLabels
                    ? $"Labels are switched off for account '{account.AccountName}'. Use the label the carrier gave you."
                    : $"{carrier.Name} issues its own labels — {account.Provider.DisplayName} cannot fetch one. " +
                      "Use the label the carrier gave you.");

        CourierLabelResult result;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_fetchTimeout);

            try
            {
                result = await account.Provider.GetLabelAsync(awb, account.Credentials, timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A label fetch changes nothing at the carrier, so unlike a booking there is no
                // unknown outcome to guard — only an answer we did not get. Try again later.
                throw new CarrierUnavailableException(
                    $"{carrier.Name} did not return the label ({ex.GetType().Name}). Try again shortly.");
            }
        }

        return result.Outcome switch
        {
            CourierOutcome.Succeeded when result.Label is not null =>
                await StoreAsync(consignment, result.Label, ConsignmentLabelSource.Fetched, account.Provider.Key,
                                 userId, DateTime.UtcNow, ct),

            CourierOutcome.Succeeded =>
                throw new CarrierUnavailableException($"{carrier.Name} answered without a label. Try again shortly."),

            CourierOutcome.Failed =>
                throw new CarrierUnavailableException(
                    $"{carrier.Name} could not supply the label: {result.Message ?? "no reason given"}. Try again shortly."),

            CourierOutcome.Refused =>
                throw new ConflictException($"{carrier.Name} refused to supply the label: {result.Message ?? "no reason given"}."),

            _ =>
                throw new ConflictException(result.Message ?? $"{carrier.Name} does not supply labels.")
        };
    }

    // ── Storing ───────────────────────────────────────────────────────────────

    public async Task<ConsignmentLabelFile> StoreAsync(
        Consignment consignment, CourierLabel label, ConsignmentLabelSource source, string providerKey,
        int userId, DateTime now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);

        var awb = consignment.MasterAwb?.Trim()
            ?? throw new ConflictException("A label can only be stored for a booked consignment.");

        var (contentType, extension) = Validate(label);
        var sha      = Convert.ToHexString(SHA256.HashData(label.Content)).ToLowerInvariant();
        var fileName = FileNameFor(consignment.ConsignmentNumber, awb, extension);

        var existing = await FindAsync(consignment.Id, sha, ct);
        if (existing is not null) return existing;

        _db.ConsignmentLabels.Add(new ConsignmentLabel
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = consignment.OrganizationId,
            ConsignmentId  = consignment.Id,
            AwbNumber      = awb,
            ContentType    = contentType,
            FileName       = fileName,
            Content        = label.Content,
            SizeBytes      = label.Content.Length,
            Sha256         = sha,
            Source         = LogisticsCode.Of(source),
            ProviderKey    = providerKey,
            CreatedBy      = userId,
            CreatedDate    = now
        });

        // A label in hand is what LABEL_READY means. Only from BOOKED: a consignment already picked
        // up or further along does not move backwards because someone reprinted its label.
        if (consignment.Status == LogisticsCode.Of(ShipmentStatus.Booked))
        {
            Machine.EnsureCanTransition(ShipmentStatus.Booked, ShipmentStatus.LabelReady);
            consignment.Status       = LogisticsCode.Of(ShipmentStatus.LabelReady);
            consignment.ModifiedDate = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request stored the same label first. Theirs is identical — use it.
            foreach (var entry in _db.ChangeTracker.Entries().Where(e => e.Entity is ConsignmentLabel or Consignment).ToList())
                entry.State = EntityState.Detached;

            return await FindAsync(consignment.Id, sha, ct) ?? throw new InvalidOperationException(
                $"Storing the label for consignment {consignment.ConsignmentNumber} failed, and no stored copy was found.");
        }

        return new ConsignmentLabelFile(label.Content, contentType, fileName);
    }

    private Task<ConsignmentLabelFile?> FindAsync(int consignmentId, string sha, CancellationToken ct) =>
        _db.ConsignmentLabels
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(l => l.ConsignmentId == consignmentId && l.Sha256 == sha)
            .Select(l => new ConsignmentLabelFile(l.Content, l.ContentType, l.FileName))
            .FirstOrDefaultAsync(ct);

    /// <summary>The media type to store and serve it as, and its extension — or why it is not a label.</summary>
    internal static (string contentType, string extension) Validate(CourierLabel label)
    {
        if (label.Content is not { Length: > 0 } bytes)
            throw new CarrierUnavailableException("The carrier returned an empty label.");

        if (bytes.Length > MaxLabelBytes)
            throw new CarrierUnavailableException(
                $"The carrier returned {bytes.Length / 1024 / 1024} MB, which is not a label (the limit is {MaxLabelBytes / 1024 / 1024} MB).");

        // Parameters such as "; charset=binary" are dropped; the type itself must be on the list.
        var contentType = (label.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

        if (!AllowedTypes.TryGetValue(contentType, out var extension))
            throw new CarrierUnavailableException(
                $"The carrier returned the label as '{label.ContentType}', which is not a label format this system " +
                $"will store or serve. Accepted: {string.Join(", ", AllowedTypes.Keys)}.");

        if (!LooksLike(contentType, bytes))
            throw new CarrierUnavailableException(
                $"The carrier said the label is {contentType}, but the content is not — most often an error page " +
                "returned in its place. Try again shortly.");

        return (contentType, extension);
    }

    private static bool LooksLike(string contentType, byte[] b) => contentType switch
    {
        "application/pdf" => StartsWith(b, "%PDF-"u8),
        "image/png"       => StartsWith(b, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/gif"       => StartsWith(b, "GIF87a"u8) || StartsWith(b, "GIF89a"u8),
        "image/jpeg"      => StartsWith(b, [0xFF, 0xD8, 0xFF]),
        // ZPL is text: a format starts with ^XA, possibly after whitespace or a leading ~ command.
        _                 => Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 256)).TrimStart().Contains("^XA", StringComparison.Ordinal)
    };

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> signature) =>
        content.AsSpan().StartsWith(signature);

    /// <summary>
    /// Built here from values this system issued. A carrier-supplied name could carry path
    /// separators or quotes into a Content-Disposition header.
    /// </summary>
    internal static string FileNameFor(string consignmentNumber, string awb, string extension)
    {
        static string Safe(string value) =>
            new(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray());

        return $"{Safe(consignmentNumber)}-{Safe(awb)}.{extension}";
    }
}
