namespace SMS.Modules.Finance.Services;

public interface IInvoiceDocumentService
{
    Task<byte[]> GeneratePdfAsync(Guid invoiceUuid);
}
