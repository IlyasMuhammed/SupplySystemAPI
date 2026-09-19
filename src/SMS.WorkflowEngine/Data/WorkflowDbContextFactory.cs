using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Data;

internal sealed class WorkflowDbContextFactory : IDesignTimeDbContextFactory<WorkflowDbContext>
{
    public WorkflowDbContext CreateDbContext(string[] args)
    {
        var connString = DesignTimeConnection.Resolve();

        var optionsBuilder = new DbContextOptionsBuilder<WorkflowDbContext>();
        optionsBuilder.UseSqlServer(connString);
        return new WorkflowDbContext(optionsBuilder.Options, new StaticTenantContext());
    }
}
