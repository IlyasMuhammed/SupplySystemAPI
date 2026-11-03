using SMS.Modules.Hrm.Domain;

namespace SMS.Modules.Hrm.Services;

/// <summary>
/// The seed values, one place. <b>Statutory data is never asserted:</b> tax slabs, EOBI, provident fund and social security rates are
/// written from memory of the public position, IsVerified = false, and payroll Finalize is blocked on them until a qualified payroll
/// consultant has reviewed them and someone with HRM_STATUTORY_VERIFY marks them (G-21). Holiday dates that depend on the moon are
/// astronomical estimates, IsEstimated = true: the government notifies the real date a few days before.
/// </summary>
internal static class HrmSeedData
{
    private const string UnverifiedNote = "UNVERIFIED seed, not professional advice. Have a qualified payroll consultant confirm against the current Finance Act / notification before use.";

    public const string StandardStructureCode = "STD_MONTHLY";

    public static readonly string[] StandardStructureComponents = ["BASIC", "HRENT", "UTIL", "CONV", "OT", "ITAX", "EOBI_EE", "EOBI_ER", "PF_EE", "PF_ER", "LOAN", "ADV", "EXPREIMB", "OTHDED"];

    public static IReadOnlyList<LeaveType> LeaveTypes(Guid org)
    {
        LeaveType T(string code, string name, decimal days, bool paid, decimal? cap, int? attachAfter, string? gender = null, string accrual = LeaveAccrualMethods.Annual) => new()
        {
            OrganizationId = org, Code = code, Name = name, IsPaid = paid, CarryForwardCap = cap, AttachmentAfterDays = attachAfter, GenderRestriction = gender, IsSeeded = true,
            Policies = { new LeavePolicy { OrganizationId = org, Entitlement = days, AccrualMethod = accrual, EffectiveFrom = new DateOnly(2026, 1, 1) } }
        };
        return
        [
            T("ANNUAL",    "Annual Leave",    14m, true,  14m, null),
            T("SICK",      "Sick Leave",      10m, true,  null, 2),
            T("CASUAL",    "Casual Leave",    10m, true,  null, null),
            T("MATERNITY", "Maternity Leave", 90m, true,  null, 1, Genders.Female),
            T("PATERNITY", "Paternity Leave", 30m, true,  null, 1, Genders.Male),
            T("UNPAID",    "Unpaid Leave",     0m, false, null, null, null, LeaveAccrualMethods.None),
        ];
    }

