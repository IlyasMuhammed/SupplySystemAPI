# SMS Task Register — FSD Addendum 41

## CRM Module: Lead Management, Pipeline & Activity Tracking

| Property | Value |
|---|---|
| Document Ref | SMS-FSD-ADD-041 |
| Total Changes | 10 (C1–C10) |
| Migrations | 14 (M1–M14) |
| New Schema | 1 (`crm`) |
| New Tables | 12 |
| Modified Tables | 2 (core.ModuleCatalog, demand.SaleOrders) |
| API Endpoints | 50+ across 6 groups |
| Permissions | 21 new |
| Business Rules | 23 |
| Test Scenarios | 25 |
| Dev Phases | 11 sub-phases (~31 days) |
| Depends On | SMS-FSD-ADD-037 (Module Registry — CRM activation), SMS-FSD-ADD-032 (Sale Inquiry, Sale Quotation, Sale Order) |
| Naming Convention | Dynamics 365 for new CRM entities; existing SMS names retained |

---

## Phase 1A — CRM Schema + Module Activation (1 day)

### M1 — Create CRM Schema

- [ ] Execute `CREATE SCHEMA [crm]` (with `IF NOT EXISTS` guard)

### M12 — Update Module Catalog

- [ ] Update `core.ModuleCatalog` — set `is_available = 1` for CRM module row (module_code = 'CRM')
- [ ] Update `display_name` to `'CRM — Lead & Opportunity Management'`
- [ ] Update `description` to `'Lead capture, qualification, pipeline tracking, and conversion to sales documents'`

### M13 — Insert CRM Module Features

- [ ] Insert feature: LEAD_MANAGEMENT — is_core=1, is_available=1 — Lead capture, qualification, conversion
- [ ] Insert feature: OPPORTUNITY_MANAGEMENT — is_core=1, is_available=1 — Deal tracking, pipeline stages, revenue forecasting
- [ ] Insert feature: ACTIVITY_TRACKING — is_core=1, is_available=1 — Conversation capture, meeting notes, document tracking
- [ ] Insert feature: CAMPAIGN_TRACKING — is_core=0 (toggleable), is_available=1 — Lead source attribution, campaign management
- [ ] Insert feature: LEAD_ASSIGNMENT — is_core=0, is_available=0 — Automatic lead routing (Phase 2)
- [ ] Insert feature: LEAD_SCORING — is_core=0, is_available=0 — Rule-based lead scoring (deferred to Add 42)
- [ ] Insert feature: LEAD_WIDGET — is_core=0, is_available=0 — External lead capture forms (deferred to Add 42)
- [ ] Insert feature: EXTERNAL_SYNC — is_core=0, is_available=0 — Odoo/SAP/Dynamics sync (deferred to Add 42)

### C1 — CRM Module Registration (Code)

- [ ] Create `CrmDbContext` or extend existing DbContext with CRM entity configurations under schema `crm`
- [ ] Register CRM module in DI container / module startup pipeline
- [ ] Verify CRM module dependency on CUSTOMERS (always-on) per Addendum 37

---

## Phase 1B — Lead Sources & Campaigns (2 days)

### M2 — Create `crm.LeadSources` Table

- [ ] Create table `crm.LeadSources` with all 8 columns (`lead_source_id` INT PK IDENTITY, `org_id` INT NOT NULL, `source_code` NVARCHAR(30) NOT NULL, `source_name` NVARCHAR(100) NOT NULL, `description` NVARCHAR(500) NULL, `is_active` BIT NOT NULL DEFAULT 1, `sort_order` INT NOT NULL DEFAULT 0, `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME())
- [ ] Add FK `org_id → tenants`
- [ ] Add UNIQUE index `UQ_LeadSources_OrgCode (org_id, source_code)`
- [ ] Seed 14 lead sources per org on CRM activation: WEB_FORM, FACEBOOK, INSTAGRAM, WHATSAPP, PHONE_CALL, WALK_IN, REFERRAL, EXISTING_CUSTOMER, EMAIL, LINKEDIN, IMPORT, PARTNER, TRADE_SHOW, OTHER

### M3 — Create `crm.Campaigns` Table

- [ ] Create table `crm.Campaigns` with all 20 columns (`campaign_id` BIGINT PK IDENTITY, `org_id` INT NOT NULL, `campaign_code` NVARCHAR(30) NOT NULL, `campaign_name` NVARCHAR(200) NOT NULL, `description` NVARCHAR(2000) NULL, `campaign_type` NVARCHAR(30) NOT NULL DEFAULT 'GENERAL', `lead_source_id` INT NULL, `status` NVARCHAR(20) NOT NULL DEFAULT 'DRAFT', `start_date` DATE NULL, `end_date` DATE NULL, `budget_amount` DECIMAL(18,2) NULL, `currency_code` NVARCHAR(3) NULL, `expected_leads` INT NULL, `actual_leads` INT NOT NULL DEFAULT 0, `external_reference` NVARCHAR(200) NULL, `notes` NVARCHAR(2000) NULL, `created_by` BIGINT NOT NULL, `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `updated_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `is_deleted` BIT NOT NULL DEFAULT 0)
- [ ] Add FK `org_id → tenants`
- [ ] Add FK `lead_source_id → crm.LeadSources(lead_source_id)`
- [ ] Add FK `currency_code → currencies`
- [ ] Add FK `created_by → auth.Users(user_id)`
- [ ] Add UNIQUE index `UQ_Campaigns_OrgCode (org_id, campaign_code)`
- [ ] Add filtered index `IX_Campaigns_Status (org_id, status)`
- [ ] Add index `IX_Campaigns_Dates (org_id, start_date, end_date)`
- [ ] Add CHECK constraint for `campaign_type` IN ('GENERAL', 'ADVERTISEMENT', 'PROMOTION', 'EVENT', 'EMAIL_BLAST', 'SOCIAL_MEDIA', 'REFERRAL_PROGRAM')
- [ ] Add CHECK constraint for `status` IN ('DRAFT', 'ACTIVE', 'PAUSED', 'COMPLETED', 'CANCELLED')

### C2 — Lead Source & Campaign Services

- [ ] Create `LeadSource` EF Core entity class with all 8 columns
- [ ] Create `Campaign` EF Core entity class with all 20 columns
- [ ] Configure EF entity mappings — schema `crm`, table names, FK relationships, query filters (org_id)
- [ ] Create `ILeadSourceService` interface — `GetAllAsync(long orgId)`, `CreateAsync`, `UpdateAsync`
- [ ] Implement `LeadSourceService`
- [ ] Create `ICampaignService` interface — `ListAsync(long orgId, ...)`, `GetByIdAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`
- [ ] Implement `CampaignService` — increment `actual_leads` on lead creation with campaign reference
- [ ] Register services in DI container

### Phase 1B — API Endpoints

- [ ] `GET /api/crm/lead-sources` — list lead sources (auth: `crm_view_leads`)
- [ ] `POST /api/crm/lead-sources` — create custom lead source (auth: `crm_admin`)
- [ ] `PUT /api/crm/lead-sources/{id}` — update lead source (auth: `crm_admin`)
- [ ] `GET /api/crm/campaigns` — list campaigns (auth: `crm_view_campaigns`)
- [ ] `GET /api/crm/campaigns/{id}` — get campaign detail with lead count (auth: `crm_view_campaigns`)
- [ ] `POST /api/crm/campaigns` — create campaign (auth: `crm_manage_campaigns`)
- [ ] `PUT /api/crm/campaigns/{id}` — update campaign (auth: `crm_manage_campaigns`)
- [ ] `DELETE /api/crm/campaigns/{id}` — soft delete campaign (auth: `crm_manage_campaigns`)

### Phase 1B — Unit Tests

- [ ] Test: LeadSources — 14 sources seeded per org on CRM activation
- [ ] Test: LeadSources — unique constraint on (org_id, source_code) prevents duplicates
- [ ] Test: Campaign creation with all required fields succeeds
- [ ] Test: Campaign duplicate code rejection per org
- [ ] Test: Campaign status transitions (DRAFT → ACTIVE → PAUSED → COMPLETED/CANCELLED)
- [ ] Test: Campaign.actual_leads incremented when lead created with campaign reference

---

## Phase 1C — Sales Teams (1 day)

### M4 — Create `crm.SalesTeams` Table

- [ ] Create table `crm.SalesTeams` with all 10 columns (`sales_team_id` INT PK IDENTITY, `org_id` INT NOT NULL, `team_code` NVARCHAR(30) NOT NULL, `team_name` NVARCHAR(100) NOT NULL, `description` NVARCHAR(500) NULL, `team_leader_user_id` BIGINT NULL, `is_active` BIT NOT NULL DEFAULT 1, `default_campaign_id` BIGINT NULL, `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `updated_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME())
- [ ] Add FK `org_id → tenants`
- [ ] Add FK `team_leader_user_id → auth.Users(user_id)`
- [ ] Add FK `default_campaign_id → crm.Campaigns(campaign_id)`
- [ ] Add UNIQUE index `UQ_SalesTeams_OrgCode (org_id, team_code)`

### M5 — Create `crm.SalesTeamMembers` Table

- [ ] Create table `crm.SalesTeamMembers` with all 7 columns (`sales_team_member_id` BIGINT PK IDENTITY, `org_id` INT NOT NULL, `sales_team_id` INT NOT NULL, `user_id` BIGINT NOT NULL, `role` NVARCHAR(20) NOT NULL DEFAULT 'MEMBER', `is_active` BIT NOT NULL DEFAULT 1, `joined_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME())
- [ ] Add FK `org_id → tenants`
- [ ] Add FK `sales_team_id → crm.SalesTeams(sales_team_id)`
- [ ] Add FK `user_id → auth.Users(user_id)`
- [ ] Add UNIQUE index `UQ_SalesTeamMembers_TeamUser (org_id, sales_team_id, user_id)`
- [ ] Add index `IX_SalesTeamMembers_User (user_id)`
- [ ] Add CHECK constraint for `role` IN ('LEADER', 'MEMBER')

