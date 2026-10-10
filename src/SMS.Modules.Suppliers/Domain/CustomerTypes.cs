using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Domain;

/// <summary>A37 D-13 — the customer master's closed vocabulary and its invariants (CUST-01, CUST-04).</summary>
internal static class CustomerTypes
{
    public const string WalkIn     = "WALK_IN";
    public const string Individual = "INDIVIDUAL";
    public const string Company    = "COMPANY";
    public const string Employee   = "EMPLOYEE";

    public static readonly IReadOnlyList<string> All = [WalkIn, Individual, Company, Employee];

    /// <summary>The per-organization walk-in customer's code (CUST-01).</summary>
    public const string WalkInCode = "C-WALKIN";
    public const string WalkInName = "Walk-in Customer";
    /// <summary>Prefix of the codes the customer API hands out (CUST-02): C-00001, C-00002…</summary>
    public const string CodePrefix = "C-";

    public const string MsgWalkInDeactivate = "The walk-in customer cannot be deactivated.";
    public const string MsgWalkInDelete     = "The walk-in customer cannot be deleted.";
    public const string MsgWalkInRetype     = "The walk-in customer cannot be changed to another type.";
    public const string MsgWalkInCredit     = "Walk-in customers cannot have a credit limit.";

    /// <summary>
    /// Checked on every save of a partner, whichever surface (customers, partners, legacy suppliers) made the change, so
    /// the walk-in customer's protections cannot be bypassed through an older endpoint.
    /// </summary>
    internal static void EnsureInvariants(BusinessPartner p)
    {
        if (p.CustomerType == WalkIn && p.CreditLimit is > 0m) throw new BadRequestException(MsgWalkInCredit);
        if (!p.IsSystem) return;
        if (p.IsDelete) throw new BadRequestException(MsgWalkInDelete);
        if (!p.IsActive) throw new BadRequestException(MsgWalkInDeactivate);
        if (!p.IsCustomer || p.CustomerType != WalkIn) throw new BadRequestException(MsgWalkInRetype);
    }
}
