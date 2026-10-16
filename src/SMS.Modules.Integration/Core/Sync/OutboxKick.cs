using System.Collections.Concurrent;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Jobs;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// A44 D-11: "send it now" — after the gateway committed work to the outbox, run the outbox job in a few seconds instead
/// of waiting for its one-minute schedule (the 2026-10-14 sandbox test found a sale took 1–2 minutes to appear in
/// QuickBooks). The recurring job stays; this only adds runs. Never throws and never blocks the caller.
/// </summary>
internal interface IOutboxKick
{
    void Request(int connectionId);
}

internal sealed class NoOutboxKick : IOutboxKick
{
    public static readonly NoOutboxKick Instance = new();
    public void Request(int connectionId) { }
}

/// <summary>
/// Debounced per connection: the first request schedules one outbox run <see cref="Delay"/> later; further requests for
/// that connection inside <see cref="Window"/> ride on it (a burst of a POS sale's invoice, payments and customer is one
/// run). The job runs every usable connection and is <c>DisableConcurrentExecution</c>, so a kick that lands while a run
/// is going simply runs after it. Without Hangfire (tests, hosts without job storage) nothing happens.
/// </summary>
internal sealed class HangfireOutboxKick : IOutboxKick
{
    /// <summary>How long after the first request the run starts — enough for the caller's follow-up calls to land.</summary>
    public static readonly TimeSpan Delay  = TimeSpan.FromSeconds(2);
    /// <summary>Requests inside this window after a scheduled kick are covered by it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<int, DateTime> _lastKick = new();
    private readonly IServiceProvider             _services;
    private readonly TimeProvider                 _clock;
    private readonly ILogger<HangfireOutboxKick>  _logger;

    public HangfireOutboxKick(IServiceProvider services, TimeProvider clock, ILogger<HangfireOutboxKick> logger)
    {
        _services = services;
        _clock    = clock;
        _logger   = logger;
    }

    /// <summary>Kicks actually scheduled (for tests and diagnostics).</summary>
    public int Scheduled { get; private set; }

    /// <summary>Overridable for tests: what a kick does once the debounce let it through.</summary>
    internal Func<IServiceProvider, bool>? ScheduleOverride { get; set; }

    public void Request(int connectionId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var due = false;
        _lastKick.AddOrUpdate(connectionId,
            _ => { due = true; return now; },
            (_, last) =>
            {
                if (now - last < Window) return last;
                due = true;
                return now;
            });
        if (!due) return;

        try
        {
            var scheduled = ScheduleOverride is { } schedule ? schedule(_services) : ScheduleWithHangfire();
            if (scheduled) Scheduled++;
        }
        catch (Exception ex)
        {
            // No job storage (a host without Hangfire), or storage down: the recurring run sends it.
            _logger.LogDebug(ex, "QuickBooks outbox kick for connection {ConnectionId} could not be scheduled.", connectionId);
        }
    }

    private bool ScheduleWithHangfire()
    {
        var client = _services.GetService<IBackgroundJobClient>();
        if (client is null) return false;
        client.Schedule<SyncOutboxJob>(job => job.RunAsync(), Delay);
        return true;
    }
}
