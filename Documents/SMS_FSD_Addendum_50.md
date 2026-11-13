# SMS — Functional Specification Document

## Addendum 50: Tenant Onboarding, Default Configuration & Data Import

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-050 |
| Version | 1.0 |
| Date | 2026-11-07 |
| Status | Draft |
| Depends On | A37 (Module Registry), A42 (Chart of Accounts), A44 (Financial Posting Gateway), A47 (GL Journal Engine) |
| Estimated Effort | 40 days (8 weeks) |

---

## 1. Purpose & Scope

### 1.1 Problem

When a new organization is created in SMS, every table is empty. The Org Admin must manually create a chart of accounts, tax codes, payment terms, units of measure, product categories, roles, approval rules, document numbering, fiscal year, and currencies before the system is usable. This takes hours, requires accounting knowledge most admins lack, and leads to misconfigured systems.

### 1.2 Solution

This addendum delivers a complete onboarding experience:

1. **Setup Wizard** — a guided 7-step flow that asks two questions (country + industry) and seeds a working system in minutes
2. **Industry Templates** — pre-built configuration packs maintained by the Super Admin (SMS product team)
3. **Onboarding Checklist** — a persistent dashboard widget that guides the admin through post-setup actions
4. **Data Import Wizard** — bring in existing customers, vendors, products, and opening balances from Excel/CSV
5. **Sample/Demo Data** — one-click training data with one-click cleanup
6. **Default Document Templates** — seeded PDF layouts for invoices, POs, quotations, receipts
7. **Default Email Templates** — seeded email content for transactional emails

### 1.3 Actors

| Actor | What They Do |
|---|---|
| **Super Admin** (SMS product team) | Creates and maintains the master default templates per country + industry. No Org Admin ever sees these screens. |
| **Org Admin** (client) | Runs the setup wizard, reviews defaults, customizes for their organization, imports data, manages onboarding. |

### 1.4 Design Principles

