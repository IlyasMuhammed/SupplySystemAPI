using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Crm;
using SMS.Modules.Crm.Data;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Tests;

/// <summary>Auth's built-in CRM role grants (AuthDataSeeder.SalesRepCodes / SalesManagerCodes), repeated so tests need no Auth reference.</summary>
internal static class Roles
{
    public static readonly string[] SalesRep =
    [
        P.CRM_VIEW_LEADS, P.CRM_CREATE_LEAD, P.CRM_EDIT_LEAD, P.CRM_QUALIFY_LEAD, P.CRM_CONVERT_LEAD, P.CRM_DISQUALIFY_LEAD,
        P.CRM_VIEW_OPPORTUNITIES, P.CRM_EDIT_OPPORTUNITY, P.CRM_CLOSE_OPPORTUNITY, P.CRM_VIEW_ACTIVITIES, P.CRM_CREATE_ACTIVITY,
        P.CRM_EDIT_ACTIVITY, P.CRM_EXPORT_TIMELINE, P.CRM_VIEW_CAMPAIGNS, P.CUSTOMER_VIEW,
        P.SALE_INQUIRY_VIEW, P.SALE_INQUIRY_CREATE, P.SALE_QUOTATION_VIEW, P.SALE_QUOTATION_CREATE, P.SALE_ORDER_VIEW, P.SALE_ORDER_CREATE,
    ];

    public static readonly string[] SalesManager =
    [
        .. SalesRep, P.CRM_ASSIGN_LEAD, P.CRM_REOPEN_LEAD, P.CRM_DELETE_LEAD, P.CRM_REOPEN_OPPORTUNITY, P.CRM_DELETE_ACTIVITY,
        P.CRM_MANAGE_CAMPAIGNS, P.CUSTOMER_CREATE, P.CUSTOMER_EDIT,
    ];

    public static readonly string[] Admin = P.All.ToArray();
}

internal sealed class FakeNumbers : IDocumentNumberGenerator
{
    private readonly Dictionary<(Guid, string, int), int> _next = [];
    public Task<string> NextAsync(string prefix, DateTime? utcNow = null, Guid? organizationId = null, CancellationToken ct = default)
    {
        var year = (utcNow ?? DateTime.UtcNow).Year;
        var key = (organizationId ?? Guid.Empty, prefix, year);
        _next[key] = _next.GetValueOrDefault(key) + 1;
        return Task.FromResult($"{prefix}-{year}-{_next[key]:D5}");
    }
}

