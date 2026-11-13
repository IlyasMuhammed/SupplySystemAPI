using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Hrm.Services.Life;

public interface IExpenseCategoryService
{
    Task<IReadOnlyList<ExpenseCategoryModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<ExpenseCategoryModel> CreateAsync(HrmCaller caller, ExpenseCategoryUpsert request, CancellationToken ct = default);
    Task<ExpenseCategoryModel> UpdateAsync(HrmCaller caller, Guid uuid, ExpenseCategoryUpsert request, CancellationToken ct = default);
    Task<ExpenseCategoryModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface IExpenseClaimService
{
    Task<PaginatedResponse<ExpenseClaimModel>> ListAsync(HrmCaller caller, string? status, Guid? employeeUuid, int page, int pageSize, CancellationToken ct = default);
    Task<ExpenseClaimModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<IReadOnlyList<ExpenseClaimModel>> MineAsync(HrmCaller caller, CancellationToken ct = default);
    Task<ExpenseClaimModel> CreateAsync(HrmCaller caller, ExpenseClaimUpsert request, CancellationToken ct = default);
    Task<ExpenseClaimModel> UpdateAsync(HrmCaller caller, Guid uuid, ExpenseClaimUpsert request, CancellationToken ct = default);
    Task<ExpenseClaimModel> SubmitAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ExpenseClaimModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<ExpenseClaimModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<ExpenseClaimModel> PayAsync(HrmCaller caller, Guid uuid, LifePayRequest request, CancellationToken ct = default);
    Task<ExpenseClaimModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class ExpenseCategoryService : HrmServiceBase, IExpenseCategoryService
{
    public ExpenseCategoryService(HrmDbContext db) : base(db) { }

    private static ExpenseCategoryModel Map(ExpenseCategory c) => new()
    {
        Uuid = c.Uuid, Code = c.Code, Name = c.Name, AccountCode = c.AccountCode, AccountRole = c.AccountRole, DefaultTaxCode = c.DefaultTaxCode, RequiresReceipt = c.RequiresReceipt,
        ReceiptThreshold = c.ReceiptThreshold, RatePerUnit = c.RatePerUnit, UnitName = c.UnitName, IsActive = c.IsActive
    };

    private Task<ExpenseCategory?> FindAsync(Guid org, Guid uuid, CancellationToken ct) => Db.ExpenseCategories.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct);

    private static void Apply(ExpenseCategory row, ExpenseCategoryUpsert r)
    {
        row.Code = HrmValidate.Code(r.Code);
        row.Name = HrmValidate.Name(r.Name);
        row.AccountCode = HrmValidate.Optional(r.AccountCode, 20, "Account code");
        row.AccountRole = HrmValidate.Optional(r.AccountRole, 50, "Account role");
        if (row.AccountRole is { } role && !role.StartsWith("DEFAULT_", StringComparison.Ordinal)) throw new BadRequestException("An account role is one of the DEFAULT_ accounting keys.");
        row.DefaultTaxCode = HrmValidate.Optional(r.DefaultTaxCode, 20, "Tax code");
        row.RequiresReceipt = r.RequiresReceipt;
        if (r.ReceiptThreshold is < 0 || r.RatePerUnit is <= 0) throw new BadRequestException("The threshold and rate cannot be negative.");
        row.ReceiptThreshold = r.ReceiptThreshold;
        row.RatePerUnit = r.RatePerUnit;
        row.UnitName = HrmValidate.Optional(r.UnitName, 20, "Unit");
    }

    public async Task<IReadOnlyList<ExpenseCategoryModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default) =>
        (await Db.ExpenseCategories.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == caller.OrganizationId && (includeInactive || c.IsActive)).OrderBy(c => c.Name).ToListAsync(ct)).Select(Map).ToList();

    private async Task CheckCodeAsync(Guid org, string code, int id, CancellationToken ct)
    {
        if (await Db.ExpenseCategories.IgnoreQueryFilters().AnyAsync(c => c.OrganizationId == org && c.Code == code && c.Id != id, ct))
            throw HrmErrors.Conflict("EXPENSE_CATEGORY_CODE_TAKEN", $"A category with code {code} already exists.");
    }

    public async Task<ExpenseCategoryModel> CreateAsync(HrmCaller caller, ExpenseCategoryUpsert request, CancellationToken ct = default)
    {
        var row = new ExpenseCategory();
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, 0, ct);
        Stamp(row, caller, isNew: true);
        Db.ExpenseCategories.Add(row);
        await SaveAsync(ct);
        return Map(row);
    }

    public async Task<ExpenseCategoryModel> UpdateAsync(HrmCaller caller, Guid uuid, ExpenseCategoryUpsert request, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Expense category", uuid);
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, row.Id, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This category");
        return Map(row);
    }

    public async Task<ExpenseCategoryModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Expense category", uuid);
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This category");
        return Map(row);
    }
}