| # | Principle |
|---|---|
| DP-1 | **Defaults are a starting point, not a constraint.** Every seeded record becomes the org's own data, fully editable. |
| DP-2 | **Org-scoped always.** Customization by one org never affects another. The `OrgId` tenant filter guarantees this. |
| DP-3 | **No new editing screens.** The wizard seeds data into existing tables. Org Admin edits through the same screens they already have (Finance → CoA, Finance → Tax Codes, etc.). |
| DP-4 | **Safe deletion.** Records referenced by existing documents cannot be deleted — only deactivated. |
| DP-5 | **Resumable wizard.** Admin can leave mid-wizard and resume later. Progress is saved per step. |
| DP-6 | **Idempotent seeding.** Running the wizard twice never creates duplicate data. Each seed checks for existing records. |

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────┐
│                        SUPER ADMIN LAYER                             │
│                                                                      │
│  Platform Admin → Configuration → Setup Defaults                     │
│  ┌──────────────────────────────────────────────────────────────┐    │
│  │  core.SetupDefaults                                          │    │
│  │  (Country × Industry × Category → JSON payload)              │    │
│  │  No OrgId — system-wide reference table                      │    │
│  └──────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────┘
                                │
                    Wizard loads defaults
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────┐
│                         ORG ADMIN LAYER                              │
│                                                                      │
│  Step 1: Country + Industry → selects template                       │
│  Step 2: Modules → filters which categories to seed                  │
│  Step 3: CoA → pre-filled, editable tree                             │
│  Step 4: Tax Codes → pre-filled, editable table                      │
│  Step 5: Fiscal Year + Currency → pre-filled, editable               │
│  Step 6: Warehouses → admin enters names, zones from defaults        │
│  Step 7: Users & Roles → invite team, assign seeded roles            │
│                                                                      │
│  On "Launch" → ITenantSetupService.ApplyDefaultsAsync()              │
│  ┌──────────────────────────────────────────────────────────────┐    │
│  │  Seeds into existing per-tenant tables:                      │    │
│  │  finance.ChartOfAccounts (OrgId = this org)                  │    │
│  │  finance.TaxCodes (OrgId = this org)                         │    │
│  │  finance.PaymentTerms (OrgId = this org)                     │    │
│  │  inventory.UnitsOfMeasure (OrgId = this org)                 │    │
│  │  ... etc — all org-scoped, all editable afterward            │    │
│  └──────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────┘
```

### 2.1 Defaults Lookup Chain (Fallback Priority)

```
Priority 1:  PK + Trading     → exact match for country + industry
Priority 2:  PK + GLOBAL      → country-level (tax, fiscal year)
Priority 3:  GLOBAL + Trading  → industry-level (CoA structure, UoMs)
Priority 4:  GLOBAL + GLOBAL   → universal skeleton
```

---

## 3. Data Dictionary

### 3.1 SetupDefaults (Master Table — No OrgId)

```sql
CREATE TABLE core.SetupDefaults
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    CountryCode         NVARCHAR(5) NOT NULL,
    IndustryType        NVARCHAR(30) NOT NULL,
    DataCategory        NVARCHAR(30) NOT NULL,
    DataPayload         NVARCHAR(MAX) NOT NULL,
    DisplayName         NVARCHAR(100) NULL,
    Version             INT NOT NULL DEFAULT 1,
    IsActive            BIT NOT NULL DEFAULT 1,
    Notes               NVARCHAR(500) NULL,
    
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL,
    ModifiedAt          DATETIME2 NULL,
    ModifiedBy          NVARCHAR(100) NULL,
    
    CONSTRAINT UQ_SD_Key UNIQUE (CountryCode, IndustryType, DataCategory)
);
```

**DataCategory values:**

| Category | Driven By | Description |
|---|---|---|
| `COA` | Country + Industry | Chart of Accounts tree with account roles |
| `TAX` | Country only | Tax codes and rates |
| `CURRENCY` | Country only | Base + common foreign currencies |
| `FISCAL` | Country only | Fiscal year start month, period type |
| `PAYMENT_TERMS` | Industry | Standard payment terms |
| `UOM` | Industry | Units of measure grouped by type |
| `PRODUCT_CATEGORIES` | Industry | Product category hierarchy |
| `APPROVAL_RULES` | Industry | Amount-based approval thresholds |
| `DOC_NUMBERING` | Industry | Document sequence patterns |
| `ROLES` | Modules enabled | Roles and permissions |
| `WAREHOUSE` | Industry | Default zone types |
| `DOC_TEMPLATES` | Country | PDF document layouts |
| `EMAIL_TEMPLATES` | Country | Transactional email content |
| `HRM_LEAVE` | Country + HRM | Leave types and entitlements |
| `HRM_SALARY` | Country + HRM | Salary components |
| `HRM_TAX` | Country + HRM | Income tax slabs |

### 3.2 TenantSetupLog

```sql
CREATE TABLE core.TenantSetupLog
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    CountryCode         NVARCHAR(5) NOT NULL,
    IndustryType        NVARCHAR(30) NOT NULL,
    DataCategory        NVARCHAR(30) NOT NULL,
    DefaultsVersion     INT NOT NULL,
    RecordsSeeded       INT NOT NULL,
    Action              NVARCHAR(20) NOT NULL DEFAULT 'Seeded',
    AppliedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    AppliedBy           NVARCHAR(100) NOT NULL,
    
    INDEX IX_TSL_Org (OrgId)
);
```

### 3.3 OnboardingChecklist

```sql
CREATE TABLE core.OnboardingChecklist
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    StepCode            NVARCHAR(30) NOT NULL,
    StepLabel           NVARCHAR(100) NOT NULL,
    StepDescription     NVARCHAR(200) NULL,
    NavigateTo          NVARCHAR(200) NULL,
    SortOrder           INT NOT NULL,
    IsCompleted         BIT NOT NULL DEFAULT 0,
    CompletedAt         DATETIME2 NULL,
    IsDismissed         BIT NOT NULL DEFAULT 0,
    DismissedAt         DATETIME2 NULL,
    
    CONSTRAINT UQ_OC_OrgStep UNIQUE (OrgId, StepCode)
);
```

**Default steps seeded on wizard completion:**

| StepCode | Label | Auto-completes when |
|---|---|---|
| `SETUP_COMPLETE` | Complete setup wizard | Wizard finishes |
| `ADD_CUSTOMER` | Add your first customer | Any customer created |
| `ADD_PRODUCT` | Add your first product | Any product created |
| `ADD_SUPPLIER` | Add your first supplier | Any vendor created |
| `CREATE_SO` | Create your first sale order | Any SO created |
| `CREATE_PO` | Create your first purchase order | Any PO created |
| `CONNECT_QB` | Connect QuickBooks (optional) | QuickBooks linked |
| `INVITE_TEAM` | Invite your team members | 2+ users exist |

### 3.4 DataImportJob

```sql
CREATE TABLE core.DataImportJobs
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    Uuid                UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    ImportType          NVARCHAR(30) NOT NULL,
    FileName            NVARCHAR(255) NOT NULL,
    FileKey             NVARCHAR(500) NOT NULL,
    Status              NVARCHAR(20) NOT NULL DEFAULT 'Uploaded',
    TotalRows           INT NULL,
    ValidRows           INT NULL,
    WarningRows         INT NULL,
    ErrorRows           INT NULL,
    ImportedRows        INT NULL,
    ColumnMapping       NVARCHAR(MAX) NULL,
    ErrorLog            NVARCHAR(MAX) NULL,
    
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL,
    CompletedAt         DATETIME2 NULL,
    
    CONSTRAINT UQ_DIJ_Uuid UNIQUE (Uuid),
    CONSTRAINT CK_DIJ_Status CHECK (Status IN 
        ('Uploaded','Validating','Validated','Importing','Completed','Failed')),
    CONSTRAINT CK_DIJ_Type CHECK (ImportType IN 
        ('Customers','Vendors','Products','OpeningBalances','PriceLists')),
    
    INDEX IX_DIJ_Org (OrgId, CreatedAt DESC)
);
```

### 3.5 DocumentTemplate

```sql
CREATE TABLE core.DocumentTemplates
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    TemplateType        NVARCHAR(30) NOT NULL,
    Name                NVARCHAR(100) NOT NULL,
    HeaderHtml          NVARCHAR(MAX) NULL,
    BodyHtml            NVARCHAR(MAX) NULL,
    FooterHtml          NVARCHAR(MAX) NULL,
    LogoFileKey         NVARCHAR(500) NULL,
    PrimaryColor        NVARCHAR(7) NULL DEFAULT '#2c3e6b',
    ShowBankDetails     BIT NOT NULL DEFAULT 1,
    BankDetailsText     NVARCHAR(500) NULL,
    TermsAndConditions  NVARCHAR(MAX) NULL,
    IsDefault           BIT NOT NULL DEFAULT 0,
    IsSeeded            BIT NOT NULL DEFAULT 0,
    IsActive            BIT NOT NULL DEFAULT 1,
    
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedAt          DATETIME2 NULL,
    
    CONSTRAINT CK_DT_Type CHECK (TemplateType IN 
        ('SalesInvoice','Quotation','PurchaseOrder','DeliveryNote',
         'PaymentReceipt','CustomerStatement')),
    CONSTRAINT UQ_DT_OrgTypeDefault UNIQUE (OrgId, TemplateType, IsDefault),
    
    INDEX IX_DT_Org (OrgId, TemplateType)
);
```

### 3.6 EmailTemplate

```sql
CREATE TABLE core.EmailTemplates
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    TemplateType        NVARCHAR(30) NOT NULL,
    Name                NVARCHAR(100) NOT NULL,
    SubjectTemplate     NVARCHAR(200) NOT NULL,
    BodyHtml            NVARCHAR(MAX) NOT NULL,
    IsDefault           BIT NOT NULL DEFAULT 0,
    IsSeeded            BIT NOT NULL DEFAULT 0,
    IsActive            BIT NOT NULL DEFAULT 1,
    
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedAt          DATETIME2 NULL,
    
    CONSTRAINT CK_ET_Type CHECK (TemplateType IN 
        ('InvoiceSent','QuotationSent','POToSupplier','PaymentReminder',
         'PaymentReceived','WelcomeEmail','PayslipSent')),
    
    INDEX IX_ET_Org (OrgId, TemplateType)
);
```

### 3.7 Organization Table Extensions

```sql
ALTER TABLE core.Organizations ADD
    IndustryType        NVARCHAR(30) NULL,
    CountryCode         NVARCHAR(5) NULL,
    SetupStatus         NVARCHAR(20) NOT NULL DEFAULT 'NotStarted',
    SetupStep           INT NOT NULL DEFAULT 0,
    SetupCompletedAt    DATETIME2 NULL,
    SetupCompletedBy    NVARCHAR(100) NULL;

