# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 41 — CRM Module: Lead Management, Pipeline & Activity Tracking

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-041 |
| Version | 1.0 |
| Date | 2026-10-11 |
| Status | Draft — Pending Approval |
| Author | System Architect |
| Classification | Internal — Development |
| Supersedes | None — new module under `crm` schema |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 |
| Change Items | 10 new features |
| Impact | 14 migrations, 12 new entities, 2 modified entities, 3 new lookups, 8 new UI panels, 2 state machines |
| Depends On | Addendum 37 (Module Registry — CRM module activation), Addendum 32 (Sale Inquiry, Sale Quotation, Sale Order extensions) |
| Deferred to Addendum 42 | Embeddable Lead Widget, External System Sync (Odoo/SAP/Dynamics connectors), Lead Scoring Rules Engine, AI Conversation Intelligence |
| Naming Convention | New CRM entities follow Microsoft Dynamics 365 naming (Lead, Opportunity, Activity, Campaign). Existing SMS entities retain their names (SaleInquiry, SaleQuotation, SaleOrder, BusinessPartner). |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [CRM Pipeline Overview](#2-crm-pipeline-overview)
3. [C1 — CRM Module Activation & Schema](#3-c1--crm-module-activation--schema)
4. [C2 — Lead Sources & Campaigns](#4-c2--lead-sources--campaigns)
5. [C3 — Sales Teams](#5-c3--sales-teams)
6. [C4 — Leads](#6-c4--leads)
7. [C5 — Pipeline Stages](#7-c5--pipeline-stages)
8. [C6 — Lead Qualification & Conversion](#8-c6--lead-qualification--conversion)
9. [C7 — Opportunities](#9-c7--opportunities)
10. [C8 — Activities & Timeline](#10-c8--activities--timeline)
11. [C9 — Activity Documents](#11-c9--activity-documents)
12. [C10 — Timeline View, Export & Reporting](#12-c10--timeline-view-export--reporting)
13. [Database Migrations](#13-database-migrations)
14. [API Endpoints](#14-api-endpoints)
15. [UI Wireframes & Specifications](#15-ui-wireframes--specifications)
16. [Business Rules](#16-business-rules)
17. [Test Scenarios](#17-test-scenarios)
18. [Development Phases](#18-development-phases)

---

## 1. Purpose & Scope

**This addendum introduces the CRM Module — a complete Lead-to-Opportunity pipeline that captures unqualified prospects, tracks all sales conversations, and converts qualified leads into the existing Sales Pre-Order Pipeline (Sale Inquiry → Sale Quotation → Sale Order) defined in Addendum 32.**

The CRM module fills the gap before the Sale Inquiry stage. Currently, SMS requires a BusinessPartner (customer) to exist before a Sale Inquiry can be created. This addendum adds a **Lead** entity that holds prospect information without requiring a customer record, and a **qualification process** that creates the BusinessPartner and Opportunity when the lead is ready.

### 1.1 Design Principles

- **Dynamics 365 Naming & Field Compatibility** — New CRM entities use Microsoft Dynamics 365 naming conventions (Lead, Opportunity, Activity, Campaign) and carry Dynamics-compatible fields to support future data sync with Dynamics, Odoo, and SAP installations.
- **No Premature Customer Creation** — A Lead exists independently of BusinessPartner. Customer and Contact records are created only when the lead is qualified, never before.
- **Individual as Company** — Individual customers (B2C) are managed using the person's name as the company name, following Dynamics' personal account pattern. Every Lead converts to a BusinessPartner + SupplierContact regardless of whether the prospect represents a company or an individual.
- **Complete Conversation Capture** — Every phone call, meeting, email, note, and document exchange between the salesperson and the prospect is recorded in a unified Activity Timeline. This data feeds future analytics for sales gap analysis and product improvement insights.
- **Document Tracking Per Step** — Every document shared with or received from the customer (brochures, price lists, proposals, customer POs) is tracked with direction, stage context, and timestamp.
- **Multi-Company Contact Isolation** — The same physical person may appear as a contact at multiple companies. Lead data, conversations, and documents for Company A are never visible when viewing the same person's records at Company B. Isolation is enforced by the Account (BusinessPartner) FK on the Lead, not by the contact person record.
- **Cross-Reference Visibility** — When viewing a Lead, the UI shows other Leads for the same Account (company) and the same Contact Person, enabling salespeople to see relationship history and avoid duplicating effort.
- **Configurable Pipeline** — Pipeline stages are configurable per organization and optionally per sales team. Stages can require specific fields or activities before a Lead advances.
- **Timeline Export** — The complete conversation timeline can be viewed in the UI, exported as an Excel workbook, or generated as a formatted PDF report.
- **API-First Lead Ingestion** — Lead creation is exposed via a documented REST API with source attribution, laying the foundation for future embeddable widgets, Odoo/SAP/Dynamics connectors, and third-party integrations (Addendum 42).
- **Multi-Tenant by Default** — Every new entity carries `org_id` with EF Core `HasQueryFilter`. No exception.

### 1.2 Scope Summary

| Change ID | Title | Type | Impact |
|---|---|---|---|
| C1 | CRM Module Activation & Schema | Module Config | Activate CRM module in Module Catalog, create `crm` schema |
| C2 | Lead Sources & Campaigns | New Lookups + Entity | 2 new tables (LeadSources, Campaigns) |
| C3 | Sales Teams | New Entity | 2 new tables (SalesTeams, SalesTeamMembers) |
| C4 | Leads | New Entity + UI | 1 new table (Leads), 1 state machine, 2 new UI panels |
| C5 | Pipeline Stages | New Entity | 1 new table (PipelineStages) with configurable stages |
| C6 | Lead Qualification & Conversion | Business Logic | Conversion service creating BP + Contact + Opportunity from Lead |
| C7 | Opportunities | New Entity + UI | 1 new table (Opportunities), 1 state machine, 1 new UI panel |
| C8 | Activities & Timeline | New Entity + UI | 2 new tables (Activities, ActivityParticipants), 1 new UI panel |
| C9 | Activity Documents | New Entity | 1 new table (ActivityDocuments) |
| C10 | Timeline View, Export & Reporting | Feature + UI | Timeline panel, Excel export, PDF generation, cross-reference panel |

### 1.3 Relationship to Existing Entities

| Existing Entity | Schema | Addendum 41 Interaction |
|---|---|---|
| suppliers.BusinessPartners | suppliers | REFERENCED — Lead.account_id FK after qualification; created by qualification service |
| suppliers.SupplierContacts | suppliers | REFERENCED — Lead.contact_person_id FK after qualification; created by qualification service |
| demand.SaleInquiries | demand | REFERENCED — Opportunity.source_inquiry_id FK; Inquiry can be created from Opportunity |
| demand.SaleQuotations | demand | REFERENCED — Opportunity.source_quotation_id FK; Quotation can be created from Opportunity |
| demand.SaleOrders | demand | UNCHANGED — existing source_type extended with 'FROM_OPPORTUNITY' |
| core.ModuleCatalog | core | MODIFIED — CRM module row updated: is_available = 1 |
| core.FileStorage | core | REFERENCED — ActivityDocuments.file_id FK |
| auth.Users | auth | REFERENCED — Lead.assigned_to_user_id, Activity.performed_by_user_id, etc. |

### 1.4 Dynamics 365 Compatibility Matrix

This matrix maps Dynamics 365 Sales entities to SMS equivalents for future data sync.

| Dynamics 365 Entity | SMS Entity | Schema | Notes |
|---|---|---|---|
| Lead | crm.Leads | crm | New — this addendum |
| Account | suppliers.BusinessPartners | suppliers | Existing — `is_customer = true` |
| Contact | suppliers.SupplierContacts | suppliers | Existing — despite "Supplier" name, holds all BP contacts |
| Opportunity | crm.Opportunities | crm | New — this addendum |
| Activity (Phone Call, Appointment, Task, Email, Note) | crm.Activities | crm | New — this addendum |
| Campaign | crm.Campaigns | crm | New — this addendum |
| Quote | demand.SaleQuotations | demand | Existing — Addendum 32 |
| Order | demand.SaleOrders | demand | Existing |
| Invoice | finance.SalesInvoices | finance | Existing |
| Product | lookups.Products | lookups | Existing |

---

## 2. CRM Pipeline Overview

### 2.1 Complete Lead-to-Sale Pipeline

```
  ┌─────────────────────────────────────────────────────────────┐
  │                    CRM MODULE (NEW)                         │
  │                    Schema: crm                              │
  │                                                             │
  │  ┌──────────┐                                               │
  │  │  LEAD    │  Unqualified prospect                         │
  │  │ (LD-xxxx)│  Contact info, source, interest               │
  │  │          │  Activities: calls, meetings, notes            │
  │  └────┬─────┘                                               │
  │       │                                                     │
  │       │  Qualify                                             │
  │       │  ► Create/Link BusinessPartner (Account)            │
  │       │  ► Create/Link SupplierContact (Contact)            │
  │       │  ► Create Opportunity                               │
  │       │                                                     │
  │  ┌────▼──────────┐                                          │
  │  │  OPPORTUNITY  │  Qualified deal                          │
  │  │  (OP-xxxx)    │  Pipeline stages, revenue estimate       │
  │  │               │  Activities continue here                │
  │  └────┬──────────┘                                          │
  │       │                                                     │
  └───────┼─────────────────────────────────────────────────────┘
          │
  ┌───────┼─────────────────────────────────────────────────────┐
  │       │          EXISTING SCM PIPELINE                      │
  │       │          Schema: demand                             │
  │       │                                                     │
  │       ├─────────────────────────────┐                       │
  │       │                             │                       │
  │       ▼                             ▼                       │
  │  ┌──────────────┐           ┌──────────────────┐            │
  │  │ Sale Inquiry │ OPTIONAL  │  Sale Quotation  │ OPTIONAL   │
  │  │  (INQ-xxxx)  │           │   (SQ-xxxx)      │            │
  │  └──────┬───────┘           └──────┬───────────┘            │
  │         │                          │                        │
  │         └──────────┬───────────────┘                        │
  │                    ▼                                        │
  │           ┌──────────────────┐                              │
  │           │   Sale Order     │  REQUIRED                    │
  │           │   (SO-xxxx)      │                              │
  │           └──────┬───────────┘                              │
  │                  ▼                                          │
  │           ┌──────────────────┐                              │
  │           │  Sales Invoice   │                              │
  │           └──────────────────┘                              │
  └─────────────────────────────────────────────────────────────┘
```

### 2.2 Document Numbering

| Document | Pattern | Example | Auto-Generated |
|---|---|---|---|
| Lead | LD-{YYYY}-{NNNN} | LD-2026-0001 | Yes — per org, per year, sequential |
| Opportunity | OP-{YYYY}-{NNNN} | OP-2026-0001 | Yes — per org, per year, sequential |
| Sale Inquiry | INQ-{YYYY}-{NNNN} | INQ-2026-0001 | Yes — existing (Addendum 32) |
| Sale Quotation | SQ-{YYYY}-{NNNN} | SQ-2026-0001 | Yes — existing (Addendum 32) |
| Sale Order | SO-{NNNN} | SO-0001 | Yes — existing |

### 2.3 Creation Paths

The CRM pipeline adds new entry paths while preserving all existing direct-creation paths from Addendum 32.

```
  Path 1: Lead → Opportunity → Inquiry → Quotation → Sale Order  (full CRM chain)
  Path 2: Lead → Opportunity → Quotation → Sale Order            (skip inquiry)
  Path 3: Lead → Opportunity → Sale Order                         (direct from opportunity)
  Path 4: Inquiry → Quotation → Sale Order                        (existing, no CRM — Add 32)
  Path 5: Quotation → Sale Order                                  (existing — Add 32)
  Path 6: Sale Order                                               (existing direct entry)
```

---

## 3. C1 — CRM Module Activation & Schema

### 3.1 Overview

The CRM module was registered as a **future** module in the Module Catalog (Addendum 37) with `is_available = 0`. This change activates it and creates the `crm` database schema.

### 3.2 Module Catalog Update

Update the existing seed data row:

```sql
UPDATE core.ModuleCatalog
SET is_available = 1,
    display_name = 'CRM — Lead & Opportunity Management',
    description = 'Lead capture, qualification, pipeline tracking, and conversion to sales documents'
WHERE module_code = 'CRM';
```

### 3.3 Module Features

| Feature Code | Display Name | Is Core | Is Available | Description |
|---|---|---|---|---|
| LEAD_MANAGEMENT | Lead Management | Yes (core) | 1 | Lead capture, qualification, conversion |
| OPPORTUNITY_MANAGEMENT | Opportunity Management | Yes (core) | 1 | Deal tracking, pipeline stages, revenue forecasting |
| ACTIVITY_TRACKING | Activity & Timeline | Yes (core) | 1 | Conversation capture, meeting notes, document tracking |
| CAMPAIGN_TRACKING | Campaign Tracking | No (toggleable) | 1 | Lead source attribution, campaign management |
| LEAD_ASSIGNMENT | Lead Assignment Rules | No (toggleable) | 0 | Automatic lead routing (Phase 2) |
| LEAD_SCORING | Lead Scoring | No (toggleable) | 0 | Rule-based lead scoring (deferred to Addendum 42) |
| LEAD_WIDGET | Embeddable Lead Widget | No (toggleable) | 0 | External lead capture forms (deferred to Addendum 42) |
| EXTERNAL_SYNC | External CRM Sync | No (toggleable) | 0 | Odoo/SAP/Dynamics sync (deferred to Addendum 42) |

### 3.4 Schema Creation

```sql
-- M1: Create CRM schema
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'crm')
    EXEC('CREATE SCHEMA crm');
```

### 3.5 CRM Module Dependency

CRM depends on CUSTOMERS (always-on) per Addendum 37 dependency graph. No additional dependencies are introduced beyond what the Module Catalog already defines.

---

## 4. C2 — Lead Sources & Campaigns

### 4.1 Overview

Lead Sources are a configurable lookup defining how leads enter the system (Facebook, WhatsApp, website, walk-in, etc.). Campaigns are specific marketing initiatives that generate leads and carry attribution data.

### 4.2 Schema — LeadSources

**New Table:** `crm.LeadSources`

This is a tenant-configurable lookup. SMS ships seed data; organizations can add their own.

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| lead_source_id | INT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| source_code | NVARCHAR(30) | NOT NULL | | Machine-readable code: WEB_FORM, FACEBOOK, WHATSAPP, PHONE_CALL, WALK_IN, REFERRAL, EXISTING_CUSTOMER, IMPORT, CAMPAIGN, PARTNER, OTHER |
| source_name | NVARCHAR(100) | NOT NULL | | Display name |
| description | NVARCHAR(500) | NULL | | |
| is_active | BIT | NOT NULL | 1 | Soft deactivation |
| sort_order | INT | NOT NULL | 0 | Display sequence |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_LeadSources_OrgCode | org_id, source_code | Unique |

**Seed Data (per-org, inserted on CRM module activation):**

| source_code | source_name |
|---|---|
| WEB_FORM | Website Form |
| FACEBOOK | Facebook |
| INSTAGRAM | Instagram |
| WHATSAPP | WhatsApp |
| PHONE_CALL | Phone Call |
| WALK_IN | Walk-in |
| REFERRAL | Referral |
| EXISTING_CUSTOMER | Existing Customer |
| EMAIL | Email |
| LINKEDIN | LinkedIn |
| IMPORT | Data Import |
| PARTNER | Partner/Reseller |
| TRADE_SHOW | Trade Show / Exhibition |
| OTHER | Other |

### 4.3 Schema — Campaigns

**New Table:** `crm.Campaigns`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| campaign_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| campaign_code | NVARCHAR(30) | NOT NULL | | Unique code per org |
| campaign_name | NVARCHAR(200) | NOT NULL | | Display name |
| description | NVARCHAR(2000) | NULL | | Campaign details |
| campaign_type | NVARCHAR(30) | NOT NULL | 'GENERAL' | GENERAL, ADVERTISEMENT, PROMOTION, EVENT, EMAIL_BLAST, SOCIAL_MEDIA, REFERRAL_PROGRAM |
| lead_source_id | INT | NULL | FK → LeadSources | Default source for leads from this campaign |
| status | NVARCHAR(20) | NOT NULL | 'DRAFT' | DRAFT, ACTIVE, PAUSED, COMPLETED, CANCELLED |
| start_date | DATE | NULL | | Campaign start |
| end_date | DATE | NULL | | Campaign end |
| budget_amount | DECIMAL(18,2) | NULL | | Planned budget |
| currency_code | NVARCHAR(3) | NULL | FK → currencies | Budget currency |
| expected_leads | INT | NULL | | Target lead count |
| actual_leads | INT | NOT NULL | 0 | Running count, incremented on lead creation |
| external_reference | NVARCHAR(200) | NULL | | External campaign ID (Facebook Campaign ID, Google Ads ID, etc.) |
| notes | NVARCHAR(2000) | NULL | | |
| created_by | BIGINT | NOT NULL | FK → auth.Users | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_Campaigns_OrgCode | org_id, campaign_code | Unique |
| IX_Campaigns_Status | org_id, status | Non-unique, filtered |
| IX_Campaigns_Dates | org_id, start_date, end_date | Non-unique |

---

## 5. C3 — Sales Teams

### 5.1 Overview

Sales Teams group salespeople for lead assignment, pipeline management, and performance tracking. A salesperson can belong to multiple teams. Each team can have its own pipeline stages (optional — falls back to org-level stages if not configured).

### 5.2 Schema — SalesTeams

**New Table:** `crm.SalesTeams`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sales_team_id | INT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| team_code | NVARCHAR(30) | NOT NULL | | Unique code per org |
| team_name | NVARCHAR(100) | NOT NULL | | Display name |
| description | NVARCHAR(500) | NULL | | |
| team_leader_user_id | BIGINT | NULL | FK → auth.Users | Team lead / manager |
| is_active | BIT | NOT NULL | 1 | |
| default_campaign_id | BIGINT | NULL | FK → Campaigns | Default campaign for leads created by this team |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_SalesTeams_OrgCode | org_id, team_code | Unique |

### 5.3 Schema — SalesTeamMembers

**New Table:** `crm.SalesTeamMembers`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sales_team_member_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| sales_team_id | INT | NOT NULL | FK → SalesTeams | Team reference |
| user_id | BIGINT | NOT NULL | FK → auth.Users | Salesperson |
| role | NVARCHAR(20) | NOT NULL | 'MEMBER' | LEADER, MEMBER |
| is_active | BIT | NOT NULL | 1 | |
| joined_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_SalesTeamMembers_TeamUser | org_id, sales_team_id, user_id | Unique |
| IX_SalesTeamMembers_User | user_id | Non-unique |

---

## 6. C4 — Leads

### 6.1 Overview

A Lead is an unqualified prospect — someone who has expressed interest but has not yet been evaluated by the sales team. The Lead entity captures contact information, source attribution, and interest details WITHOUT requiring a BusinessPartner (customer) record to exist.

Leads follow Microsoft Dynamics 365 field naming and carry Dynamics-compatible qualification fields (BANT: Budget, Authority, Need, Timeline).

> **📝 NOTE:** For individual customers (B2C), the person's full name is stored in `company_name`. This follows the Dynamics personal-account pattern — every Lead has a company name, even if the company is the individual themselves.

### 6.2 Schema — Leads

**New Table:** `crm.Leads`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| lead_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| lead_number | NVARCHAR(20) | NOT NULL | Auto | Pattern: LD-{YYYY}-{NNNN}, unique per org |
| | | | | |
| **— Topic —** | | | | |
| topic | NVARCHAR(200) | NOT NULL | | Short subject (e.g. "AC + Ovens inquiry from Facebook") |
| description | NVARCHAR(4000) | NULL | | Detailed requirement / interest |
| | | | | |
| **— Contact Info (captured before BP exists) —** | | | | |
| salutation | NVARCHAR(10) | NULL | | Mr, Mrs, Ms, Dr, etc. |
| first_name | NVARCHAR(100) | NOT NULL | | Contact person first name |
| last_name | NVARCHAR(100) | NOT NULL | | Contact person last name |
| full_name | NVARCHAR(200) | NOT NULL | Computed | first_name + ' ' + last_name |
| job_title | NVARCHAR(100) | NULL | | Contact's job title / designation |
| email | NVARCHAR(200) | NULL | | Primary email |
| phone | NVARCHAR(30) | NULL | | Business phone |
| mobile_phone | NVARCHAR(30) | NULL | | Mobile phone |
| company_name | NVARCHAR(200) | NOT NULL | | Company name. For B2C individuals, use the person's full name. |
| website | NVARCHAR(500) | NULL | | Company website |
| address_line1 | NVARCHAR(200) | NULL | | |
| address_line2 | NVARCHAR(200) | NULL | | |
| city | NVARCHAR(100) | NULL | | |
| state_province | NVARCHAR(100) | NULL | | |
| postal_code | NVARCHAR(20) | NULL | | |
| country | NVARCHAR(100) | NULL | | |
| | | | | |
| **— Linked Records (populated during/after qualification) —** | | | | |
| account_id | BIGINT | NULL | FK → BusinessPartners | Linked company (set on qualify or manual link) |
| contact_person_id | BIGINT | NULL | FK → SupplierContacts | Linked contact person (set on qualify or manual link) |
| | | | | |
| **— Source Attribution —** | | | | |
| lead_source_id | INT | NULL | FK → LeadSources | How the lead found us |
| campaign_id | BIGINT | NULL | FK → Campaigns | Which campaign generated this lead |
| source_medium | NVARCHAR(50) | NULL | | UTM medium (social, email, cpc, organic) |
| source_reference | NVARCHAR(200) | NULL | | Ad ID, form ID, referrer name, UTM content |
| external_source_system | NVARCHAR(30) | NULL | | Source system for synced leads: ODOO, SAP, DYNAMICS, HUBSPOT, OTHER |
| external_source_id | NVARCHAR(200) | NULL | | Original record ID in the source system |
| | | | | |
| **— Qualification (Dynamics BANT) —** | | | | |
| budget_amount | DECIMAL(18,2) | NULL | | Customer's stated budget |
| budget_currency_code | NVARCHAR(3) | NULL | FK → currencies | |
| budget_confirmed | BIT | NOT NULL | 0 | Budget discussed and confirmed? |
| authority_confirmed | BIT | NOT NULL | 0 | Decision maker identified? |
| need_identified | BIT | NOT NULL | 0 | Genuine need established? |
| purchase_timeframe | NVARCHAR(20) | NULL | | IMMEDIATE, THIS_QUARTER, THIS_YEAR, NEXT_YEAR, UNKNOWN |
| | | | | |
| **— Rating & Priority —** | | | | |
| rating | NVARCHAR(10) | NOT NULL | 'WARM' | HOT, WARM, COLD |
| priority | NVARCHAR(10) | NOT NULL | 'NORMAL' | HIGH, NORMAL, LOW |
| | | | | |
| **— Ownership —** | | | | |
| assigned_to_user_id | BIGINT | NULL | FK → auth.Users | Assigned salesperson |
| assigned_at | DATETIME2 | NULL | | When assignment was made |
| sales_team_id | INT | NULL | FK → SalesTeams | Assigned team |
| previous_owner_user_id | BIGINT | NULL | FK → auth.Users | Last owner before reassignment |
| | | | | |
| **— Pipeline —** | | | | |
| pipeline_stage_id | INT | NULL | FK → PipelineStages | Current stage in configurable pipeline |
| | | | | |
| **— Status —** | | | | |
| status | NVARCHAR(20) | NOT NULL | 'NEW' | State machine: NEW → CONTACTED → QUALIFIED → DISQUALIFIED |
| status_reason | NVARCHAR(500) | NULL | | Reason (especially for disqualification) |
| qualified_at | DATETIME2 | NULL | | Timestamp of qualification |
| disqualified_at | DATETIME2 | NULL | | Timestamp of disqualification |
| converted_at | DATETIME2 | NULL | | Timestamp of conversion to Opportunity |
| | | | | |
| **— Conversion References —** | | | | |
| converted_account_id | BIGINT | NULL | FK → BusinessPartners | BP created/linked during conversion |
| converted_contact_id | BIGINT | NULL | FK → SupplierContacts | Contact created/linked during conversion |
| converted_opportunity_id | BIGINT | NULL | FK → Opportunities | Opportunity created during conversion |
| | | | | |
| **— Estimated Deal Value —** | | | | |
| estimated_revenue | DECIMAL(18,2) | NULL | | Expected deal value |
| estimated_close_date | DATE | NULL | | Expected close date |
| currency_code | NVARCHAR(3) | NULL | FK → currencies | Revenue currency |
| | | | | |
| **— Standard Audit —** | | | | |
| created_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |
| trace_id | UNIQUEIDENTIFIER | NOT NULL | NEWID() | Correlation ID |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_Leads_OrgNumber | org_id, lead_number | Unique |
| IX_Leads_Status | org_id, status | Non-unique, filtered (WHERE is_deleted = 0) |
| IX_Leads_AssignedTo | assigned_to_user_id | Non-unique, filtered (WHERE is_deleted = 0) |
| IX_Leads_SalesTeam | org_id, sales_team_id | Non-unique, filtered (WHERE is_deleted = 0) |
| IX_Leads_Campaign | campaign_id | Non-unique |
| IX_Leads_Account | account_id | Non-unique, filtered (WHERE account_id IS NOT NULL) |
| IX_Leads_ContactPerson | contact_person_id | Non-unique, filtered (WHERE contact_person_id IS NOT NULL) |
| IX_Leads_Email | org_id, email | Non-unique, filtered (WHERE email IS NOT NULL) — duplicate detection |
| IX_Leads_Phone | org_id, mobile_phone | Non-unique, filtered (WHERE mobile_phone IS NOT NULL) — duplicate detection |
| IX_Leads_CompanyName | org_id, company_name | Non-unique — duplicate detection |
| IX_Leads_CreatedAt | org_id, created_at DESC | Non-unique — list view sorting |
| IX_Leads_PipelineStage | org_id, pipeline_stage_id | Non-unique, filtered (WHERE is_deleted = 0) — Kanban grouping |

### 6.3 Lead State Machine

```
                        ┌─────────────────────┐
                        │        NEW          │
                        │                     │
                        │  Lead just created. │
                        │  Not yet contacted. │
                        └──────┬──────────────┘
                               │
              ┌────────────────┤
              │                │
              ▼                ▼
  ┌───────────────────┐  ┌───────────────────────┐
  │    CONTACTED      │  │    DISQUALIFIED       │
  │                   │  │                       │
  │  First contact    │  │  Not a valid prospect │
  │  has been made.   │  │  Reason recorded.     │
  │  Evaluation in    │  │                       │
  │  progress.        │  │  Terminal state.      │
  └──────┬────────────┘  └───────────────────────┘
         │                         ▲
         │                         │
         ├─────────────────────────┤
         │                         │
         ▼                         │
  ┌───────────────────┐            │
  │    QUALIFIED      │────────────┘
  │                   │  (can be disqualified
  │  BANT confirmed.  │   even after qualifying
  │  Ready to convert │   if deal falls through
  │  to Opportunity.  │   before conversion)
  │                   │
  └──────┬────────────┘
         │
         ▼
  ┌───────────────────┐
  │    CONVERTED      │
  │                   │
  │  Opportunity      │
  │  created.         │
  │  Lead is closed.  │
  │                   │
  │  Terminal state.  │
  └───────────────────┘
```

**Transition Rules:**

| From | To | Condition | Side Effects |
|---|---|---|---|
| NEW | CONTACTED | At least one Activity (call, meeting, email) recorded | Sets first_contact timestamp |
| NEW | DISQUALIFIED | Manual action by salesperson or manager | Records status_reason, disqualified_at |
| CONTACTED | QUALIFIED | Manual action; at least one BANT field confirmed recommended but not enforced | Records qualified_at |
| CONTACTED | DISQUALIFIED | Manual action | Records status_reason, disqualified_at |
| QUALIFIED | CONVERTED | Qualification service invoked (C6) | Creates BP + Contact + Opportunity; records converted_at, converted_*_id fields |
| QUALIFIED | DISQUALIFIED | Manual action (deal fell through before conversion) | Records status_reason, disqualified_at |

**Re-opening:** A DISQUALIFIED lead can be re-opened to NEW status by a user with `crm_reopen_lead` permission. A CONVERTED lead cannot be re-opened — the Opportunity is the active record.

---

## 7. C5 — Pipeline Stages

### 7.1 Overview

Pipeline Stages provide a configurable Kanban-style view of leads progressing through the sales process. Stages are configured per organization and optionally per sales team. If a team has no custom stages, it uses the organization's default stages.

### 7.2 Schema — PipelineStages

**New Table:** `crm.PipelineStages`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| pipeline_stage_id | INT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| stage_code | NVARCHAR(30) | NOT NULL | | Machine-readable code |
| stage_name | NVARCHAR(100) | NOT NULL | | Display name |
| stage_type | NVARCHAR(20) | NOT NULL | 'LEAD' | LEAD (pre-qualification stages) or OPPORTUNITY (post-qualification stages) |
| sales_team_id | INT | NULL | FK → SalesTeams | NULL = org-wide default; set = team-specific |
| sequence | INT | NOT NULL | 0 | Display order |
| win_probability | INT | NULL | | 0–100, displayed on pipeline view |
| is_won | BIT | NOT NULL | 0 | Marks the "Won" terminal stage |
| is_lost | BIT | NOT NULL | 0 | Marks the "Lost" terminal stage |
| description | NVARCHAR(500) | NULL | | |
| is_active | BIT | NOT NULL | 1 | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_PipelineStages_OrgTeamCode | org_id, sales_team_id, stage_code | Unique |
| IX_PipelineStages_Sequence | org_id, sales_team_id, stage_type, sequence | Non-unique |

**Seed Data (per-org, inserted on CRM module activation):**

| stage_code | stage_name | stage_type | sequence | win_probability |
|---|---|---|---|---|
| NEW_LEAD | New Lead | LEAD | 10 | 5 |
| INITIAL_CONTACT | Initial Contact | LEAD | 20 | 10 |
| QUALIFICATION | Qualification | LEAD | 30 | 20 |
| NEEDS_ANALYSIS | Needs Analysis | OPPORTUNITY | 40 | 40 |
| PROPOSAL | Proposal / Quotation | OPPORTUNITY | 50 | 60 |
| NEGOTIATION | Negotiation | OPPORTUNITY | 60 | 75 |
| WON | Won | OPPORTUNITY | 70 | 100 |
| LOST | Lost | OPPORTUNITY | 80 | 0 |

---

## 8. C6 — Lead Qualification & Conversion

### 8.1 Overview

When a Lead is qualified, the system creates (or links to existing) BusinessPartner and SupplierContact records, then creates an Opportunity in the CRM pipeline. This is the bridge between the CRM module and the existing SCM pipeline.

### 8.2 Conversion Process

**Service:** `ILeadConversionService.ConvertAsync(long leadId, LeadConversionOptions options)`

**Pseudocode:**

```csharp
public async Task<LeadConversionResult> ConvertAsync(long leadId, LeadConversionOptions options)
{
    var lead = await _leadRepository.GetByIdAsync(leadId);

    // 1. Validate lead status
    if (lead.Status != LeadStatus.QUALIFIED)
        throw new BusinessRuleException("Only QUALIFIED leads can be converted.");

    // 2. Create or Link Account (BusinessPartner)
    BusinessPartner account;
    if (options.ExistingAccountId.HasValue)
    {
        // Link to existing BusinessPartner
        account = await _bpRepository.GetByIdAsync(options.ExistingAccountId.Value);
        if (!account.IsCustomer)
        {
            account.IsCustomer = true;
            await _bpRepository.UpdateAsync(account);
        }
    }
    else
    {
        // Create new BusinessPartner
        account = new BusinessPartner
        {
            OrgId = lead.OrgId,
            PartnerName = lead.CompanyName,
            IsCustomer = true,
            IsVendor = false,
            Phone = lead.Phone,
            Email = lead.Email,
            Website = lead.Website,
            AddressLine1 = lead.AddressLine1,
            City = lead.City,
            StateProvince = lead.StateProvince,
            PostalCode = lead.PostalCode,
            Country = lead.Country,
            // ... other fields
        };
        await _bpRepository.CreateAsync(account);
    }

    // 3. Create or Link Contact Person (SupplierContact)
    SupplierContact contact;
    if (options.ExistingContactId.HasValue)
    {
        contact = await _contactRepository.GetByIdAsync(options.ExistingContactId.Value);
    }
    else
    {
        contact = new SupplierContact
        {
            OrgId = lead.OrgId,
            BusinessPartnerId = account.BusinessPartnerId,
            Salutation = lead.Salutation,
            FirstName = lead.FirstName,
            LastName = lead.LastName,
            JobTitle = lead.JobTitle,
            Email = lead.Email,
            Phone = lead.Phone,
            MobilePhone = lead.MobilePhone,
            // ... other fields
        };
        await _contactRepository.CreateAsync(contact);
    }

    // 4. Create Opportunity
    var opportunity = new Opportunity
    {
        OrgId = lead.OrgId,
        OpportunityNumber = await _numberingService.NextAsync("OP", lead.OrgId),
        Topic = lead.Topic,
        Description = lead.Description,
        AccountId = account.BusinessPartnerId,
        ContactPersonId = contact.SupplierContactId,
        SourceLeadId = lead.LeadId,
        EstimatedRevenue = lead.EstimatedRevenue,
        EstimatedCloseDate = lead.EstimatedCloseDate,
        CurrencyCode = lead.CurrencyCode,
        SalesTeamId = lead.SalesTeamId,
        AssignedToUserId = lead.AssignedToUserId,
        PipelineStageId = _defaultOpportunityStageId, // First OPPORTUNITY-type stage
        Status = OpportunityStatus.OPEN,
        // ... audit fields
    };
    await _opportunityRepository.CreateAsync(opportunity);

    // 5. Update Lead to CONVERTED
    lead.Status = LeadStatus.CONVERTED;
    lead.ConvertedAt = DateTime.UtcNow;
    lead.ConvertedAccountId = account.BusinessPartnerId;
    lead.ConvertedContactId = contact.SupplierContactId;
    lead.ConvertedOpportunityId = opportunity.OpportunityId;
    lead.AccountId = account.BusinessPartnerId;
    lead.ContactPersonId = contact.SupplierContactId;
    await _leadRepository.UpdateAsync(lead);

    // 6. Copy Activities from Lead to Opportunity
    // Activities remain linked to the Lead but are ALSO visible on the Opportunity
    // via the source_lead_id relationship. No duplication needed.

    // 7. Publish MediatR event
    await _mediator.Publish(new LeadConvertedEvent
    {
        LeadId = lead.LeadId,
        OpportunityId = opportunity.OpportunityId,
        AccountId = account.BusinessPartnerId,
        ContactId = contact.SupplierContactId,
        OrgId = lead.OrgId
    });

    return new LeadConversionResult
    {
        OpportunityId = opportunity.OpportunityId,
        AccountId = account.BusinessPartnerId,
        ContactId = contact.SupplierContactId,
        IsNewAccount = !options.ExistingAccountId.HasValue,
        IsNewContact = !options.ExistingContactId.HasValue
    };
}
```

### 8.3 Conversion Options

The conversion UI presents the salesperson with these choices:

| Option | Behavior |
|---|---|
| **Create new Account** | Creates a new BusinessPartner from Lead's company_name and address fields |
| **Link to existing Account** | Salesperson searches and selects an existing BusinessPartner. System shows potential matches based on company_name similarity. |
| **Create new Contact** | Creates a new SupplierContact under the selected/created Account |
| **Link to existing Contact** | Salesperson selects an existing SupplierContact under the selected Account. System shows contacts for the selected Account only. |

### 8.4 Duplicate Detection During Conversion

Before creating a new Account, the system checks for potential duplicates:

```sql
-- Potential duplicate check
SELECT TOP 5 business_partner_id, partner_name, phone, email
FROM suppliers.BusinessPartners
WHERE org_id = @OrgId
  AND is_deleted = 0
  AND (
      partner_name LIKE '%' + @CompanyName + '%'
      OR email = @Email
      OR phone = @Phone
  )
ORDER BY
    CASE WHEN partner_name = @CompanyName THEN 0 ELSE 1 END,
    partner_name;
```

If duplicates are found, the UI shows them and asks the salesperson to choose: create new or link existing.

### 8.5 Data Isolation Rule

**CRITICAL:** When the same physical person (e.g., Ahmed Khan) is a contact at Company A and Company B:

- Each company has its own SupplierContact row (different `contact_id`, same name, different `business_partner_id`)
- Leads for Company A use Company A's `contact_id`
- Leads for Company B use Company B's `contact_id`
- When viewing Ahmed's leads at Company A, the query filters by `account_id = CompanyA_Id`
- Company B's leads, conversations, and documents are NEVER returned

**The isolation is enforced at the Account (BusinessPartner) level, not the Contact Person level.** The Account FK on the Lead is the primary filter, with Contact as a secondary scope.

---

## 9. C7 — Opportunities

### 9.1 Overview

An Opportunity represents a qualified deal — the bridge between the CRM Lead and the existing SCM Sale Inquiry / Quotation pipeline. Opportunities are always created by the Lead Conversion process (C6); they cannot be created standalone.

An Opportunity tracks the deal through pipeline stages, estimates revenue and close date, and links to the Sale Inquiry, Quotation, and/or Sale Order that the deal produces.

### 9.2 Schema — Opportunities

**New Table:** `crm.Opportunities`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| opportunity_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| opportunity_number | NVARCHAR(20) | NOT NULL | Auto | Pattern: OP-{YYYY}-{NNNN}, unique per org |
| | | | | |
| **— Topic —** | | | | |
| topic | NVARCHAR(200) | NOT NULL | | Deal subject (carried from Lead) |
| description | NVARCHAR(4000) | NULL | | Detailed requirement |
| | | | | |
| **— Account & Contact —** | | | | |
| account_id | BIGINT | NOT NULL | FK → BusinessPartners | Company (must exist — created during Lead conversion) |
| contact_person_id | BIGINT | NULL | FK → SupplierContacts | Primary contact for this deal |
| | | | | |
| **— Source —** | | | | |
| source_lead_id | BIGINT | NOT NULL | FK → Leads | Lead that generated this Opportunity |
| campaign_id | BIGINT | NULL | FK → Campaigns | Carried from Lead |
| | | | | |
| **— Pipeline —** | | | | |
| pipeline_stage_id | INT | NULL | FK → PipelineStages | Current stage (OPPORTUNITY-type stages) |
| status | NVARCHAR(20) | NOT NULL | 'OPEN' | OPEN, WON, LOST |
| | | | | |
| **— Revenue —** | | | | |
| estimated_revenue | DECIMAL(18,2) | NULL | | Expected deal value |
| actual_revenue | DECIMAL(18,2) | NULL | | Actual value when won (from SO total) |
| currency_code | NVARCHAR(3) | NULL | FK → currencies | |
| estimated_close_date | DATE | NULL | | Expected close date |
| actual_close_date | DATE | NULL | | Date when won or lost |
| | | | | |
| **— Win/Loss —** | | | | |
| win_loss_reason | NVARCHAR(500) | NULL | | Why won or lost |
| competitor_name | NVARCHAR(200) | NULL | | Lost to whom (if applicable) |
| | | | | |
| **— Ownership —** | | | | |
| assigned_to_user_id | BIGINT | NULL | FK → auth.Users | Salesperson owning this deal |
| sales_team_id | INT | NULL | FK → SalesTeams | Team |
| | | | | |
| **— Linked SCM Documents —** | | | | |
| source_inquiry_id | BIGINT | NULL | FK → SaleInquiries | Sale Inquiry created from this opportunity |
| source_quotation_id | BIGINT | NULL | FK → SaleQuotations | Sale Quotation created from this opportunity |
| source_order_id | BIGINT | NULL | FK → SaleOrders | Sale Order created from this opportunity |
| | | | | |
| **— Standard Audit —** | | | | |
| created_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |
| trace_id | UNIQUEIDENTIFIER | NOT NULL | NEWID() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_Opportunities_OrgNumber | org_id, opportunity_number | Unique |
| IX_Opportunities_Account | account_id | Non-unique |
| IX_Opportunities_Contact | contact_person_id | Non-unique, filtered |
| IX_Opportunities_SourceLead | source_lead_id | Non-unique |
| IX_Opportunities_Status | org_id, status | Non-unique, filtered |
| IX_Opportunities_PipelineStage | org_id, pipeline_stage_id | Non-unique, filtered |
| IX_Opportunities_AssignedTo | assigned_to_user_id | Non-unique, filtered |
| IX_Opportunities_CloseDate | org_id, estimated_close_date | Non-unique |

### 9.3 Opportunity State Machine

```
  ┌──────────┐
  │   OPEN   │  Active deal, moving through pipeline stages
  │          │
  └────┬─────┘
       │
  ┌────┼──────────────┐
  │                    │
  ▼                    ▼
┌──────┐          ┌────────┐
│ WON  │          │  LOST  │
│      │          │        │
│ Sale │          │ Reason │
│ Order│          │ and    │
│ made │          │ compet-│
│      │          │ itor   │
└──────┘          │ logged │
                  └────────┘
```

**Transition Rules:**

| From | To | Condition | Side Effects |
|---|---|---|---|
| OPEN | WON | Sale Order confirmed (status = CONFIRMED) linked to this opportunity | Sets actual_close_date, actual_revenue (from SO total), moves pipeline_stage to won-stage |
| OPEN | LOST | Manual action by salesperson | Requires win_loss_reason. Sets actual_close_date. Moves pipeline_stage to lost-stage |
| LOST | OPEN | Re-open by user with `crm_reopen_opportunity` permission | Clears actual_close_date, win_loss_reason, competitor_name |

**Opportunity → SCM Document Creation:**

From an open Opportunity, the salesperson can:

| Action | Creates | Link |
|---|---|---|
| "Create Inquiry" | demand.SaleInquiries | Sets opportunity.source_inquiry_id; inquiry.customer_id = opportunity.account_id |
| "Create Quotation" | demand.SaleQuotations | Sets opportunity.source_quotation_id; quotation.customer_id = opportunity.account_id |
| "Create Sale Order" | demand.SaleOrders | Sets opportunity.source_order_id; order.source_type = 'FROM_OPPORTUNITY' |

These actions use the existing Addendum 32 creation logic. The only additions are the source reference FKs back to the Opportunity.

### 9.4 Sale Order Extension

**Modified Table:** `demand.SaleOrders`

Add one new value to the `source_type` check constraint:

```sql
-- Existing values: MANUAL, FROM_QUOTATION, PORTAL, INTER_TENANT
-- Add: FROM_OPPORTUNITY
ALTER TABLE demand.SaleOrders
    DROP CONSTRAINT CK_SaleOrders_SourceType;

ALTER TABLE demand.SaleOrders
    ADD CONSTRAINT CK_SaleOrders_SourceType
    CHECK (source_type IN ('MANUAL', 'FROM_QUOTATION', 'PORTAL', 'INTER_TENANT', 'FROM_OPPORTUNITY'));
```

Add optional FK:

| Column | Type | Nullable | Description |
|---|---|---|---|
| source_opportunity_id | BIGINT | NULL | FK → crm.Opportunities — set when SO is created from an Opportunity |

---

## 10. C8 — Activities & Timeline

### 10.1 Overview

Activities capture every interaction between the salesperson and the prospect/customer. All activity types are stored in a single table with a `activity_type` discriminator, providing a unified timeline across the entire CRM lifecycle.

Activities are linked to a Lead and/or an Opportunity. When a Lead is converted, its activities remain linked to the Lead but are ALSO visible on the Opportunity through the Lead→Opportunity relationship. New activities after conversion are linked directly to the Opportunity.

### 10.2 Schema — Activities

**New Table:** `crm.Activities`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| activity_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| | | | | |
| **— Parent Reference —** | | | | |
| lead_id | BIGINT | NULL | FK → Leads | Linked lead (set for pre-conversion activities) |
| opportunity_id | BIGINT | NULL | FK → Opportunities | Linked opportunity (set for post-conversion activities) |
| | | | | |
| **— Activity Type —** | | | | |
| activity_type | NVARCHAR(30) | NOT NULL | | PHONE_CALL, MEETING, EMAIL_SENT, EMAIL_RECEIVED, NOTE, TASK, FOLLOW_UP, SITE_VISIT, DEMO, PRESENTATION, STATUS_CHANGE, DOCUMENT_SHARED, OTHER |
| | | | | |
| **— Content —** | | | | |
| subject | NVARCHAR(200) | NOT NULL | | Short title |
| description | NVARCHAR(MAX) | NULL | | Full conversation notes / meeting minutes / email body |
| outcome | NVARCHAR(30) | NULL | | SUCCESSFUL, UNSUCCESSFUL, NO_RESPONSE, FOLLOW_UP_NEEDED, REFERRED, COMPLETED, CANCELLED |
| outcome_notes | NVARCHAR(2000) | NULL | | |
| | | | | |
| **— Timing —** | | | | |
| activity_date | DATETIME2 | NOT NULL | SYSUTCDATETIME() | When the activity happened |
| duration_minutes | INT | NULL | | Call/meeting duration |
| | | | | |
| **— Scheduling —** | | | | |
| scheduled_date | DATETIME2 | NULL | | If this was a planned activity |
| due_date | DATETIME2 | NULL | | Deadline for tasks |
| completed_date | DATETIME2 | NULL | | When marked complete |
| is_completed | BIT | NOT NULL | 0 | |
| | | | | |
| **— Participants —** | | | | |
| performed_by_user_id | BIGINT | NOT NULL | FK → auth.Users | Salesperson who performed the activity |
| contact_person_id | BIGINT | NULL | FK → SupplierContacts | Customer contact involved |
| | | | | |
| **— Follow-up —** | | | | |
| follow_up_date | DATE | NULL | | Next action due date |
| follow_up_type | NVARCHAR(30) | NULL | | Same enum as activity_type |
| follow_up_notes | NVARCHAR(500) | NULL | | What to do next |
| | | | | |
| **— Context —** | | | | |
| pipeline_stage_id | INT | NULL | FK → PipelineStages | Which stage the lead/opportunity was in |
| | | | | |
| **— Standard Audit —** | | | | |
| created_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_by_user_id | BIGINT | NOT NULL | FK → auth.Users | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |

**Constraint:** At least one of `lead_id` or `opportunity_id` must be non-null:

```sql
ALTER TABLE crm.Activities
    ADD CONSTRAINT CK_Activities_ParentRef
    CHECK (lead_id IS NOT NULL OR opportunity_id IS NOT NULL);
```

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_Activities_Lead | lead_id, activity_date DESC | Non-unique, filtered (WHERE lead_id IS NOT NULL) |
| IX_Activities_Opportunity | opportunity_id, activity_date DESC | Non-unique, filtered (WHERE opportunity_id IS NOT NULL) |
| IX_Activities_PerformedBy | performed_by_user_id, activity_date DESC | Non-unique |
| IX_Activities_Type | org_id, activity_type, activity_date DESC | Non-unique |
| IX_Activities_FollowUp | org_id, follow_up_date | Non-unique, filtered (WHERE follow_up_date IS NOT NULL AND is_completed = 0) |
| IX_Activities_DueDate | org_id, due_date | Non-unique, filtered (WHERE due_date IS NOT NULL AND is_completed = 0) |
| IX_Activities_Scheduled | org_id, scheduled_date | Non-unique, filtered (WHERE scheduled_date IS NOT NULL AND is_completed = 0) |

### 10.3 Schema — ActivityParticipants

For meetings and calls with multiple participants (internal or external):

**New Table:** `crm.ActivityParticipants`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| activity_participant_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| activity_id | BIGINT | NOT NULL | FK → Activities | Parent activity |
| participant_type | NVARCHAR(20) | NOT NULL | | INTERNAL_USER, CUSTOMER_CONTACT, EXTERNAL |
| user_id | BIGINT | NULL | FK → auth.Users | For INTERNAL_USER type |
| contact_person_id | BIGINT | NULL | FK → SupplierContacts | For CUSTOMER_CONTACT type |
| external_name | NVARCHAR(200) | NULL | | For EXTERNAL type (e.g. third-party consultant) |
| external_email | NVARCHAR(200) | NULL | | |
| role | NVARCHAR(30) | NULL | | ORGANIZER, REQUIRED, OPTIONAL, ATTENDEE |

**Index:**

| Index | Columns | Type |
|---|---|---|
| IX_ActivityParticipants_Activity | activity_id | Non-unique |

---

## 11. C9 — Activity Documents

### 11.1 Overview

Every document shared with or received from the customer during the sales process is tracked with direction, context, and stage reference. Documents can be linked to a specific Activity (e.g., "brochure sent during the Oct 6 phone call") or to a Lead/Opportunity directly.

### 11.2 Schema — ActivityDocuments

**New Table:** `crm.ActivityDocuments`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| activity_document_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| | | | | |
| **— Parent References —** | | | | |
| activity_id | BIGINT | NULL | FK → Activities | Linked activity (which conversation) |
| lead_id | BIGINT | NULL | FK → Leads | Linked lead |
| opportunity_id | BIGINT | NULL | FK → Opportunities | Linked opportunity |
| | | | | |
| **— Document Info —** | | | | |
| document_type | NVARCHAR(30) | NOT NULL | 'OTHER' | BROCHURE, PRICE_LIST, PROPOSAL, SPECIFICATION, CONTRACT, CUSTOMER_PO, QUOTATION_PDF, PRESENTATION, IMAGE, MEETING_MINUTES, NDA, OTHER |
| document_name | NVARCHAR(200) | NOT NULL | | Display name |
| file_id | BIGINT | NOT NULL | FK → core.FileStorage | Stored file reference |
| direction | NVARCHAR(30) | NOT NULL | | SENT_TO_CUSTOMER, RECEIVED_FROM_CUSTOMER, INTERNAL |
| | | | | |
| **— Context —** | | | | |
| pipeline_stage_id | INT | NULL | FK → PipelineStages | Stage when document was shared |
| notes | NVARCHAR(500) | NULL | | |
| | | | | |
| **— Audit —** | | | | |
| shared_by_user_id | BIGINT | NOT NULL | FK → auth.Users | Who uploaded/shared |
| shared_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | When |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_ActivityDocuments_Activity | activity_id | Non-unique, filtered |
| IX_ActivityDocuments_Lead | lead_id | Non-unique, filtered |
| IX_ActivityDocuments_Opportunity | opportunity_id | Non-unique, filtered |

---

## 12. C10 — Timeline View, Export & Reporting

### 12.1 Overview

The Timeline is a unified chronological view of ALL interactions on a Lead or Opportunity, aggregating data from multiple sources into a single scrollable feed.

### 12.2 Timeline Data Sources

The timeline for a Lead or Opportunity merges the following records:

| Source | Displayed As | From Table |
|---|---|---|
| Activities (calls, meetings, emails, notes) | Activity cards with icon, subject, notes, outcome | crm.Activities |
| Documents shared/received | Document cards with filename, direction, type | crm.ActivityDocuments |
| Status changes | Status transition badges | Derived from Lead/Opportunity audit trail |
| Pipeline stage changes | Stage transition badges | Derived from Lead/Opportunity audit trail |
| Linked SCM documents | Reference cards (Inquiry created, Quotation sent, etc.) | demand.SaleInquiries, SaleQuotations, SaleOrders via FKs |
| Conversion event | Conversion card (Lead → Opportunity) | Lead.converted_at |

### 12.3 Timeline API

**Endpoint:** `GET /api/crm/leads/{id}/timeline`
**Endpoint:** `GET /api/crm/opportunities/{id}/timeline`

**Response:** Array of timeline entries sorted by date descending.

```json
{
  "timeline": [
    {
      "entry_type": "ACTIVITY",
      "activity_type": "PHONE_CALL",
      "date": "2026-10-05T14:30:00Z",
      "subject": "Initial contact with Ahmed Khan",
      "description": "Customer interested in 1.5-ton inverter AC...",
      "outcome": "FOLLOW_UP_NEEDED",
      "performed_by": { "user_id": 42, "name": "Salim Ahmed" },
      "contact_person": { "contact_id": 101, "name": "Ahmed Khan" },
      "duration_minutes": 15,
      "pipeline_stage": "Initial Contact",
      "documents": [],
      "follow_up": { "date": "2026-10-06", "type": "EMAIL_SENT", "notes": "Send product catalog" }
    },
    {
      "entry_type": "DOCUMENT",
      "document_type": "BROCHURE",
      "date": "2026-10-06T09:15:00Z",
      "document_name": "AC-Product-Catalog-2026.pdf",
      "direction": "SENT_TO_CUSTOMER",
      "shared_by": { "user_id": 42, "name": "Salim Ahmed" },
      "pipeline_stage": "Initial Contact"
    },
    {
      "entry_type": "STATUS_CHANGE",
      "date": "2026-10-08T11:00:00Z",
      "from_status": "CONTACTED",
      "to_status": "QUALIFIED",
      "changed_by": { "user_id": 42, "name": "Salim Ahmed" }
    }
  ],
  "total_count": 12,
  "page": 1,
  "page_size": 25
}
```

### 12.4 Timeline Export — Excel

**Endpoint:** `GET /api/crm/leads/{id}/timeline/export?format=xlsx`
**Endpoint:** `GET /api/crm/opportunities/{id}/timeline/export?format=xlsx`

Generates an Excel workbook with:

| Sheet | Contents |
|---|---|
| **Summary** | Lead/Opportunity header info, account, contact, source, current status, total activities count |
| **Timeline** | One row per timeline entry: Date, Type, Subject, Description, Outcome, Performed By, Contact, Duration, Stage, Follow-up Date, Follow-up Notes |
| **Documents** | One row per document: Date, Name, Type, Direction, Shared By, Stage, Notes |

### 12.5 Cross-Reference Panel

When viewing a Lead or Opportunity, the UI sidebar shows:

**"Other Leads for this Account"** — All leads (current and historical) for the same BusinessPartner (account_id), excluding the current record. Shows lead_number, topic, status, date.

**"Other Leads for this Contact"** — All leads where contact_person_id matches, AND account_id matches (data isolation — never show leads from other companies). Shows lead_number, topic, status, date.

**Data Isolation Enforcement:**

```sql
-- Other leads for same account
SELECT lead_number, topic, status, created_at
FROM crm.Leads
WHERE org_id = @OrgId
  AND account_id = @CurrentAccountId
  AND lead_id != @CurrentLeadId
  AND is_deleted = 0
ORDER BY created_at DESC;

-- Other leads for same contact at same account (NOT cross-company)
SELECT lead_number, topic, status, created_at
FROM crm.Leads
WHERE org_id = @OrgId
  AND contact_person_id = @CurrentContactPersonId
  AND account_id = @CurrentAccountId  -- ← CRITICAL: same company only
  AND lead_id != @CurrentLeadId
  AND is_deleted = 0
ORDER BY created_at DESC;
```

---

## 13. Database Migrations

| Migration | Description | Objects |
|---|---|---|
| M1 | Create `crm` schema | Schema |
| M2 | Create crm.LeadSources | Table + seed data |
| M3 | Create crm.Campaigns | Table + indexes |
| M4 | Create crm.SalesTeams | Table + indexes |
| M5 | Create crm.SalesTeamMembers | Table + indexes |
| M6 | Create crm.PipelineStages | Table + seed data |
| M7 | Create crm.Leads | Table + 12 indexes |
| M8 | Create crm.Opportunities | Table + 8 indexes |
| M9 | Create crm.Activities | Table + constraint + 7 indexes |
| M10 | Create crm.ActivityParticipants | Table + index |
| M11 | Create crm.ActivityDocuments | Table + 3 indexes |
| M12 | Update core.ModuleCatalog — CRM is_available = 1 | Update row |
| M13 | Update core.ModuleFeatures — insert CRM features | Insert rows |
| M14 | Alter demand.SaleOrders — add source_opportunity_id, update source_type constraint | Alter table |

**Migration Execution Order:** M1 → M2 → M3 → M4 → M5 → M6 → M7 → M8 → M9 → M10 → M11 → M12 → M13 → M14

---

## 14. API Endpoints

### 14.1 Lead Endpoints

| Method | Route | Description | Permission |
|---|---|---|---|
| GET | /api/crm/leads | List leads with filtering, sorting, pagination | crm_view_leads |
| GET | /api/crm/leads/{id} | Get lead detail | crm_view_leads |
| POST | /api/crm/leads | Create lead | crm_create_lead |
| PUT | /api/crm/leads/{id} | Update lead | crm_edit_lead |
| PATCH | /api/crm/leads/{id}/assign | Assign lead to user/team | crm_assign_lead |
| PATCH | /api/crm/leads/{id}/status | Change lead status | crm_edit_lead |
| POST | /api/crm/leads/{id}/qualify | Qualify lead (CONTACTED → QUALIFIED) | crm_qualify_lead |
| POST | /api/crm/leads/{id}/convert | Convert lead to Opportunity (C6 flow) | crm_convert_lead |
| POST | /api/crm/leads/{id}/disqualify | Disqualify lead | crm_disqualify_lead |
| POST | /api/crm/leads/{id}/reopen | Re-open disqualified lead | crm_reopen_lead |
| DELETE | /api/crm/leads/{id} | Soft delete | crm_delete_lead |
| GET | /api/crm/leads/{id}/timeline | Get unified timeline | crm_view_leads |
| GET | /api/crm/leads/{id}/timeline/export | Export timeline (xlsx/pdf) | crm_export_timeline |
| GET | /api/crm/leads/{id}/cross-references | Other leads for same account/contact | crm_view_leads |
| GET | /api/crm/leads/{id}/duplicate-check | Check for potential duplicate leads | crm_view_leads |
| GET | /api/crm/leads/kanban | Leads grouped by pipeline stage (Kanban) | crm_view_leads |

### 14.2 Opportunity Endpoints

| Method | Route | Description | Permission |
|---|---|---|---|
| GET | /api/crm/opportunities | List opportunities | crm_view_opportunities |
| GET | /api/crm/opportunities/{id} | Get opportunity detail | crm_view_opportunities |
| PUT | /api/crm/opportunities/{id} | Update opportunity | crm_edit_opportunity |
| PATCH | /api/crm/opportunities/{id}/stage | Change pipeline stage | crm_edit_opportunity |
| POST | /api/crm/opportunities/{id}/won | Mark as Won | crm_close_opportunity |
| POST | /api/crm/opportunities/{id}/lost | Mark as Lost (requires reason) | crm_close_opportunity |
| POST | /api/crm/opportunities/{id}/reopen | Re-open lost opportunity | crm_reopen_opportunity |
| POST | /api/crm/opportunities/{id}/create-inquiry | Create Sale Inquiry from Opportunity | crm_edit_opportunity + demand_create_inquiry |
| POST | /api/crm/opportunities/{id}/create-quotation | Create Sale Quotation from Opportunity | crm_edit_opportunity + demand_create_quotation |
| POST | /api/crm/opportunities/{id}/create-order | Create Sale Order from Opportunity | crm_edit_opportunity + demand_create_order |
| GET | /api/crm/opportunities/{id}/timeline | Get unified timeline | crm_view_opportunities |
| GET | /api/crm/opportunities/{id}/timeline/export | Export timeline (xlsx/pdf) | crm_export_timeline |
| GET | /api/crm/opportunities/kanban | Opportunities grouped by pipeline stage | crm_view_opportunities |

### 14.3 Activity Endpoints

| Method | Route | Description | Permission |
|---|---|---|---|
| GET | /api/crm/activities | List activities (filterable by lead, opportunity, user, type, date range) | crm_view_activities |
| GET | /api/crm/activities/{id} | Get activity detail | crm_view_activities |
| POST | /api/crm/activities | Create activity | crm_create_activity |
| PUT | /api/crm/activities/{id} | Update activity | crm_edit_activity |
| PATCH | /api/crm/activities/{id}/complete | Mark activity complete | crm_edit_activity |
| DELETE | /api/crm/activities/{id} | Soft delete | crm_delete_activity |
| POST | /api/crm/activities/{id}/documents | Attach document to activity | crm_create_activity |
| GET | /api/crm/activities/pending | Get pending follow-ups and tasks | crm_view_activities |

### 14.4 Campaign Endpoints

| Method | Route | Description | Permission |
|---|---|---|---|
| GET | /api/crm/campaigns | List campaigns | crm_view_campaigns |
| GET | /api/crm/campaigns/{id} | Get campaign detail with lead count | crm_view_campaigns |
| POST | /api/crm/campaigns | Create campaign | crm_manage_campaigns |
| PUT | /api/crm/campaigns/{id} | Update campaign | crm_manage_campaigns |
| DELETE | /api/crm/campaigns/{id} | Soft delete | crm_manage_campaigns |

### 14.5 Configuration Endpoints

| Method | Route | Description | Permission |
|---|---|---|---|
| GET | /api/crm/lead-sources | List lead sources | crm_view_leads |
| POST | /api/crm/lead-sources | Create custom lead source | crm_admin |
| PUT | /api/crm/lead-sources/{id} | Update lead source | crm_admin |
| GET | /api/crm/pipeline-stages | List pipeline stages | crm_view_leads |
| POST | /api/crm/pipeline-stages | Create pipeline stage | crm_admin |
| PUT | /api/crm/pipeline-stages/{id} | Update pipeline stage (name, sequence, probability) | crm_admin |
| DELETE | /api/crm/pipeline-stages/{id} | Deactivate stage (only if no leads in it) | crm_admin |
| GET | /api/crm/sales-teams | List sales teams | crm_view_leads |
| POST | /api/crm/sales-teams | Create team | crm_admin |
| PUT | /api/crm/sales-teams/{id} | Update team | crm_admin |
| POST | /api/crm/sales-teams/{id}/members | Add member | crm_admin |
| DELETE | /api/crm/sales-teams/{id}/members/{userId} | Remove member | crm_admin |

### 14.6 Public Lead Ingestion API (Foundation for Addendum 42)

This endpoint is designed for external lead creation — website forms, third-party integrations, and future embeddable widgets. It uses API key authentication instead of user sessions.

| Method | Route | Description | Auth |
|---|---|---|---|
| POST | /api/public/crm/leads | Create lead from external source | API Key (X-Api-Key header) |

**Request body:**

```json
{
  "first_name": "Ahmed",
  "last_name": "Khan",
  "company_name": "PakElectro",
  "email": "ahmed@pakelec.com",
  "phone": "+92-300-1234567",
  "topic": "Interested in inverter AC",
  "description": "Saw your Facebook ad for summer AC sale",
  "source_code": "FACEBOOK",
  "campaign_code": "SUMMER-AC-2026",
  "source_medium": "social",
  "source_reference": "fb-ad-12345",
  "external_source_system": null,
  "external_source_id": null
}
```

**Response:** Returns lead_number and lead_id. The lead is created with status = NEW and no assignment. Assignment rules (Addendum 42) will auto-assign; until then, a manager assigns manually.

**Rate Limiting:** 100 requests per minute per API key. Configurable per org.

---

## 15. UI Wireframes & Specifications

### 15.1 Lead List View

```
┌─────────────────────────────────────────────────────────────────┐
│  CRM > Leads                                      [+ New Lead] │
├─────────────────────────────────────────────────────────────────┤
│  [List View ▼]  [Kanban]     🔍 Search...                      │
│                                                                 │
│  Filters: [Status ▼] [Source ▼] [Team ▼] [Rating ▼] [Date ▼]  │
├──────┬──────────────────┬──────────┬────────┬───────┬──────────┤
│  #   │ Lead             │ Company  │ Source │Rating │ Status   │
├──────┼──────────────────┼──────────┼────────┼───────┼──────────┤
│LD-01 │ Ahmed Khan       │PakElectro│Facebook│ 🔴HOT │CONTACTED │
│      │ AC + Ovens inquiry│         │        │       │ Salim A. │
├──────┼──────────────────┼──────────┼────────┼───────┼──────────┤
│LD-02 │ Bilal Raza       │SunTech   │Walk-in │ 🟡WARM│  NEW     │
│      │ Server inquiry    │         │        │       │Unassigned│
├──────┼──────────────────┼──────────┼────────┼───────┼──────────┤
│LD-03 │ Sarah Malik      │Sarah M.  │WhatsApp│ 🔵COLD│QUALIFIED │
│      │ Mobile phone      │(B2C)    │        │       │ Faisal K.│
└──────┴──────────────────┴──────────┴────────┴───────┴──────────┘
```

### 15.2 Lead Kanban View

```
┌─────────────────────────────────────────────────────────────────┐
│  CRM > Leads > Kanban                              [+ New Lead]│
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│ ┌──New Lead──┐ ┌──Initial───┐ ┌──Qualifi──┐ ┌──Converted─┐    │
│ │  (5%)      │ │  Contact   │ │  cation   │ │  (100%)    │    │
│ │            │ │  (10%)     │ │  (20%)    │ │            │    │
│ │ ┌────────┐ │ │ ┌────────┐ │ │ ┌────────┐│ │ ┌────────┐ │    │
│ │ │LD-02   │ │ │ │LD-01   │ │ │ │LD-03   ││ │ │LD-04   │ │    │
│ │ │Bilal   │ │ │ │Ahmed   │ │ │ │Sarah   ││ │ │Hamza   │ │    │
│ │ │SunTech │ │ │ │PakElec │ │ │ │B2C     ││ │ │ABC Ltd │ │    │
│ │ │🟡 WARM │ │ │ │🔴 HOT  │ │ │ │🔵 COLD ││ │ │→OP-01  │ │    │
│ │ └────────┘ │ │ └────────┘ │ │ └────────┘│ │ └────────┘ │    │
│ │ ┌────────┐ │ │            │ │           │ │            │    │
│ │ │LD-05   │ │ │            │ │           │ │            │    │
│ │ │Fatima  │ │ │            │ │           │ │            │    │
│ │ │PhoneCo │ │ │            │ │           │ │            │    │
│ │ │🟡 WARM │ │ │            │ │           │ │            │    │
│ │ └────────┘ │ │            │ │           │ │            │    │
│ └────────────┘ └────────────┘ └───────────┘ └────────────┘    │
└─────────────────────────────────────────────────────────────────┘
```

### 15.3 Lead Detail View

```
┌─────────────────────────────────────────────────────────────────┐
│  ← Back   Lead LD-2026-0001                                    │
│                                                                 │
│  ┌─[Qualify]─┐  ┌─[Convert]─┐  ┌─[Disqualify]─┐  [Assign ▼]  │
│  └───────────┘  └───────────┘  └──────────────┘               │
│                                                                 │
│  Status: CONTACTED  │  Rating: 🔴 HOT  │  Stage: Initial Contact│
├───────────────────────────────┬─────────────────────────────────┤
│                               │                                 │
│  CONTACT INFORMATION          │  SOURCE                         │
│  ─────────────────            │  ──────                         │
│  Name:    Ahmed Khan          │  Source:   Facebook              │
│  Title:   Procurement Mgr     │  Campaign: Summer AC Sale 2026  │
│  Company: PakElectro          │  Medium:   social               │
│  Email:   ahmed@pakelec.com   │  Ref:      fb-ad-12345          │
│  Phone:   +92-300-1234567     │                                 │
│  Mobile:  +92-321-7654321     │  QUALIFICATION (BANT)           │
│                               │  ─────────────────              │
│  ADDRESS                      │  Budget:  ☑ PKR 280,000        │
│  ───────                      │  Authority: ☑ Decision maker   │
│  123 Industrial Area          │  Need:      ☑ Identified       │
│  Lahore, Punjab 54000         │  Timeline:  THIS_QUARTER       │
│  Pakistan                     │                                 │
│                               │  DEAL ESTIMATE                  │
│  LINKED ACCOUNT               │  ─────────────                  │
│  ──────────────               │  Revenue:  PKR 280,000          │
│  ⚠ Not yet linked            │  Close:    Oct 30, 2026         │
│  (linked on conversion)      │                                 │
│                               │  OWNERSHIP                      │
│                               │  ─────────                      │
│                               │  Assigned: Salim Ahmed          │
│                               │  Team:     Retail Sales         │
│                               │  Since:    Oct 5, 2026          │
│                               │                                 │
├───────────────────────────────┴─────────────────────────────────┤
│                                                                 │
│  TOPIC                                                          │
│  ─────                                                          │
│  AC + Ovens inquiry from Facebook                               │
│                                                                 │
│  DESCRIPTION                                                    │
│  ───────────                                                    │
│  Customer responded to Summer AC Sale Facebook ad. Interested   │
│  in 1 inverter AC (1.5 ton), 2 ovens, and possibly a mobile    │
│  phone. Budget approximately PKR 280,000.                       │
│                                                                 │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌─ TIMELINE ──────────────────────────────────┐ ┌────────────┐│
│  │                              [Export ▼]     │ │ CROSS-REF  ││
│  │  [+ Call] [+ Meeting] [+ Note] [+ Task]    │ │            ││
│  │  [+ Email] [+ Document]                    │ │ Other leads ││
│  │                                            │ │ for PakElec:││
│  │  ● Oct 8 — MEETING (45 min)               │ │            ││
│  │    Salim + Faisal → Ahmed Khan             │ │ (none)     ││
│  │    PakElectro office                       │ │            ││
│  │    "Customer confirmed specs. Wants Haier  │ │ Other leads ││
│  │     1.5-ton inverter, 2x cooking range...  │ │ for Ahmed: ││
│  │     Requested formal quotation by Oct 10." │ │            ││
│  │    Outcome: ✅ Successful                   │ │ (none)     ││
│  │    📎 Meeting-Minutes-Oct8.docx (Internal) │ │            ││
│  │    Next: Create Quotation by Oct 10        │ │            ││
│  │                                            │ │            ││
│  │  ● Oct 6 — DOCUMENT SHARED                │ │            ││
│  │    → Customer: AC-Product-Catalog-2026.pdf │ │            ││
│  │    → Customer: Oven-Range-Brochure.pdf     │ │            ││
│  │    By: Salim Ahmed                         │ │            ││
│  │                                            │ │            ││
│  │  ● Oct 5 — PHONE CALL (15 min)            │ │            ││
│  │    Salim → Ahmed Khan                      │ │            ││
│  │    "Customer interested in 1.5-ton..."     │ │            ││
│  │    Outcome: Follow-up needed               │ │            ││
│  │    Next: Send catalog by Oct 6             │ │            ││
│  │                                            │ │            ││
│  │  ● Oct 5 — LEAD CREATED                   │ │            ││
│  │    Source: Facebook / Summer AC Sale 2026   │ │            ││
│  │    Assigned to: Salim Ahmed                │ │            ││
│  └────────────────────────────────────────────┘ └────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

### 15.4 Lead Conversion Dialog

```
┌─────────────────────────────────────────────────────┐
│  Convert Lead: LD-2026-0001                         │
│  Ahmed Khan — PakElectro                            │
├─────────────────────────────────────────────────────┤
│                                                     │
│  ACCOUNT (BusinessPartner)                          │
│  ─────────────────────────                          │
│  ○ Create new Account: "PakElectro"                 │
│  ● Link to existing Account                        │
│                                                     │
│  ┌─────────────────────────────────────────┐        │
│  │ 🔍 Search accounts...                  │        │
│  │                                         │        │
│  │ ⚠ Potential matches found:              │        │
│  │                                         │        │
│  │ ☑ PakElectro Pvt Ltd (BP-0042)         │        │
│  │   Phone: +92-42-35761234               │        │
│  │   Existing customer since 2024          │        │
│  │                                         │        │
│  │ ☐ Pakistan Electronics (BP-0089)       │        │
│  │   Phone: +92-42-35889900               │        │
│  └─────────────────────────────────────────┘        │
│                                                     │
│  CONTACT (SupplierContact)                          │
│  ─────────────────────────                          │
│  ○ Create new Contact: "Ahmed Khan"                 │
│  ● Link to existing Contact                        │
│                                                     │
│  ┌─────────────────────────────────────────┐        │
│  │ Contacts at PakElectro Pvt Ltd:         │        │
│  │                                         │        │
│  │ ☑ Ahmed Khan — Procurement Manager     │        │
│  │   ahmed@pakelec.com, +92-300-1234567   │        │
│  │                                         │        │
│  │ ☐ Farhan Ali — Director               │        │
│  │   farhan@pakelec.com                   │        │
│  └─────────────────────────────────────────┘        │
│                                                     │
│  OPPORTUNITY                                        │
│  ───────────                                        │
│  Topic:     AC + Ovens inquiry from Facebook        │
│  Revenue:   PKR 280,000                             │
│  Close by:  Oct 30, 2026                            │
│  Assign to: Salim Ahmed                             │
│                                                     │
│  ┌─────────┐  ┌──────────┐                          │
│  │ Convert │  │  Cancel  │                          │
│  └─────────┘  └──────────┘                          │
└─────────────────────────────────────────────────────┘
```

### 15.5 Opportunity Detail View

```
┌─────────────────────────────────────────────────────────────────┐
│  ← Back   Opportunity OP-2026-0001                              │
│                                                                 │
│  [Create Inquiry]  [Create Quotation]  [Create Order]           │
│  [Won ✓]  [Lost ✗]                                             │
│                                                                 │
│  ┌─Pipeline───────────────────────────────────────────────────┐ │
│  │ Needs Analysis ──► [Proposal] ──► Negotiation ──► Won     │ │
│  │                     ▲ current                              │ │
│  └────────────────────────────────────────────────────────────┘ │
│                                                                 │
│  Status: OPEN  │  Stage: Proposal (60%)  │  Est: PKR 280,000   │
├───────────────────────────────┬─────────────────────────────────┤
│                               │                                 │
│  ACCOUNT                      │  SOURCE                         │
│  ───────                      │  ──────                         │
│  PakElectro Pvt Ltd (BP-0042) │  Lead: LD-2026-0001            │
│  [View Account →]             │  Campaign: Summer AC Sale 2026  │
│                               │                                 │
│  CONTACT                      │  DEAL                           │
│  ───────                      │  ────                           │
│  Ahmed Khan                   │  Revenue: PKR 280,000           │
│  Procurement Manager          │  Close:   Oct 30, 2026          │
│  ahmed@pakelec.com            │  Prob:    60%                   │
│  +92-300-1234567              │                                 │
│                               │  LINKED DOCUMENTS               │
│  OWNERSHIP                    │  ────────────────               │
│  ─────────                    │  INQ-2026-0089 (Review Complete)│
│  Assigned: Salim Ahmed        │  SQ-2026-0045  (Sent)          │
│  Team:     Retail Sales       │  SO: (not yet created)         │
│                               │                                 │
├───────────────────────────────┴─────────────────────────────────┤
│                                                                 │
│  ┌─ TIMELINE ──────────────────────────────────────────────────┐│
│  │                                          [Export ▼]         ││
│  │  (Same timeline as Lead, plus post-conversion activities)   ││
│  │                                                             ││
│  │  ● Oct 10 — SALE QUOTATION CREATED                         ││
│  │    SQ-2026-0045 — PKR 280,000 — Sent to customer           ││
│  │    📎 SQ-2026-0045-Quotation.pdf (→ Customer)              ││
│  │                                                             ││
│  │  ● Oct 9 — SALE INQUIRY CREATED                            ││
│  │    INQ-2026-0089 — 3 lines, all CAN_SUPPLY                 ││
│  │                                                             ││
│  │  ● Oct 8 — LEAD CONVERTED TO OPPORTUNITY                   ││
│  │    Account: PakElectro Pvt Ltd (linked existing)            ││
│  │    Contact: Ahmed Khan (linked existing)                    ││
│  │                                                             ││
│  │  ─── Activities from Lead LD-2026-0001 ────                ││
│  │                                                             ││
│  │  ● Oct 8 — MEETING (45 min)                                ││
│  │    ... (carried from lead)                                  ││
│  └─────────────────────────────────────────────────────────────┘│
└─────────────────────────────────────────────────────────────────┘
```

### 15.6 Activity Creation Form

```
┌─────────────────────────────────────────────────────┐
│  Log Activity for LD-2026-0001                      │
├─────────────────────────────────────────────────────┤
│                                                     │
│  Type: [Phone Call ▼]                               │
│                                                     │
│  Subject: ______________________________________    │
│                                                     │
│  Date: [Oct 8, 2026]  Time: [14:30]                │
│  Duration: [45] minutes                             │
│                                                     │
│  Contact: [Ahmed Khan ▼] (from linked contacts)     │
│                                                     │
│  Description / Meeting Notes:                       │
│  ┌─────────────────────────────────────────────┐    │
│  │                                             │    │
│  │  (Rich text editor — full conversation      │    │
│  │   notes, meeting minutes, key points)       │    │
│  │                                             │    │
│  │                                             │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
│  Outcome: [Successful ▼]                            │
│  Outcome Notes: _________________________________   │
│                                                     │
│  Participants: [+ Add]                              │
│  ┌─────────────────────────────────────────────┐    │
│  │ ☑ Salim Ahmed (Internal — Organizer)       │    │
│  │ ☑ Faisal Khan (Internal — Attendee)        │    │
│  │ ☑ Ahmed Khan (Customer Contact)            │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
│  Documents: [📎 Attach files]                       │
│  ┌─────────────────────────────────────────────┐    │
│  │ Meeting-Minutes-Oct8.docx (Internal)       │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
│  FOLLOW-UP                                          │
│  ─────────                                          │
│  ☑ Schedule follow-up                              │
│  Date: [Oct 10, 2026]                               │
│  Type: [Task ▼]                                     │
│  Notes: Create and send formal quotation            │
│                                                     │
│  ┌──────┐  ┌────────┐                               │
│  │ Save │  │ Cancel │                               │
│  └──────┘  └────────┘                               │
└─────────────────────────────────────────────────────┘
```

---

## 16. Business Rules

### 16.1 Lead Rules

| # | Rule | Enforcement |
|---|---|---|
| BR-01 | Lead number (LD-{YYYY}-{NNNN}) is auto-generated per org, per year, sequential | Backend — NumberingService |
| BR-02 | `first_name`, `last_name`, and `company_name` are required on creation | Backend validation |
| BR-03 | For B2C individual leads, `company_name` must be set to the person's full name | UI guidance + backend default |
| BR-04 | Lead status transitions must follow the state machine (C4, section 6.3) | Backend — state machine service |
| BR-05 | Only QUALIFIED leads can be converted to Opportunities | Backend — ConvertAsync validates |
| BR-06 | A CONVERTED lead is terminal — cannot be edited, re-opened, or deleted | Backend |
| BR-07 | A DISQUALIFIED lead can only be re-opened by users with `crm_reopen_lead` permission | Backend + RBAC |
| BR-08 | Lead-to-CONTACTED transition requires at least one logged Activity | Backend validation |
| BR-09 | Duplicate detection runs on create and on convert (email, phone, company_name match) | Backend — advisory, not blocking |
| BR-10 | Lead assignment updates `assigned_at` and `previous_owner_user_id` | Backend |

### 16.2 Opportunity Rules

| # | Rule | Enforcement |
|---|---|---|
| BR-11 | Opportunities are ONLY created via Lead conversion — no standalone creation | Backend — no direct create endpoint |
| BR-12 | WON status requires a linked Sale Order with status ≥ CONFIRMED | Backend validation |
| BR-13 | LOST status requires `win_loss_reason` | Backend validation |
| BR-14 | LOST opportunities can be re-opened with `crm_reopen_opportunity` permission | Backend + RBAC |
| BR-15 | When an Opportunity creates a Sale Inquiry, `customer_id` on the Inquiry is auto-set from `account_id` | Backend |
| BR-16 | `actual_revenue` is computed from the linked Sale Order's grand total on WON | Backend |

### 16.3 Activity Rules

| # | Rule | Enforcement |
|---|---|---|
| BR-17 | Every Activity must be linked to at least one of: lead_id, opportunity_id | Database constraint |
| BR-18 | Activities on a Lead are visible on its converted Opportunity (via source_lead_id join) | Backend — timeline query |
| BR-19 | Activity `description` supports rich text (stored as HTML) for detailed meeting minutes | Frontend + backend |
| BR-20 | Follow-up date creates a pending task visible in the salesperson's activity dashboard | Frontend + query on follow_up_date |

### 16.4 Data Isolation Rules

| # | Rule | Enforcement |
|---|---|---|
| BR-21 | org_id tenant filter applies to ALL CRM entities via EF Core HasQueryFilter | EF Core global filter |
| BR-22 | Cross-reference queries for same contact MUST filter by account_id | Backend — hardcoded in query |
| BR-23 | Same person at multiple companies has separate SupplierContact rows; CRM isolation is at Account level | Architecture |

---

## 17. Test Scenarios

### 17.1 Lead Lifecycle

| # | Scenario | Expected Result |
|---|---|---|
| TS-01 | Create lead with all required fields | Lead created with status NEW, lead_number generated |
| TS-02 | Create B2C lead (no company) | company_name auto-set to full_name |
| TS-03 | Attempt status change NEW → QUALIFIED (no activities) | Rejected — must be CONTACTED first |
| TS-04 | Log a phone call on NEW lead | Lead status transitions to CONTACTED |
| TS-05 | Qualify CONTACTED lead | Status changes to QUALIFIED |
| TS-06 | Convert QUALIFIED lead (new account + new contact) | BP created, Contact created, Opportunity created, Lead = CONVERTED |
| TS-07 | Convert QUALIFIED lead (link existing account + existing contact) | No new BP/Contact; Opportunity created; Lead = CONVERTED |
| TS-08 | Convert lead with duplicate BP match | Duplicate warning shown; user chooses link or create |
| TS-09 | Disqualify lead | Status = DISQUALIFIED, reason recorded |
| TS-10 | Re-open disqualified lead (with permission) | Status = NEW |
| TS-11 | Re-open disqualified lead (without permission) | Rejected — 403 |
| TS-12 | Edit CONVERTED lead | Rejected — terminal state |

### 17.2 Opportunity Lifecycle

| # | Scenario | Expected Result |
|---|---|---|
| TS-13 | Create Inquiry from Opportunity | Inquiry created with customer_id = opportunity.account_id, source linked |
| TS-14 | Create Quotation from Opportunity | Quotation created with customer_id = opportunity.account_id, source linked |
| TS-15 | Mark Opportunity as Won (SO confirmed) | Status = WON, actual_revenue set, actual_close_date set |
| TS-16 | Mark Opportunity as Won (no SO) | Rejected — requires linked confirmed SO |
| TS-17 | Mark Opportunity as Lost (with reason) | Status = LOST, reason recorded |
| TS-18 | Mark Opportunity as Lost (no reason) | Rejected — reason required |

### 17.3 Timeline & Activities

| # | Scenario | Expected Result |
|---|---|---|
| TS-19 | Get timeline for lead with activities + docs + status changes | All entries merged, sorted by date desc |
| TS-20 | Get timeline for opportunity (includes lead activities) | Lead activities shown below conversion line |
| TS-21 | Export timeline as Excel | Workbook generated with Summary, Timeline, Documents sheets |
| TS-22 | Attach document to activity | Document linked to activity and visible in timeline |

### 17.4 Data Isolation

| # | Scenario | Expected Result |
|---|---|---|
| TS-23 | Ahmed Khan at Company A — view cross-references | Only Company A leads shown |
| TS-24 | Ahmed Khan at Company B — view cross-references | Only Company B leads shown, Company A data invisible |
| TS-25 | Same org, different tenant | Tenant filter isolates all data |

---

## 18. Development Phases

### Phase 1 — Core CRM (This Addendum)

| Priority | Component | Estimated Effort |
|---|---|---|
| P1 | M1–M6: Schema + lookups + seed data | 2 days |
| P1 | M7: Leads table + CRUD API + List/Detail UI | 5 days |
| P1 | Lead state machine + status transitions | 2 days |
| P1 | M9–M10: Activities + CRUD API + Timeline UI | 5 days |
| P1 | M11: Activity Documents | 2 days |
| P1 | M8: Opportunities table + CRUD API + Detail UI | 3 days |
| P1 | C6: Lead Conversion Service + Conversion Dialog UI | 4 days |
| P1 | M14: SaleOrder extension (source_opportunity_id) | 1 day |
| P1 | Kanban view (Lead + Opportunity) | 3 days |
| P1 | Timeline export (Excel) | 2 days |
| P1 | Cross-reference panel | 1 day |
| P1 | RBAC permissions setup | 1 day |
| | **Phase 1 Total** | **~31 days** |

### Phase 2 — Assignment & Automation (Addendum 42)

| Component | Description |
|---|---|
| Lead Assignment Rules | Round-robin, territory, campaign-based, workload-based auto-assignment |
| SLA Enforcement | Auto-escalation if lead not contacted within X hours |
| Lead Scoring | Rule-based scoring engine |
| Follow-up Reminders | Hangfire jobs for pending follow-ups |
| Duplicate Detection Rules | Configurable matching rules beyond simple field comparison |
| Pipeline Analytics Dashboard | Win rate, conversion funnel, average deal time |

### Phase 3 — Integration & Widgets (Addendum 42 or 43)

| Component | Description |
|---|---|
| Embeddable Lead Widget | JavaScript snippet + widget configuration UI |
| Odoo Sync Connector | Import leads from Odoo via API |
| SAP Sync Connector | Import leads from SAP via API |
| Dynamics Sync Connector | Import leads from Dynamics via API |
| Webhook Outbound | Push lead events to external systems |
| Facebook Lead Ads Integration | Auto-create leads from Facebook Lead Ads |
| WhatsApp Business Integration | Auto-create leads from WhatsApp messages |
| Timeline PDF Export | Formatted PDF report generation |

---

## Appendix A — RBAC Permission Matrix

| Permission Code | Description | Default Roles |
|---|---|---|
| crm_view_leads | View leads list and detail | Sales Rep, Sales Manager, Admin |
| crm_create_lead | Create new leads | Sales Rep, Sales Manager, Admin |
| crm_edit_lead | Edit lead details | Sales Rep (own), Sales Manager, Admin |
| crm_assign_lead | Assign/reassign leads | Sales Manager, Admin |
| crm_qualify_lead | Qualify a lead | Sales Rep, Sales Manager, Admin |
| crm_convert_lead | Convert lead to opportunity | Sales Rep, Sales Manager, Admin |
| crm_disqualify_lead | Disqualify a lead | Sales Rep, Sales Manager, Admin |
| crm_reopen_lead | Re-open a disqualified lead | Sales Manager, Admin |
| crm_delete_lead | Soft-delete a lead | Sales Manager, Admin |
| crm_view_opportunities | View opportunities | Sales Rep, Sales Manager, Admin |
| crm_edit_opportunity | Edit opportunity details | Sales Rep (own), Sales Manager, Admin |
| crm_close_opportunity | Mark opportunity as Won/Lost | Sales Rep (own), Sales Manager, Admin |
| crm_reopen_opportunity | Re-open a lost opportunity | Sales Manager, Admin |
| crm_view_activities | View activities and timeline | Sales Rep, Sales Manager, Admin |
| crm_create_activity | Log new activities | Sales Rep, Sales Manager, Admin |
| crm_edit_activity | Edit activities | Sales Rep (own), Sales Manager, Admin |
| crm_delete_activity | Delete activities | Sales Manager, Admin |
| crm_export_timeline | Export timeline to Excel/PDF | Sales Rep, Sales Manager, Admin |
| crm_view_campaigns | View campaigns | Sales Rep, Sales Manager, Admin |
| crm_manage_campaigns | Create/edit/delete campaigns | Sales Manager, Admin |
| crm_admin | CRM configuration (pipeline stages, lead sources, teams) | Admin |

---

## Appendix B — MediatR Events

| Event | Published When | Payload |
|---|---|---|
| LeadCreatedEvent | New lead created | LeadId, OrgId, LeadSourceId, CampaignId |
| LeadAssignedEvent | Lead assigned/reassigned | LeadId, OrgId, NewUserId, PreviousUserId |
| LeadStatusChangedEvent | Lead status transition | LeadId, OrgId, OldStatus, NewStatus, Reason |
| LeadQualifiedEvent | Lead qualified | LeadId, OrgId |
| LeadConvertedEvent | Lead converted to Opportunity | LeadId, OpportunityId, AccountId, ContactId, OrgId |
| LeadDisqualifiedEvent | Lead disqualified | LeadId, OrgId, Reason |
| OpportunityCreatedEvent | Opportunity created (via conversion) | OpportunityId, LeadId, AccountId, OrgId |
| OpportunityStageChangedEvent | Pipeline stage changed | OpportunityId, OrgId, OldStageId, NewStageId |
| OpportunityWonEvent | Opportunity marked won | OpportunityId, OrgId, SaleOrderId, ActualRevenue |
| OpportunityLostEvent | Opportunity marked lost | OpportunityId, OrgId, Reason, Competitor |
| ActivityCreatedEvent | Activity logged | ActivityId, LeadId, OpportunityId, ActivityType, OrgId |

These events enable future cross-module integrations (notifications, dashboards, analytics) without tight coupling.

---

## Appendix C — Dynamics 365 Field Mapping Reference

For future data sync (Addendum 42), this table maps Dynamics fields to SMS fields:

| Dynamics Field | SMS Field | Entity |
|---|---|---|
| lead.topic | Leads.topic | crm.Leads |
| lead.firstname | Leads.first_name | crm.Leads |
| lead.lastname | Leads.last_name | crm.Leads |
| lead.jobtitle | Leads.job_title | crm.Leads |
| lead.emailaddress1 | Leads.email | crm.Leads |
| lead.telephone1 | Leads.phone | crm.Leads |
| lead.mobilephone | Leads.mobile_phone | crm.Leads |
| lead.companyname | Leads.company_name | crm.Leads |
| lead.websiteurl | Leads.website | crm.Leads |
| lead.budgetamount | Leads.budget_amount | crm.Leads |
| lead.decisionmaker | Leads.authority_confirmed | crm.Leads |
| lead.need | Leads.need_identified | crm.Leads |
| lead.purchasetimeframe | Leads.purchase_timeframe | crm.Leads |
| lead.leadqualitycode | Leads.rating | crm.Leads |
| lead.estimatedvalue | Leads.estimated_revenue | crm.Leads |
| lead.estimatedclosedate | Leads.estimated_close_date | crm.Leads |
| lead.leadsourcecode | Leads.lead_source_id | crm.Leads (via LeadSources lookup) |
| lead.campaignid | Leads.campaign_id | crm.Leads |
| lead.statuscode | Leads.status | crm.Leads |
| opportunity.name | Opportunities.topic | crm.Opportunities |
| opportunity.estimatedvalue | Opportunities.estimated_revenue | crm.Opportunities |
| opportunity.estimatedclosedate | Opportunities.estimated_close_date | crm.Opportunities |
| opportunity.stepname | Opportunities.pipeline_stage_id | crm.Opportunities (via PipelineStages) |
| opportunity.statuscode | Opportunities.status | crm.Opportunities |
| activitypointer.subject | Activities.subject | crm.Activities |
| activitypointer.description | Activities.description | crm.Activities |
| activitypointer.activitytypecode | Activities.activity_type | crm.Activities |
| activitypointer.scheduledstart | Activities.scheduled_date | crm.Activities |
| activitypointer.actualdurationminutes | Activities.duration_minutes | crm.Activities |

---

*End of Addendum 41*