/// <summary>Claim lifecycle. Depends on the workflow engine, so it can never itself be a status handler: the engine depends on every handler (DI cycle).</summary>
internal sealed class ExpenseClaimService : LifeServiceBase, IExpenseClaimService
{
    private const string ClaimPayable = "DEFAULT_EXPENSE_CLAIM_PAYABLE";

    private readonly IHrmScopeService _scope;
    private readonly IHrmEmployeeDirectory _directory;
    private readonly IHrmApprovalGateway _approvals;
    private readonly IWorkflowActionService _workflow;
    private readonly ExpenseClaimDecisions _decisions;
    private readonly IHrmAttachmentGuard? _attachments;

    public ExpenseClaimService(HrmDbContext db, IHrmScopeService scope, IHrmEmployeeDirectory directory, IHrmApprovalGateway approvals, IWorkflowActionService workflow, ExpenseClaimDecisions decisions, IHrmPostingHelper posting,
        ICurrencyService? currency = null, IOrgCurrencyLookup? currencies = null, IHrmAttachmentGuard? attachments = null) : base(db, posting, currency, currencies)
    {
        _scope = scope;
        _directory = directory;
        _approvals = approvals;
        _workflow = workflow;
        _decisions = decisions;
        _attachments = attachments;
    }



    private IQueryable<ExpenseClaim> Query(Guid org) => Db.ExpenseClaims.IgnoreQueryFilters().Include(c => c.Employee).Include(c => c.PaidInRun).Include(c => c.Lines).ThenInclude(l => l.Category).Where(c => c.OrganizationId == org);

    private static ExpenseClaimModel Map(ExpenseClaim c) => new()
    {
        Uuid = c.Uuid, ClaimNumber = c.ClaimNumber, EmployeeUuid = c.Employee!.Uuid, EmployeeName = c.Employee.FullName, ClaimDate = c.ClaimDate, Status = c.Status, PaymentMode = c.PaymentMode, SettlementMethod = c.SettlementMethod,
        CurrencyCode = c.CurrencyCode, TotalAmount = c.TotalAmount, TotalTax = c.TotalTax, Description = c.Description, ApprovalUuid = c.ApprovalUuid, SubmittedAt = c.SubmittedAt, DecidedByName = c.DecidedByName, DecidedAt = c.DecidedAt,
        DecisionNote = c.DecisionNote, PaidOn = c.PaidOn, PaidInRunNumber = c.PaidInRun?.RunNumber, PaidFromAccount = c.PaidFromAccount, PaymentReference = c.PaymentReference, PostingState = c.PostingState,
        PaymentPostingState = c.PaymentPostingState, PostingNote = c.PostingNote,
        Lines = c.Lines.OrderBy(l => l.Id).Select(l => new ExpenseLineModel
        {
            Uuid = l.Uuid, CategoryUuid = l.Category!.Uuid, CategoryName = l.Category.Name, ExpenseDate = l.ExpenseDate, Description = l.Description, Amount = l.Amount, Quantity = l.Quantity, TaxCode = l.TaxCode, TaxAmount = l.TaxAmount, CostCenterCode = l.CostCenterCode, AttachmentUuid = l.AttachmentUuid
        }).ToList()
    };