ALTER TABLE core.Organizations ADD
    CONSTRAINT CK_Org_SetupStatus CHECK (SetupStatus IN 
        ('NotStarted','InProgress','Completed','Skipped'));
```

### 3.8 IsSeeded Flag on Existing Tables

```sql
-- Added to every table that receives seeded data
-- so the system can track and reset defaults

ALTER TABLE finance.ChartOfAccounts ADD
    IsSeeded        BIT NOT NULL DEFAULT 0,
    SeededAt        DATETIME2 NULL,
    SeededVersion   INT NULL;

ALTER TABLE finance.TaxCodes ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE finance.PaymentTerms ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE inventory.UnitsOfMeasure ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE inventory.ProductCategories ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE core.ApprovalWorkflowRules ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE core.SequenceDefinitions ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;

ALTER TABLE core.Roles ADD
    IsSeeded BIT NOT NULL DEFAULT 0, SeededAt DATETIME2 NULL, SeededVersion INT NULL;
```

---

## 4. Service Interfaces

### 4.1 ITenantSetupService

```csharp
namespace SMS.Core.Services;

public interface ITenantSetupService
{
    // Wizard dropdown data
    Task<List<CountryOption>> GetAvailableCountriesAsync();
    Task<List<IndustryOption>> GetAvailableIndustriesAsync(string countryCode);
    
    // Preview before applying
    Task<SetupPreview> PreviewDefaultsAsync(
        string countryCode, string industryType, List<string> enabledModules);
    
    // Apply all defaults (called on wizard "Launch")
    Task<SetupResult> ApplyDefaultsAsync(
        Guid orgId, SetupRequest request, string appliedBy);
    
    // Wizard progress management
    Task<SetupState> GetSetupStateAsync(Guid orgId);
    Task SaveStepProgressAsync(Guid orgId, int step, string stepData);
    
    // Reset individual category
    Task<ResetResult> ResetCategoryAsync(
        Guid orgId, string dataCategory, ResetMode mode, string resetBy);
    
    // Setup status for Default Data dashboard
    Task<List<CategoryStatus>> GetCategoryStatusesAsync(Guid orgId);
}

public record SetupRequest
{
    public string CountryCode { get; init; }
    public string IndustryType { get; init; }
    public List<string> EnabledModules { get; init; }
    public string CompanyName { get; init; }
    public string BaseCurrencyCode { get; init; }
    public string FiscalYearStartMonth { get; init; }
    public List<WarehouseInput> Warehouses { get; init; }
    public List<UserInvite> TeamMembers { get; init; }
    public string QuickBooksConnectionId { get; init; }
}

public record SetupPreview
{
    public int AccountsToCreate { get; init; }
    public int TaxCodesToCreate { get; init; }
    public int PaymentTermsToCreate { get; init; }
    public int UoMsToCreate { get; init; }
    public int RolesToCreate { get; init; }
    public string FiscalYearRange { get; init; }
    public string BaseCurrency { get; init; }
}

public record SetupResult
{
    public bool Success { get; init; }
    public Dictionary<string, int> RecordsPerCategory { get; init; }
    public List<string> Warnings { get; init; }
}

public enum ResetMode
{
    Merge,          // Add missing defaults, keep custom + modified
    Overwrite,      // Replace seeded records, keep custom
    FullReset       // Delete all + re-seed (blocked if records in use)
}

public record CategoryStatus
{
    public string Category { get; init; }
    public string DisplayName { get; init; }
    public int SeededCount { get; init; }
    public int CurrentCount { get; init; }
    public bool IsModified { get; init; }
    public DateTime? LastApplied { get; init; }
    public int DefaultsVersion { get; init; }
}
```

### 4.2 ISetupDefaultsService (Super Admin)

```csharp
namespace SMS.Core.Services;

public interface ISetupDefaultsService
{
    // List all defaults with filtering
    Task<List<SetupDefaultDto>> ListAsync(
        string countryCode = null, string industryType = null, 
        string dataCategory = null);
    
    // CRUD
    Task<SetupDefaultDto> GetAsync(int id);
    Task<SetupDefaultDto> CreateAsync(CreateSetupDefaultRequest request, string createdBy);
    Task<SetupDefaultDto> UpdateAsync(int id, UpdateSetupDefaultRequest request, string updatedBy);
    Task DeleteAsync(int id);
    
    // Bulk import/export
    Task<int> ImportFromJsonAsync(string json, string importedBy);
    Task<string> ExportAllAsync();
    
    // Lookup with fallback chain
    Task<string> ResolvePayloadAsync(
        string countryCode, string industryType, string dataCategory);
}
```

### 4.3 IDataImportService

```csharp
namespace SMS.Core.Services;

public interface IDataImportService
{
    // Download template for a specific import type
    Task<byte[]> GetImportTemplateAsync(string importType);
    
    // Upload and validate
    Task<DataImportJobDto> UploadAndValidateAsync(
        Guid orgId, string importType, Stream fileStream, 
        string fileName, string uploadedBy);
    
    // Get column mapping suggestions
    Task<List<ColumnMapping>> GetColumnMappingsAsync(Guid jobId);
    
    // Update column mappings (admin adjusts)
    Task UpdateColumnMappingsAsync(Guid jobId, List<ColumnMapping> mappings);
    
    // Re-validate after mapping changes
    Task<ValidationResult> RevalidateAsync(Guid jobId);
    
    // Execute the import
    Task<ImportResult> ExecuteImportAsync(Guid jobId, string importedBy);
    
    // Get import history
    Task<List<DataImportJobDto>> GetImportHistoryAsync(Guid orgId);
}

public record ColumnMapping
{
    public string SourceColumn { get; init; }     // "Company Name" (from Excel)
    public string TargetField { get; init; }      // "BusinessPartner.Name"
    public bool IsRequired { get; init; }
    public bool IsMapped { get; init; }
    public string SampleValue { get; init; }      // first row value for preview
}