### C3 — Sales Team Services

- [ ] Create `SalesTeam` EF Core entity class with all 10 columns
- [ ] Create `SalesTeamMember` EF Core entity class with all 7 columns
- [ ] Configure EF entity mappings — schema `crm`, FKs, query filters
- [ ] Create `ISalesTeamService` interface — `ListAsync`, `GetByIdAsync`, `CreateAsync`, `UpdateAsync`, `AddMemberAsync`, `RemoveMemberAsync`
- [ ] Implement `SalesTeamService`
- [ ] Register service in DI container

### Phase 1C — API Endpoints

- [ ] `GET /api/crm/sales-teams` — list sales teams (auth: `crm_view_leads`)
- [ ] `POST /api/crm/sales-teams` — create team (auth: `crm_admin`)
- [ ] `PUT /api/crm/sales-teams/{id}` — update team (auth: `crm_admin`)
- [ ] `POST /api/crm/sales-teams/{id}/members` — add member (auth: `crm_admin`)
- [ ] `DELETE /api/crm/sales-teams/{id}/members/{userId}` — remove member (auth: `crm_admin`)

### Phase 1C — Unit Tests

- [ ] Test: SalesTeam creation with unique code per org
- [ ] Test: SalesTeamMember unique constraint (team + user per org) prevents duplicate membership
- [ ] Test: Team leader role correctly assigned
- [ ] Test: A user can belong to multiple teams

---

## Phase 1D — Pipeline Stages (1 day)

### M6 — Create `crm.PipelineStages` Table