    private async Task<ExpenseClaim> RequireAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Query(caller.OrganizationId).FirstOrDefaultAsync(c => c.Uuid == uuid, ct) ?? throw new NotFoundException("Expense claim", uuid);
        // The owner always sees their own claim; everyone else through scope, with a role on it.
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId && caller.Has(PermissionCodes.HRM_SELF_SERVICE)) return row;
        if (!caller.HasAny(PermissionCodes.HRM_EXPENSE_VIEW, PermissionCodes.HRM_EXPENSE_APPROVE, PermissionCodes.HRM_EXPENSE_PAY) || !await _scope.CanAccessAsync(caller, row.EmployeeId, ct))
            throw new NotFoundException("Expense claim", uuid);
        return row;
    }

    public async Task<PaginatedResponse<ExpenseClaimModel>> ListAsync(HrmCaller caller, string? status, Guid? employeeUuid, int page, int pageSize, CancellationToken ct = default)
    {
        var scope = await _scope.GetAsync(caller, ct);
        var people = _scope.Apply(Db.Employees, caller, scope).Select(e => e.Id);
        var query = Query(caller.OrganizationId).AsNoTracking().Where(c => people.Contains(c.EmployeeId));
        if (!string.IsNullOrWhiteSpace(status)) { var s = HrmValidate.OneOf(status, ExpenseClaimStatuses.All, "status"); query = query.Where(c => c.Status == s); }
        if (employeeUuid is { } e) query = query.Where(c => c.Employee!.Uuid == e);
        (page, pageSize) = LifePaging.Normalize(page, pageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return LifePaging.Page(rows.Select(Map).ToList(), total, page, pageSize);
    }

    public async Task<ExpenseClaimModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) => Map(await RequireAsync(caller, uuid, ct));

    public async Task<IReadOnlyList<ExpenseClaimModel>> MineAsync(HrmCaller caller, CancellationToken ct = default)
    {
        var me = await _scope.RequireOwnEmployeeAsync(caller, ct);
        return (await Query(caller.OrganizationId).AsNoTracking().Where(c => c.EmployeeId == me.Id).OrderByDescending(c => c.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    private async Task ApplyAsync(HrmCaller caller, ExpenseClaim row, ExpenseClaimUpsert r, HrmSettings settings, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        row.ClaimDate = r.ClaimDate ?? HrmValidate.Today;
        row.PaymentMode = HrmValidate.OneOf(r.PaymentMode ?? ExpensePaymentModes.OwnAccount, ExpensePaymentModes.All, "paymentMode");
        row.SettlementMethod = HrmValidate.OneOf(r.SettlementMethod ?? settings.DefaultExpenseSettlement, ExpenseSettlementMethods.All, "settlementMethod");
        row.PaidFromAccount = HrmValidate.OneOf(r.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount");
        row.Description = HrmValidate.Optional(r.Description, 500, "Description");

        var terms = await _directory.GetTermsAsync(org, row.EmployeeId, row.ClaimDate, ct);
        var shares = terms is null ? [] : await _directory.GetCostCenterSharesAsync(org, terms.RecordId, PayrollCostCenterModes.ByDepartment, ct);
        var defaultCenter = shares.Count > 0 ? shares[0].CostCenterCode : null;

        var uuids = r.Lines.Select(l => l.CategoryUuid).Distinct().ToList();
        var categories = await Db.ExpenseCategories.IgnoreQueryFilters().Where(c => c.OrganizationId == org && uuids.Contains(c.Uuid)).ToDictionaryAsync(c => c.Uuid, ct);
        var centers = r.Lines.Where(l => l.CostCenterCode is not null).Select(l => l.CostCenterCode!.Trim().ToUpperInvariant()).Distinct().ToList();
        var known = await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && c.IsActive && centers.Contains(c.Code)).Select(c => c.Code).ToListAsync(ct);

        var onRow = row.Lines.Select(l => l.AttachmentUuid).ToArray(); // a resaved draft keeps its files
        var lines = new List<ExpenseLine>();
        foreach (var l in r.Lines)
        {
            var receipt = await _attachments.CheckedAsync(caller, l.AttachmentUuid, HrmAttachmentCodes.ExpenseClaim, onRow);
            if (!categories.TryGetValue(l.CategoryUuid, out var category)) throw new NotFoundException("Expense category", l.CategoryUuid);
            if (!category.IsActive) throw new BadRequestException($"The category {category.Name} is inactive.");
            if (l.ExpenseDate == default) throw new BadRequestException("Each line needs a date.");
            decimal amount;
            if (category.RatePerUnit is { } rate)
            {
                if (l.Quantity is not > 0) throw new BadRequestException($"{category.Name} is paid by the {category.UnitName ?? "unit"}: enter the quantity.");
                amount = LifeMoney.Round(l.Quantity.Value * rate); // Rate-based: the system's figure, whatever was sent.
            }
            else amount = l.Amount;
            if (amount <= 0 || amount != LifeMoney.Round(amount)) throw new BadRequestException("A line amount must be positive, with at most 2 decimals.");
            if (l.TaxAmount < 0 || l.TaxAmount > amount || l.TaxAmount != LifeMoney.Round(l.TaxAmount)) throw new BadRequestException("The tax on a line is part of its amount, with at most 2 decimals.");
            var taxCode = HrmValidate.Optional(l.TaxCode, 20, "Tax code") ?? category.DefaultTaxCode;
            if (l.TaxAmount > 0 && taxCode is null) throw new BadRequestException("A line with tax needs a tax code.");
            var center = l.CostCenterCode?.Trim().ToUpperInvariant();
            if (center is not null && !known.Contains(center)) throw HrmErrors.Bad("COST_CENTER_INACTIVE", $"{center} is not an active cost center.");
            var line = new ExpenseLine
            {
                OrganizationId = org, CategoryId = category.Id, Category = category, ExpenseDate = l.ExpenseDate, Description = HrmValidate.Optional(l.Description, 500, "Description"), Amount = amount, Quantity = l.Quantity,
                TaxCode = taxCode, TaxAmount = l.TaxAmount, CostCenterCode = center ?? defaultCenter, AttachmentUuid = receipt
            };
            Stamp(line, caller, isNew: true);
            lines.Add(line);
        }
        row.Lines = lines;
        row.TotalAmount = lines.Sum(l => l.Amount);
        row.TotalTax = lines.Sum(l => l.TaxAmount);
    }

    public async Task<ExpenseClaimModel> CreateAsync(HrmCaller caller, ExpenseClaimUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var me = await _scope.RequireOwnEmployeeAsync(caller, ct);
        var settings = await _directory.GetSettingsAsync(org, ct);
        var row = new ExpenseClaim { EmployeeId = me.Id, CurrencyCode = await BaseCurrencyAsync(org, ct) };
        await ApplyAsync(caller, row, request, settings, ct);
        Stamp(row, caller, isNew: true);
        Db.ExpenseClaims.Add(row);
        await SaveNumberedAsync($"EXP-{HrmValidate.Today.Year}", Db.ExpenseClaims.IgnoreQueryFilters().Where(c => c.OrganizationId == org).Select(c => c.ClaimNumber), n => row.ClaimNumber = n, ct);
        return await GetAsync(caller, row.Uuid, ct);
    }

    public async Task<ExpenseClaimModel> UpdateAsync(HrmCaller caller, Guid uuid, ExpenseClaimUpsert request, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (await _scope.OwnEmployeeIdAsync(caller, ct) != row.EmployeeId) throw new ForbiddenException("Only the employee can change their claim.");
        if (row.Status != ExpenseClaimStatuses.Draft) throw HrmErrors.Conflict("EXPENSE_NOT_DRAFT", "Only a draft claim can be changed.");
        var old = row.Lines.ToList();
        var settings = await _directory.GetSettingsAsync(caller.OrganizationId, ct);
        await ApplyAsync(caller, row, request, settings, ct);
        Db.ExpenseLines.RemoveRange(old);
        Stamp(row, caller);
        await SaveAsync(ct, "This claim");
        return Map(row);
    }

    /// <summary>Task 387: a line needs a receipt when its category requires one and the amount is above the category's threshold, else the organization's. No threshold = always.</summary>
    internal static void CheckReceipts(ExpenseClaim claim, decimal? defaultThreshold)
    {
        foreach (var line in claim.Lines)
        {
            var category = line.Category!;
            if (category.RequiresReceipt && line.AttachmentUuid is null && line.Amount > (category.ReceiptThreshold ?? defaultThreshold ?? 0m))
                throw HrmErrors.Bad("EXPENSE_RECEIPT_REQUIRED", $"{category.Name} of {line.Amount:N2} on {line.ExpenseDate:yyyy-MM-dd} needs a receipt.");
        }
    }

    public async Task<ExpenseClaimModel> SubmitAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (await _scope.OwnEmployeeIdAsync(caller, ct) != row.EmployeeId) throw new ForbiddenException("Only the employee can submit their claim.");
        if (row.Status != ExpenseClaimStatuses.Draft) throw HrmErrors.Conflict("EXPENSE_NOT_DRAFT", "Only a draft claim can be submitted.");
        if (row.Lines.Count == 0) throw HrmErrors.Bad("EXPENSE_NO_LINES", "Add at least one expense.");
        var settings = await _directory.GetSettingsAsync(caller.OrganizationId, ct);
        CheckReceipts(row, settings.DefaultReceiptThreshold);

        // Status first, so a stale double submit dies on RowVersion before a second approval exists. Undone if no approver can be found.
        row.Status = ExpenseClaimStatuses.Submitted;
        row.SubmittedAt = DateTime.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This claim");
        // The claim is already Submitted: finish (open the approval, link it) even if the caller disconnects, or it would sit Submitted with no approval.
        var finish = CancellationToken.None;
        try
        {
            row.ApprovalUuid = await _approvals.SubmitAsync(caller, HrmWorkflowCodes.ExpenseClaim, row.Uuid, row.ClaimNumber, row.EmployeeId, PermissionCodes.HRM_EXPENSE_APPROVE, row.TotalAmount, finish);
        }
        catch
        {
            row.Status = ExpenseClaimStatuses.Draft;
            row.SubmittedAt = null;
            await SaveAsync(finish, "This claim");
            throw;
        }
        await SaveAsync(finish, "This claim");
        return Map(row);
    }

    public async Task<ExpenseClaimModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != ExpenseClaimStatuses.Submitted) throw HrmErrors.Conflict("EXPENSE_NOT_SUBMITTED", "Only a submitted claim can be approved.");
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot approve your own claim.");
        ExpenseClaimDecisions.CheckMapped(row);
        _decisions.SetDecider(caller.UserIdOrNull, caller.UserName, null);
        // The engine checks the caller is an approver of the current step and calls back UpdateStatusAsync("APPROVED") on the last one.
        await _workflow.ApproveAsync(new ApproveCommand { ApprovalUUID = row.ApprovalUuid!.Value, ApprovedBy = caller.UserId });
        return await GetAsync(caller, uuid, ct);
    }

    public async Task<ExpenseClaimModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != ExpenseClaimStatuses.Submitted) throw HrmErrors.Conflict("EXPENSE_NOT_SUBMITTED", "Only a submitted claim can be rejected.");
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot reject your own claim.");
        var reason = HrmValidate.Name(note, 500, "Note");
        _decisions.SetDecider(caller.UserIdOrNull, caller.UserName, reason);
        await _workflow.RejectAsync(new RejectCommand { ApprovalUUID = row.ApprovalUuid!.Value, RejectedBy = caller.UserId, RejectionReason = reason });
        return await GetAsync(caller, uuid, ct);
    }

    /// <summary>TS-G9-002.</summary>
    internal static FinancialTransaction BuildPayment(ExpenseClaim claim, string? center)
    {
        var label = $"Expense claim payment {claim.ClaimNumber}";
        return new FinancialTransaction
        {
            DocumentType = FinDocType.ExpensePayment, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = claim.Uuid, SourceDocumentNumber = claim.ClaimNumber,
            TransactionDate = LifeMoney.Date(claim.PaidOn!.Value), CurrencyCode = claim.CurrencyCode, Description = label,
            Lines = LifeMoney.Number(
            [
                new() { AccountRole = ClaimPayable, Debit = claim.TotalAmount, CostCenter = center, Label = label },
                new() { AccountRole = LifeMoney.CashRole(claim.PaidFromAccount), Credit = claim.TotalAmount, Label = label }
            ])
        };
    }

    private async Task ApplyPaymentPostingAsync(ExpenseClaim row, CancellationToken ct)
    {
        var outcome = await Posting.PostAsync(BuildPayment(row, await _decisions.PayrollCenterAsync(row.OrganizationId, ct)), ct);
        row.PaymentPostingState = outcome.State;
        row.PostingNote = outcome.Note;
        await SaveAsync(ct, "This claim");
    }

    public async Task<ExpenseClaimModel> PayAsync(HrmCaller caller, Guid uuid, LifePayRequest request, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status != ExpenseClaimStatuses.Approved) throw HrmErrors.Conflict("EXPENSE_NOT_PAYABLE", "Only an approved, unpaid claim can be paid.");
        if (row.PaymentMode != ExpensePaymentModes.OwnAccount || row.SettlementMethod != ExpenseSettlementMethods.SeparatePayment)
            throw HrmErrors.Conflict("EXPENSE_NOT_PAYABLE", "This claim is settled through payroll.");
        if (row.PostingState is HrmPostingStates.Failed or HrmPostingStates.Pending)
            throw HrmErrors.Conflict("EXPENSE_NOT_PAYABLE", "The claim's expense posting has not gone through, so there is no payable to clear. Retry it first.");
        if (request.PaidOn == default) throw new BadRequestException("The payment date is required.");
        row.Status = ExpenseClaimStatuses.Paid;
        row.PaidOn = request.PaidOn;
        row.PaidFromAccount = HrmValidate.OneOf(request.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount");
        row.PaymentReference = HrmValidate.Optional(request.Reference, 100, "Reference");
        row.PaymentPostingState = HrmPostingStates.Pending;
        Stamp(row, caller);
        await SaveAsync(ct, "This claim"); // RowVersion: of two concurrent payments one gets 409 and posts nothing.
        await ApplyPaymentPostingAsync(row, ct);
        return Map(row);
    }

    public async Task<ExpenseClaimModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await RequireAsync(caller, uuid, ct);
        if (row.Status is not (ExpenseClaimStatuses.Approved or ExpenseClaimStatuses.Paid)) throw HrmErrors.Conflict("EXPENSE_NOT_POSTABLE", "This claim has not been approved.");
        await _decisions.ApplyExpensePostingAsync(row, ct);
        if (row.PaymentPostingState != HrmPostingStates.None && row.PostingState != HrmPostingStates.Failed) await ApplyPaymentPostingAsync(row, ct);
        return Map(row);
    }
}

