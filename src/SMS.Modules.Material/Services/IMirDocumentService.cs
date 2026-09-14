namespace SMS.Modules.Material.Services;

// REQ — downloadable MIR PDF, matching Procurement's PO/Invoice document design.
public interface IMirDocumentService
{
    Task<byte[]> GeneratePdfAsync(Guid mirUuid);
}