- [ ] Create table `crm.PipelineStages` with all 12 columns (`pipeline_stage_id` INT PK IDENTITY, `org_id` INT NOT NULL, `stage_code` NVARCHAR(30) NOT NULL, `stage_name` NVARCHAR(100) NOT NULL, `stage_type` NVARCHAR(20) NOT NULL DEFAULT 'LEAD', `sales_team_id` INT NULL, `sequence` INT NOT NULL DEFAULT 0, `win_probability` INT NULL, `is_won` BIT NOT NULL DEFAULT 0, `is_lost` BIT NOT NULL DEFAULT 0, `description` NVARCHAR(500) NULL, `is_active` BIT NOT NULL DEFAULT 1, `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME())
- [ ] Add FK `org_id → tenants`
- [ ] Add FK `sales_team_id → crm.SalesTeams(sales_team_id)`
- [ ] Add UNIQUE index `UQ_PipelineStages_OrgTeamCode (org_id, sales_team_id, stage_code)`
- [ ] Add index `IX_PipelineStages_Sequence (org_id, sales_team_id, stage_type, sequence)`
- [ ] Add CHECK constraint for `stage_type` IN ('LEAD', 'OPPORTUNITY')
- [ ] Add CHECK constraint for `win_probability` BETWEEN 0 AND 100

### M6 — Seed Default Pipeline Stages

- [ ] Seed 8 default pipeline stages (per-org, sales_team_id = NULL):
  - [ ] NEW_LEAD — stage_type=LEAD, sequence=10, win_probability=5
  - [ ] INITIAL_CONTACT — stage_type=LEAD, sequence=20, win_probability=10
  - [ ] QUALIFICATION — stage_type=LEAD, sequence=30, win_probability=20
  - [ ] NEEDS_ANALYSIS — stage_type=OPPORTUNITY, sequence=40, win_probability=40
  - [ ] PROPOSAL — stage_type=OPPORTUNITY, sequence=50, win_probability=60
  - [ ] NEGOTIATION — stage_type=OPPORTUNITY, sequence=60, win_probability=75
  - [ ] WON — stage_type=OPPORTUNITY, sequence=70, win_probability=100, is_won=1
  - [ ] LOST — stage_type=OPPORTUNITY, sequence=80, win_probability=0, is_lost=1

### C5 — Pipeline Stage Service

- [ ] Create `PipelineStage` EF Core entity class with all 12 columns
- [ ] Configure EF entity mapping — schema `crm`, FKs, query filter
- [ ] Create `IPipelineStageService` interface — `GetStagesAsync(long orgId, int? salesTeamId, string? stageType)`, `CreateAsync`, `UpdateAsync`, `DeactivateAsync`
- [ ] Implement `PipelineStageService` — falls back to org-level stages when no team-specific stages exist
- [ ] Implement `DeactivateAsync` — only if no leads/opportunities currently in that stage
- [ ] Register service in DI container

### Phase 1D — API Endpoints

- [ ] `GET /api/crm/pipeline-stages` — list pipeline stages (auth: `crm_view_leads`). Filter by stage_type and sales_team_id optional
- [ ] `POST /api/crm/pipeline-stages` — create pipeline stage (auth: `crm_admin`)
- [ ] `PUT /api/crm/pipeline-stages/{id}` — update pipeline stage name, sequence, probability (auth: `crm_admin`)
- [ ] `DELETE /api/crm/pipeline-stages/{id}` — deactivate stage, only if no leads in it (auth: `crm_admin`)

### Phase 1D — Unit Tests

- [ ] Test: 8 default stages seeded per org on CRM activation
- [ ] Test: Stages returned in sequence order
- [ ] Test: Team-specific stages override org-level stages for that team
- [ ] Test: Fallback to org-level when team has no custom stages
- [ ] Test: Deactivation rejected when leads exist in the stage
- [ ] Test: UNIQUE constraint on (org_id, sales_team_id, stage_code)

---

## Phase 1E — Leads Entity + CRUD (5 days)

### M7 — Create `crm.Leads` Table

- [ ] Create table `crm.Leads` with all columns per section 6.2:
  - [ ] `lead_id` BIGINT PK IDENTITY
  - [ ] `org_id` INT NOT NULL FK → tenants
  - [ ] `lead_number` NVARCHAR(20) NOT NULL — auto-generated LD-{YYYY}-{NNNN}
  - [ ] Topic columns: `topic` NVARCHAR(200) NOT NULL, `description` NVARCHAR(4000) NULL
  - [ ] Contact info: `salutation` NVARCHAR(10) NULL, `first_name` NVARCHAR(100) NOT NULL, `last_name` NVARCHAR(100) NOT NULL, `full_name` NVARCHAR(200) NOT NULL (computed), `job_title` NVARCHAR(100) NULL, `email` NVARCHAR(200) NULL, `phone` NVARCHAR(30) NULL, `mobile_phone` NVARCHAR(30) NULL, `company_name` NVARCHAR(200) NOT NULL, `website` NVARCHAR(500) NULL
  - [ ] Address: `address_line1` NVARCHAR(200), `address_line2` NVARCHAR(200), `city` NVARCHAR(100), `state_province` NVARCHAR(100), `postal_code` NVARCHAR(20), `country` NVARCHAR(100) — all NULL
  - [ ] Linked records: `account_id` BIGINT NULL FK → suppliers.BusinessPartners, `contact_person_id` BIGINT NULL FK → suppliers.SupplierContacts
  - [ ] Source attribution: `lead_source_id` INT NULL FK → crm.LeadSources, `campaign_id` BIGINT NULL FK → crm.Campaigns, `source_medium` NVARCHAR(50) NULL, `source_reference` NVARCHAR(200) NULL, `external_source_system` NVARCHAR(30) NULL, `external_source_id` NVARCHAR(200) NULL
  - [ ] BANT: `budget_amount` DECIMAL(18,2) NULL, `budget_currency_code` NVARCHAR(3) NULL FK → currencies, `budget_confirmed` BIT NOT NULL DEFAULT 0, `authority_confirmed` BIT NOT NULL DEFAULT 0, `need_identified` BIT NOT NULL DEFAULT 0, `purchase_timeframe` NVARCHAR(20) NULL
  - [ ] Rating & priority: `rating` NVARCHAR(10) NOT NULL DEFAULT 'WARM', `priority` NVARCHAR(10) NOT NULL DEFAULT 'NORMAL'
  - [ ] Ownership: `assigned_to_user_id` BIGINT NULL FK → auth.Users, `assigned_at` DATETIME2 NULL, `sales_team_id` INT NULL FK → crm.SalesTeams, `previous_owner_user_id` BIGINT NULL FK → auth.Users
  - [ ] Pipeline: `pipeline_stage_id` INT NULL FK → crm.PipelineStages
  - [ ] Status: `status` NVARCHAR(20) NOT NULL DEFAULT 'NEW', `status_reason` NVARCHAR(500) NULL, `qualified_at` DATETIME2 NULL, `disqualified_at` DATETIME2 NULL, `converted_at` DATETIME2 NULL
  - [ ] Conversion refs: `converted_account_id` BIGINT NULL FK → BusinessPartners, `converted_contact_id` BIGINT NULL FK → SupplierContacts, `converted_opportunity_id` BIGINT NULL FK → crm.Opportunities
  - [ ] Deal estimate: `estimated_revenue` DECIMAL(18,2) NULL, `estimated_close_date` DATE NULL, `currency_code` NVARCHAR(3) NULL FK → currencies
  - [ ] Audit: `created_by_user_id` BIGINT NOT NULL FK → auth.Users, `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `updated_by_user_id` BIGINT NOT NULL FK → auth.Users, `updated_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `is_deleted` BIT NOT NULL DEFAULT 0, `trace_id` UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()
- [ ] Add computed column `full_name` = `first_name + ' ' + last_name`
- [ ] Add CHECK constraint for `status` IN ('NEW', 'CONTACTED', 'QUALIFIED', 'DISQUALIFIED', 'CONVERTED')
- [ ] Add CHECK constraint for `rating` IN ('HOT', 'WARM', 'COLD')
- [ ] Add CHECK constraint for `priority` IN ('HIGH', 'NORMAL', 'LOW')
- [ ] Add CHECK constraint for `purchase_timeframe` IN ('IMMEDIATE', 'THIS_QUARTER', 'THIS_YEAR', 'NEXT_YEAR', 'UNKNOWN')
- [ ] Add all 12 indexes per section 6.2:
  - [ ] UNIQUE `UQ_Leads_OrgNumber (org_id, lead_number)`
  - [ ] Filtered `IX_Leads_Status (org_id, status) WHERE is_deleted = 0`
  - [ ] Filtered `IX_Leads_AssignedTo (assigned_to_user_id) WHERE is_deleted = 0`
  - [ ] Filtered `IX_Leads_SalesTeam (org_id, sales_team_id) WHERE is_deleted = 0`
  - [ ] `IX_Leads_Campaign (campaign_id)`
  - [ ] Filtered `IX_Leads_Account (account_id) WHERE account_id IS NOT NULL`
  - [ ] Filtered `IX_Leads_ContactPerson (contact_person_id) WHERE contact_person_id IS NOT NULL`
  - [ ] Filtered `IX_Leads_Email (org_id, email) WHERE email IS NOT NULL` — duplicate detection
  - [ ] Filtered `IX_Leads_Phone (org_id, mobile_phone) WHERE mobile_phone IS NOT NULL` — duplicate detection
  - [ ] `IX_Leads_CompanyName (org_id, company_name)` — duplicate detection
  - [ ] `IX_Leads_CreatedAt (org_id, created_at DESC)` — list view sorting
  - [ ] Filtered `IX_Leads_PipelineStage (org_id, pipeline_stage_id) WHERE is_deleted = 0` — Kanban grouping

### C4 — Lead Entity & Service

- [ ] Create `Lead` EF Core entity class with all columns from section 6.2
- [ ] Create `LeadStatus` enum: NEW=0, CONTACTED=1, QUALIFIED=2, DISQUALIFIED=3, CONVERTED=4
- [ ] Create `LeadRating` enum: HOT, WARM, COLD
- [ ] Create `LeadPriority` enum: HIGH, NORMAL, LOW
- [ ] Create `PurchaseTimeframe` enum: IMMEDIATE, THIS_QUARTER, THIS_YEAR, NEXT_YEAR, UNKNOWN
- [ ] Configure EF entity mapping — schema `crm`, table, all FKs, global query filter (org_id, is_deleted)
- [ ] Implement `full_name` as computed column in EF configuration
- [ ] Create `ILeadService` interface with methods:
  - [ ] `ListAsync(long orgId, LeadFilterParams filters)` — paginated, sortable
  - [ ] `GetByIdAsync(long orgId, long leadId)` — includes source, team, stage details
  - [ ] `CreateAsync(long orgId, long userId, CreateLeadDto dto)` — auto-generates lead_number
  - [ ] `UpdateAsync(long orgId, long leadId, UpdateLeadDto dto)` — reject if CONVERTED (BR-06)
  - [ ] `AssignAsync(long orgId, long leadId, long userId, int? teamId)` — sets assigned_at, previous_owner (BR-10)
  - [ ] `ChangeStatusAsync(long orgId, long leadId, LeadStatus newStatus, string? reason)` — validates state machine
  - [ ] `QualifyAsync(long orgId, long leadId)` — CONTACTED → QUALIFIED (BR-05)
  - [ ] `DisqualifyAsync(long orgId, long leadId, string reason)` — any non-terminal → DISQUALIFIED (BR-09)
  - [ ] `ReopenAsync(long orgId, long leadId)` — DISQUALIFIED → NEW (BR-07, permission check)
  - [ ] `DeleteAsync(long orgId, long leadId)` — soft delete
  - [ ] `GetKanbanAsync(long orgId, LeadKanbanFilters filters)` — grouped by pipeline_stage_id
  - [ ] `DuplicateCheckAsync(long orgId, string email, string phone, string companyName)` — advisory duplicates
- [ ] Implement `LeadService` with all methods
- [ ] Implement lead number generation: LD-{YYYY}-{NNNN} per org, per year, sequential — via existing NumberingService
- [ ] Implement state machine validation per section 6.3 transition rules:
  - [ ] NEW → CONTACTED requires at least one Activity (BR-08)
  - [ ] NEW → DISQUALIFIED allowed with reason
  - [ ] CONTACTED → QUALIFIED allowed (BANT recommended not enforced)
  - [ ] CONTACTED → DISQUALIFIED allowed with reason
  - [ ] QUALIFIED → CONVERTED via ConvertAsync only (C6)
  - [ ] QUALIFIED → DISQUALIFIED allowed with reason
  - [ ] DISQUALIFIED → NEW requires `crm_reopen_lead` permission (BR-07)
  - [ ] CONVERTED is terminal — reject all changes (BR-06)
- [ ] Implement B2C handling: if no company_name provided, default to full_name (BR-03)
- [ ] Register service in DI container
- [ ] Publish `LeadCreatedEvent` on create
- [ ] Publish `LeadAssignedEvent` on assign/reassign
- [ ] Publish `LeadStatusChangedEvent` on status transitions
- [ ] Publish `LeadQualifiedEvent` on qualify
- [ ] Publish `LeadDisqualifiedEvent` on disqualify

### Phase 1E — Lead API Endpoints

- [ ] `GET /api/crm/leads` — list leads with filtering (status, source, team, rating, date range), sorting, pagination (auth: `crm_view_leads`)
- [ ] `GET /api/crm/leads/{id}` — get lead detail (auth: `crm_view_leads`)
- [ ] `POST /api/crm/leads` — create lead (auth: `crm_create_lead`). Validate required: first_name, last_name, company_name, topic
- [ ] `PUT /api/crm/leads/{id}` — update lead (auth: `crm_edit_lead`). Reject if CONVERTED
- [ ] `PATCH /api/crm/leads/{id}/assign` — assign lead to user/team (auth: `crm_assign_lead`)
- [ ] `PATCH /api/crm/leads/{id}/status` — change lead status (auth: `crm_edit_lead`). Validate state machine
- [ ] `POST /api/crm/leads/{id}/qualify` — qualify lead (auth: `crm_qualify_lead`)
- [ ] `POST /api/crm/leads/{id}/disqualify` — disqualify lead (auth: `crm_disqualify_lead`). Require reason
- [ ] `POST /api/crm/leads/{id}/reopen` — re-open disqualified lead (auth: `crm_reopen_lead`)
- [ ] `DELETE /api/crm/leads/{id}` — soft delete (auth: `crm_delete_lead`)
- [ ] `GET /api/crm/leads/{id}/duplicate-check` — check for potential duplicate leads (auth: `crm_view_leads`)
- [ ] `GET /api/crm/leads/kanban` — leads grouped by pipeline stage for Kanban view (auth: `crm_view_leads`)

### Phase 1E — Unit Tests

- [ ] Test: Lead creation with all required fields — lead_number generated (LD-{YYYY}-{NNNN})
- [ ] Test: Lead creation — B2C with no company — company_name defaults to full_name (BR-03)
- [ ] Test: Lead creation — LeadCreatedEvent published
- [ ] Test: Lead creation — Campaign.actual_leads incremented when campaign_id provided
- [ ] Test: Lead status transition NEW → CONTACTED — requires at least one Activity (BR-08)
- [ ] Test: Lead status transition NEW → CONTACTED — rejected without activities
- [ ] Test: Lead status transition CONTACTED → QUALIFIED succeeds
- [ ] Test: Lead status transition NEW → QUALIFIED — rejected (must go through CONTACTED first) (TS-03)
- [ ] Test: Lead disqualification — status_reason recorded, disqualified_at set (TS-09)
- [ ] Test: Lead re-open — DISQUALIFIED → NEW with permission (TS-10)
- [ ] Test: Lead re-open — DISQUALIFIED → NEW without permission — 403 (TS-11)
- [ ] Test: CONVERTED lead edit — rejected (BR-06, TS-12)
- [ ] Test: Lead assignment — assigned_at and previous_owner_user_id updated (BR-10)
- [ ] Test: Lead assignment — LeadAssignedEvent published
- [ ] Test: Duplicate detection — matches on email, phone, company_name (BR-09)
- [ ] Test: Kanban grouping — leads grouped by pipeline_stage_id correctly
- [ ] Test: Pagination and sorting on lead list
- [ ] Test: Tenant isolation — org_id filter applies (BR-21)

---

## Phase 1F — Lead State Machine (2 days)

### Lead State Machine Service

- [ ] Create `ILeadStateMachineService` interface — `ValidateTransition(LeadStatus current, LeadStatus target)`, `GetAllowedTransitions(LeadStatus current)`
- [ ] Implement `LeadStateMachineService` with transition matrix per section 6.3
- [ ] Enforce state machine in `LeadService.ChangeStatusAsync`
- [ ] Create unit tests for all valid transitions
- [ ] Create unit tests for all invalid transitions (rejected with appropriate error message)

---

## Phase 1G — Activities & Timeline (5 days)

### M9 — Create `crm.Activities` Table

- [ ] Create table `crm.Activities` with all columns per section 10.2:
  - [ ] `activity_id` BIGINT PK IDENTITY
  - [ ] `org_id` INT NOT NULL FK → tenants
  - [ ] Parent refs: `lead_id` BIGINT NULL FK → crm.Leads, `opportunity_id` BIGINT NULL FK → crm.Opportunities
  - [ ] `activity_type` NVARCHAR(30) NOT NULL
  - [ ] Content: `subject` NVARCHAR(200) NOT NULL, `description` NVARCHAR(MAX) NULL, `outcome` NVARCHAR(30) NULL, `outcome_notes` NVARCHAR(2000) NULL
  - [ ] Timing: `activity_date` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `duration_minutes` INT NULL
  - [ ] Scheduling: `scheduled_date` DATETIME2 NULL, `due_date` DATETIME2 NULL, `completed_date` DATETIME2 NULL, `is_completed` BIT NOT NULL DEFAULT 0
  - [ ] Participants: `performed_by_user_id` BIGINT NOT NULL FK → auth.Users, `contact_person_id` BIGINT NULL FK → suppliers.SupplierContacts
  - [ ] Follow-up: `follow_up_date` DATE NULL, `follow_up_type` NVARCHAR(30) NULL, `follow_up_notes` NVARCHAR(500) NULL
  - [ ] Context: `pipeline_stage_id` INT NULL FK → crm.PipelineStages
  - [ ] Audit: `created_by_user_id` BIGINT NOT NULL, `created_at` DATETIME2 NOT NULL, `updated_by_user_id` BIGINT NOT NULL, `updated_at` DATETIME2 NOT NULL, `is_deleted` BIT NOT NULL DEFAULT 0
- [ ] Add CHECK constraint `CK_Activities_ParentRef` — `lead_id IS NOT NULL OR opportunity_id IS NOT NULL` (BR-17)
- [ ] Add CHECK constraint for `activity_type` IN ('PHONE_CALL', 'MEETING', 'EMAIL_SENT', 'EMAIL_RECEIVED', 'NOTE', 'TASK', 'FOLLOW_UP', 'SITE_VISIT', 'DEMO', 'PRESENTATION', 'STATUS_CHANGE', 'DOCUMENT_SHARED', 'OTHER')
- [ ] Add CHECK constraint for `outcome` IN ('SUCCESSFUL', 'UNSUCCESSFUL', 'NO_RESPONSE', 'FOLLOW_UP_NEEDED', 'REFERRED', 'COMPLETED', 'CANCELLED')
- [ ] Add all 7 indexes per section 10.2:
  - [ ] Filtered `IX_Activities_Lead (lead_id, activity_date DESC) WHERE lead_id IS NOT NULL`
  - [ ] Filtered `IX_Activities_Opportunity (opportunity_id, activity_date DESC) WHERE opportunity_id IS NOT NULL`
  - [ ] `IX_Activities_PerformedBy (performed_by_user_id, activity_date DESC)`
  - [ ] `IX_Activities_Type (org_id, activity_type, activity_date DESC)`
  - [ ] Filtered `IX_Activities_FollowUp (org_id, follow_up_date) WHERE follow_up_date IS NOT NULL AND is_completed = 0`
  - [ ] Filtered `IX_Activities_DueDate (org_id, due_date) WHERE due_date IS NOT NULL AND is_completed = 0`
  - [ ] Filtered `IX_Activities_Scheduled (org_id, scheduled_date) WHERE scheduled_date IS NOT NULL AND is_completed = 0`

### M10 — Create `crm.ActivityParticipants` Table

- [ ] Create table `crm.ActivityParticipants` with all 9 columns (`activity_participant_id` BIGINT PK IDENTITY, `activity_id` BIGINT NOT NULL, `participant_type` NVARCHAR(20) NOT NULL, `user_id` BIGINT NULL, `contact_person_id` BIGINT NULL, `external_name` NVARCHAR(200) NULL, `external_email` NVARCHAR(200) NULL, `role` NVARCHAR(30) NULL)
- [ ] Add FK `activity_id → crm.Activities(activity_id) ON DELETE CASCADE`
- [ ] Add FK `user_id → auth.Users(user_id)`
- [ ] Add FK `contact_person_id → suppliers.SupplierContacts(supplier_contact_id)`
- [ ] Add index `IX_ActivityParticipants_Activity (activity_id)`
- [ ] Add CHECK constraint for `participant_type` IN ('INTERNAL_USER', 'CUSTOMER_CONTACT', 'EXTERNAL')
- [ ] Add CHECK constraint for `role` IN ('ORGANIZER', 'REQUIRED', 'OPTIONAL', 'ATTENDEE')

### C8 — Activity & Timeline Services

- [ ] Create `Activity` EF Core entity class with all columns
- [ ] Create `ActivityParticipant` EF Core entity class with all 9 columns
- [ ] Create `ActivityType` enum: PHONE_CALL, MEETING, EMAIL_SENT, EMAIL_RECEIVED, NOTE, TASK, FOLLOW_UP, SITE_VISIT, DEMO, PRESENTATION, STATUS_CHANGE, DOCUMENT_SHARED, OTHER
- [ ] Create `ActivityOutcome` enum: SUCCESSFUL, UNSUCCESSFUL, NO_RESPONSE, FOLLOW_UP_NEEDED, REFERRED, COMPLETED, CANCELLED
- [ ] Create `ParticipantType` enum: INTERNAL_USER, CUSTOMER_CONTACT, EXTERNAL
- [ ] Configure EF entity mappings — schema, FKs, CASCADE on participants, query filters
- [ ] Create `IActivityService` interface:
  - [ ] `ListAsync(long orgId, ActivityFilterParams filters)` — filter by lead, opportunity, user, type, date range
  - [ ] `GetByIdAsync(long orgId, long activityId)` — with participants and documents
  - [ ] `CreateAsync(long orgId, long userId, CreateActivityDto dto)` — validates parent ref constraint
  - [ ] `UpdateAsync(long orgId, long activityId, UpdateActivityDto dto)`
  - [ ] `CompleteAsync(long orgId, long activityId)` — set is_completed, completed_date
  - [ ] `DeleteAsync(long orgId, long activityId)` — soft delete
  - [ ] `GetPendingFollowUpsAsync(long orgId, long? userId)` — follow_up_date IS NOT NULL AND is_completed = 0
- [ ] Implement `ActivityService`:
  - [ ] On create: if lead_id provided and lead.status = NEW, auto-transition lead to CONTACTED (BR-08)
  - [ ] Manage participants — create/update/delete in same transaction
  - [ ] Publish `ActivityCreatedEvent` on create
- [ ] Create `ITimelineService` interface:
  - [ ] `GetTimelineAsync(long orgId, long? leadId, long? opportunityId, int page, int pageSize)` — unified timeline
- [ ] Implement `TimelineService`:
  - [ ] Merge activities (from crm.Activities)
  - [ ] Merge documents (from crm.ActivityDocuments)
  - [ ] Merge status changes (derived from audit trail or status_change activities)
  - [ ] Merge pipeline stage changes (derived from audit trail)
  - [ ] Merge linked SCM documents (SaleInquiries, SaleQuotations, SaleOrders via opportunity FKs)
  - [ ] Merge conversion event (Lead.converted_at)
  - [ ] For opportunity timeline: include lead activities via source_lead_id (BR-18)
  - [ ] Sort all entries by date descending
  - [ ] Paginate
- [ ] Register services in DI container

### Phase 1G — Activity API Endpoints

- [ ] `GET /api/crm/activities` — list activities with filters (auth: `crm_view_activities`)
- [ ] `GET /api/crm/activities/{id}` — get activity detail with participants (auth: `crm_view_activities`)
- [ ] `POST /api/crm/activities` — create activity (auth: `crm_create_activity`). Validate lead_id or opportunity_id required
- [ ] `PUT /api/crm/activities/{id}` — update activity (auth: `crm_edit_activity`)
- [ ] `PATCH /api/crm/activities/{id}/complete` — mark complete (auth: `crm_edit_activity`)
- [ ] `DELETE /api/crm/activities/{id}` — soft delete (auth: `crm_delete_activity`)
- [ ] `GET /api/crm/activities/pending` — get pending follow-ups and tasks (auth: `crm_view_activities`)
- [ ] `GET /api/crm/leads/{id}/timeline` — get unified timeline for lead (auth: `crm_view_leads`)
- [ ] `GET /api/crm/opportunities/{id}/timeline` — get unified timeline for opportunity (auth: `crm_view_opportunities`)

### Phase 1G — Unit Tests

- [ ] Test: Activity creation with lead_id — succeeds
- [ ] Test: Activity creation with opportunity_id — succeeds
- [ ] Test: Activity creation with neither lead_id nor opportunity_id — rejected (BR-17)
- [ ] Test: Activity creation on NEW lead — auto-transitions lead to CONTACTED (BR-08)
- [ ] Test: Activity description supports NVARCHAR(MAX) rich text (BR-19)
- [ ] Test: ActivityCreatedEvent published on create
- [ ] Test: Activity complete — is_completed=true, completed_date set
- [ ] Test: Participants — INTERNAL_USER, CUSTOMER_CONTACT, EXTERNAL all supported
- [ ] Test: Timeline for lead — merges activities, documents, status changes, sorted by date desc (TS-19)
- [ ] Test: Timeline for opportunity — includes lead activities via source_lead_id (TS-20, BR-18)
- [ ] Test: Pending follow-ups — returns only uncompleted activities with follow_up_date (BR-20)
- [ ] Test: Timeline pagination works correctly

---

## Phase 1H — Activity Documents (2 days)

### M11 — Create `crm.ActivityDocuments` Table

- [ ] Create table `crm.ActivityDocuments` with all 14 columns per section 11.2:
  - [ ] `activity_document_id` BIGINT PK IDENTITY
  - [ ] `org_id` INT NOT NULL FK → tenants
  - [ ] Parent refs: `activity_id` BIGINT NULL FK → crm.Activities, `lead_id` BIGINT NULL FK → crm.Leads, `opportunity_id` BIGINT NULL FK → crm.Opportunities
  - [ ] Document: `document_type` NVARCHAR(30) NOT NULL DEFAULT 'OTHER', `document_name` NVARCHAR(200) NOT NULL, `file_id` BIGINT NOT NULL FK → core.FileStorage, `direction` NVARCHAR(30) NOT NULL
  - [ ] Context: `pipeline_stage_id` INT NULL FK → crm.PipelineStages, `notes` NVARCHAR(500) NULL
  - [ ] Audit: `shared_by_user_id` BIGINT NOT NULL FK → auth.Users, `shared_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `created_at` DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), `is_deleted` BIT NOT NULL DEFAULT 0