/// <summary>
/// What a workflow decision does to a claim: the EXPENSE_CLAIM status handler, the approval's posting and the adjustment. No dependency on the workflow
/// engine, which depends on every handler. Scoped, so a route's approve and the engine's callback in that request share the decider.
/// </summary>
internal sealed class ExpenseClaimDecisions : LifeServiceBase, IDocumentStatusHandler
{
    private const string ClaimPayable = "DEFAULT_EXPENSE_CLAIM_PAYABLE", InputTax = "DEFAULT_TAX_RECEIVABLE", Reimbursement = "EXPREIMB";
    private readonly IHrmEmployeeDirectory _directory;
    private readonly IHttpContextAccessor? _http;
    private (int? Id, string? Name, string? Note)? _decider;

    private readonly LifeUserNames? _names;

    public ExpenseClaimDecisions(HrmDbContext db, IHrmEmployeeDirectory directory, IHrmPostingHelper posting, IHttpContextAccessor? http = null, LifeUserNames? names = null) : base(db, posting, null, null)
    {
        _names = names;
        _directory = directory;
        _http = http;
    }

    public string InterfaceCode => HrmWorkflowCodes.ExpenseClaim;
    internal void SetDecider(int? id, string? name, string? note) => _decider = (id, name, note);

    /// <summary>A line's Dr: the category's account code, else its role. Unmapped → 409, because nothing else would catch it before a target does.</summary>
    internal static void CheckMapped(ExpenseClaim claim)
    {
        var unmapped = claim.Lines.Select(l => l.Category!).Where(c => c.AccountCode is null && c.AccountRole is null).Select(c => c.Name).Distinct().ToList();
        if (unmapped.Count > 0) throw HrmErrors.Conflict("EXPENSE_CATEGORY_UNMAPPED", $"No account is set for: {string.Join(", ", unmapped)}. An accountant must map the category first.");
    }

