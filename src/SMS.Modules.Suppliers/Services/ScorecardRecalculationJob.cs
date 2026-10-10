using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// Scheduled scorecard roll-up. A37 D-9 — organizations without MODULE_SUPPLIERS (grace counts as off) are skipped:
/// when any is, the recalculation runs once per enabled organization under its tenant scope instead of once for all.
/// </summary>
internal sealed class ScorecardRecalculationJob
{
    private readonly IScorecardRecalculationService _svc;
    private readonly IConfiguration _config;
    private readonly IModuleGate? _gate;
    private readonly IOrganizationDirectory? _organizations;
    private readonly ILogger _log;

    public ScorecardRecalculationJob(
        IScorecardRecalculationService svc, IConfiguration config, IModuleGate? gate = null,
        IOrganizationDirectory? organizations = null, ILogger<ScorecardRecalculationJob>? log = null)
    {
        _svc           = svc;
        _config        = config;
        _gate          = gate;
        _organizations = organizations;
        _log           = log ?? (ILogger)NullLogger.Instance;
    }

    public Task RunAsync()
    {
        var frequency = _config["SupplierScorecard:RecalculationFrequency"];
        var (start, end) = ScorecardPeriodResolver.ResolvePreviousPeriod(frequency, DateTime.UtcNow);
        return _gate.RunForEnabledOrganizationsAsync(_organizations, ModuleCodes.Suppliers, _log, nameof(ScorecardRecalculationJob),
            () => _svc.RecalculateAllAsync(start, end, triggeredBy: 0)); // 0 = system-triggered (scheduled job)
    }
}