- [ ] Add CHECK constraint for `document_type` IN ('BROCHURE', 'PRICE_LIST', 'PROPOSAL', 'SPECIFICATION', 'CONTRACT', 'CUSTOMER_PO', 'QUOTATION_PDF', 'PRESENTATION', 'IMAGE', 'MEETING_MINUTES', 'NDA', 'OTHER')
- [ ] Add CHECK constraint for `direction` IN ('SENT_TO_CUSTOMER', 'RECEIVED_FROM_CUSTOMER', 'INTERNAL')
- [ ] Add filtered index `IX_ActivityDocuments_Activity (activity_id) WHERE activity_id IS NOT NULL`
- [ ] Add filtered index `IX_ActivityDocuments_Lead (lead_id) WHERE lead_id IS NOT NULL`
- [ ] Add filtered index `IX_ActivityDocuments_Opportunity (opportunity_id) WHERE opportunity_id IS NOT NULL`

### C9 — Activity Document Service

- [ ] Create `ActivityDocument` EF Core entity class with all 14 columns
- [ ] Create `DocumentType` enum: BROCHURE, PRICE_LIST, PROPOSAL, SPECIFICATION, CONTRACT, CUSTOMER_PO, QUOTATION_PDF, PRESENTATION, IMAGE, MEETING_MINUTES, NDA, OTHER
- [ ] Create `DocumentDirection` enum: SENT_TO_CUSTOMER, RECEIVED_FROM_CUSTOMER, INTERNAL
- [ ] Configure EF entity mapping — schema, FKs, query filter
- [ ] Create `IActivityDocumentService` interface — `ListByActivityAsync`, `ListByLeadAsync`, `ListByOpportunityAsync`, `AttachAsync`, `DeleteAsync`
- [ ] Implement `ActivityDocumentService`:
  - [ ] `AttachAsync` — upload file via FileStorage, create ActivityDocument record
  - [ ] Auto-set pipeline_stage_id from current lead/opportunity stage
