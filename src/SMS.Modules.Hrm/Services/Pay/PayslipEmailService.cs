using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Hrm.Services.Pay;

public static class PayslipEmailOutcomes
{
    public const string Sent = "SENT", NoAddress = "NO_ADDRESS", Failed = "SEND_FAILED";
}

/// <summary>One payslip. The address is masked (a***@example.com): the caller already knows whom they asked about.</summary>
public sealed class PayslipEmailResult
{
    public bool Sent { get; set; }
    public string Outcome { get; set; } = PayslipEmailOutcomes.Sent;
    public string? SentTo { get; set; }
}

/// <summary>A whole run. Counts and employee numbers only: no names, no amounts.</summary>
public sealed class PayslipBulkEmailResult
{
    public int Sent { get; set; }
    public int AlreadySent { get; set; }
    public int NoAddress { get; set; }
    public int Failed { get; set; }
    public List<string> NoAddressEmployeeNumbers { get; set; } = [];
    public List<string> FailedEmployeeNumbers { get; set; } = [];
}

public sealed class PayslipBulkEmailRequest
{
    /// <summary>Send again to employees who already got this run's payslip. Default false.</summary>
    public bool Resend { get; set; }
}

public interface IPayslipEmailService
{
    /// <summary>Emails one payslip to the employee's own address. HR (payroll view, in scope) or the employee themselves (self service).</summary>
    Task<PayslipEmailResult> SendAsync(HrmCaller caller, Guid payslipUuid, CancellationToken ct = default);

    /// <summary>Emails every payslip of a finalized run. Needs HRM_PAYSLIP_EMAIL (D-98) and access to all employees.</summary>
    Task<PayslipBulkEmailResult> SendRunAsync(HrmCaller caller, Guid runUuid, PayslipBulkEmailRequest? request, CancellationToken ct = default);
}

/// <summary>
/// A50 D-30 / D-93 — the PayslipSent email. A payslip is the most sensitive document HRM produces, so:
/// <list type="bullet">
/// <item>the only recipient is the employee's own address on file (work email, else personal); there is no "to" in either request;</item>
/// <item>one payslip needs payroll-view access in scope, or self service on one's own; the whole run needs HRM_PAYSLIP_EMAIL and access to all
/// employees (like the bank file), and skips employees already sent unless asked;</item>
/// <item>only a finalized (posted, paid) run; a reversed run's payslips are not sent;</item>
/// <item>nothing identifying goes to a log: no name, no address, no amount, no payslip contents — an outcome and the payslip's uuid at most;</item>
/// <item>each send is written to the sensitive-access log (employee, who, when), as the bank file is;</item>
/// <item>subject and body come from the organization's PayslipSent template (A50), else the built-in text.</item>
/// </list>
/// </summary>
internal sealed class PayslipEmailService : HrmServiceBase, IPayslipEmailService
{
    private const string AuditField = "PAYSLIP_EMAIL";
    private static readonly string[] Issuable = [PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted, PayrollRunStatuses.Paid];

    private readonly IPayslipService _slips;
    private readonly IHrmScopeService _scope;
    private readonly IEmailSender? _sender;
    private readonly IEmailTemplateRenderer? _templates;
    private readonly IOrganizationNameLookup? _names;
    private readonly ILogger _log;