    public async Task<string?> GetStatusAsync(Guid documentId) =>
        await Db.ExpenseClaims.IgnoreQueryFilters().AsNoTracking().Where(c => c.Uuid == documentId).Select(c => c.Status).FirstOrDefaultAsync();

    /// <summary>The engine's callback. Only the tenant it runs under: the row's own organization, and every status arrives at most once in effect.</summary>
    public async Task UpdateStatusAsync(Guid documentId, string newStatus)
    {
        var row = await Db.ExpenseClaims.IgnoreQueryFilters().Include(c => c.Employee).Include(c => c.Lines).ThenInclude(l => l.Category).FirstOrDefaultAsync(c => c.Uuid == documentId);
        if (row is null || row.Status != ExpenseClaimStatuses.Submitted) return;
        var decider = _decider ?? ((int?, string?, string?))(CurrentUser() ?? (null, null, null));
        var deciderName = _names is null ? decider.Item2 : await _names.NameAsync(row.OrganizationId, decider.Item1, decider.Item2);
        switch (newStatus.ToUpperInvariant())
        {
            case "APPROVED": await ApproveInternalAsync(row, decider.Item1, deciderName); break;
            case "REJECTED":
                row.Status = ExpenseClaimStatuses.Rejected;
                row.DecidedBy = decider.Item1; row.DecidedByName = deciderName; row.DecidedAt = DateTime.UtcNow; row.DecisionNote = decider.Item3;
                await SaveAsync(default, "This claim");
                break;
            case "DRAFT" or "CANCELLED" or "RECALLED":
                row.Status = ExpenseClaimStatuses.Draft;
                row.ApprovalUuid = null; row.SubmittedAt = null;
                await SaveAsync(default, "This claim");
                break;
        }
    }

