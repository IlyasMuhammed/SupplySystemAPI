namespace SMS.Modules.Suppliers.Models;

// A37 D-13 — the customer master facade over suppliers.BusinessPartners (IsCustomer). Shapes per
// docs/module-registry/API-CONTRACT.md §5/§6; the same rows sale orders, invoices and QuickBooks already use.

public class CustomerListItem
{
    public Guid Uuid { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CustomerType { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Balance { get; set; }
    public string? CurrencyCode { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystem { get; set; }
}

public class CustomerDetail : CustomerListItem
{
    public string? TaxId { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? ProvinceState { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public int PaymentTermsDays { get; set; }
    public Guid? DefaultSaleCurrencyId { get; set; }
    public string? Notes { get; set; }
    public bool IsVendor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}

/// <summary>Body of POST / PUT /api/customers. CreditLimit is applied only with FEATURE_CREDIT_MANAGEMENT (ignored otherwise).</summary>
public class CustomerUpsert
{
    public string? Name { get; set; }
    public string? CustomerType { get; set; }
    public string? Phone { get; set; }
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? TaxId { get; set; }
    public decimal? CreditLimit { get; set; }
    public int? PaymentTermsDays { get; set; }
    public Guid? DefaultSaleCurrencyId { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? ProvinceState { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? Notes { get; set; }
}

public class CustomerStatusRequest
{
    public bool IsActive { get; set; }
}

public class CustomerListFilter
{
    public string? Search { get; set; }
    public string? Type { get; set; }
    /// <summary>ACTIVE | INACTIVE; anything else = both.</summary>
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    /// <summary>code | name | customerType | creditLimit | balance | status (default name).</summary>
    public string? SortField { get; set; }
    /// <summary>asc | desc (also accepts 1 / -1).</summary>
    public string? SortOrder { get; set; }
}

public record CustomerBalanceModel(decimal Balance, string? CurrencyCode, decimal Overdue);

public class CustomerSyncItem : CustomerListItem
{
    public DateTime ModifiedAt { get; set; }
}

public class CustomerSyncResponse
{
    public DateTime ServerTime { get; set; }
    public bool HasMore { get; set; }
    public List<CustomerSyncItem> Customers { get; set; } = [];
}
