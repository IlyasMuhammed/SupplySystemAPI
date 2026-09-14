namespace SMS.Modules.Warehouse.Services;

public interface ISroAckValidationService
{
    Task<SroAckValidationResult> ValidateAsync(string rawToken);
}
