namespace SMS.Modules.Demand.Services;

public interface IPoDocumentService
{
    Task<byte[]> GeneratePdfAsync(Guid poUuid);
}