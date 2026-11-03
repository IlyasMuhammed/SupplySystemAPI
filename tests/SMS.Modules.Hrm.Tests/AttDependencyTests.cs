using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>The engine resolves every status handler, so a handler reaching the engine stops the host from starting. Unit tests with fakes cannot see that.</summary>
public sealed class AttDependencyTests
{
    [Fact]
    public void The_leave_status_handler_never_reaches_the_workflow_engine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HrmDbContext>(o => o.UseInMemoryDatabase("att-di-" + Guid.NewGuid()));
        services.AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        HrmModuleExtensions.AddHrmServices(services);
        services.AddScoped<IWorkflowActionService>(_ => throw new InvalidOperationException("The handler's chain reached the workflow engine."));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handler = ActivatorUtilities.CreateInstance<LeaveRequestStatusHandler>(scope.ServiceProvider);
        handler.InterfaceCode.Should().Be(HrmWorkflowCodes.LeaveRequest);
    }
}