- [ ] Register service in DI container

### Phase 1H — API Endpoint

- [ ] `POST /api/crm/activities/{id}/documents` — attach document to activity (auth: `crm_create_activity`). Multipart form: file + document_type + direction + notes

### Phase 1H — Unit Tests

- [ ] Test: Document attachment to activity — file stored, ActivityDocument created (TS-22)
- [ ] Test: Document direction tracking — SENT_TO_CUSTOMER, RECEIVED_FROM_CUSTOMER, INTERNAL
- [ ] Test: Documents appear in timeline for lead and opportunity
- [ ] Test: Pipeline stage auto-captured at time of sharing

---

## Phase 1I — Opportunities + Lead Conversion (7 days)

### M8 — Create `crm.Opportunities` Table

- [ ] Create table `crm.Opportunities` with all columns per section 9.2:
  - [ ] `opportunity_id` BIGINT PK IDENTITY
  - [ ] `org_id` INT NOT NULL FK → tenants
  - [ ] `opportunity_number` NVARCHAR(20) NOT NULL — auto-generated OP-{YYYY}-{NNNN}
  - [ ] Topic: `topic` NVARCHAR(200) NOT NULL, `description` NVARCHAR(4000) NULL
  - [ ] Account/Contact: `account_id` BIGINT NOT NULL FK → suppliers.BusinessPartners, `contact_person_id` BIGINT NULL FK → suppliers.SupplierContacts
  - [ ] Source: `source_lead_id` BIGINT NOT NULL FK → crm.Leads, `campaign_id` BIGINT NULL FK → crm.Campaigns
  - [ ] Pipeline: `pipeline_stage_id` INT NULL FK → crm.PipelineStages, `status` NVARCHAR(20) NOT NULL DEFAULT 'OPEN'
  - [ ] Revenue: `estimated_revenue` DECIMAL(18,2) NULL, `actual_revenue` DECIMAL(18,2) NULL, `currency_code` NVARCHAR(3) NULL FK → currencies, `estimated_close_date` DATE NULL, `actual_close_date` DATE NULL
  - [ ] Win/Loss: `win_loss_reason` NVARCHAR(500) NULL, `competitor_name` NVARCHAR(200) NULL
  - [ ] Ownership: `assigned_to_user_id` BIGINT NULL FK → auth.Users, `sales_team_id` INT NULL FK → crm.SalesTeams
  - [ ] Linked SCM: `source_inquiry_id` BIGINT NULL FK → demand.SaleInquiries, `source_quotation_id` BIGINT NULL FK → demand.SaleQuotations, `source_order_id` BIGINT NULL FK → demand.SaleOrders
  - [ ] Audit: `created_by_user_id`, `created_at`, `updated_by_user_id`, `updated_at`, `is_deleted`, `trace_id`