    private (int?, string?, string?)? CurrentUser()
    {
        var user = _http?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return null;
        return (user.GetUserId(), user.FindFirst("name")?.Value, null);
    }

    private async Task ApproveInternalAsync(ExpenseClaim row, int? by, string? byName)
    {
        var org = row.OrganizationId;
        var reimburse = row.PaymentMode == ExpensePaymentModes.OwnAccount;
        await Db.InTransactionAsync(async () =>
        {
            row.Status = reimburse ? ExpenseClaimStatuses.Approved : ExpenseClaimStatuses.Paid;
            row.DecidedBy = by; row.DecidedByName = byName; row.DecidedAt = DateTime.UtcNow;
            row.PostingState = HrmPostingStates.Pending;
            if (!reimburse) row.PaidOn = HrmValidate.Today; // Already paid by company card: Cr bank in the same posting, nothing to reimburse.
            await SaveAsync(default, "This claim");

            if (reimburse && row.SettlementMethod == ExpenseSettlementMethods.WithPayroll
                && !await Db.PayrollAdjustments.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && a.ExpenseClaimId == row.Id))
            {
                Db.PayrollAdjustments.Add(new PayrollAdjustment
                {
                    OrganizationId = org, EmployeeId = row.EmployeeId, ComponentCode = Reimbursement, Amount = row.TotalAmount, Reason = $"Expense claim {row.ClaimNumber}", ExpenseClaimId = row.Id, CreatedBy = by, CreatedByName = byName
                });
                await SaveAsync(default, "This claim");
            }
        });
        await ApplyExpensePostingAsync(row, default);
    }

    /// <summary>TS-G9-001. Dr expense net of tax per account × cost center, Dr input tax per tax code; Cr the claim payable, or bank/cash when the company paid. No employee data (G-11).</summary>
    internal static FinancialTransaction BuildExpense(ExpenseClaim claim, string? creditCenter)
    {
        CheckMapped(claim);
        var label = $"Expense claim {claim.ClaimNumber}";
        var lines = new List<FinancialTransactionLine>();
        foreach (var g in claim.Lines.GroupBy(l => new { l.Category!.AccountCode, Role = l.Category.AccountCode is null ? l.Category.AccountRole : null, l.Category.Code, l.CostCenterCode }).OrderBy(g => g.Key.Code).ThenBy(g => g.Key.CostCenterCode))
            lines.Add(new() { AccountCode = g.Key.AccountCode, AccountRole = g.Key.Role, Debit = g.Sum(l => l.Amount - l.TaxAmount), CostCenter = g.Key.CostCenterCode, Label = $"{label} {g.Key.Code}" });
        foreach (var g in claim.Lines.Where(l => l.TaxAmount > 0).GroupBy(l => l.TaxCode).OrderBy(g => g.Key))
            lines.Add(new() { AccountRole = InputTax, Debit = g.Sum(l => l.TaxAmount), TaxCode = g.Key, TaxAmount = g.Sum(l => l.TaxAmount), Label = $"{label} tax" });
        lines.Add(new()
        {
            AccountRole = claim.PaymentMode == ExpensePaymentModes.CompanyAccount ? LifeMoney.CashRole(claim.PaidFromAccount) : ClaimPayable,
            Credit = claim.TotalAmount, CostCenter = creditCenter, Label = label
        });
        return new FinancialTransaction
        {
            DocumentType = FinDocType.Expense, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = claim.Uuid, SourceDocumentNumber = claim.ClaimNumber,
            TransactionDate = LifeMoney.Date(claim.ClaimDate), CurrencyCode = claim.CurrencyCode, Description = label, Lines = LifeMoney.Number(lines)
        };
    }

    internal async Task<string?> PayrollCenterAsync(Guid org, CancellationToken ct)
    {
        var settings = await _directory.GetSettingsAsync(org, ct);
        return settings.PayrollCostCenterId is { } id
            ? await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == id && c.OrganizationId == org).Select(c => c.Code).FirstOrDefaultAsync(ct)
            : null;
    }

    internal async Task ApplyExpensePostingAsync(ExpenseClaim row, CancellationToken ct)
    {
        var center = await PayrollCenterAsync(row.OrganizationId, ct);
        // Reached through the inbox a category can still be unmapped: the approval stands, the posting is Failed with the reason, and retry-posting works once it is mapped.
        var outcome = await PostSafelyAsync(() => Task.FromResult(BuildExpense(row, center)), ct);
        row.PostingState = outcome.State;
        row.PostingNote = outcome.Note;
        await SaveAsync(ct, "This claim");
    }

}
/// <summary>
/// TS-G9-003: WithPayroll claims. The EXPREIMB adjustment is reserved through its RunId with one conditional UPDATE, so two runs cannot both pay it;
/// the claim turns Paid at Finalize (only while Approved, so a claim is paid once). The run's own posting clears the payable (EXPREIMB posts to it).
/// </summary>
internal sealed class ExpenseReimbursementInputProvider : IPayrollInputProvider
{
    private static readonly string[] LiveRun = [PayrollRunStatuses.Draft, PayrollRunStatuses.Calculated, PayrollRunStatuses.UnderReview, PayrollRunStatuses.Approved];
    private readonly HrmDbContext _db;
    public ExpenseReimbursementInputProvider(HrmDbContext db) => _db = db;

