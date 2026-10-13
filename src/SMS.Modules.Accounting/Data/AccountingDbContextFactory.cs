using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Data;

internal sealed class AccountingDbContextFactory : IDesignTimeDbContextFactory<AccountingDbContext>
{
    public AccountingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccountingDbContext>().UseSqlServer(DesignTimeConnection.Resolve()).Options, new StaticTenantContext());
}