    public PayslipEmailService(HrmDbContext db, IPayslipService slips, IHrmScopeService scope, IEmailSender? sender = null,
        IEmailTemplateRenderer? templates = null, IOrganizationNameLookup? names = null, ILogger<PayslipEmailService>? log = null) : base(db)
    {
        _slips = slips;
        _scope = scope;
        _sender = sender;
        _templates = templates;
        _names = names;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    private sealed record SlipRow(Guid PayslipUuid, int EmployeeId, string EmployeeNumber, string EmployeeName, string? WorkEmail, string? PersonalEmail,
        string RunNumber, string RunStatus, DateOnly PeriodEnd, decimal Net, string Currency, Guid RunUuid, Guid LineUuid);

    /// <summary>The filter goes on the payslip, before the projection: a Where on the projected record does not translate.</summary>
    private IQueryable<SlipRow> Rows(Guid org, System.Linq.Expressions.Expression<Func<Payslip, bool>> where) =>
        Db.Payslips.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org).Where(where)
            .Select(p => new SlipRow(p.Uuid, p.RunLine!.EmployeeId, p.RunLine.Employee!.EmployeeNumber,
                p.RunLine.Employee.FirstName + " " + p.RunLine.Employee.LastName, p.RunLine.Employee.WorkEmail, p.RunLine.Employee.PersonalEmail,
                p.RunLine.Run!.RunNumber, p.RunLine.Run.Status, p.RunLine.Run.PeriodEnd, p.RunLine.Net, p.RunLine.Run.CurrencyCode,
                p.RunLine.Run.Uuid, p.RunLine.Uuid));

    public async Task<PayslipEmailResult> SendAsync(HrmCaller caller, Guid payslipUuid, CancellationToken ct = default)
    {
        // The access rule is the PDF download's: HR in scope, or the employee on their own (not for a reversed run). Anyone else gets a 404.
        var file = await _slips.GetPdfAsync(caller, payslipUuid, ct);
        var row = await Rows(caller.OrganizationId, p => p.Uuid == payslipUuid).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("Payslip", payslipUuid);
        if (!Issuable.Contains(row.RunStatus))
            throw HrmErrors.Conflict("PAYROLL_NOT_FINALIZED", "Payslips are emailed once the run is finalized, and not after it was reversed.");
        if (_sender is null) throw HrmErrors.Conflict("PAYSLIP_EMAIL_UNAVAILABLE", "Email is not configured on this server.");

        var outcome = await DeliverAsync(caller, row, file, ct);
        return outcome;
    }

    public async Task<PayslipBulkEmailResult> SendRunAsync(HrmCaller caller, Guid runUuid, PayslipBulkEmailRequest? request, CancellationToken ct = default)
    {
        // Bulk is its own permission, checked here as well as on the route: a caller must never reach it through another door.
        caller.Require(PermissionCodes.HRM_PAYSLIP_EMAIL, "Emailing a whole run of payslips needs the permission to email payslips.");
        if (!(await _scope.GetAsync(caller, ct)).All) throw new ForbiddenException("Emailing a run needs access to all employees.");
        var org = caller.OrganizationId;

        var run = await Db.PayrollRuns.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == runUuid, ct)
                  ?? throw new NotFoundException("Payroll run", runUuid);
        if (!Issuable.Contains(run.Status)) throw HrmErrors.Conflict("PAYROLL_NOT_FINALIZED", "Payslips are emailed once the run is finalized, and not after it was reversed.");
        if (_sender is null) throw HrmErrors.Conflict("PAYSLIP_EMAIL_UNAVAILABLE", "Email is not configured on this server.");

