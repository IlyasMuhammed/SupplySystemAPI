using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A48 AR — the receivables world (<see cref="ReceivablesWorld"/>: real invoices, payments and ledger on InMemory) plus the AR
/// services wired over the same database, with only Suppliers (partner lookup) and mail faked.
/// </summary>
internal sealed class A48ArWorld
{
    public ReceivablesWorld World { get; } = new();
    public Guid Org { get; } = Guid.NewGuid();
    public FakePartnerLookup Partners { get; } = new();
    public List<(string To, string Subject, string Body, int Attachments)> Mail { get; } = [];
    /// <summary>Non-null: the mail sender reports this failure (REV-1).</summary>
    public string? MailFailure { get; set; }
    private int _codes;

    public ReceivablesDesk Desk => World.For(Org);
    public TestClock Clock => World.Clock;
    public ArApCaller Caller => new(Org, Receivables.User, PermissionCodes.ArApCodes, "Tester");

    public Guid Customer(string name = "Acme Ltd", decimal? limit = null, string? email = "ar@acme.test")
    {
        var id = Desk.NewCustomer(name);
        Partners.Add(new PartnerInfo(id, $"C-{++_codes:000}", name, true, false, true, email, null, null, null, null, limit, 30, null));
        return id;
    }

    public async Task SetModeAsync(string mode)
    {
        await using var db = Receivables.Db(Org, World.DbName);
        var row = await db.OrgSettings.FirstOrDefaultAsync(s => s.OrganizationId == Org);
        if (row is null) db.OrgSettings.Add(new FinanceOrgSettings { OrganizationId = Org, CreditControlMode = mode });
        else row.CreditControlMode = mode;
        await db.SaveChangesAsync();
    }

    public ArScope Scope() => new(this);

    public async Task<T> InScopeAsync<T>(Func<ArScope, Task<T>> work)
    {
        await using var s = Scope();
        return await work(s);
    }
}

internal sealed class ArScope : IAsyncDisposable
{
    public ArScope(A48ArWorld w)
    {
        var tenant = new StaticTenantContext { OrganizationId = w.Org };
        Db       = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(w.World.DbName).Options, tenant);
        Demand   = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(w.World.DbName).Options, tenant);
        Profiles = new PartnerFinancialProfileService(Db, w.Partners, w.Clock);
        Exposure = new CreditExposureReader(Db, Demand);
        Credit   = new CreditControlService(Db, Exposure, Profiles, NullLogger<CreditControlService>.Instance, w.Partners, clock: w.Clock);
        Ledger   = new CustomerLedgerService(Db, w.Clock);
        var currency = new ArDocumentCurrency();
        Charges  = new DunningChargeWriter(Db, Ledger, currency, null, w.Clock);

        // REV-1 — dunning sends through SendWithResultAsync; MailFailure set = the sender reports that failure.
        var mail = new Mock<IEmailSender>();
        mail.Setup(m => m.SendWithResultAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<EmailAttachment>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string to, string s, string b, IReadOnlyList<EmailAttachment>? a, CancellationToken _) =>
            {
                if (w.MailFailure is { } failure) return EmailSendResult.Failed(failure);
                lock (w.Mail) w.Mail.Add((to, s, b, a?.Count ?? 0));
                return EmailSendResult.Sent;
            });

        Dunning = new DunningService(Db, Profiles, new ArApDefaultsSeeder(Db), Credit, Charges, new DunningTemplateEngine(),
            NullLogger<DunningService>.Instance, w.Partners, mail.Object, clock: w.Clock);
        Memos = new ArCreditMemoService(Db, Ledger, currency, NullLogger<ArCreditMemoService>.Instance, w.Partners, null, w.Clock);

        // The sales invoice service as ReceivablesScope wires it, plus the credit check (task 120).
        var reader = new Mock<IDeliveryFulfillmentReader>();
        reader.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((Guid id, CancellationToken _) => w.World.DeliveryFor(w.Org, id));
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => w.World.NamesFor(w.Org, ids));
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = ReceivablesWorld.PkrId, Name = "Pakistani Rupee", Code = "PKR" }]);
        Invoices = new SalesInvoiceService(Db, Demand, reader.Object, Ledger, new ProductLedgerService(Db, w.World.Variants, w.Clock),
            names.Object, lookups.Object, Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance, w.Clock,
            creditCheck: Credit);
    }

    public FinanceDbContext                Db       { get; }
    public DemandDbContext                 Demand   { get; }
    public PartnerFinancialProfileService  Profiles { get; }
    public CreditExposureReader            Exposure { get; }
    public CreditControlService            Credit   { get; }
    public CustomerLedgerService           Ledger   { get; }
    public DunningChargeWriter             Charges  { get; }
    public DunningService                  Dunning  { get; }
    public ArCreditMemoService             Memos    { get; }
    public SalesInvoiceService             Invoices { get; }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await Demand.DisposeAsync();
    }
}

/// <summary>What Suppliers' partner lookup answers, from a dictionary.</summary>
internal sealed class FakePartnerLookup : IPartnerLookupService
{
    private readonly Dictionary<Guid, PartnerInfo> _partners = [];

    public void Add(PartnerInfo p) { lock (_partners) _partners[p.Uuid] = p; }

    public void SetLimit(Guid uuid, decimal? limit) { lock (_partners) _partners[uuid] = _partners[uuid] with { CreditLimit = limit }; }

    public Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default)
    {
        lock (_partners) return Task.FromResult(_partners.GetValueOrDefault(partnerUuid));
    }

    public Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(Guid organizationId, IEnumerable<Guid> partnerUuids, string? role = null, CancellationToken ct = default)
    {
        lock (_partners)
            return Task.FromResult<IReadOnlyDictionary<Guid, PartnerInfo>>(
                partnerUuids.Where(_partners.ContainsKey).Distinct().ToDictionary(u => u, u => _partners[u]));
    }

    public Task<IReadOnlyList<PartnerInfo>> SearchAsync(Guid organizationId, string search, string? role = null, int limit = 200, CancellationToken ct = default)
    {
        lock (_partners)
            return Task.FromResult<IReadOnlyList<PartnerInfo>>(_partners.Values
                .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(limit).ToList());
    }

    public Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default) =>
        Task.FromResult<PartnerBankInfo?>(null);
}
