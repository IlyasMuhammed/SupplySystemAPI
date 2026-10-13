namespace SMS.Shared.Common;

// A41 — CRM (docs/crm/ADDENDUM-41-ANALYSIS.md D-1). SMS.Modules.Crm reaches the rest of the system only through these
// seams. Every method takes the organization explicitly (the tenant filter is off for super admins and for the public
// lead API) and treats another organization's rows as absent.

/// <summary>CRM feature codes (A37 registry, D-6). The module code is <see cref="ModuleCodes.Crm"/>.</summary>
public static class CrmFeatureCodes
{
    public const string LeadManagement        = "FEATURE_CRM_LEAD_MANAGEMENT";
    public const string OpportunityManagement = "FEATURE_CRM_OPPORTUNITY_MANAGEMENT";
    public const string ActivityTracking      = "FEATURE_CRM_ACTIVITY_TRACKING";
    public const string CampaignTracking      = "FEATURE_CRM_CAMPAIGN_TRACKING";
    public const string LeadAssignment        = "FEATURE_CRM_LEAD_ASSIGNMENT";
    public const string LeadScoring           = "FEATURE_CRM_LEAD_SCORING";
    public const string LeadWidget            = "FEATURE_CRM_LEAD_WIDGET";
    public const string ExternalSync          = "FEATURE_CRM_EXTERNAL_SYNC";
}

// ─── Users (Auth implements) ────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A user of one organization as other modules need it: who, whether active, and what they may do.</summary>
public sealed record OrgUserInfo(int UserId, string Name, string Email, bool IsActive, IReadOnlyList<string> Permissions);

/// <summary>A41 D-5 — the organization's users (assignment pickers, owner names). Deleted users are absent.</summary>
public interface IOrgUserDirectory
{
    Task<IReadOnlyList<OrgUserInfo>> GetUsersAsync(Guid organizationId, IReadOnlyCollection<int> userIds, CancellationToken ct = default);
    /// <summary>Active users whose name or email contains <paramref name="search"/>, by name.</summary>
    Task<IReadOnlyList<OrgUserInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default);
}

// ─── Accounts & contacts (Suppliers implements) ─────────────────────────────────────────────────────────────────────

/// <summary>A business partner as the CRM shows it (BusinessPartners: SupplierName = name, SupplierCode = code).</summary>
public sealed record CrmAccountInfo(
    Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive,
    string? Phone, string? Email, string? CustomerType, DateTime CreatedAt);

/// <summary>A SupplierContacts row (the table holds the contact persons of every partner type).</summary>
public sealed record CrmContactInfo(
    int Id, Guid AccountUuid, string Name, string? Title, string? Phone, string? Email, bool IsPrimary, bool IsActive);

/// <summary>D-19 — a new customer made from a lead. CustomerType COMPANY or INDIVIDUAL (B2C, BR-03).</summary>
public sealed record CrmNewAccount(
    string Name, string CustomerType, string? Phone, string? Mobile, string? Email, string? Website,
    string? AddressLine1, string? AddressLine2, string? City, string? ProvinceState, string? PostalCode, string? Country);

/// <summary>D-20 — a new contact person under an account.</summary>
public sealed record CrmNewContact(string Name, string? Title, string? Phone, string? Email);

/// <summary>
/// A41 D-19/D-20 — accounts (business partners) and their contact persons for lead conversion, the duplicate check and
/// the opportunity / lead screens. Writes follow the customer master's own rules (A37 customer service: code C-nnnnn,
/// walk-in protection, QuickBooks publish). Throws <c>BadRequestException</c> / <c>NotFoundException</c> with a
/// user-facing message on a refusal.
/// </summary>
public interface ICrmAccountGateway
{
    Task<IReadOnlyDictionary<Guid, CrmAccountInfo>> GetAccountsAsync(Guid organizationId, IReadOnlyCollection<Guid> accountUuids, CancellationToken ct = default);

    /// <summary>§8.4 — up to <paramref name="limit"/> non-deleted partners whose name contains the company name, or whose
    /// email / phone equals the lead's; exact name first, then by name.</summary>
    Task<IReadOnlyList<CrmAccountInfo>> FindMatchesAsync(Guid organizationId, string? companyName, string? email, string? phone, int limit, CancellationToken ct = default);

    /// <summary>The conversion dialog's "link to existing account" search: active, non-walk-in partners by name/code/phone/email.</summary>
    Task<IReadOnlyList<CrmAccountInfo>> SearchAsync(Guid organizationId, string? search, int limit, CancellationToken ct = default);

    /// <summary>Creates a customer (must be the caller's organization — the customer service works on the ambient tenant).</summary>
    Task<Guid> CreateCustomerAsync(Guid organizationId, CrmNewAccount account, int userId, CancellationToken ct = default);

    /// <summary>Makes an existing active partner a customer too (a vendor becomes BOTH). No-op when it already is one.</summary>
    Task EnsureCustomerAsync(Guid organizationId, Guid accountUuid, int userId, CancellationToken ct = default);

    Task<IReadOnlyList<CrmContactInfo>> ListContactsAsync(Guid organizationId, Guid accountUuid, CancellationToken ct = default);
    Task<IReadOnlyDictionary<int, CrmContactInfo>> GetContactsAsync(Guid organizationId, IReadOnlyCollection<int> contactIds, CancellationToken ct = default);
    Task<int> CreateContactAsync(Guid organizationId, Guid accountUuid, CrmNewContact contact, CancellationToken ct = default);
}

// ─── Sales documents (Demand implements) ────────────────────────────────────────────────────────────────────────────

public static class CrmSalesDocumentKind
{
    public const string Inquiry = "INQUIRY", Quotation = "QUOTATION", Order = "ORDER";
}

/// <summary>A sale inquiry, quotation or order linked to an opportunity (Demand's SourceOpportunityUuid, D-13).</summary>
public sealed record CrmSalesDocument(
    string Kind, Guid Uuid, string Number, string Status, DateTime CreatedAt, decimal? GrandTotal, string? CurrencyCode);

/// <summary>
/// A41 D-13 — the opportunity's way into the A32 pipeline. Creation runs the A32 services' own rules on the caller's
/// organization (ambient tenant) and stamps <c>SourceOpportunityUuid</c>; a sale order is never created here (it needs
/// lines — the sale order form takes <c>sourceOpportunityUuid</c>). Throws <c>BadRequestException</c> on a refusal.
/// </summary>
public interface ICrmSalesDocumentGateway
{
    /// <summary>A RECEIVED inquiry with no lines for the account, assigned to <paramref name="assignedToUserId"/>.</summary>
    Task<CrmSalesDocument> CreateInquiryAsync(Guid opportunityUuid, Guid accountUuid, int? assignedToUserId, string? notes, int userId, CancellationToken ct = default);

    /// <summary>From <paramref name="fromInquiryUuid"/> when it is REVIEW_COMPLETE, else an empty DRAFT quotation for the account.</summary>
    Task<CrmSalesDocument> CreateQuotationAsync(Guid opportunityUuid, Guid accountUuid, Guid? fromInquiryUuid, string? notes, int userId, CancellationToken ct = default);

    /// <summary>Every inquiry, quotation and sale order carrying this opportunity, oldest first.</summary>
    Task<IReadOnlyList<CrmSalesDocument>> ListForOpportunityAsync(Guid organizationId, Guid opportunityUuid, CancellationToken ct = default);
}