- [ ] Add CHECK constraint for `status` IN ('OPEN', 'WON', 'LOST')
- [ ] Add all 8 indexes per section 9.2:
  - [ ] UNIQUE `UQ_Opportunities_OrgNumber (org_id, opportunity_number)`
  - [ ] `IX_Opportunities_Account (account_id)`
  - [ ] Filtered `IX_Opportunities_Contact (contact_person_id)`
  - [ ] `IX_Opportunities_SourceLead (source_lead_id)`
  - [ ] Filtered `IX_Opportunities_Status (org_id, status)`
  - [ ] Filtered `IX_Opportunities_PipelineStage (org_id, pipeline_stage_id)`
  - [ ] Filtered `IX_Opportunities_AssignedTo (assigned_to_user_id)`
  - [ ] `IX_Opportunities_CloseDate (org_id, estimated_close_date)`

### M14 — Alter `demand.SaleOrders`

- [ ] Add column `source_opportunity_id` BIGINT NULL FK → crm.Opportunities
- [ ] Drop existing CHECK constraint `CK_SaleOrders_SourceType`
- [ ] Add new CHECK constraint: `source_type IN ('MANUAL', 'FROM_QUOTATION', 'PORTAL', 'INTER_TENANT', 'FROM_OPPORTUNITY')`

### C7 — Opportunity Service

- [ ] Create `Opportunity` EF Core entity class with all columns
- [ ] Create `OpportunityStatus` enum: OPEN, WON, LOST
- [ ] Configure EF entity mapping — schema `crm`, FKs, query filter
- [ ] Create `IOpportunityService` interface:
  - [ ] `ListAsync(long orgId, OpportunityFilterParams filters)` — paginated
  - [ ] `GetByIdAsync(long orgId, long opportunityId)` — full detail with linked documents
  - [ ] `UpdateAsync(long orgId, long opportunityId, UpdateOpportunityDto dto)`
  - [ ] `ChangeStageAsync(long orgId, long opportunityId, int newStageId)` — pipeline movement
  - [ ] `MarkWonAsync(long orgId, long opportunityId)` — requires linked SO confirmed (BR-12)
  - [ ] `MarkLostAsync(long orgId, long opportunityId, string reason, string? competitor)` — requires reason (BR-13)
  - [ ] `ReopenAsync(long orgId, long opportunityId)` — LOST → OPEN (BR-14, permission)
  - [ ] `CreateInquiryAsync(long orgId, long opportunityId)` — creates SaleInquiry from opportunity data (BR-15)
  - [ ] `CreateQuotationAsync(long orgId, long opportunityId)` — creates SaleQuotation from opportunity data
  - [ ] `CreateOrderAsync(long orgId, long opportunityId)` — creates SaleOrder with source_type=FROM_OPPORTUNITY
  - [ ] `GetKanbanAsync(long orgId, OpportunityKanbanFilters filters)`
- [ ] Implement `OpportunityService`:
  - [ ] No standalone create endpoint — opportunities only via Lead conversion (BR-11)
  - [ ] WON sets actual_close_date, actual_revenue from SO grand total (BR-16)
  - [ ] Publish `OpportunityStageChangedEvent` on stage change
  - [ ] Publish `OpportunityWonEvent` on won
  - [ ] Publish `OpportunityLostEvent` on lost
- [ ] Register service in DI container

### C6 — Lead Conversion Service

- [ ] Create `ILeadConversionService` interface — `ConvertAsync(long leadId, LeadConversionOptions options)`
- [ ] Create `LeadConversionOptions` class — ExistingAccountId?, ExistingContactId?
- [ ] Create `LeadConversionResult` class — OpportunityId, AccountId, ContactId, IsNewAccount, IsNewContact
- [ ] Implement `LeadConversionService.ConvertAsync` per section 8.2 pseudocode:
  - [ ] Validate lead status = QUALIFIED (BR-05)
  - [ ] Create or link Account (BusinessPartner):
    - [ ] If ExistingAccountId provided → link, ensure IsCustomer = true
    - [ ] Else → create new BP from lead fields (company_name, address, phone, email, website)
  - [ ] Create or link Contact (SupplierContact):
    - [ ] If ExistingContactId provided → link
    - [ ] Else → create new SupplierContact under the account (salutation, first_name, last_name, job_title, email, phone, mobile_phone)
  - [ ] Create Opportunity — auto-generate OP-{YYYY}-{NNNN}, carry topic, description, revenue, close date, team, assigned user, first OPPORTUNITY-type pipeline stage
  - [ ] Update Lead to CONVERTED — set converted_at, converted_account_id, converted_contact_id, converted_opportunity_id, account_id, contact_person_id
  - [ ] Publish `LeadConvertedEvent` and `OpportunityCreatedEvent`
  - [ ] Return LeadConversionResult
- [ ] Implement duplicate detection during conversion per section 8.4:
  - [ ] Query BusinessPartners for potential matches (partner_name LIKE, email match, phone match)
  - [ ] Return top 5 matches for UI display
- [ ] Enforce data isolation per section 8.5 (BR-22, BR-23):
  - [ ] SupplierContact rows are per-account
  - [ ] Lead isolation at account_id level
- [ ] Register service in DI container

### Phase 1I — Opportunity API Endpoints

- [ ] `GET /api/crm/opportunities` — list opportunities (auth: `crm_view_opportunities`)
- [ ] `GET /api/crm/opportunities/{id}` — get opportunity detail (auth: `crm_view_opportunities`)
- [ ] `PUT /api/crm/opportunities/{id}` — update opportunity (auth: `crm_edit_opportunity`)
- [ ] `PATCH /api/crm/opportunities/{id}/stage` — change pipeline stage (auth: `crm_edit_opportunity`)
- [ ] `POST /api/crm/opportunities/{id}/won` — mark as Won (auth: `crm_close_opportunity`). Validate linked SO
- [ ] `POST /api/crm/opportunities/{id}/lost` — mark as Lost (auth: `crm_close_opportunity`). Require reason
- [ ] `POST /api/crm/opportunities/{id}/reopen` — re-open (auth: `crm_reopen_opportunity`)
- [ ] `POST /api/crm/opportunities/{id}/create-inquiry` — create Sale Inquiry (auth: `crm_edit_opportunity` + `demand_create_inquiry`)
- [ ] `POST /api/crm/opportunities/{id}/create-quotation` — create Sale Quotation (auth: `crm_edit_opportunity` + `demand_create_quotation`)
- [ ] `POST /api/crm/opportunities/{id}/create-order` — create Sale Order (auth: `crm_edit_opportunity` + `demand_create_order`)
- [ ] `GET /api/crm/opportunities/{id}/timeline` — unified timeline (auth: `crm_view_opportunities`)
- [ ] `GET /api/crm/opportunities/kanban` — Kanban by pipeline stage (auth: `crm_view_opportunities`)
- [ ] `POST /api/crm/leads/{id}/convert` — convert lead to opportunity (auth: `crm_convert_lead`). Accept LeadConversionOptions

