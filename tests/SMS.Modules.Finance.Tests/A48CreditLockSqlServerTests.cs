using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A48 AR TS-CR-008 on SQL Server (LocalDB only): the per-customer credit lock is a real session app lock on its own
/// connection — another connection (another API instance) cannot take it while it is held, and can once it is released.
/// No schema needed: the lock lives in master.
/// </summary>
public class A48CreditLockSqlServerTests
{
    private const string Master = @"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    private static async Task<int> TryTakeAsync(Guid org, Guid customer)
    {
        await using var conn = new SqlConnection(Master);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
        cmd.Parameters.AddWithValue("@res", $"finance.credit/{org:N}/{customer:N}");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task The_lock_holds_across_connections_until_disposed()
    {
        var org = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };
        await using var db = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(Master).Options, tenant);
        await using var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(Master).Options, tenant);
        var credit = new CreditControlService(db, new CreditExposureReader(db, demand), new PartnerFinancialProfileService(db),
            NullLogger<CreditControlService>.Instance);

        var held = await credit.LockCustomerAsync(org, customer);
        (await TryTakeAsync(org, customer)).Should().BeLessThan(0, "another connection must wait while the credit check holds it");
        (await TryTakeAsync(org, Guid.NewGuid())).Should().BeGreaterOrEqualTo(0, "another customer is not blocked");

        await held.DisposeAsync();
        (await TryTakeAsync(org, customer)).Should().BeGreaterOrEqualTo(0);
    }
}