        await _slips.GenerateAllAsync(caller, runUuid, ct);
        var rows = (await Rows(org, p => p.RunLine!.Run!.Uuid == runUuid).ToListAsync(ct)).OrderBy(r => r.EmployeeNumber, StringComparer.Ordinal).ToList();
        var purpose = PurposeOf(run.RunNumber);
        var already = (await Db.SensitiveAccessLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.Field == AuditField && l.Purpose == purpose).Select(l => l.EmployeeId).Distinct().ToListAsync(ct)).ToHashSet();

        var result = new PayslipBulkEmailResult();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (request?.Resend != true && already.Contains(row.EmployeeId)) { result.AlreadySent++; continue; }
            if (AddressOf(row) is null) { result.NoAddress++; result.NoAddressEmployeeNumbers.Add(row.EmployeeNumber); continue; }

            var file = await _slips.GetForLineAsync(caller, runUuid, row.LineUuid, ct);
            var one = await DeliverAsync(caller, row, file, ct);
            if (one.Sent) result.Sent++;
            else { result.Failed++; result.FailedEmployeeNumbers.Add(row.EmployeeNumber); }
        }
        _log.LogInformation("Payslip run email finished for run {RunUuid}: {Sent} sent, {NoAddress} without an address, {Failed} failed.",
            runUuid, result.Sent, result.NoAddress, result.Failed);
        return result;
    }

    private static string PurposeOf(string runNumber)
    {
        var purpose = "Payslip emailed " + runNumber;
        return purpose.Length > 100 ? purpose[..100] : purpose;
    }

    /// <summary>The employee's own address: work email, else personal; null when neither is a plain valid address.</summary>
    private static string? AddressOf(SlipRow row)
    {
        foreach (var candidate in new[] { row.WorkEmail, row.PersonalEmail })
            if (DocumentEmailAddresses.IsValid(candidate?.Trim())) return candidate!.Trim();
        return null;
    }

    private static string Mask(string address)
    {
        var at = address.IndexOf('@');
        return at <= 0 ? "***" : address[0] + "***" + address[at..];
    }

    private async Task<PayslipEmailResult> DeliverAsync(HrmCaller caller, SlipRow row, PayFile file, CancellationToken ct)
    {
        var to = AddressOf(row);
        if (to is null)
            throw HrmErrors.Unprocessable("PAYSLIP_NO_EMAIL", "This employee has no email address on file. Add one to the employee record first.");

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["EmployeeName"] = row.EmployeeName.Trim(),
            ["PayPeriod"] = row.PeriodEnd.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            ["NetPay"] = row.Net.ToString("N2", CultureInfo.InvariantCulture),
            ["CurrencyCode"] = row.Currency,
            ["CompanyName"] = (_names is null ? null : await _names.GetNameAsync(caller.OrganizationId, ct)) ?? string.Empty,
            ["CompanyEmail"] = string.Empty, ["CompanyPhone"] = string.Empty, ["CompanyAddress"] = string.Empty, ["Message"] = string.Empty
        };
        var rendered = (_templates is null ? null : await _templates.RenderAsync(EmailTemplateTypes.PayslipSent, values, ct)) ?? BuiltIn(values);

        var sent = await _sender!.SendWithResultAsync(to, rendered.Subject, rendered.BodyHtml,
            [new EmailAttachment(file.FileName, file.Content, "application/pdf")], ct) ?? EmailSendResult.Failed("The email sender gave no result.");

        if (!sent.Success)
        {
            // The sender's error can name the address; only the outcome and the payslip's uuid are logged.
            _log.LogWarning("Payslip {PayslipUuid} was not emailed: the sender reported a failure.", row.PayslipUuid);
            return new PayslipEmailResult { Sent = false, Outcome = PayslipEmailOutcomes.Failed };
        }

        var log = new SensitiveAccessLog
        {
            EmployeeId = row.EmployeeId, Field = AuditField, Purpose = PurposeOf(row.RunNumber), UserId = caller.UserIdOrNull, UserName = caller.UserName
        };
        Stamp(log, caller, isNew: true);
        Db.SensitiveAccessLogs.Add(log);
        await Db.SaveChangesAsync(ct);
        _log.LogInformation("Payslip {PayslipUuid} was emailed.", row.PayslipUuid);
        return new PayslipEmailResult { Sent = true, Outcome = PayslipEmailOutcomes.Sent, SentTo = Mask(to) };
    }

    /// <summary>The text used when the organization has no PayslipSent template (Onboarding's built-in wording).</summary>
    internal static RenderedEmail BuiltIn(IReadOnlyDictionary<string, string?> v)
    {
        string E(string key) => WebUtility.HtmlEncode(v.TryGetValue(key, out var s) ? s ?? "" : "");
        string P(string key) => (v.TryGetValue(key, out var s) ? s ?? "" : "").Replace("\r", " ").Replace("\n", " ");
        return new RenderedEmail($"Your payslip for {P("PayPeriod")}",
            $"<p>Dear {E("EmployeeName")},</p><p>Your payslip for {E("PayPeriod")} is attached. Net pay: {E("NetPay")} {E("CurrencyCode")}.</p><p>Regards,<br>{E("CompanyName")}</p>");
    }
}