    public static IReadOnlyList<Shift> Shifts(Guid org) =>
    [
        new Shift
        {
            OrganizationId = org, Code = "GENERAL", Name = "General Shift", StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0), BreakMinutes = 60,
            GraceInMinutes = 15, GraceOutMinutes = 0, HalfDayMinutes = 240, OvertimeThresholdMinutes = 30, WeeklyOffDays = 1 | 64, IsDefault = true
        },
    ];

    public static IReadOnlyList<Holiday> PakistanHolidays(Guid org)
    {
        Holiday H(int y, int m, int d, string name, bool estimated = false) => new()
        {
            OrganizationId = org, Date = new DateOnly(y, m, d), Name = name, CountryCode = "PK", IsEstimated = estimated, IsSeeded = true
        };
        var rows = new List<Holiday>();
        foreach (var y in new[] { 2026, 2027 })
        {
            rows.Add(H(y, 2, 5, "Kashmir Day"));
            rows.Add(H(y, 3, 23, "Pakistan Day"));
            rows.Add(H(y, 5, 1, "Labour Day"));
            rows.Add(H(y, 8, 14, "Independence Day"));
            rows.Add(H(y, 11, 9, "Iqbal Day"));
            rows.Add(H(y, 12, 25, "Quaid-e-Azam Day"));
        }
        // Moon-dependent: ESTIMATES. Correct them when the date is notified.
        rows.AddRange(
        [
            H(2026, 3, 21, "Eid ul-Fitr (estimated)", true), H(2026, 3, 22, "Eid ul-Fitr holiday (estimated)", true), H(2026, 3, 23, "Eid ul-Fitr holiday (estimated)", true),
            H(2026, 5, 27, "Eid ul-Adha (estimated)", true), H(2026, 5, 28, "Eid ul-Adha holiday (estimated)", true), H(2026, 5, 29, "Eid ul-Adha holiday (estimated)", true),
            H(2026, 6, 25, "Ashura (estimated)", true), H(2026, 6, 26, "Ashura (estimated)", true),
            H(2026, 8, 26, "Eid Milad un-Nabi (estimated)", true),
            H(2027, 3, 10, "Eid ul-Fitr (estimated)", true), H(2027, 3, 11, "Eid ul-Fitr holiday (estimated)", true), H(2027, 3, 12, "Eid ul-Fitr holiday (estimated)", true),
            H(2027, 5, 17, "Eid ul-Adha (estimated)", true), H(2027, 5, 18, "Eid ul-Adha holiday (estimated)", true), H(2027, 5, 19, "Eid ul-Adha holiday (estimated)", true),
            H(2027, 6, 15, "Ashura (estimated)", true), H(2027, 6, 16, "Ashura (estimated)", true),
            H(2027, 8, 15, "Eid Milad un-Nabi (estimated)", true),
        ]);
        return rows;
    }

    /// <summary>§6.1 — names and posting roles only; values are the assignment's or the statutory tables'.</summary>
    public static IReadOnlyList<SalaryComponent> Components(Guid org)
    {
        var order = 0;
        SalaryComponent C(string code, string name, string type, bool taxable, bool inGross, string? role, string calc = SalaryCalcMethods.Fixed, string? expense = null, string? formula = null)
        {
            order += 10;
            return new SalaryComponent
            {
                OrganizationId = org, Code = code, Name = name, Type = type, IsTaxable = taxable, InGross = inGross, CalcMethod = calc, PostingRole = role,
                ExpenseRole = expense, Formula = formula, IsSystem = true, SortOrder = order
            };
        }
        const string E = SalaryComponentTypes.Earning, D = SalaryComponentTypes.Deduction, R = SalaryComponentTypes.EmployerContribution;
        return
        [
            C("BASIC",    "Basic salary",            E, true,  true,  "DEFAULT_SALARY_EXPENSE"),
            C("HRENT",    "House rent",              E, true,  true,  "DEFAULT_SALARY_EXPENSE"),
            C("UTIL",     "Utilities",               E, true,  true,  "DEFAULT_SALARY_EXPENSE"),
            C("CONV",     "Conveyance / allowances", E, true,  true,  "DEFAULT_SALARY_EXPENSE"),
            C("OT",       "Overtime",                E, true,  true,  "DEFAULT_OVERTIME_EXPENSE"),
            C("BONUS",    "Bonus",                   E, true,  true,  "DEFAULT_BONUS_EXPENSE"),
            C("ARREARS",  "Arrears",                 E, true,  true,  "DEFAULT_SALARY_EXPENSE"),
            C("EXPREIMB", "Expense reimbursement",   E, false, false, "DEFAULT_EXPENSE_CLAIM_PAYABLE"),
            C("ITAX",     "Income tax withheld",     D, false, false, "DEFAULT_EMPLOYEE_TAX_PAYABLE", SalaryCalcMethods.Formula, null, "SLAB(TAXABLE_INCOME)"),
            C("EOBI_EE",  "EOBI (employee)",         D, false, false, "DEFAULT_EOBI_PAYABLE"),
            C("EOBI_ER",  "EOBI (employer)",         R, false, false, "DEFAULT_EOBI_PAYABLE", SalaryCalcMethods.Fixed, "DEFAULT_EMPLOYER_CONTRIB_EXPENSE"),
            C("PF_EE",    "Provident fund (employee)", D, false, false, "DEFAULT_PF_PAYABLE"),
            C("PF_ER",    "Provident fund (employer)", R, false, false, "DEFAULT_PF_PAYABLE", SalaryCalcMethods.Fixed, "DEFAULT_EMPLOYER_CONTRIB_EXPENSE"),
            C("SS_EE",    "Social security (employee)", D, false, false, "DEFAULT_SOCIAL_SECURITY_PAYABLE"),
            C("SS_ER",    "Social security (employer)", R, false, false, "DEFAULT_SOCIAL_SECURITY_PAYABLE", SalaryCalcMethods.Fixed, "DEFAULT_EMPLOYER_CONTRIB_EXPENSE"),
            C("LOAN",     "Loan recovery",           D, false, false, "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE"),
            C("ADV",      "Advance recovery",        D, false, false, "DEFAULT_EMPLOYEE_ADVANCE"),
            C("OTHDED",   "Other deductions",        D, false, false, "DEFAULT_OTHER_DEDUCTION_PAYABLE"),
            C("NET",      "Net pay",                 SalaryComponentTypes.Result, false, false, "DEFAULT_SALARY_PAYABLE"),
        ];
    }

    /// <summary>Salaried individuals, annual PKR. UNVERIFIED (G-21): from memory of the 2025-26 schedule; a reviewer must confirm or replace it.</summary>
    public static IReadOnlyList<TaxSlabTable> TaxTables(Guid org)
    {
        TaxSlab S(decimal from, decimal? to, decimal fix, decimal rate) => new() { OrganizationId = org, FromAmount = from, ToAmount = to, FixedAmount = fix, RatePercent = rate };
        return
        [
            new TaxSlabTable
            {
                OrganizationId = org, Code = "PK-SALARIED-2026", Name = "Pakistan salaried income tax 2025-26 (UNVERIFIED)", CountryCode = "PK",
                EffectiveFrom = new DateOnly(2025, 7, 1), EffectiveTo = new DateOnly(2026, 6, 30), IsVerified = false, SourceNote = UnverifiedNote,
                Slabs =
                {
                    S(0m, 600_000m, 0m, 0m), S(600_000m, 1_200_000m, 0m, 1m), S(1_200_000m, 2_200_000m, 6_000m, 11m),
                    S(2_200_000m, 3_200_000m, 116_000m, 23m), S(3_200_000m, 4_100_000m, 346_000m, 30m), S(4_100_000m, null, 616_000m, 35m),
                }
            },
        ];
    }

    /// <summary>UNVERIFIED (G-21). EOBI on the notified minimum wage; PF rate is the commonly used one, but a fund's own rules decide; SS is provincial.</summary>
    public static IReadOnlyList<StatutoryRate> StatutoryRates(Guid org)
    {
        StatutoryRate R(string type, string name, decimal ee, decimal er, decimal? basis) => new()
        {
            OrganizationId = org, RateType = type, Name = name + " (UNVERIFIED)", CountryCode = "PK", EmployeePercent = ee, EmployerPercent = er, FixedBasis = basis,
            EffectiveFrom = new DateOnly(2025, 7, 1), IsVerified = false, SourceNote = UnverifiedNote
        };
        return
        [
            R(StatutoryRateTypes.Eobi, "EOBI", 1m, 5m, 37_000m),
            R(StatutoryRateTypes.ProvidentFund, "Provident fund", 8.33m, 8.33m, null),
            R(StatutoryRateTypes.SocialSecurity, "Provincial social security", 0m, 6m, null),
            // Settlement: gratuity = wage x service years x EmployerPercent / 100, so 100 = one month's wage per year of service.
            R(StatutoryRateTypes.Gratuity, "Gratuity", 0m, 100m, null),
        ];
    }

    /// <summary>G-9. AccountCode is left empty: the spec's 63xx codes exist in no seeded chart; the accountant picks the account (posting refuses an unmapped category).</summary>
    public static IReadOnlyList<ExpenseCategory> ExpenseCategories(Guid org)
    {
        ExpenseCategory C(string code, string name, bool receipt) => new() { OrganizationId = org, Code = code, Name = name, RequiresReceipt = receipt, IsSeeded = true };
        return
        [
            C("TRAVEL", "Travel & Transport", true), C("MEALS", "Meals & Entertainment", true), C("OFFICE", "Office Supplies", false),
            C("TELECOM", "Telecom & Internet", false), C("LODGING", "Lodging", true), C("OTHER", "Other Expenses", true),
        ];
    }

    public static IReadOnlyList<LetterTemplate> LetterTemplates(Guid org)
    {
        LetterTemplate T(string code, string name, string subject, string body) => new()
        {
            OrganizationId = org, Code = code, Kind = code, Name = name, Subject = subject, Body = body, IsSystem = true
        };
        return
        [
            T(LetterTemplateCodes.OfferLetter, "Offer Letter", "Offer of employment",
                "Dear {EmployeeName},\n\nWe are pleased to offer you the position of {Designation} in {Department}, starting {JoinDate}, at a monthly salary of {Salary}.\n\nPlease confirm your acceptance.\n\n{OrganizationName}"),
            T(LetterTemplateCodes.ExperienceCertificate, "Experience Certificate", "Experience certificate",
                "To whom it may concern,\n\nThis is to certify that {EmployeeName} ({EmployeeNumber}) worked with {OrganizationName} as {Designation} in {Department} from {JoinDate} to {LeaveDate}.\n\n{OrganizationName}"),
            T(LetterTemplateCodes.SalaryCertificate, "Salary Certificate", "Salary certificate",
                "To whom it may concern,\n\nThis is to certify that {EmployeeName} ({EmployeeNumber}) is employed with {OrganizationName} as {Designation} since {JoinDate}, and draws a gross monthly salary of {Salary}.\n\n{OrganizationName}"),
        ];
    }
}
