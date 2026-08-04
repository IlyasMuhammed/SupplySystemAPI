using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SMS.Modules.Tenancy.Data;

internal sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args)
    {
        var connString = Environment.GetEnvironmentVariable("SMS_DB_CONNECTION")
            ?? "Server=(localdb)\\mssqllocaldb;Database=SMS_Dev;Trusted_Connection=True;MultipleActiveResultSets=true";

        var options = new DbContextOptionsBuilder<TenancyDbContext>()
            .UseSqlServer(connString)
            .Options;

        return new TenancyDbContext(options);
    }
}