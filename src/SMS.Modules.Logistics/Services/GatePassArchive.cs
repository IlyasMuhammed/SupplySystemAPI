using Microsoft.Extensions.Logging;
using SMS.Shared.Authorization;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Keeps a delivery's gate pass on file as an attachment on the delivery (A29 §8.3: "stored as an
/// attachment on the DO; used by warehouse security").
/// </summary>
public interface IGatePassArchive
{
    /// <summary>
    /// Files the collection gate pass of a delivery whose collection has just been recorded, so it
    /// names who took the goods. <b>Never throws</b>: the collection is recorded and the goods are
    /// issued whether or not this works, so a failure is logged and reported as <c>null</c>, and the
    /// pass can be filed afterwards with <see cref="FileGatePassAsync"/>.
    /// </summary>
    Task<Guid?> TryFileCollectionPassAsync(Guid deliveryUuid, int userId);

    /// <summary>
    /// Files the delivery's gate pass as it stands now. Refused, as the gate pass itself is, until the
    /// goods are staged at the dock. Each filing is kept: a later one does not replace an earlier one.
    /// </summary>
    Task<StoredAttachment> FileGatePassAsync(Guid deliveryUuid, int userId);
}

/// <summary>
/// <b>Filed under <c>DELIVERY</c>,</b> the code the delivery page opens its attachment panel with,
/// so the pass appears there beside anything else attached to the delivery. The gate pass carries the
/// collector's name and the number of the ID they showed, so it is kept in the database and read back
/// through the authenticated attachment endpoint, gated by <c>DELIVERY_VIEW</c> — the permission that
/// lets a person print it in the first place — and never left as a file anyone with the URL could fetch.
/// </summary>
internal sealed class GatePassArchive : IGatePassArchive
{
    /// <summary>What a delivery's attachment panel is opened with — and what its files are filed under.</summary>
    internal const string InterfaceCode = "DELIVERY";

    private readonly IDeliveryDocumentService _documents;
    private readonly IAttachmentService       _attachments;
    private readonly ILogger<GatePassArchive> _log;

    public GatePassArchive(
        IDeliveryDocumentService documents, IAttachmentService attachments, ILogger<GatePassArchive> log)
    {
        _documents   = documents;
        _attachments = attachments;
        _log         = log;
    }

    public async Task<Guid?> TryFileCollectionPassAsync(Guid deliveryUuid, int userId)
    {
        try
        {
            var stored = await FileAsync(deliveryUuid, userId, "Collection gate pass, filed when the collection was recorded.");
            return stored.Uuid;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Delivery {DeliveryUuid}'s collection was recorded but its gate pass could not be filed as an attachment; file it from the delivery.",
                deliveryUuid);
            return null;
        }
    }

    public Task<StoredAttachment> FileGatePassAsync(Guid deliveryUuid, int userId) =>
        FileAsync(deliveryUuid, userId, "Gate pass, filed on request.");

    private async Task<StoredAttachment> FileAsync(Guid deliveryUuid, int userId, string note)
    {
        // Rendering applies the gate pass's own rules: an unknown delivery is not found, and one that
        // is not yet staged is refused, because a pass filed early would be one to walk out with.
        var (content, fileName) = await _documents.GenerateGatePassAsync(deliveryUuid);

        return await _attachments.StoreGeneratedAsync(new GeneratedAttachmentRequest
        {
            InterfaceCode      = InterfaceCode,
            DocumentId         = deliveryUuid,
            FileName           = fileName,
            ContentType        = "application/pdf",
            Content            = content,
            Notes              = note,
            RequiredPermission = PermissionCodes.DELIVERY_VIEW
        }, userId);
    }
}