    private IQueryable<PayrollAdjustment> Open(Guid org, int employeeId) => _db.PayrollAdjustments.IgnoreQueryFilters()
        .Where(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.ExpenseClaimId != null && a.AppliedInRunId == null && a.ExpenseClaim!.Status == ExpenseClaimStatuses.Approved);

    public async Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var free = Open(organizationId, employeeId).Where(a => a.RunId == null || a.RunId == runId || !LiveRun.Contains(a.Run!.Status));
        if (_db.Database.IsRelational())
            await free.ExecuteUpdateAsync(s => s.SetProperty(a => a.RunId, (int?)runId), ct);
        else
        {
            foreach (var row in await free.ToListAsync(ct)) row.RunId = runId;
            await _db.SaveChangesAsync(ct);
        }
        var held = await Open(organizationId, employeeId).AsNoTracking().Where(a => a.RunId == runId).OrderBy(a => a.Id).Select(a => new { a.ComponentCode, a.Amount, a.Reason }).ToListAsync(ct);
        return held.Select(a => new PayrollInput(a.ComponentCode, a.Amount, a.Reason)).ToList();
    }

    public async Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default)
    {
        var run = await _db.PayrollRuns.IgnoreQueryFilters().AsNoTracking().Where(r => r.Id == runId && r.OrganizationId == organizationId).Select(r => new { r.PayDate }).FirstAsync(ct);
        var held = Open(organizationId, employeeId).Where(a => a.RunId == runId);
        var claimIds = await held.Select(a => a.ExpenseClaimId!.Value).ToListAsync(ct);
        var claims = _db.ExpenseClaims.IgnoreQueryFilters().Where(c => c.OrganizationId == organizationId && claimIds.Contains(c.Id) && c.Status == ExpenseClaimStatuses.Approved);
        if (_db.Database.IsRelational())
        {
            await held.ExecuteUpdateAsync(s => s.SetProperty(a => a.AppliedInRunId, (int?)runId), ct);
            await claims.ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ExpenseClaimStatuses.Paid).SetProperty(c => c.PaidInRunId, (int?)runId).SetProperty(c => c.PaidOn, (DateOnly?)run.PayDate), ct);
            return;
        }
        foreach (var a in await held.ToListAsync(ct)) a.AppliedInRunId = runId;
        foreach (var c in await claims.ToListAsync(ct)) { c.Status = ExpenseClaimStatuses.Paid; c.PaidInRunId = runId; c.PaidOn = run.PayDate; }
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        var claims = _db.ExpenseClaims.IgnoreQueryFilters().Where(c => c.OrganizationId == organizationId && c.PaidInRunId == runId);
        var rows = _db.PayrollAdjustments.IgnoreQueryFilters().Where(a => a.OrganizationId == organizationId && a.ExpenseClaimId != null && (a.RunId == runId || a.AppliedInRunId == runId));
        if (_db.Database.IsRelational())
        {
            await claims.ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ExpenseClaimStatuses.Approved).SetProperty(c => c.PaidInRunId, (int?)null).SetProperty(c => c.PaidOn, (DateOnly?)null), ct);
            await rows.ExecuteUpdateAsync(s => s.SetProperty(a => a.RunId, (int?)null).SetProperty(a => a.AppliedInRunId, (int?)null), ct);
            return;
        }
        foreach (var c in await claims.ToListAsync(ct)) { c.Status = ExpenseClaimStatuses.Approved; c.PaidInRunId = null; c.PaidOn = null; }
        foreach (var a in await rows.ToListAsync(ct)) { a.RunId = null; a.AppliedInRunId = null; }
        await _db.SaveChangesAsync(ct);
    }
}


