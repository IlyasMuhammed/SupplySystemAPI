using SMS.Modules.Warehouse.Models;

namespace SMS.Modules.Warehouse.Services;

public abstract record SroAckValidationResult
{
    private SroAckValidationResult() { }

    public sealed record Valid(SroAckPublicPayload Payload) : SroAckValidationResult;
    public sealed record Consumed : SroAckValidationResult;
    public sealed record Expired : SroAckValidationResult;
    public sealed record Invalid : SroAckValidationResult;
}
