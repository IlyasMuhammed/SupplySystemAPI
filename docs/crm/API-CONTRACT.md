# Addendum 41 — CRM API contract

Decisions: [ADDENDUM-41-ANALYSIS.md](ADDENDUM-41-ANALYSIS.md). Every response is the house envelope `{ success, message, result }`;
errors are 400 (rule), 401, 403 (permission / own-records rule D-5), 404 (absent or another organization's), 409 (state).
Every endpoint except §7 needs a JWT and `MODULE_CRM` on (`[RequiresFeature]`). Ids in URLs are uuids; users are int ids;
`accountUuid` = BusinessPartners.UUID; `contactPersonId` = SupplierContacts.Id. Dates are ISO; date-only fields (close dates,
follow-up date, campaign dates) carry no time. Lists are `PaginatedResponse { data, totalRecords, page, pageSize, totalPages }`.

## §0 Vocabularies (`GET api/crm/meta`)
Lead status NEW · CONTACTED · QUALIFIED · DISQUALIFIED · CONVERTED · rating HOT/WARM/COLD · priority HIGH/NORMAL/LOW ·
purchase timeframe IMMEDIATE/THIS_QUARTER/THIS_YEAR/NEXT_YEAR/UNKNOWN · external system ODOO/SAP/DYNAMICS/HUBSPOT/OTHER ·
opportunity OPEN/WON/LOST · campaign type GENERAL/ADVERTISEMENT/PROMOTION/EVENT/EMAIL_BLAST/SOCIAL_MEDIA/REFERRAL_PROGRAM ·
campaign status DRAFT/ACTIVE/PAUSED/COMPLETED/CANCELLED · activity type PHONE_CALL/MEETING/EMAIL_SENT/EMAIL_RECEIVED/NOTE/TASK/
FOLLOW_UP/SITE_VISIT/DEMO/PRESENTATION/STATUS_CHANGE/DOCUMENT_SHARED/OTHER (contact types = `contactActivityTypes`) · outcome
SUCCESSFUL/UNSUCCESSFUL/NO_RESPONSE/FOLLOW_UP_NEEDED/REFERRED/COMPLETED/CANCELLED · participant INTERNAL_USER/CUSTOMER_CONTACT/
EXTERNAL, role ORGANIZER/REQUIRED/OPTIONAL/ATTENDEE · document type BROCHURE/PRICE_LIST/PROPOSAL/SPECIFICATION/CONTRACT/
CUSTOMER_PO/QUOTATION_PDF/PRESENTATION/IMAGE/MEETING_MINUTES/NDA/OTHER · direction SENT_TO_CUSTOMER/RECEIVED_FROM_CUSTOMER/INTERNAL.
`meta.campaignTrackingEnabled` = FEATURE_CRM_CAMPAIGN_TRACKING on.

## §1 Lookups
| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | api/crm/meta | any CRM view / admin | vocabularies |
| GET | api/crm/users?search=&limit= | CRM view / admin | active org users `{ userId, name, email, isActive }` |
| GET | api/crm/accounts/{uuid}/contacts | CRM view | `CrmContactModel[]` |

## §2 Configuration
| Method | Route | Permission | Body / notes |
|---|---|---|---|
| GET | api/crm/lead-sources?includeInactive= | CRM_VIEW_LEADS / VIEW_CAMPAIGNS / ADMIN | 14 seeded per org (isSystem) |
| POST | api/crm/lead-sources | CRM_ADMIN | `{ code, name, description?, isActive?, sortOrder? }` code A-Z0-9_- ≤30, unique (409) |
| PUT | api/crm/lead-sources/{uuid} | CRM_ADMIN | same; a built-in code cannot change |
| GET | api/crm/campaigns?search=&status=&type=&page=&pageSize= | VIEW_CAMPAIGNS / MANAGE / VIEW_LEADS + FEATURE_CRM_CAMPAIGN_TRACKING | |
| GET | api/crm/campaigns/{uuid} | VIEW_CAMPAIGNS / MANAGE | + `leadsByStatus`, `opportunityCount` |
| POST/PUT | api/crm/campaigns[/{uuid}] | CRM_MANAGE_CAMPAIGNS | `{ code, name, description, campaignType, leadSourceUuid, status, startDate, endDate, budgetAmount, currencyCode, expectedLeads, externalReference, notes }`; status DRAFT→ACTIVE→PAUSED⇄ACTIVE→COMPLETED, CANCELLED from DRAFT/ACTIVE/PAUSED |
| DELETE | api/crm/campaigns/{uuid} | CRM_MANAGE_CAMPAIGNS | soft delete |
| GET | api/crm/sales-teams?includeInactive= · /{uuid} | VIEW_LEADS / VIEW_OPPS / ADMIN | members, `hasOwnStages` |
| POST/PUT | api/crm/sales-teams[/{uuid}] | CRM_ADMIN | `{ code, name, description, teamLeaderUserId, isActive, defaultCampaignUuid }` (leader = LEADER member) |
| POST | api/crm/sales-teams/{uuid}/members | CRM_ADMIN | `{ userId, role: LEADER|MEMBER }` (409 already member) |
| DELETE | api/crm/sales-teams/{uuid}/members/{userId} | CRM_ADMIN | |
| GET | api/crm/pipeline-stages?stageType=&salesTeamUuid=&includeInactive= | VIEW_LEADS / VIEW_OPPS / ADMIN | the team's own stages of a type, else the org's; sequence order |
| POST/PUT | api/crm/pipeline-stages[/{uuid}] | CRM_ADMIN | `{ code, name, stageType, salesTeamUuid, sequence, winProbability 0-100, isWon, isLost, description }` |
| DELETE | api/crm/pipeline-stages/{uuid} | CRM_ADMIN | deactivate; 409 while open leads/opportunities are in it |
| GET/POST | api/crm/api-keys | CRM_ADMIN | POST `{ name, requestsPerMinute? (1-10000, 100) }` → `{ key, apiKey }` (key shown once) |
| PUT/DELETE | api/crm/api-keys/{uuid} | CRM_ADMIN | `{ name?, requestsPerMinute?, isActive? }` |

## §3 Leads (`api/crm/leads`)
| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | / | CRM_VIEW_LEADS | `search, status (csv), leadSourceUuid, campaignUuid, salesTeamUuid, rating, assignedToUserId, mine, unassigned, from, to, sortField (createdAt|leadNumber|fullName|companyName|status|rating|estimatedRevenue|estimatedCloseDate), sortOrder, page, pageSize` → `LeadListItem` |
| GET | /kanban | CRM_VIEW_LEADS | `search, salesTeamUuid, assignedToUserId, mine, rating, from, to` → `{ columns: [{ stageUuid, code, name, sequence, winProbability, count, totalRevenue, items }], unstaged, mixedCurrencies }` (open leads) |
| GET | /duplicate-check?email=&phone=&companyName=&excludeUuid= | CRM_VIEW_LEADS | `{ leads: [{ lead, matchedOn: EMAIL|PHONE|COMPANY[] }], accounts: CrmAccountModel[] }` advisory |
| GET | /{uuid} | CRM_VIEW_LEADS | `LeadDetail` incl. `account`, `contactPerson`, `convertedOpportunity`, `allowedActions` (MARK_CONTACTED, QUALIFY, CONVERT, DISQUALIFY, REOPEN, ASSIGN, DELETE), `canEdit`, `activityCount` |
| POST | / | CRM_CREATE_LEAD | `LeadUpsert` — firstName, lastName, topic required; companyName blank = full name; amounts need a currency; `assignedToUserId` (default caller; another user needs CRM_ASSIGN_LEAD), `salesTeamUuid` |
| PUT | /{uuid} | CRM_EDIT_LEAD (own) | full `LeadUpsert` (assignee/team ignored); 409 CONVERTED |
| PATCH | /{uuid}/assign | CRM_ASSIGN_LEAD | `{ userId|null, salesTeamUuid? }` → previous owner, assignedAt, LeadAssigned (notification) |
| PATCH | /{uuid}/status | CRM_EDIT_LEAD | `{ status: CONTACTED|QUALIFIED|DISQUALIFIED|NEW, reason }` — same rules/permissions as the endpoints below; CONTACTED needs a contact activity |
| PATCH | /{uuid}/stage | CRM_EDIT_LEAD (own) | `{ stageUuid }` an active LEAD stage of the lead's pipeline (kanban drag) |
| POST | /{uuid}/qualify | CRM_QUALIFY_LEAD (own) | CONTACTED → QUALIFIED |
| POST | /{uuid}/disqualify | CRM_DISQUALIFY_LEAD (own) | `{ reason }` required |
| POST | /{uuid}/reopen | CRM_REOPEN_LEAD | DISQUALIFIED → NEW |
| DELETE | /{uuid} | CRM_DELETE_LEAD | 409 CONVERTED |
| GET | /{uuid}/duplicate-check | CRM_VIEW_LEADS | the lead's own check |
| GET | /{uuid}/cross-references | VIEW_LEADS / VIEW_OPPS | `{ account, contactPerson, accountLeads, contactLeads }` — same account only (BR-22) |
| GET | /{uuid}/timeline?page=&pageSize=&search= | CRM_VIEW_LEADS | §6 (`search`: case-insensitive contains over subject, plain-text notes, outcome/follow-up notes, reason, values, people, document names, numbers; `totalCount` is the filtered count; same param on the opportunity timeline) |
| GET | /{uuid}/timeline/export?format=xlsx | CRM_EXPORT_TIMELINE | file |
| GET | /{uuid}/account-matches?search= | CRM_CONVERT_LEAD | top 5 duplicates of the lead's company (no search) or a search (20) |
| POST | /{uuid}/convert | CRM_CONVERT_LEAD (own) | `{ existingAccountUuid?, existingContactId?, topic?, estimatedRevenue?, estimatedCloseDate?, assignedToUserId? }` → `{ opportunityUuid, opportunityNumber, accountUuid, contactId, isNewAccount, isNewContact }` |

## §4 Opportunities (`api/crm/opportunities`) — no create (BR-11)
| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | / | CRM_VIEW_OPPORTUNITIES | `search, status, pipelineStageUuid, salesTeamUuid, assignedToUserId, mine, accountUuid, closeFrom, closeTo, sortField, sortOrder, page, pageSize` |
| GET | /kanban | CRM_VIEW_OPPORTUNITIES | as leads; `includeClosed` adds the won/lost columns |
| GET | /{uuid} | CRM_VIEW_OPPORTUNITIES | `OpportunityDetail` + `salesDocuments` (kind INQUIRY/QUOTATION/ORDER, number, status, grandTotal, currencyCode), `pipeline` (stages), `allowedActions` (WON, LOST, REOPEN, CREATE_INQUIRY, CREATE_QUOTATION, CREATE_ORDER) |
| PUT | /{uuid} | CRM_EDIT_OPPORTUNITY (own) | `{ topic, description, contactPersonId, estimatedRevenue, estimatedCloseDate, currencyCode, assignedToUserId (change = manager), salesTeamUuid }` |
| PATCH | /{uuid}/stage | CRM_EDIT_OPPORTUNITY (own) | `{ stageUuid }`; a won/lost stage → 400 |
| POST | /{uuid}/won | CRM_CLOSE_OPPORTUNITY (own) | needs a CONFIRMED+ sale order of the opportunity; actualRevenue = its total |
| POST | /{uuid}/lost | CRM_CLOSE_OPPORTUNITY (own) | `{ reason (required), competitor? }` |
| POST | /{uuid}/reopen | CRM_REOPEN_OPPORTUNITY | LOST → OPEN (WON is final, 409) |
| POST | /{uuid}/create-inquiry | CRM_EDIT_OPPORTUNITY + SALE_INQUIRY_CREATE | → `LinkedSalesDocumentModel` (RECEIVED inquiry, no lines) |
| POST | /{uuid}/create-quotation | CRM_EDIT_OPPORTUNITY + SALE_QUOTATION_CREATE | from the REVIEW_COMPLETE inquiry, else an empty DRAFT (valid 30 days) |
| POST | /{uuid}/create-order | CRM_EDIT_OPPORTUNITY + SALE_ORDER_CREATE | → `{ accountUuid, accountName, opportunityUuid, opportunityNumber, acceptedQuotationUuid?, acceptedQuotationNumber? }` — FE opens the quotation (convert there) or the new SO form with `?customer=&opportunity=` |
| GET | /{uuid}/timeline · /timeline/export | VIEW_OPPS · EXPORT_TIMELINE | §6 |

Demand: `POST api/sale-orders` accepts `sourceOpportunityUuid` → `sourceType = FROM_OPPORTUNITY`; inquiry, quotation and order
models expose `sourceOpportunityUuid` (link back to `/portal/pages/crm/opportunities/{uuid}`).

## §5 Activities & documents
| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | api/crm/activities | CRM_VIEW_ACTIVITIES | `leadUuid, opportunityUuid, performedByUserId, mine, activityType, from, to, state (OPEN|DONE), search, page, pageSize` |
| GET | api/crm/activities/pending | CRM_VIEW_ACTIVITIES | `userId, mine (default true; reps always own), dueBy` → `[{ activity, reason FOLLOW_UP|TASK|SCHEDULED, dueOn, overdue }]` |
| GET | api/crm/activities/{uuid} | CRM_VIEW_ACTIVITIES | with participants and documents |
| POST | api/crm/activities | CRM_CREATE_ACTIVITY | `ActivityUpsert` — leadUuid or opportunityUuid (BR-17), activityType, subject required; description = HTML (sanitized); performedByUserId (someone else = manager); participants[]; follow-up needs its date. A contact activity on a NEW lead makes it CONTACTED |
| PUT | api/crm/activities/{uuid} | CRM_EDIT_ACTIVITY (own) | participants null = unchanged |
| PATCH | api/crm/activities/{uuid}/complete | CRM_EDIT_ACTIVITY (own) | 409 when already completed |
| DELETE | api/crm/activities/{uuid} | CRM_DELETE_ACTIVITY | soft, its documents too |
| POST | api/crm/activities/{uuid}/documents | CRM_CREATE_ACTIVITY | multipart `file, documentType, direction (required), notes, documentName` ≤20 MB, UploadRules allow-list |
| GET | api/crm/documents/{uuid}/content | CRM_VIEW_ACTIVITIES | the file |
| DELETE | api/crm/documents/{uuid} | CRM_EDIT_ACTIVITY (own: shared by caller, or manager) | |

## §6 Timeline
`{ timeline: TimelineEntry[], totalCount, page, pageSize }`, newest first. `entryType` ACTIVITY (activityType, subject, description
HTML, outcome, performedBy, contactPerson, durationMinutes, pipelineStage, followUp*, documents[]), DOCUMENT, STATUS_CHANGE /
STAGE_CHANGE / ASSIGNMENT (fromValue, toValue, reason, changedBy), CREATED, CONVERTED (toValue = OP number), SCM_DOCUMENT (scmKind,
scmUuid, scmNumber, scmStatus, grandTotal, currencyCode). On an opportunity, entries of its source lead carry `fromLead: true` +
`leadNumber`. Export: workbook `{number}-timeline.xlsx` with Summary / Timeline / Documents.

## §7 Public lead API
`POST api/public/crm/leads`, header `X-Api-Key`, anonymous. Body (snake_case per the spec; camelCase accepted): `first_name,
last_name, company_name, email, phone, topic, description, source_code, campaign_code, source_medium, source_reference,
external_source_system, external_source_id`. → `{ leadId, leadNumber }`. 401 no/invalid/inactive key · 403 CRM off · 400 unknown
source/campaign code or a missing required field · 429 over the key's per-minute limit (`Retry-After: 60`). Campaign code is
ignored while campaign tracking is off. The lead is NEW, unassigned, created by "API: {key name}".
