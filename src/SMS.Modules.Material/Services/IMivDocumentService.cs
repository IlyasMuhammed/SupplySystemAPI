namespace SMS.Modules.Material.Services;

// REQ — downloadable MIV PDF, matching Procurement's PO/Invoice document design.
public interface IMivDocumentService
{
    Task<byte[]> GeneratePdfAsync(Guid mivUuid);
}