public record ValidationResult
{
    public int TotalRows { get; init; }
    public int ValidRows { get; init; }
    public int WarningRows { get; init; }
    public int ErrorRows { get; init; }
    public List<ValidationIssue> Issues { get; init; }
}

public record ValidationIssue
{
    public int RowNumber { get; init; }
    public string Column { get; init; }
    public string Value { get; init; }
    public string Issue { get; init; }         // "Missing required field"
    public string Severity { get; init; }      // "Error" or "Warning"
}
```

### 4.4 IOnboardingService

```csharp
namespace SMS.Core.Services;

public interface IOnboardingService
{
    // Get checklist status for dashboard widget
    Task<OnboardingStatus> GetStatusAsync(Guid orgId);
    
    // Mark a step as complete (called by event handlers)
    Task CompleteStepAsync(Guid orgId, string stepCode);
    
    // Dismiss the entire checklist
    Task DismissAsync(Guid orgId);
    
    // Seed checklist for a new org
    Task SeedChecklistAsync(Guid orgId, List<string> enabledModules);
}

public record OnboardingStatus
{
    public int TotalSteps { get; init; }
    public int CompletedSteps { get; init; }
    public bool IsDismissed { get; init; }
    public List<OnboardingStep> Steps { get; init; }
}
```

### 4.5 ISampleDataService

```csharp
namespace SMS.Core.Services;

public interface ISampleDataService
{
    // Preview what will be created
    Task<SampleDataPreview> PreviewAsync(Guid orgId);
    
    // Load sample data (all records tagged IsDemo = true)
    Task<SampleDataResult> LoadSampleDataAsync(Guid orgId, string loadedBy);
    
    // Check if org has demo data
    Task<bool> HasDemoDataAsync(Guid orgId);
    
    // Delete all demo data at once
    Task<int> CleanupDemoDataAsync(Guid orgId, string cleanedBy);
}
```

### 4.6 IDocumentTemplateService

```csharp
namespace SMS.Core.Services;

public interface IDocumentTemplateService
{
    Task<List<DocumentTemplateDto>> ListAsync(Guid orgId);
    Task<DocumentTemplateDto> GetAsync(Guid orgId, int templateId);
    Task<DocumentTemplateDto> UpdateAsync(
        Guid orgId, int templateId, UpdateDocumentTemplateRequest request, string updatedBy);
    Task SetDefaultAsync(Guid orgId, int templateId);
    Task<DocumentTemplateDto> CreateCustomAsync(
        Guid orgId, CreateDocumentTemplateRequest request, string createdBy);
    Task ResetToDefaultAsync(Guid orgId, string templateType, string resetBy);
    
    // Render a document using a template
    Task<byte[]> RenderPdfAsync(Guid orgId, string templateType, object documentData);
}
```

### 4.7 IEmailTemplateService

```csharp
namespace SMS.Core.Services;

public interface IEmailTemplateService
{
    Task<List<EmailTemplateDto>> ListAsync(Guid orgId);
    Task<EmailTemplateDto> GetAsync(Guid orgId, int templateId);
    Task<EmailTemplateDto> UpdateAsync(
        Guid orgId, int templateId, UpdateEmailTemplateRequest request, string updatedBy);
    Task ResetToDefaultAsync(Guid orgId, string templateType, string resetBy);
    
    // Render an email using a template + placeholders
    Task<RenderedEmail> RenderAsync(
        Guid orgId, string templateType, Dictionary<string, string> placeholders);
    
    // Send a test email
    Task SendTestAsync(Guid orgId, int templateId, string toEmail);
}