internal sealed class FakeUsers : IOrgUserDirectory
{
    public Dictionary<int, OrgUserInfo> Users { get; } = [];
    public void Add(int id, string name, bool active = true) => Users[id] = new OrgUserInfo(id, name, $"u{id}@x.test", active, []);
    public Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgUserInfo>>(userIds.Where(Users.ContainsKey).Select(i => Users[i]).ToList());
    public Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgUserInfo>>(Users.Values.Where(u => u.IsActive && (search is null || u.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Take(limit).ToList());
}

/// <summary>In-memory accounts (business partners) and contacts, scoped by organization like the Suppliers gateway.</summary>
internal sealed class FakeAccounts : ICrmAccountGateway
{
    public sealed record Account(Guid Org, Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive, string? Phone, string? Email,
        string? CustomerType, string? Website = null);
    public sealed record Contact(Guid Org, int Id, Guid AccountUuid, string Name, string? Title, string? Phone, string? Email, bool IsPrimary);

    public List<Account> Accounts { get; } = [];
    public List<Contact> Contacts { get; } = [];
    public List<CrmNewAccount> Created { get; } = [];
    private int _nextContact = 100;

    public Guid AddAccount(Guid org, string name, bool customer = true, bool vendor = false, string? phone = null, string? email = null, bool active = true)
    {
        var uuid = Guid.NewGuid();
        Accounts.Add(new Account(org, uuid, $"C-{Accounts.Count + 1:D5}", name, customer, vendor, active, phone, email, customer ? "COMPANY" : null));
        return uuid;
    }

    public int AddContact(Guid org, Guid account, string name, string? email = null)
    {
        var id = ++_nextContact;
        Contacts.Add(new Contact(org, id, account, name, null, null, email, !Contacts.Any(c => c.AccountUuid == account)));
        return id;
    }

    private static CrmAccountInfo Info(Account a) =>
        new(a.Uuid, a.Code, a.Name, a.IsCustomer, a.IsVendor, a.IsActive, a.Phone, a.Email, a.CustomerType, DateTime.UtcNow);

    private static CrmContactInfo Info(Contact c) => new(c.Id, c.AccountUuid, c.Name, c.Title, c.Phone, c.Email, c.IsPrimary, true);

    public Task<IReadOnlyDictionary<Guid, CrmAccountInfo>> GetAccountsAsync(Guid organizationId, IReadOnlyCollection<Guid> accountUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CrmAccountInfo>>(Accounts.Where(a => a.Org == organizationId && accountUuids.Contains(a.Uuid))
            .ToDictionary(a => a.Uuid, Info));

    public Task<IReadOnlyList<CrmAccountInfo>> FindMatchesAsync(Guid organizationId, string? companyName, string? email, string? phone, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CrmAccountInfo>>(Accounts.Where(a => a.Org == organizationId
                && ((companyName != null && a.Name.Contains(companyName, StringComparison.OrdinalIgnoreCase))
                    || (email != null && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase))
                    || (phone != null && a.Phone == phone)))
            .OrderBy(a => string.Equals(a.Name, companyName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).Take(limit).Select(Info).ToList());

    public Task<IReadOnlyList<CrmAccountInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CrmAccountInfo>>(Accounts.Where(a => a.Org == organizationId && a.IsActive
            && (search == null || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase))).Take(limit).Select(Info).ToList());

    public Task<Guid> CreateCustomerAsync(Guid organizationId, CrmNewAccount account, int userId, CancellationToken ct = default)
    {
        Created.Add(account);
        var uuid = Guid.NewGuid();
        Accounts.Add(new Account(organizationId, uuid, $"C-{Accounts.Count + 1:D5}", account.Name, true, false, true,
            account.Phone ?? account.Mobile, account.Email, account.CustomerType, account.Website));
        return Task.FromResult(uuid);
    }

    public Task EnsureCustomerAsync(Guid organizationId, Guid accountUuid, int userId, CancellationToken ct = default)
    {
        var i = Accounts.FindIndex(a => a.Org == organizationId && a.Uuid == accountUuid);
        if (i < 0) throw new NotFoundException("Account not found.");
        Accounts[i] = Accounts[i] with { IsCustomer = true, CustomerType = Accounts[i].CustomerType ?? "COMPANY" };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CrmContactInfo>> ListContactsAsync(Guid organizationId, Guid accountUuid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CrmContactInfo>>(Contacts.Where(c => c.Org == organizationId && c.AccountUuid == accountUuid).Select(Info).ToList());

    public Task<IReadOnlyDictionary<int, CrmContactInfo>> GetContactsAsync(Guid organizationId, IReadOnlyCollection<int> contactIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, CrmContactInfo>>(Contacts.Where(c => c.Org == organizationId && contactIds.Contains(c.Id)).ToDictionary(c => c.Id, Info));

    public Task<int> CreateContactAsync(Guid organizationId, Guid accountUuid, CrmNewContact contact, CancellationToken ct = default)
    {
        var id = ++_nextContact;
        Contacts.Add(new Contact(organizationId, id, accountUuid, contact.Name, contact.Title, contact.Phone, contact.Email,
            !Contacts.Any(c => c.AccountUuid == accountUuid)));
        return Task.FromResult(id);
    }
}

/// <summary>Demand's documents as the CRM sees them: linked by opportunity uuid.</summary>
internal sealed class FakeSales : ICrmSalesDocumentGateway
{
    public sealed record Doc(Guid Org, Guid Opportunity, CrmSalesDocument Document);
    public List<Doc> Docs { get; } = [];
    public Guid Org { get; set; }
    public List<(Guid Opportunity, Guid Account, Guid? FromInquiry)> QuotationCalls { get; } = [];
    private int _n;

    public CrmSalesDocument Add(Guid opportunity, string kind, string status, decimal? total = null, string? currency = "PKR")
    {
        var number = kind switch { CrmSalesDocumentKind.Inquiry => "INQ", CrmSalesDocumentKind.Quotation => "SQ", _ => "SO" } + $"-2026-{++_n:D5}";
        var doc = new CrmSalesDocument(kind, Guid.NewGuid(), number, status, DateTime.UtcNow.AddSeconds(_n), total, currency);
        Docs.Add(new Doc(Org, opportunity, doc));
        return doc;
    }

    public Task<CrmSalesDocument> CreateInquiryAsync(Guid opportunityUuid, Guid accountUuid, int? assignedToUserId, string? notes, int userId, CancellationToken ct = default) =>
        Task.FromResult(Add(opportunityUuid, CrmSalesDocumentKind.Inquiry, "RECEIVED"));

    public Task<CrmSalesDocument> CreateQuotationAsync(Guid opportunityUuid, Guid accountUuid, Guid? fromInquiryUuid, string? notes, int userId, CancellationToken ct = default)
    {
        QuotationCalls.Add((opportunityUuid, accountUuid, fromInquiryUuid));
        return Task.FromResult(Add(opportunityUuid, CrmSalesDocumentKind.Quotation, "DRAFT", 0m));
    }

    public Task<IReadOnlyList<CrmSalesDocument>> ListForOpportunityAsync(Guid organizationId, Guid opportunityUuid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CrmSalesDocument>>(Docs.Where(d => d.Org == organizationId && d.Opportunity == opportunityUuid).Select(d => d.Document).ToList());
}

internal sealed class FakeGate : IModuleGate
{
    public HashSet<string> Off { get; } = [];
    public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(!Off.Contains(featureCode));
}

internal sealed class FakeCurrencies : IOrgCurrencyLookup
{
    public HashSet<string> Active { get; } = ["PKR", "USD"];
    private static OrgCurrencyInfo Info(string code, bool active) => new(Guid.NewGuid(), code, code, code, 2, 0.01m, "BEFORE", active, 0);
    public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult<OrgCurrencyInfo?>(null);
    public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
        Task.FromResult(Active.Contains(code) ? Info(code, true) : code == "EUR" ? Info(code, false) : null);
    public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>(Active.Select(c => Info(c, true)).ToList());
    public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) => Task.FromResult(2);
}

internal sealed class FakeNotifications : INotificationService
{
    public List<NotificationRequest> Sent { get; } = [];
    public Task CreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
    public Task TryCreateAsync(NotificationRequest request) { Sent.Add(request); return Task.CompletedTask; }
    public Task BroadcastAsync(string type, string category, string title, string message, int createdBy = 0) => Task.CompletedTask;
    public Task SendEmailAsync(string to, string subject, string htmlBody) => Task.CompletedTask;
}

/// <summary>Records every published event, then runs the real handlers (the notifier) like the module's publisher.</summary>
internal sealed class RecordingEvents : ICrmEventPublisher
{
    private readonly IServiceProvider _services;
    public RecordingEvents(IServiceProvider services) => _services = services;
    public List<ICrmEvent> Published { get; } = [];
    public IEnumerable<T> Of<T>() where T : ICrmEvent => Published.OfType<T>();

    public async Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : ICrmEvent
    {
        Published.Add(evt);
        foreach (var h in _services.GetServices<ICrmEventHandler<TEvent>>()) await h.HandleAsync(evt, ct);
    }
}

/// <summary>The CRM services over an in-memory store, with every Shared seam faked.</summary>
internal sealed class CrmHarness : IDisposable
{
    public Guid Org { get; } = Guid.NewGuid();
    public StaticTenantContext Tenant { get; }
    public FakeUsers Users { get; } = new();
    public FakeAccounts Accounts { get; } = new();
    public FakeSales Sales { get; } = new();
    public FakeGate Gate { get; } = new();
    public FakeCurrencies Currencies { get; } = new();
    public FakeNotifications Notifications { get; } = new();
    public RecordingEvents Events => (RecordingEvents)_scope.ServiceProvider.GetRequiredService<ICrmEventPublisher>();

    private readonly ServiceProvider _root;
    private readonly IServiceScope _scope;

    public CrmHarness(bool withSales = true)
    {
        Tenant = new StaticTenantContext { OrganizationId = Org };
        Sales.Org = Org;
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(Tenant);
        services.AddDbContext<CrmDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IDocumentNumberGenerator>(new FakeNumbers());
        services.AddSingleton<IOrgUserDirectory>(Users);
        services.AddSingleton<ICrmAccountGateway>(Accounts);
        if (withSales) services.AddSingleton<ICrmSalesDocumentGateway>(Sales);
        services.AddSingleton<IModuleGate>(Gate);
        services.AddSingleton<IOrgCurrencyLookup>(Currencies);
        services.AddSingleton<INotificationService>(Notifications);
        CrmModuleExtensions.AddCrmServices(services);
        services.AddScoped<ICrmEventPublisher, RecordingEvents>();
        _root = services.BuildServiceProvider();
        _scope = _root.CreateScope();

        Users.Add(10, "Salim Ahmed");
        Users.Add(11, "Faisal Khan");
        Users.Add(20, "Maria Manager");
        Users.Add(30, "Ina Active", active: false);
    }

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
    public CrmDbContext Db => Get<CrmDbContext>();

    public CrmCaller Rep(int userId = 10) => new(Org, userId, Roles.SalesRep);
    public CrmCaller Manager(int userId = 20) => new(Org, userId, Roles.SalesManager);
    public CrmCaller Admin(int userId = 1) => new(Org, userId, Roles.Admin);
    public CrmCaller With(int userId, params string[] permissions) => new(Org, userId, permissions);

    public ILeadService Leads => Get<ILeadService>();
    public IActivityService Activities => Get<IActivityService>();
    public IOpportunityService Opportunities => Get<IOpportunityService>();
    public ILeadConversionService Conversion => Get<ILeadConversionService>();

    public static LeadUpsert NewLead(string first = "Ahmed", string last = "Khan", string? company = "PakElectro", string topic = "AC + Ovens inquiry",
        string? email = "ahmed@pakelec.com", string? mobile = "+92-321-7654321") => new()
    {
        FirstName = first, LastName = last, CompanyName = company, Topic = topic, Email = email, MobilePhone = mobile, Phone = "+92-300-1234567",
        EstimatedRevenue = 280000m, CurrencyCode = "PKR", EstimatedCloseDate = new DateTime(2026, 10, 30)
    };

    public Task<LeadDetail> CreateLeadAsync(CrmCaller? caller = null, LeadUpsert? lead = null) => Leads.CreateAsync(caller ?? Rep(), lead ?? NewLead());

    public Task<ActivityModel> LogCallAsync(Guid leadUuid, CrmCaller? caller = null, string subject = "Initial call") =>
        Activities.CreateAsync(caller ?? Rep(), new ActivityUpsert { LeadUuid = leadUuid, ActivityType = "PHONE_CALL", Subject = subject, DurationMinutes = 15 });

    /// <summary>NEW → CONTACTED (a call) → QUALIFIED.</summary>
    public async Task<LeadDetail> QualifiedLeadAsync(CrmCaller? caller = null, LeadUpsert? lead = null)
    {
        caller ??= Rep();
        var created = await CreateLeadAsync(caller, lead);
        await LogCallAsync(created.Uuid, caller);
        return await Leads.QualifyAsync(caller, created.Uuid);
    }

    /// <summary>A qualified lead converted with a new account and contact.</summary>
    public async Task<(LeadDetail Lead, LeadConversionResult Result)> ConvertedAsync(CrmCaller? caller = null)
    {
        caller ??= Rep();
        var lead = await QualifiedLeadAsync(caller);
        var result = await Conversion.ConvertAsync(caller, lead.Uuid, new LeadConvertRequest());
        return (await Leads.GetByIdAsync(caller, lead.Uuid), result);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _root.Dispose();
    }
}