### Phase 1I — Unit Tests

- [ ] Test: Lead conversion — new account + new contact → BP created, Contact created, Opportunity created, Lead=CONVERTED (TS-06)
- [ ] Test: Lead conversion — existing account + existing contact → no new BP/Contact, Opportunity created (TS-07)
- [ ] Test: Lead conversion — only QUALIFIED leads can convert (BR-05)
- [ ] Test: Lead conversion — duplicate detection shows matches (TS-08)
- [ ] Test: Lead conversion — customer flag (is_customer=true) set on linked existing BP
- [ ] Test: Lead conversion — LeadConvertedEvent + OpportunityCreatedEvent published
- [ ] Test: Opportunity creation — only via conversion, no standalone create endpoint (BR-11)
- [ ] Test: Opportunity Won — requires linked confirmed SO (BR-12, TS-15, TS-16)
- [ ] Test: Opportunity Won — actual_revenue set from SO total (BR-16)
- [ ] Test: Opportunity Lost — requires reason (BR-13, TS-17, TS-18)
- [ ] Test: Opportunity re-open — LOST → OPEN with permission (BR-14)
- [ ] Test: Create Inquiry from Opportunity — customer_id auto-set from account_id (BR-15, TS-13)
- [ ] Test: Create Quotation from Opportunity — source linked (TS-14)
- [ ] Test: Create Sale Order from Opportunity — source_type='FROM_OPPORTUNITY', source_opportunity_id set
- [ ] Test: Data isolation — same person at 2 companies, leads filtered by account_id (TS-23, TS-24, BR-22)

---

## Phase 1J — Kanban Views + Timeline Export + Cross-References (6 days)

### Kanban View — Leads

- [ ] Create `LeadKanban` React component
- [ ] Load pipeline stages (LEAD type) as columns
- [ ] Load leads grouped by `pipeline_stage_id`
- [ ] Display lead cards: lead_number, contact name, company, rating badge, assigned user
- [ ] Implement drag-and-drop between pipeline stages
- [ ] On drop: call `PATCH /api/crm/leads/{id}/status` or stage update endpoint
- [ ] Show win_probability percentage on each column header
- [ ] Filter by: assigned user, sales team, rating, date range

### Kanban View — Opportunities

- [ ] Create `OpportunityKanban` React component
- [ ] Load pipeline stages (OPPORTUNITY type) as columns
- [ ] Load opportunities grouped by `pipeline_stage_id`
- [ ] Display opportunity cards: opportunity_number, topic, account name, estimated_revenue, assigned user
- [ ] Implement drag-and-drop between pipeline stages
- [ ] Show win_probability and total revenue per column
- [ ] Filter by: assigned user, sales team, date range

### Timeline Export — Excel

- [ ] Create `ITimelineExportService` interface — `ExportToExcelAsync(long orgId, long? leadId, long? opportunityId)`
- [ ] Implement using ClosedXML (install if not already present from Add 40):
  - [ ] **Sheet 1: "Summary"** — Lead/Opportunity header info: number, topic, account, contact, source, campaign, current status, total activities count, total documents count
  - [ ] **Sheet 2: "Timeline"** — one row per timeline entry: Date, Type, Subject, Description, Outcome, Performed By, Contact Person, Duration (minutes), Pipeline Stage, Follow-up Date, Follow-up Notes
  - [ ] **Sheet 3: "Documents"** — one row per document: Date, Name, Type, Direction, Shared By, Pipeline Stage, Notes
  - [ ] Apply formatting: bold headers, frozen header row, auto-column width, date formatting
- [ ] Register service in DI container

### Timeline Export API

- [ ] `GET /api/crm/leads/{id}/timeline/export?format=xlsx` — export lead timeline (auth: `crm_export_timeline`). Return file download
- [ ] `GET /api/crm/opportunities/{id}/timeline/export?format=xlsx` — export opportunity timeline (auth: `crm_export_timeline`). Return file download

### Cross-Reference Panel

- [ ] Create `CrossReferencePanel` React component
- [ ] Implement "Other Leads for this Account" query — filter by account_id, exclude current, org_id filtered (section 12.5 SQL)
- [ ] Implement "Other Leads for this Contact" query — filter by contact_person_id AND account_id (CRITICAL: same company only — BR-22)
- [ ] Display: lead_number, topic, status, created_at for each cross-reference
- [ ] Show on Lead Detail sidebar and Opportunity Detail sidebar

### Cross-Reference API

- [ ] `GET /api/crm/leads/{id}/cross-references` — other leads for same account/contact (auth: `crm_view_leads`)

### Phase 1J — Unit Tests

- [ ] Test: Timeline Excel export — 3 sheets generated with correct structure (TS-21)
- [ ] Test: Timeline Excel — Summary sheet has correct header info
- [ ] Test: Timeline Excel — Timeline sheet has all activities in date desc order
- [ ] Test: Timeline Excel — Documents sheet has all documents
- [ ] Test: Cross-references — other leads for same account returned correctly
- [ ] Test: Cross-references — same contact at same account only, NOT cross-company (BR-22, TS-23, TS-24)
- [ ] Test: Cross-references — different tenant data isolated (TS-25)

---

## Phase 1K — UI Panels + RBAC + Public API (5 days)

### Lead List View UI

- [ ] Create `LeadListPage` React page component at route `/crm/leads`
- [ ] Implement list/table view with columns: #, Lead (name + topic), Company, Source, Rating, Status + assigned user
- [ ] Implement filter bar: Status, Source, Team, Rating, Date range
- [ ] Implement [List View / Kanban] toggle
- [ ] Implement search (by name, company, email, phone, lead_number)
- [ ] Implement [+ New Lead] button → lead creation form
- [ ] Row click navigates to Lead Detail

### Lead Detail View UI

- [ ] Create `LeadDetailPage` React page component at route `/crm/leads/:id`
- [ ] Implement action buttons: [Qualify], [Convert], [Disqualify], [Assign], [Re-open] — conditional on status
- [ ] Implement status/rating/stage display bar
- [ ] Implement two-column layout: Contact Info + Address (left), Source + BANT + Deal Estimate + Ownership (right)
- [ ] Implement Topic and Description section
- [ ] Implement Timeline panel (section 15.3):
  - [ ] Quick-add buttons: [+ Call] [+ Meeting] [+ Note] [+ Task] [+ Email] [+ Document]
  - [ ] Chronological feed of all timeline entries with icons per type
  - [ ] [Export ▼] button for xlsx download
- [ ] Implement Cross-Reference sidebar panel
- [ ] Implement Linked Account display (with "Not yet linked" warning pre-conversion)

### Lead Conversion Dialog UI

- [ ] Create `LeadConversionDialog` React dialog component per section 15.4
- [ ] Account section: radio "Create new Account" / "Link to existing Account"
  - [ ] Search existing accounts
  - [ ] Display potential duplicate matches with selection checkboxes
- [ ] Contact section: radio "Create new Contact" / "Link to existing Contact"
  - [ ] Show contacts for the selected account only
- [ ] Opportunity section: pre-filled topic, revenue, close date, assigned user
- [ ] [Convert] and [Cancel] buttons
- [ ] Call `POST /api/crm/leads/{id}/convert` on submit with options

### Opportunity Detail View UI

- [ ] Create `OpportunityDetailPage` React page component at route `/crm/opportunities/:id`
- [ ] Implement action buttons: [Create Inquiry], [Create Quotation], [Create Order], [Won ✓], [Lost ✗]
- [ ] Implement pipeline progress bar with stage names and win_probability
- [ ] Implement two-column layout: Account + Contact + Ownership (left), Source + Deal + Linked SCM Documents (right)
- [ ] Implement Timeline panel (same component, includes lead activities below "Activities from Lead LD-xxxx" separator)
- [ ] Show linked SCM documents: Inquiry, Quotation, Sale Order with status and links

### Activity Creation Form UI

- [ ] Create `ActivityForm` React form component per section 15.6
- [ ] Fields: Type dropdown, Subject, Date + Time, Duration, Contact dropdown (from linked contacts)
- [ ] Rich text editor for Description / Meeting Notes (NVARCHAR(MAX))
- [ ] Outcome dropdown + outcome notes
- [ ] Participants section: [+ Add] with type selector (Internal User, Customer Contact, External) and role
- [ ] Documents section: [Attach files] with document_type and direction
- [ ] Follow-up section: checkbox "Schedule follow-up" → date, type, notes
- [ ] Save and Cancel buttons

