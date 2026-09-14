namespace SMS.Modules.Warehouse.Services;

public interface ISroAcknowledgmentEmailJob
{
    Task SendAsync(int linkId);
}