public record RenderedEmail
{
    public string Subject { get; init; }
    public string BodyHtml { get; init; }
}
```

---

## 5. Template Data Payloads (JSON Specifications)

### 5.1 COA Payload

```json
{
  "accounts": [
    {
      "code": "1000", "name": "Assets", "type": "Asset", 
      "isGroup": true, "parent": null, "role": null
    },
    {
      "code": "1100", "name": "Cash & Bank", "type": "Asset", 
      "isGroup": true, "parent": "1000", "role": null
    },
    {
      "code": "1110", "name": "Cash in Hand", "type": "Asset", 
      "isGroup": false, "parent": "1100", "role": "DEFAULT_CASH"
    },
    {
      "code": "1120", "name": "Bank Account", "type": "Asset", 
      "isGroup": false, "parent": "1100", "role": "DEFAULT_BANK"
    },
    {
      "code": "1200", "name": "Accounts Receivable", "type": "Asset",
      "isGroup": false, "parent": "1000", "role": "DEFAULT_RECEIVABLE"
    }
  ]
}
```

### 5.2 TAX Payload

```json
{
  "taxCodes": [
    {"code": "GST18", "name": "Sales Tax 18%", "rate": 18.0, "type": "Output", "isDefault": true},
    {"code": "GST0", "name": "Exempt", "rate": 0.0, "type": "Output"},
    {"code": "FED16", "name": "Federal Excise Duty 16%", "rate": 16.0, "type": "Output"},
    {"code": "WHT_G", "name": "WHT on Goods 4.5%", "rate": 4.5, "type": "Withholding"},
    {"code": "WHT_S", "name": "WHT on Services 8%", "rate": 8.0, "type": "Withholding"},
    {"code": "INPUT18", "name": "Input Tax 18%", "rate": 18.0, "type": "Input"}
  ]
}
```

### 5.3 FISCAL Payload

```json
{
  "startMonth": 7,
  "periodType": "Monthly",
  "dateFormat": "DD/MM/YYYY"
}
```

### 5.4 CURRENCY Payload

```json
{
  "base": {"code": "PKR", "name": "Pakistani Rupee", "symbol": "Rs"},
  "additional": [
    {"code": "USD", "name": "US Dollar", "symbol": "$"},
    {"code": "AED", "name": "UAE Dirham", "symbol": "د.إ"},
    {"code": "SAR", "name": "Saudi Riyal", "symbol": "﷼"},
    {"code": "GBP", "name": "British Pound", "symbol": "£"},
    {"code": "EUR", "name": "Euro", "symbol": "€"}
  ]
}
```

### 5.5 PAYMENT_TERMS Payload

```json
{
  "terms": [
    {"code": "COD", "name": "Cash on Delivery", "days": 0, "discountPercent": 0},
    {"code": "NET15", "name": "Net 15 Days", "days": 15, "discountPercent": 0},
    {"code": "NET30", "name": "Net 30 Days", "days": 30, "discountPercent": 0, "isDefault": true},
    {"code": "NET60", "name": "Net 60 Days", "days": 60, "discountPercent": 0},
    {"code": "2_10_30", "name": "2/10 Net 30", "days": 30, "discountPercent": 2, "discountDays": 10}
  ]
}
```

### 5.6 UOM Payload

```json
{
  "groups": [
    {
      "name": "Weight",
      "units": [
        {"code": "KG", "name": "Kilogram", "isBase": true},
        {"code": "G", "name": "Gram", "conversionFactor": 0.001},
        {"code": "LB", "name": "Pound", "conversionFactor": 0.453592},
        {"code": "TON", "name": "Metric Ton", "conversionFactor": 1000}
      ]
    },
    {
      "name": "Count",
      "units": [
        {"code": "PCS", "name": "Pieces", "isBase": true},
        {"code": "DOZ", "name": "Dozen", "conversionFactor": 12},
        {"code": "BOX", "name": "Box", "conversionFactor": 1},
        {"code": "CTN", "name": "Carton", "conversionFactor": 1},
        {"code": "PLT", "name": "Pallet", "conversionFactor": 1}
      ]
    },
    {
      "name": "Volume",
      "units": [
        {"code": "LTR", "name": "Litre", "isBase": true},
        {"code": "ML", "name": "Millilitre", "conversionFactor": 0.001},
        {"code": "GAL", "name": "Gallon", "conversionFactor": 3.78541}
      ]
    },
    {
      "name": "Length",
      "units": [
        {"code": "MTR", "name": "Metre", "isBase": true},
        {"code": "CM", "name": "Centimetre", "conversionFactor": 0.01},
        {"code": "FT", "name": "Foot", "conversionFactor": 0.3048}
      ]
    }
  ]
}
```

### 5.7 APPROVAL_RULES Payload

```json
{
  "rules": [
    {
      "documentType": "PurchaseOrder",
      "levels": [
        {"minAmount": 0, "maxAmount": 50000, "approverRole": "AUTO", "label": "Auto-approve"},
        {"minAmount": 50001, "maxAmount": 500000, "approverRole": "PROCUREMENT_MANAGER", "label": "Manager approval"},
        {"minAmount": 500001, "maxAmount": null, "approverRole": "DIRECTOR", "label": "Director approval"}
      ]
    },
    {
      "documentType": "ExpenseClaim",
      "levels": [
        {"minAmount": 0, "maxAmount": 10000, "approverRole": "DEPARTMENT_HEAD", "label": "Department head"},
        {"minAmount": 10001, "maxAmount": null, "approverRole": "FINANCE_OFFICER", "label": "Finance approval"}
      ]
    }
  ]
}
```

### 5.8 DOC_NUMBERING Payload

```json
{
  "sequences": [
    {"documentType": "SaleOrder", "pattern": "SO-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true},
    {"documentType": "PurchaseOrder", "pattern": "PO-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true},
    {"documentType": "SalesInvoice", "pattern": "INV-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true},
    {"documentType": "Quotation", "pattern": "QT-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true},
    {"documentType": "GoodsReceipt", "pattern": "GRN-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true},
    {"documentType": "PaymentVoucher", "pattern": "PV-{YYYY}-{NNNN}", "startNumber": 1, "resetYearly": true}
  ]
}
```

### 5.9 ROLES Payload

```json
{
  "roles": [
    {
      "code": "ORG_ADMIN", "name": "Organization Admin",
      "permissions": ["ALL"],
      "requiredModules": []
    },
    {
      "code": "SALES_MANAGER", "name": "Sales Manager",
      "permissions": ["SALES_VIEW","SALES_CREATE","SALES_APPROVE","SALES_REPORTS","CUSTOMER_MANAGE"],
      "requiredModules": ["Sales"]
    },
    {
      "code": "SALES_REP", "name": "Sales Representative",
      "permissions": ["SALES_VIEW","SALES_CREATE","CUSTOMER_VIEW","LEAD_MANAGE"],
      "requiredModules": ["Sales"]
    },
    {
      "code": "PURCHASE_OFFICER", "name": "Purchase Officer",
      "permissions": ["PURCHASE_VIEW","PURCHASE_CREATE","VENDOR_VIEW"],
      "requiredModules": ["Purchasing"]
    },
    {
      "code": "PROCUREMENT_MANAGER", "name": "Procurement Manager",
      "permissions": ["PURCHASE_VIEW","PURCHASE_CREATE","PURCHASE_APPROVE","VENDOR_MANAGE","PURCHASE_REPORTS"],
      "requiredModules": ["Purchasing"]
    },
    {
      "code": "WAREHOUSE_OPERATOR", "name": "Warehouse Operator",
      "permissions": ["WAREHOUSE_VIEW","WAREHOUSE_RECEIVE","WAREHOUSE_PICK","WAREHOUSE_DISPATCH"],
      "requiredModules": ["Warehouse"]
    },
    {
      "code": "INVENTORY_MANAGER", "name": "Inventory Manager",
      "permissions": ["WAREHOUSE_VIEW","WAREHOUSE_RECEIVE","WAREHOUSE_APPROVE","INVENTORY_REPORTS","WAREHOUSE_MANAGE"],
      "requiredModules": ["Warehouse"]
    },
    {
      "code": "FINANCE_OFFICER", "name": "Finance Officer",
      "permissions": ["FINANCE_VIEW","FINANCE_CREATE","FINANCE_APPROVE","FINANCE_REPORTS","GL_VIEW","QB_MANAGE"],
      "requiredModules": ["Finance"]
    }
  ]
}
```

---

## 6. Data Import Templates

### 6.1 Customer Import Template

| Column | SMS Field | Required | Validation |
|---|---|---|---|
| Company Name | BusinessPartner.Name | Yes | Max 200 chars |
| Contact First Name | Contact.FirstName | No | Max 60 chars |
| Contact Last Name | Contact.LastName | No | Max 60 chars |
| Phone | Contact.Phone | No | Valid phone format |
| Email | Contact.Email | No | Valid email format |
| Address Line 1 | Address.Line1 | No | Max 200 chars |
| City | Address.City | No | Max 100 chars |
| Country | Address.Country | No | ISO country code |
| Tax ID | BusinessPartner.TaxId | No | Max 50 chars |
| Payment Terms | BusinessPartner.PaymentTermCode | No | Must match existing code |
| Credit Limit | BusinessPartner.CreditLimit | No | Decimal >= 0 |

### 6.2 Product Import Template

| Column | SMS Field | Required | Validation |
|---|---|---|---|
| Product Code | Product.Code | Yes | Unique per org, max 30 chars |
| Product Name | Product.Name | Yes | Max 200 chars |
| Category | Product.CategoryCode | No | Must match existing code |
| Unit of Measure | Product.UoMCode | Yes | Must match existing code |
| Sale Price | Product.SalePrice | No | Decimal >= 0 |
| Cost Price | Product.CostPrice | No | Decimal >= 0 |
| Tax Code | Product.TaxCode | No | Must match existing code |
| Barcode | Product.Barcode | No | Max 50 chars |
| Min Stock Level | Product.MinStockLevel | No | Integer >= 0 |
| Reorder Quantity | Product.ReorderQty | No | Integer >= 0 |

### 6.3 Opening Balances Import Template

| Column | SMS Field | Required | Validation |
|---|---|---|---|
| Account Code | JournalEntryLine.AccountCode | Yes | Must match existing CoA code |
| Account Name | (display only) | No | Ignored, for reference |
| Debit | JournalEntryLine.Debit | Conditional | Decimal >= 0, either debit or credit |
| Credit | JournalEntryLine.Credit | Conditional | Decimal >= 0, either debit or credit |

**Business rule:** Total debits must equal total credits. Import creates a single "Opening Balance" journal entry with entry date = fiscal year start.

---

## 7. Sample/Demo Data Specification

### 7.1 What Gets Created

| Entity | Count | Examples |
|---|---|---|
| Customers | 5 | Acme Corporation, BlueStar Enterprises, Lone Star Offices LLC, Cedar Trading Co, Highland Industries |
| Vendors | 3 | GlobalSupply Inc, Hartwell Furniture, QuickParts Co |
| Products | 20 | Office Table (3 variants), Office Chair (3 variants), Desk Lamp, Monitor Stand, etc. |
| Price Lists | 2 | Retail Price List, Wholesale Price List |
| Sale Orders | 2 | SO to Lone Star (5 items), SO to Acme (3 items) |
| Purchase Orders | 1 | PO to Hartwell (4 items) |
| Opening Stock | 20 products | Random quantities between 10-200 |

### 7.2 Demo Data Tagging

Every demo record carries `IsDemo = 1`. The cleanup function:

```csharp
public async Task<int> CleanupDemoDataAsync(Guid orgId, string cleanedBy)
{
    // Delete in reverse dependency order
    int deleted = 0;
    deleted += await DeleteDemoAsync<PurchaseOrderLine>(orgId);
    deleted += await DeleteDemoAsync<PurchaseOrder>(orgId);
    deleted += await DeleteDemoAsync<SaleOrderLine>(orgId);
    deleted += await DeleteDemoAsync<SaleOrder>(orgId);
    deleted += await DeleteDemoAsync<StockTransaction>(orgId);
    deleted += await DeleteDemoAsync<Product>(orgId);
    deleted += await DeleteDemoAsync<PriceList>(orgId);
    deleted += await DeleteDemoAsync<BusinessPartner>(orgId);  // customers + vendors
    
    await _auditLog.LogAsync(orgId, "DEMO_DATA_CLEANUP", 
        $"Deleted {deleted} demo records", cleanedBy);
    
    return deleted;
}
```

---

## 8. Database Migrations

| # | Migration | Description | Phase |
|---|---|---|---|
| M50_001 | CreateSetupDefaults | `core.SetupDefaults` table | P1 |
| M50_002 | CreateTenantSetupLog | `core.TenantSetupLog` table | P1 |
| M50_003 | CreateOnboardingChecklist | `core.OnboardingChecklist` table | P1 |
| M50_004 | AlterOrganizations | Add IndustryType, CountryCode, SetupStatus columns | P1 |
| M50_005 | AddIsSeededFlags | Add IsSeeded, SeededAt, SeededVersion to 8 existing tables | P1 |
| M50_006 | CreateDataImportJobs | `core.DataImportJobs` table | P2 |
| M50_007 | CreateDocumentTemplates | `core.DocumentTemplates` table | P3 |
| M50_008 | CreateEmailTemplates | `core.EmailTemplates` table | P3 |
| M50_009 | AddIsDemoFlags | Add IsDemo to BusinessPartners, Products, SaleOrders, PurchaseOrders, StockTransactions | P3 |
| M50_010 | SeedPakistanTradingDefaults | Full PK + Trading template data | P1 |
| M50_011 | SeedUAETradingDefaults | AE + Trading template data | P1 |
| M50_012 | SeedSaudiTradingDefaults | SA + Trading template data | P1 |
| M50_013 | SeedGlobalDefaults | GLOBAL + GLOBAL skeleton | P1 |
| M50_014 | SeedDocumentTemplates | Default PDF layouts per country | P3 |
| M50_015 | SeedEmailTemplates | Default email content per country | P3 |

---

## 9. API Endpoints

### 9.1 Setup Wizard Endpoints (Org Admin)

| # | Method | Route | Description |
|---|---|---|---|
| 1 | GET | `/api/v1/setup/countries` | List available countries |
| 2 | GET | `/api/v1/setup/industries?country={code}` | List industries for a country |
| 3 | POST | `/api/v1/setup/preview` | Preview what will be seeded |
| 4 | POST | `/api/v1/setup/apply` | Apply defaults (launch wizard) |
| 5 | GET | `/api/v1/setup/state` | Get current wizard progress |
| 6 | POST | `/api/v1/setup/state/{step}` | Save step progress |

### 9.2 Default Data Dashboard Endpoints (Org Admin)

| # | Method | Route | Description |
|---|---|---|---|
| 7 | GET | `/api/v1/setup/categories` | Get all category statuses |
| 8 | POST | `/api/v1/setup/categories/{category}/reset` | Reset a category (merge/overwrite/full) |

### 9.3 Onboarding Endpoints

| # | Method | Route | Description |
|---|---|---|---|
| 9 | GET | `/api/v1/onboarding/status` | Get checklist status |
| 10 | POST | `/api/v1/onboarding/dismiss` | Dismiss the checklist |

### 9.4 Data Import Endpoints

| # | Method | Route | Description |
|---|---|---|---|
| 11 | GET | `/api/v1/import/templates/{type}` | Download import template Excel |
| 12 | POST | `/api/v1/import/upload` | Upload file for import |
| 13 | GET | `/api/v1/import/{jobId}/mappings` | Get column mappings |
| 14 | PUT | `/api/v1/import/{jobId}/mappings` | Update column mappings |
| 15 | POST | `/api/v1/import/{jobId}/validate` | Re-validate after mapping change |
| 16 | POST | `/api/v1/import/{jobId}/execute` | Execute the import |
| 17 | GET | `/api/v1/import/history` | Import job history |

### 9.5 Sample Data Endpoints

| # | Method | Route | Description |
|---|---|---|---|
| 18 | GET | `/api/v1/demo-data/preview` | Preview what will be created |
| 19 | POST | `/api/v1/demo-data/load` | Load sample data |
| 20 | GET | `/api/v1/demo-data/status` | Check if demo data exists |
| 21 | DELETE | `/api/v1/demo-data` | Delete all demo data |

### 9.6 Document Template Endpoints

| # | Method | Route | Description |
|---|---|---|---|
| 22 | GET | `/api/v1/document-templates` | List all templates |
| 23 | GET | `/api/v1/document-templates/{id}` | Get template detail |
| 24 | PUT | `/api/v1/document-templates/{id}` | Update template |
| 25 | POST | `/api/v1/document-templates/{id}/set-default` | Set as default |
| 26 | POST | `/api/v1/document-templates` | Create custom template |
| 27 | POST | `/api/v1/document-templates/{type}/reset` | Reset to default |
| 28 | POST | `/api/v1/document-templates/{id}/preview` | Preview with sample data |

### 9.7 Email Template Endpoints

| # | Method | Route | Description |
|---|---|---|---|
| 29 | GET | `/api/v1/email-templates` | List all templates |
| 30 | GET | `/api/v1/email-templates/{id}` | Get template detail |
| 31 | PUT | `/api/v1/email-templates/{id}` | Update template |
| 32 | POST | `/api/v1/email-templates/{type}/reset` | Reset to default |
| 33 | POST | `/api/v1/email-templates/{id}/test` | Send test email |

### 9.8 Super Admin Endpoints (Platform Admin Only)

| # | Method | Route | Description |
|---|---|---|---|
| 34 | GET | `/api/v1/admin/setup-defaults` | List all defaults (filtered) |
| 35 | GET | `/api/v1/admin/setup-defaults/{id}` | Get default detail |
| 36 | POST | `/api/v1/admin/setup-defaults` | Create new default |
| 37 | PUT | `/api/v1/admin/setup-defaults/{id}` | Update default |
| 38 | DELETE | `/api/v1/admin/setup-defaults/{id}` | Delete default |
| 39 | POST | `/api/v1/admin/setup-defaults/import` | Bulk import from JSON |
| 40 | GET | `/api/v1/admin/setup-defaults/export` | Export all as JSON |

**Total: 40 API endpoints**

---

## 10. UI Screens

### 10.1 New Screens

| # | Screen | Actor | Description |
|---|---|---|---|
| 1 | Setup Wizard (7 steps) | Org Admin | Guided first-time setup flow |
| 2 | Default Data Dashboard | Org Admin | View seeded data, reset categories |
| 3 | Data Import Wizard (6 steps) | Org Admin | Upload, map, validate, import |
| 4 | Import History | Org Admin | Past import jobs with status |
| 5 | Document Template Editor | Org Admin | Customize PDF layouts |
| 6 | Email Template Editor | Org Admin | Customize email content |
| 7 | Sample Data Manager | Org Admin | Load / delete demo data |
| 8 | Onboarding Checklist Widget | Org Admin | Dashboard widget |
| 9 | Setup Defaults Management | Super Admin | CRUD for master templates |

### 10.2 Existing Screens Modified

| Screen | Modification |
|---|---|
| Dashboard | Add Onboarding Checklist widget (dismissible) |
| Dashboard | Add "Setup incomplete" banner for NotStarted orgs |
| Settings → Organization | Add "Setup Wizard" and "Default Data" menu items |
| Settings → Organization | Add "Data Import" menu item |
| All seeded-data screens | Subtle "Seeded" badge on seeded records (optional) |

---

## 11. Business Rules

| ID | Rule | Description |
|---|---|---|
| BR-50-001 | Wizard launches on first login for new orgs | SetupStatus = 'NotStarted' → redirect to wizard |
| BR-50-002 | Wizard is resumable | Progress saved per step, admin can leave and return |
| BR-50-003 | Wizard can be skipped | Sets SetupStatus = 'Skipped', applies GLOBAL defaults |
| BR-50-004 | Seeding is idempotent | Same template applied twice never creates duplicates |
| BR-50-005 | Org data is org-scoped | No customization by any org affects any other org |
| BR-50-006 | Deletion guard on referenced data | Records used on documents cannot be deleted, only deactivated |
| BR-50-007 | Merge reset preserves custom | Adds missing defaults, keeps admin's additions and changes |
| BR-50-008 | Full reset blocked if in use | Cannot full-reset a category if any seeded record is referenced |
| BR-50-009 | Demo data is tagged | Every demo record has IsDemo = 1 |
| BR-50-010 | Demo cleanup is one-click | Deletes in reverse dependency order |
| BR-50-011 | Opening balance import must balance | Total debits must equal total credits |
| BR-50-012 | Import validates before executing | No row is imported until all rows are validated |
| BR-50-013 | Template version tracking | Each seed records the version number for upgrade tracking |
| BR-50-014 | Auto-complete checklist steps | Steps auto-check when the admin does the action (event-driven) |
| BR-50-015 | Super Admin changes don't affect existing tenants | Updating a master default only affects future tenants |

---

## 12. Test Scenarios

| ID | Scenario | Expected |
|---|---|---|
| TS-50-001 | New org → first login → wizard launches | Redirect to Step 1 |
| TS-50-002 | Select PK + Trading → preview shows 45 accounts, 6 tax codes | Correct counts |
| TS-50-003 | Complete wizard → all categories seeded | Records exist in all per-org tables |
| TS-50-004 | Second wizard run → no duplicates created | Same record count |
| TS-50-005 | Admin edits a seeded account → IsModified tracked | Category shows "Modified: Yes" |
| TS-50-006 | Admin adds custom tax code → total increases | Current > Seeded on dashboard |
| TS-50-007 | Reset (Merge) → missing defaults restored, custom kept | Custom code still exists |
| TS-50-008 | Reset (Overwrite) → seeded replaced, custom kept | Custom code still exists, seeded reverted |
| TS-50-009 | Reset (Full) on unused category → all deleted + re-seeded | Fresh data |
| TS-50-010 | Reset (Full) on used category → blocked | Error: "X records are referenced" |
| TS-50-011 | Org A edits data → Org B unaffected | Org B sees original seeded values |
| TS-50-012 | Skip wizard → GLOBAL defaults applied | Minimal skeleton works |
| TS-50-013 | Leave wizard at Step 3 → resume → starts at Step 3 | Progress saved |
| TS-50-014 | Import 500 customers from Excel → all created | 500 records in BusinessPartners |
| TS-50-015 | Import with 13 warnings → admin reviews → imports 487 | 487 imported, 13 skipped |
| TS-50-016 | Import opening balances → unbalanced → rejected | Error: "Debits do not equal credits" |
| TS-50-017 | Import opening balances → balanced → JE created | Opening Balance journal entry posted |
| TS-50-018 | Load demo data → 5 customers, 3 vendors, 20 products | All tagged IsDemo = 1 |
| TS-50-019 | Delete demo data → all demo records removed | IsDemo records gone, custom data untouched |
| TS-50-020 | Edit document template → PDF renders with changes | Updated logo, colors, footer |
| TS-50-021 | Edit email template → test email sent | Correct subject and body |
| TS-50-022 | Onboarding: create first customer → step auto-completes | Checkbox ticked |
| TS-50-023 | Dismiss checklist → widget hidden | Dashboard clean |
| TS-50-024 | Super Admin updates PK TAX defaults → existing PK orgs unaffected | Only new orgs get v4 |
| TS-50-025 | Fallback chain: AE + Manufacturing → AE + GLOBAL (tax) + GLOBAL + GLOBAL (CoA) | Correct fallback |

---

## 13. Development Phases & Effort

| Phase | Description | Includes | Effort |
|---|---|---|---|
| **P1** | Foundation: Setup Wizard + Industry Templates | Entities (SetupDefaults, TenantSetupLog, OnboardingChecklist), ITenantSetupService, Setup Wizard UI (7 steps), Super Admin management screens, 4 country/industry template packs (PK Trading, AE Trading, SA Trading, Global), IsSeeded flags, migrations M50_001–M50_005, M50_010–M50_013 | 15 days |
| **P2** | Data Import | DataImportJobs entity, IDataImportService, Import Wizard UI (6 steps), Excel template generation, column auto-mapping, validation engine, import execution, import history, migration M50_006 | 8 days |
| **P3** | Templates, Demo Data & Onboarding | DocumentTemplates + EmailTemplates entities, template editors, PDF rendering integration, email rendering, sample data loader + cleanup, onboarding checklist widget, auto-complete event handlers, migrations M50_007–M50_009, M50_014–M50_015 | 10 days |
| **P4** | Testing & Documentation | All 25 test scenarios, integration tests, multi-tenant isolation test, API documentation, admin guides, template data review | 7 days |
| **Total** | | | **40 days** |

---

## 14. Appendix A: Initial Template Pack — Pakistan Trading

The Pakistan Trading template pack (`CountryCode = 'PK'`, `IndustryType = 'Trading'`) is the first and most complete. It includes:

| Category | Records | Source |
|---|---|---|
| COA | 45 accounts | Standard Pakistan trading CoA |
| TAX | 6 codes | FBR GST/WHT rates (2026) |
| CURRENCY | 6 currencies | PKR (base) + USD, AED, SAR, GBP, EUR |
| FISCAL | Jul–Jun, monthly | Pakistan fiscal year |
| PAYMENT_TERMS | 5 terms | COD, Net 15/30/60, 2/10 Net 30 |
| UOM | 16 units in 4 groups | Weight, Count, Volume, Length |
| PRODUCT_CATEGORIES | 8 categories | Raw Materials, Finished Goods, Packaging, Consumables, Services, Spare Parts, Trading Goods, Other |
| APPROVAL_RULES | 2 rule sets | PO (3 levels), Expense (2 levels) |
| DOC_NUMBERING | 6 sequences | SO, PO, INV, QT, GRN, PV |
| ROLES | 8 roles | Admin, Sales Manager/Rep, Purchase Officer/Manager, Warehouse Operator/Manager, Finance Officer |
| WAREHOUSE | 4 zone types | Receiving, Storage, Dispatch, Quality Hold |
| DOC_TEMPLATES | 6 templates | Invoice, Quotation, PO, Delivery Note, Receipt, Statement |
| EMAIL_TEMPLATES | 7 templates | Invoice, Quotation, PO, Reminder, Payment, Welcome, Payslip |

---

## 15. Appendix B: Future Template Packs

| Template Code | Status | Notes |
|---|---|---|
| `DIST_PK` | Planned | Extends PK Trading + route/zone categories, vehicle tracking |
| `MFG_PK` | Planned | BOM accounts, WIP, production categories |
| `RETAIL_PK` | Planned | POS accounts, cash registers, shift settings |
| `SERVICE_PK` | Planned | Service revenue accounts, timesheet categories, no inventory |
| `TRADING_US` | Planned | USD, state sales tax placeholder, Jan-Dec fiscal |
| `TRADING_UK` | Planned | GBP, VAT 20%, Apr-Mar fiscal |

Each new pack is a set of rows in `core.SetupDefaults` — no code changes needed, just data.

---

**End of FSD — Addendum 50**
