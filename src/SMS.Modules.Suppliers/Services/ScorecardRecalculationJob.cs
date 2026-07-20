using Microsoft.Extensions.Configuration;

namespace SMS.Modules.Suppliers.Services;

internal sealed class ScorecardRecalculationJob
{
    private readonly IScorecardRecalculationService _svc;
    private readonly IConfiguration _config;

    public ScorecardRecalculationJob(IScorecardRecalculationService svc, IConfiguration config)
    {
        _svc    = svc;
        _config = config;
    }

    public Task RunAsync()
    {
        var frequency = _config["SupplierScorecard:RecalculationFrequency"];
        var (start, end) = ScorecardPeriodResolver.ResolvePreviousPeriod(frequency, DateTime.UtcNow);
        return _svc.RecalculateAllAsync(start, end, triggeredBy: 0); // 0 = system-triggered (scheduled job)
    }
}
