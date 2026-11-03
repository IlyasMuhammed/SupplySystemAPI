using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>The real registrations, and the background path of a large calculation.</summary>
public sealed class PayrollWiringTests
{
    private sealed class RecordingJobs : IBackgroundJobClient
    {
        public List<Job> Created { get; } = [];
        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return "1";
        }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private sealed class Provider(PayrollProgressTracker tracker) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(PayrollProgressTracker) ? tracker : null;
    }

    [Fact]
    public async Task More_than_the_threshold_queues_a_job_that_a_second_call_cannot_duplicate_and_the_job_calculates()
    {
        var h = await new PayHarness().ReadyAsync();
        await h.StaffAsync("Ali", 50_000m);
        await h.StaffAsync("Bano", 60_000m);
        var tracker = new PayrollProgressTracker();
        var jobs = new RecordingJobs();
        var engine = new PayrollCalculationEngine(h.Db, h.Core.Directory, h.Formulas, h.Attendance, h.Providers, tracker);
        var runs = new PayrollRunService(h.Db, h.Core.Scope, h.Core.Directory, engine, h.Builder, h.Gateway, h.Readiness, h.Providers, h.Attendance, tracker, h.Core.Seeder, jobs: jobs) { BackgroundThreshold = 1 };
        var run = await runs.CreateAsync(h.Admin, new PayrollRunCreate { PeriodStart = PayHarness.October, PeriodEnd = PayHarness.OctoberEnd, PayDate = new DateOnly(2026, 11, 1) });

        var queued = await runs.CalculateAsync(h.Admin, run.Uuid);
        queued.CalculationQueued.Should().BeTrue();
        queued.Status.Should().Be("Draft");
        jobs.Created.Should().ContainSingle();
        jobs.Created[0].Args.Should().Contain(h.Org, "the job takes the organization explicitly");
        (await runs.GetProgressAsync(h.Admin, run.Uuid)).Running.Should().BeTrue();

        var again = () => runs.CalculateAsync(h.Admin, run.Uuid);
        await again.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_CALCULATING*");
        jobs.Created.Should().ContainSingle();

        await new PayrollCalculationJob(engine, new Provider(tracker)).RunAsync(h.Org, run.Uuid, 900, "HR Admin");
        h.Db.ChangeTracker.Clear();
        var progress = await runs.GetProgressAsync(h.Admin, run.Uuid);
        progress.Running.Should().BeFalse();
        progress.Status.Should().Be("Calculated");
        progress.Processed.Should().Be(2);
        (await runs.GetAsync(h.Admin, run.Uuid)).TotalGross.Should().Be(110_000m);
    }

    [Fact]
    public void Every_pay_service_resolves_from_the_real_registrations_and_the_owners_seams_win_over_the_defaults()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        services.AddSingleton(Options.Create(new AppSettings { AesEncryptionKey = HrmHarness.AesKey }));
        services.AddDbContext<HrmDbContext>(o => o.UseInMemoryDatabase("wiring-" + Guid.NewGuid()));
        HrmModuleExtensions.AddHrmServices(services);
        // Hosted by other modules in the real application.
        services.AddScoped<IHrmApprovalGateway, PayApprovals>();
        services.AddScoped<IHrmDocumentStore, PayDocumentStore>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        sp.GetRequiredService<IPayrollRunService>().Should().NotBeNull();
        sp.GetRequiredService<IPayslipService>().Should().NotBeNull();
        sp.GetRequiredService<IPayrollAdjustmentService>().Should().NotBeNull();
        sp.GetRequiredService<ISalaryComponentService>().Should().NotBeNull();
        sp.GetRequiredService<ISalaryStructureService>().Should().NotBeNull();
        sp.GetRequiredService<ISalaryAssignmentService>().Should().NotBeNull();
        sp.GetRequiredService<IStatutoryService>().Should().NotBeNull();
        sp.GetRequiredService<PayrollCalculationJob>().Should().NotBeNull();
        sp.GetServices<IDocumentStatusHandler>().Should().Contain(h => h.InterfaceCode == HrmWorkflowCodes.SalaryChange);

        sp.GetRequiredService<IAttendancePayInput>().Should().BeOfType<AttendancePayInput>("ATT registers before AddPay");
        sp.GetRequiredService<IPayrollPostingReadinessService>().Should().NotBeOfType<DefaultPayrollPostingReadiness>("POST registers after AddPay");
        sp.GetServices<IPayrollInputProvider>().Should().Contain(p => p is PayrollAdjustmentInputProvider);
    }
}