### RBAC Permissions Setup

- [ ] Register all 21 CRM permissions per Appendix A:
  - [ ] `crm_view_leads` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_create_lead` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_edit_lead` — default: Sales Rep (own), Sales Manager, Admin
  - [ ] `crm_assign_lead` — default: Sales Manager, Admin
  - [ ] `crm_qualify_lead` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_convert_lead` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_disqualify_lead` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_reopen_lead` — default: Sales Manager, Admin
  - [ ] `crm_delete_lead` — default: Sales Manager, Admin
  - [ ] `crm_view_opportunities` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_edit_opportunity` — default: Sales Rep (own), Sales Manager, Admin
  - [ ] `crm_close_opportunity` — default: Sales Rep (own), Sales Manager, Admin
  - [ ] `crm_reopen_opportunity` — default: Sales Manager, Admin
  - [ ] `crm_view_activities` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_create_activity` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_edit_activity` — default: Sales Rep (own), Sales Manager, Admin
  - [ ] `crm_delete_activity` — default: Sales Manager, Admin
  - [ ] `crm_export_timeline` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_view_campaigns` — default: Sales Rep, Sales Manager, Admin
  - [ ] `crm_manage_campaigns` — default: Sales Manager, Admin
  - [ ] `crm_admin` — default: Admin
- [ ] Add "CRM" section in Settings > Roles & Permissions UI

### Public Lead Ingestion API

- [ ] Implement `POST /api/public/crm/leads` — API key auth (X-Api-Key header)
- [ ] Accept request body per section 14.6: first_name, last_name, company_name, email, phone, topic, description, source_code, campaign_code, source_medium, source_reference, external_source_system, external_source_id
- [ ] Create lead with status=NEW, no assignment
- [ ] Return lead_number and lead_id
- [ ] Implement rate limiting: 100 requests per minute per API key (configurable per org)
- [ ] Validate API key against org and resolve org_id
- [ ] Resolve source_code → lead_source_id from org's LeadSources
- [ ] Resolve campaign_code → campaign_id from org's Campaigns

### Navigation & Routing

- [ ] Add route `/crm/leads` → LeadListPage
- [ ] Add route `/crm/leads/:id` → LeadDetailPage
- [ ] Add route `/crm/opportunities` → OpportunityListPage
- [ ] Add route `/crm/opportunities/:id` → OpportunityDetailPage
- [ ] Add CRM section in main navigation sidebar: "CRM" with sub-items "Leads", "Opportunities", "Campaigns"
- [ ] Add navigation permission checks based on CRM RBAC

### Phase 1K — Unit Tests

- [ ] Test: Public API — valid lead creation returns lead_number (lead created, status=NEW)
- [ ] Test: Public API — invalid API key → 401
- [ ] Test: Public API — rate limit exceeded → 429
- [ ] Test: Public API — source_code resolved to lead_source_id
- [ ] Test: Public API — campaign_code resolved to campaign_id + actual_leads incremented
- [ ] Test: Permission enforcement — Sales Rep cannot assign leads (crm_assign_lead restricted)
- [ ] Test: Permission enforcement — Sales Rep cannot re-open leads (crm_reopen_lead restricted)
- [ ] Test: Permission enforcement — Admin has all CRM permissions

---

## MediatR Events Checklist

All 11 events from Appendix B:

- [ ] `LeadCreatedEvent` — published on lead create (LeadId, OrgId, LeadSourceId, CampaignId)
- [ ] `LeadAssignedEvent` — published on assign/reassign (LeadId, OrgId, NewUserId, PreviousUserId)
- [ ] `LeadStatusChangedEvent` — published on status transition (LeadId, OrgId, OldStatus, NewStatus, Reason)
- [ ] `LeadQualifiedEvent` — published on qualify (LeadId, OrgId)
- [ ] `LeadConvertedEvent` — published on conversion (LeadId, OpportunityId, AccountId, ContactId, OrgId)
- [ ] `LeadDisqualifiedEvent` — published on disqualify (LeadId, OrgId, Reason)
- [ ] `OpportunityCreatedEvent` — published on conversion (OpportunityId, LeadId, AccountId, OrgId)
- [ ] `OpportunityStageChangedEvent` — published on stage change (OpportunityId, OrgId, OldStageId, NewStageId)
- [ ] `OpportunityWonEvent` — published on won (OpportunityId, OrgId, SaleOrderId, ActualRevenue)
- [ ] `OpportunityLostEvent` — published on lost (OpportunityId, OrgId, Reason, Competitor)
- [ ] `ActivityCreatedEvent` — published on activity create (ActivityId, LeadId, OpportunityId, ActivityType, OrgId)

---

## End-to-End Test Scenarios

### Lead Lifecycle (TS-01 through TS-12)

- [ ] TS-01: Create lead with all required fields → status=NEW, lead_number generated
- [ ] TS-02: Create B2C lead (no company) → company_name auto-set to full_name
- [ ] TS-03: Attempt NEW → QUALIFIED (skip CONTACTED) → rejected
- [ ] TS-04: Log phone call on NEW lead → lead auto-transitions to CONTACTED
- [ ] TS-05: Qualify CONTACTED lead → status=QUALIFIED
- [ ] TS-06: Convert QUALIFIED lead (new account + new contact) → BP created, Contact created, Opportunity created, Lead=CONVERTED
- [ ] TS-07: Convert QUALIFIED lead (link existing account + existing contact) → no new BP/Contact, Opportunity created, Lead=CONVERTED
- [ ] TS-08: Convert lead with duplicate BP match → duplicate warning, user chooses link or create
- [ ] TS-09: Disqualify lead → status=DISQUALIFIED, reason recorded
- [ ] TS-10: Re-open disqualified lead (with permission) → status=NEW
- [ ] TS-11: Re-open disqualified lead (without permission) → 403
- [ ] TS-12: Edit CONVERTED lead → rejected (terminal state)

### Opportunity Lifecycle (TS-13 through TS-18)

- [ ] TS-13: Create Inquiry from Opportunity → Inquiry created with customer_id = account_id, source linked
- [ ] TS-14: Create Quotation from Opportunity → Quotation created with customer_id = account_id, source linked
- [ ] TS-15: Mark Opportunity as Won (SO confirmed) → status=WON, actual_revenue set, actual_close_date set
- [ ] TS-16: Mark Opportunity as Won (no SO) → rejected
- [ ] TS-17: Mark Opportunity as Lost (with reason) → status=LOST, reason recorded
- [ ] TS-18: Mark Opportunity as Lost (no reason) → rejected

### Timeline & Activities (TS-19 through TS-22)

- [ ] TS-19: Get timeline for lead with activities + docs + status changes → all entries merged, sorted desc
- [ ] TS-20: Get timeline for opportunity (includes lead activities) → lead activities below conversion line
- [ ] TS-21: Export timeline as Excel → workbook with Summary, Timeline, Documents sheets
- [ ] TS-22: Attach document to activity → document linked, visible in timeline

### Data Isolation (TS-23 through TS-25)

- [ ] TS-23: Ahmed Khan at Company A — view cross-references → only Company A leads shown
- [ ] TS-24: Ahmed Khan at Company B — view cross-references → only Company B leads, Company A invisible
- [ ] TS-25: Same org, different tenant → tenant filter isolates all data

---

## Summary Checklist

| Phase | Focus | Days | Task Count |
|---|---|---|---|
| Phase 1A | CRM Schema + Module Activation | 1 | 14 |
| Phase 1B | Lead Sources & Campaigns | 2 | 30 |
| Phase 1C | Sales Teams | 1 | 18 |
| Phase 1D | Pipeline Stages | 1 | 22 |
| Phase 1E | Leads Entity + CRUD | 5 | 56 |
| Phase 1F | Lead State Machine | 2 | 5 |
| Phase 1G | Activities & Timeline | 5 | 48 |
| Phase 1H | Activity Documents | 2 | 16 |
| Phase 1I | Opportunities + Lead Conversion | 7 | 62 |
| Phase 1J | Kanban + Timeline Export + Cross-Refs | 6 | 26 |
| Phase 1K | UI Panels + RBAC + Public API | 5 | 58 |
| — | MediatR Events | — | 11 |
| — | End-to-End Test Scenarios | — | 25 |
| **Total** | | **~31 days** | **391 tasks** |

---

*Task register generated from SMS-FSD-ADD-041 v1.0*
