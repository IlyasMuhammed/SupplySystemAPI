import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A41 — CRM: leads, pipeline, opportunities, activities, campaigns (docs/crm/API-CONTRACT.md). Feature MODULE_CRM.

export type LeadStatus = 'NEW' | 'CONTACTED' | 'QUALIFIED' | 'DISQUALIFIED' | 'CONVERTED';
export type LeadAction = 'MARK_CONTACTED' | 'QUALIFY' | 'CONVERT' | 'DISQUALIFY' | 'REOPEN' | 'ASSIGN' | 'DELETE';
export type OpportunityStatus = 'OPEN' | 'WON' | 'LOST';
export type OpportunityAction = 'WON' | 'LOST' | 'REOPEN' | 'CREATE_INQUIRY' | 'CREATE_QUOTATION' | 'CREATE_ORDER';

export const CRM_ROUTE = '/portal/pages/crm';

export const LEAD_STATUSES: { value: LeadStatus; label: string }[] = [
  { value: 'NEW', label: 'New' },
  { value: 'CONTACTED', label: 'Contacted' },
  { value: 'QUALIFIED', label: 'Qualified' },
  { value: 'DISQUALIFIED', label: 'Disqualified' },
  { value: 'CONVERTED', label: 'Converted' }
];

const LEAD_TONE: Record<string, string> = { NEW: '', CONTACTED: 'in', QUALIFIED: 'te', DISQUALIFIED: 'er', CONVERTED: 'ok' };
const OPP_TONE: Record<string, string> = { OPEN: 'in', WON: 'ok', LOST: 'er' };
const RATING_TONE: Record<string, string> = { HOT: 'er', WARM: 'wn', COLD: 'in' };

export const leadStatusTone = (s: string | null | undefined) => LEAD_TONE[s ?? ''] ?? '';
export const opportunityStatusTone = (s: string | null | undefined) => OPP_TONE[s ?? ''] ?? '';
export const ratingTone = (r: string | null | undefined) => RATING_TONE[r ?? ''] ?? '';

/** "PHONE_CALL" → "Phone call". */
export function crmLabel(code: string | null | undefined): string {
  const s = (code ?? '').replace(/_/g, ' ').toLowerCase().trim();
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
}

const ACTIVITY_ICONS: Record<string, string> = {
  PHONE_CALL: 'pi-phone', MEETING: 'pi-users', EMAIL_SENT: 'pi-send', EMAIL_RECEIVED: 'pi-envelope', NOTE: 'pi-pencil',
  TASK: 'pi-check-square', FOLLOW_UP: 'pi-replay', SITE_VISIT: 'pi-map-marker', DEMO: 'pi-desktop', PRESENTATION: 'pi-chart-bar',
  STATUS_CHANGE: 'pi-flag', DOCUMENT_SHARED: 'pi-paperclip', OTHER: 'pi-circle'
};
export const activityIcon = (type: string | null | undefined) => ACTIVITY_ICONS[type ?? ''] ?? 'pi-circle';

// ── Shared shapes ────────────────────────────────────────────────────────────

export interface CrmMeta {
  leadStatuses: string[]; ratings: string[]; priorities: string[]; purchaseTimeframes: string[]; externalSystems: string[];
  opportunityStatuses: string[]; campaignTypes: string[]; campaignStatuses: string[]; activityTypes: string[];
  contactActivityTypes: string[]; activityOutcomes: string[]; participantTypes: string[]; participantRoles: string[];
  documentTypes: string[]; documentDirections: string[]; campaignTrackingEnabled: boolean;
}

export interface CrmUser { userId: number; name: string; email: string; isActive: boolean; }

export interface CrmAccount {
  uuid: string; code: string; name: string; isCustomer: boolean; isVendor: boolean; isActive: boolean;
  phone?: string | null; email?: string | null; customerType?: string | null; createdAt: string;
}

export interface CrmContact {
  id: number; accountUuid: string; name: string; title?: string | null; phone?: string | null; email?: string | null;
  isPrimary: boolean; isActive: boolean;
}

export interface CrmRef { uuid: string; number: string; name?: string | null; }

// ── Configuration ────────────────────────────────────────────────────────────

export interface LeadSource { uuid: string; code: string; name: string; description?: string | null; isActive: boolean; sortOrder: number; isSystem: boolean; }
export interface LeadSourceUpsert { code?: string; name?: string; description?: string | null; isActive?: boolean; sortOrder?: number; }

export interface Campaign {
  uuid: string; code: string; name: string; description?: string | null; campaignType: string; leadSourceUuid?: string | null;
  leadSourceName?: string | null; status: string; startDate?: string | null; endDate?: string | null; budgetAmount?: number | null;
  currencyCode?: string | null; expectedLeads?: number | null; actualLeads: number; externalReference?: string | null;
  notes?: string | null; createdBy: number; createdByName?: string | null; createdAt: string;
  leadsByStatus?: Record<string, number> | null; opportunityCount?: number | null;
}
export interface CampaignUpsert {
  code?: string; name?: string; description?: string | null; campaignType?: string; leadSourceUuid?: string | null; status?: string;
  startDate?: string | null; endDate?: string | null; budgetAmount?: number | null; currencyCode?: string | null;
  expectedLeads?: number | null; externalReference?: string | null; notes?: string | null;
}
export interface CampaignListFilter { search?: string; status?: string; type?: string; page?: number; pageSize?: number; }

export interface SalesTeamMember { userId: number; name: string; role: 'LEADER' | 'MEMBER'; isActive: boolean; joinedAt: string; }
export interface SalesTeam {
  uuid: string; code: string; name: string; description?: string | null; teamLeaderUserId?: number | null; teamLeaderName?: string | null;
  isActive: boolean; defaultCampaignUuid?: string | null; defaultCampaignName?: string | null; hasOwnStages: boolean; members: SalesTeamMember[];
}
export interface SalesTeamUpsert {
  code?: string; name?: string; description?: string | null; teamLeaderUserId?: number | null; isActive?: boolean; defaultCampaignUuid?: string | null;
}

export interface PipelineStage {
  uuid: string; code: string; name: string; stageType: 'LEAD' | 'OPPORTUNITY'; salesTeamUuid?: string | null; salesTeamName?: string | null;
  sequence: number; winProbability?: number | null; isWon: boolean; isLost: boolean; description?: string | null; isActive: boolean;
}
export interface PipelineStageUpsert {
  code?: string; name?: string; stageType?: string; salesTeamUuid?: string | null; sequence?: number; winProbability?: number | null;
  isWon?: boolean; isLost?: boolean; description?: string | null;
}

export interface LeadApiKey { uuid: string; name: string; keyPrefix: string; requestsPerMinute: number; isActive: boolean; lastUsedAt?: string | null; createdAt: string; }
export interface LeadApiKeyCreated { key: string; apiKey: LeadApiKey; }

// ── Leads ────────────────────────────────────────────────────────────────────

export interface LeadListItem {
  uuid: string; leadNumber: string; fullName: string; topic: string; companyName: string; email?: string | null; phone?: string | null;
  mobilePhone?: string | null; leadSourceUuid?: string | null; leadSourceName?: string | null; campaignUuid?: string | null;
  campaignName?: string | null; rating: string; priority: string; status: LeadStatus; pipelineStageUuid?: string | null;
  pipelineStageName?: string | null; assignedToUserId?: number | null; assignedToName?: string | null; salesTeamUuid?: string | null;
  salesTeamName?: string | null; estimatedRevenue?: number | null; currencyCode?: string | null; estimatedCloseDate?: string | null;
  createdAt: string;
}

export interface LeadDetail extends LeadListItem {
  description?: string | null; salutation?: string | null; firstName: string; lastName: string; jobTitle?: string | null;
  website?: string | null; addressLine1?: string | null; addressLine2?: string | null; city?: string | null; stateProvince?: string | null;
  postalCode?: string | null; country?: string | null; account?: CrmAccount | null; contactPerson?: CrmContact | null;
  sourceMedium?: string | null; sourceReference?: string | null; externalSourceSystem?: string | null; externalSourceId?: string | null;
  budgetAmount?: number | null; budgetCurrencyCode?: string | null; budgetConfirmed: boolean; authorityConfirmed: boolean;
  needIdentified: boolean; purchaseTimeframe?: string | null; assignedAt?: string | null; previousOwnerUserId?: number | null;
  previousOwnerName?: string | null; pipelineStageProbability?: number | null; statusReason?: string | null; qualifiedAt?: string | null;
  disqualifiedAt?: string | null; convertedAt?: string | null; convertedOpportunity?: CrmRef | null; createdByUserId: number;
  createdByName?: string | null; modifiedAt: string; allowedActions: LeadAction[]; canEdit: boolean; activityCount: number;
}

export interface LeadUpsert {
  topic?: string; description?: string | null; salutation?: string | null; firstName?: string; lastName?: string; jobTitle?: string | null;
  email?: string | null; phone?: string | null; mobilePhone?: string | null; companyName?: string | null; website?: string | null;
  addressLine1?: string | null; addressLine2?: string | null; city?: string | null; stateProvince?: string | null; postalCode?: string | null;
  country?: string | null; leadSourceUuid?: string | null; campaignUuid?: string | null; sourceMedium?: string | null;
  sourceReference?: string | null; externalSourceSystem?: string | null; externalSourceId?: string | null; budgetAmount?: number | null;
  budgetCurrencyCode?: string | null; budgetConfirmed?: boolean; authorityConfirmed?: boolean; needIdentified?: boolean;
  purchaseTimeframe?: string | null; rating?: string; priority?: string; estimatedRevenue?: number | null;
  estimatedCloseDate?: string | null; currencyCode?: string | null; assignedToUserId?: number | null; salesTeamUuid?: string | null;
}

export interface LeadListFilter {
  search?: string; status?: string[]; leadSourceUuid?: string; campaignUuid?: string; salesTeamUuid?: string; rating?: string;
  assignedToUserId?: number; mine?: boolean; unassigned?: boolean; from?: string; to?: string; sortField?: string;
  sortOrder?: 'asc' | 'desc'; page?: number; pageSize?: number;
}

export interface KanbanColumn<T> {
  stageUuid: string; code: string; name: string; sequence: number; winProbability?: number | null; isWon: boolean; isLost: boolean;
  count: number; totalRevenue: number; items: T[];
}
export interface KanbanBoard<T> { columns: KanbanColumn<T>[]; unstaged: T[]; mixedCurrencies: boolean; }
export interface KanbanFilter {
  search?: string; salesTeamUuid?: string; assignedToUserId?: number; mine?: boolean; rating?: string; from?: string; to?: string;
  closeFrom?: string; closeTo?: string; includeClosed?: boolean;
}

export interface LeadDuplicate { lead: LeadListItem; matchedOn: ('EMAIL' | 'PHONE' | 'COMPANY')[]; }
export interface LeadDuplicateResult { leads: LeadDuplicate[]; accounts: CrmAccount[]; }

export interface CrossReferenceItem { uuid: string; leadNumber: string; topic: string; status: string; createdAt: string; }
export interface CrossReferenceResult {
  account?: CrmAccount | null; contactPerson?: CrmContact | null; accountLeads: CrossReferenceItem[]; contactLeads: CrossReferenceItem[];
}

export interface LeadConvertRequest {
  existingAccountUuid?: string | null; existingContactId?: number | null; topic?: string | null; estimatedRevenue?: number | null;
  estimatedCloseDate?: string | null; assignedToUserId?: number | null;
}
export interface LeadConversionResult {
  opportunityUuid: string; opportunityNumber: string; accountUuid: string; contactId: number; isNewAccount: boolean; isNewContact: boolean;
}

// ── Opportunities ────────────────────────────────────────────────────────────

export interface OpportunityListItem {
  uuid: string; opportunityNumber: string; topic: string; accountUuid: string; accountName?: string | null; contactPersonId?: number | null;
  contactPersonName?: string | null; status: OpportunityStatus; pipelineStageUuid?: string | null; pipelineStageName?: string | null;
  winProbability?: number | null; estimatedRevenue?: number | null; actualRevenue?: number | null; currencyCode?: string | null;
  estimatedCloseDate?: string | null; actualCloseDate?: string | null; assignedToUserId?: number | null; assignedToName?: string | null;
  salesTeamUuid?: string | null; salesTeamName?: string | null; sourceLeadUuid: string; sourceLeadNumber: string; createdAt: string;
}

export interface LinkedSalesDocument {
  kind: 'INQUIRY' | 'QUOTATION' | 'ORDER'; uuid: string; number: string; status: string; createdAt: string;
  grandTotal?: number | null; currencyCode?: string | null;
}

export interface OpportunityDetail extends OpportunityListItem {
  description?: string | null; account?: CrmAccount | null; contactPerson?: CrmContact | null; campaignUuid?: string | null;
  campaignName?: string | null; winLossReason?: string | null; competitorName?: string | null; isWonStage: boolean; isLostStage: boolean;
  salesDocuments: LinkedSalesDocument[]; pipeline: PipelineStage[]; allowedActions: OpportunityAction[]; canEdit: boolean;
  createdByUserId: number; createdByName?: string | null; modifiedAt: string;
}

export interface OpportunityUpdate {
  topic?: string; description?: string | null; contactPersonId?: number | null; estimatedRevenue?: number | null;
  estimatedCloseDate?: string | null; currencyCode?: string | null; assignedToUserId?: number | null; salesTeamUuid?: string | null;
}

export interface OpportunityListFilter {
  search?: string; status?: string; pipelineStageUuid?: string; salesTeamUuid?: string; assignedToUserId?: number; mine?: boolean;
  accountUuid?: string; closeFrom?: string; closeTo?: string; sortField?: string; sortOrder?: 'asc' | 'desc'; page?: number; pageSize?: number;
}

export interface SaleOrderPrefill {
  accountUuid: string; accountName?: string | null; opportunityUuid: string; opportunityNumber: string;
  acceptedQuotationUuid?: string | null; acceptedQuotationNumber?: string | null;
}

// ── Activities & timeline ────────────────────────────────────────────────────

export interface ActivityParticipant {
  participantType: 'INTERNAL_USER' | 'CUSTOMER_CONTACT' | 'EXTERNAL'; userId?: number | null; contactPersonId?: number | null;
  name?: string | null; externalName?: string | null; externalEmail?: string | null; role?: string | null;
}

export interface ActivityDocument {
  uuid: string; activityUuid?: string | null; documentType: string; documentName: string; direction: string; notes?: string | null;
  contentType: string; sizeBytes: number; sharedByUserId: number; sharedByName?: string | null; sharedAt: string; pipelineStageName?: string | null;
}

export interface Activity {
  uuid: string; leadUuid?: string | null; leadNumber?: string | null; opportunityUuid?: string | null; opportunityNumber?: string | null;
  regardingName?: string | null; activityType: string; subject: string; description?: string | null; outcome?: string | null;
  outcomeNotes?: string | null; activityDate: string; durationMinutes?: number | null; scheduledDate?: string | null; dueDate?: string | null;
  completedDate?: string | null; isCompleted: boolean; performedByUserId: number; performedByName?: string | null;
  contactPersonId?: number | null; contactPersonName?: string | null; followUpDate?: string | null; followUpType?: string | null;
  followUpNotes?: string | null; pipelineStageName?: string | null; participants: ActivityParticipant[]; documents: ActivityDocument[];
  createdAt: string; canEdit: boolean;
}

export interface ActivityUpsert {
  leadUuid?: string | null; opportunityUuid?: string | null; activityType?: string; subject?: string; description?: string | null;
  outcome?: string | null; outcomeNotes?: string | null; activityDate?: string | null; durationMinutes?: number | null;
  scheduledDate?: string | null; dueDate?: string | null; isCompleted?: boolean; performedByUserId?: number | null;
  contactPersonId?: number | null; followUpDate?: string | null; followUpType?: string | null; followUpNotes?: string | null;
  participants?: ActivityParticipant[] | null;
}

export interface ActivityListFilter {
  leadUuid?: string; opportunityUuid?: string; performedByUserId?: number; mine?: boolean; activityType?: string; from?: string;
  to?: string; state?: 'OPEN' | 'DONE'; search?: string; page?: number; pageSize?: number;
}

export interface PendingActivity { activity: Activity; reason: 'FOLLOW_UP' | 'TASK' | 'SCHEDULED'; dueOn: string; overdue: boolean; }

export interface TimelineUser { userId: number; name: string; }
export interface TimelineEntry {
  entryType: 'ACTIVITY' | 'DOCUMENT' | 'STATUS_CHANGE' | 'STAGE_CHANGE' | 'ASSIGNMENT' | 'CREATED' | 'CONVERTED' | 'SCM_DOCUMENT';
  date: string; fromLead: boolean; leadNumber?: string | null;
  activityUuid?: string | null; activityType?: string | null; subject?: string | null; description?: string | null; outcome?: string | null;
  outcomeNotes?: string | null; isCompleted?: boolean | null; performedBy?: TimelineUser | null; contactPerson?: CrmContact | null;
  durationMinutes?: number | null; pipelineStage?: string | null; followUpDate?: string | null; followUpType?: string | null;
  followUpNotes?: string | null; documents: ActivityDocument[];
  documentUuid?: string | null; documentType?: string | null; documentName?: string | null; direction?: string | null;
  fromValue?: string | null; toValue?: string | null; reason?: string | null; changedBy?: TimelineUser | null;
  scmKind?: string | null; scmUuid?: string | null; scmNumber?: string | null; scmStatus?: string | null;
  grandTotal?: number | null; currencyCode?: string | null;
}
export interface TimelineResponse { timeline: TimelineEntry[]; totalCount: number; page: number; pageSize: number; }

/** Query params from a filter object (undefined / null / '' / [] are left out; arrays are comma-joined). */
function toParams(filter: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(filter)) {
    if (value === undefined || value === null || value === '') continue;
    if (Array.isArray(value)) { if (value.length) params = params.set(key, value.join(',')); continue; }
    params = params.set(key, String(value));
  }
  return params;
}

@Injectable({ providedIn: 'root' })
export class CrmService {
  private readonly base = `${environment.apiUrl}/crm`;

  constructor(private http: HttpClient) {}

  // §1 lookups
  getMeta(): Observable<ApiResponse<CrmMeta>> { return this.http.get<ApiResponse<CrmMeta>>(`${this.base}/meta`); }
  getUsers(search?: string, limit = 100): Observable<ApiResponse<CrmUser[]>> {
    return this.http.get<ApiResponse<CrmUser[]>>(`${this.base}/users`, { params: toParams({ search, limit }) });
  }
  getAccountContacts(accountUuid: string): Observable<ApiResponse<CrmContact[]>> {
    return this.http.get<ApiResponse<CrmContact[]>>(`${this.base}/accounts/${accountUuid}/contacts`);
  }

  // §2 configuration
  getLeadSources(includeInactive = false): Observable<ApiResponse<LeadSource[]>> {
    return this.http.get<ApiResponse<LeadSource[]>>(`${this.base}/lead-sources`, { params: toParams({ includeInactive }) });
  }
  createLeadSource(req: LeadSourceUpsert): Observable<ApiResponse<LeadSource>> { return this.http.post<ApiResponse<LeadSource>>(`${this.base}/lead-sources`, req); }
  updateLeadSource(uuid: string, req: LeadSourceUpsert): Observable<ApiResponse<LeadSource>> {
    return this.http.put<ApiResponse<LeadSource>>(`${this.base}/lead-sources/${uuid}`, req);
  }

  getCampaigns(filter: CampaignListFilter = {}): Observable<ApiResponse<PaginatedResponse<Campaign>>> {
    return this.http.get<ApiResponse<PaginatedResponse<Campaign>>>(`${this.base}/campaigns`, { params: toParams({ page: 1, pageSize: 25, ...filter }) });
  }
  getCampaign(uuid: string): Observable<ApiResponse<Campaign>> { return this.http.get<ApiResponse<Campaign>>(`${this.base}/campaigns/${uuid}`); }
  createCampaign(req: CampaignUpsert): Observable<ApiResponse<Campaign>> { return this.http.post<ApiResponse<Campaign>>(`${this.base}/campaigns`, req); }
  updateCampaign(uuid: string, req: CampaignUpsert): Observable<ApiResponse<Campaign>> {
    return this.http.put<ApiResponse<Campaign>>(`${this.base}/campaigns/${uuid}`, req);
  }
  deleteCampaign(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/campaigns/${uuid}`); }

  getSalesTeams(includeInactive = false): Observable<ApiResponse<SalesTeam[]>> {
    return this.http.get<ApiResponse<SalesTeam[]>>(`${this.base}/sales-teams`, { params: toParams({ includeInactive }) });
  }
  createSalesTeam(req: SalesTeamUpsert): Observable<ApiResponse<SalesTeam>> { return this.http.post<ApiResponse<SalesTeam>>(`${this.base}/sales-teams`, req); }
  updateSalesTeam(uuid: string, req: SalesTeamUpsert): Observable<ApiResponse<SalesTeam>> {
    return this.http.put<ApiResponse<SalesTeam>>(`${this.base}/sales-teams/${uuid}`, req);
  }
  addTeamMember(uuid: string, userId: number, role: 'LEADER' | 'MEMBER' = 'MEMBER'): Observable<ApiResponse<SalesTeam>> {
    return this.http.post<ApiResponse<SalesTeam>>(`${this.base}/sales-teams/${uuid}/members`, { userId, role });
  }
  removeTeamMember(uuid: string, userId: number): Observable<ApiResponse<SalesTeam>> {
    return this.http.delete<ApiResponse<SalesTeam>>(`${this.base}/sales-teams/${uuid}/members/${userId}`);
  }

  getPipelineStages(filter: { stageType?: string; salesTeamUuid?: string; includeInactive?: boolean } = {}): Observable<ApiResponse<PipelineStage[]>> {
    return this.http.get<ApiResponse<PipelineStage[]>>(`${this.base}/pipeline-stages`, { params: toParams(filter) });
  }
  createPipelineStage(req: PipelineStageUpsert): Observable<ApiResponse<PipelineStage>> {
    return this.http.post<ApiResponse<PipelineStage>>(`${this.base}/pipeline-stages`, req);
  }
  updatePipelineStage(uuid: string, req: PipelineStageUpsert): Observable<ApiResponse<PipelineStage>> {
    return this.http.put<ApiResponse<PipelineStage>>(`${this.base}/pipeline-stages/${uuid}`, req);
  }
  deactivatePipelineStage(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/pipeline-stages/${uuid}`); }

  getApiKeys(): Observable<ApiResponse<LeadApiKey[]>> { return this.http.get<ApiResponse<LeadApiKey[]>>(`${this.base}/api-keys`); }
  createApiKey(name: string, requestsPerMinute?: number): Observable<ApiResponse<LeadApiKeyCreated>> {
    return this.http.post<ApiResponse<LeadApiKeyCreated>>(`${this.base}/api-keys`, { name, requestsPerMinute });
  }
  updateApiKey(uuid: string, req: { name?: string; requestsPerMinute?: number; isActive?: boolean }): Observable<ApiResponse<LeadApiKey>> {
    return this.http.put<ApiResponse<LeadApiKey>>(`${this.base}/api-keys/${uuid}`, req);
  }
  deleteApiKey(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/api-keys/${uuid}`); }

  // §3 leads
  getLeads(filter: LeadListFilter = {}): Observable<ApiResponse<PaginatedResponse<LeadListItem>>> {
    return this.http.get<ApiResponse<PaginatedResponse<LeadListItem>>>(`${this.base}/leads`, { params: toParams({ page: 1, pageSize: 25, ...filter }) });
  }
  getLeadKanban(filter: KanbanFilter = {}): Observable<ApiResponse<KanbanBoard<LeadListItem>>> {
    return this.http.get<ApiResponse<KanbanBoard<LeadListItem>>>(`${this.base}/leads/kanban`, { params: toParams(filter) });
  }
  checkLeadDuplicates(q: { email?: string | null; phone?: string | null; companyName?: string | null; excludeUuid?: string }):
    Observable<ApiResponse<LeadDuplicateResult>> {
    return this.http.get<ApiResponse<LeadDuplicateResult>>(`${this.base}/leads/duplicate-check`, { params: toParams(q) });
  }
  getLead(uuid: string): Observable<ApiResponse<LeadDetail>> { return this.http.get<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}`); }
  createLead(req: LeadUpsert): Observable<ApiResponse<LeadDetail>> { return this.http.post<ApiResponse<LeadDetail>>(`${this.base}/leads`, req); }
  updateLead(uuid: string, req: LeadUpsert): Observable<ApiResponse<LeadDetail>> { return this.http.put<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}`, req); }
  assignLead(uuid: string, userId: number | null, salesTeamUuid?: string | null): Observable<ApiResponse<LeadDetail>> {
    return this.http.patch<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/assign`, { userId, salesTeamUuid });
  }
  changeLeadStatus(uuid: string, status: LeadStatus, reason?: string): Observable<ApiResponse<LeadDetail>> {
    return this.http.patch<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/status`, { status, reason });
  }
  moveLeadStage(uuid: string, stageUuid: string): Observable<ApiResponse<LeadDetail>> {
    return this.http.patch<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/stage`, { stageUuid });
  }
  qualifyLead(uuid: string): Observable<ApiResponse<LeadDetail>> { return this.http.post<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/qualify`, {}); }
  disqualifyLead(uuid: string, reason: string): Observable<ApiResponse<LeadDetail>> {
    return this.http.post<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/disqualify`, { reason });
  }
  reopenLead(uuid: string): Observable<ApiResponse<LeadDetail>> { return this.http.post<ApiResponse<LeadDetail>>(`${this.base}/leads/${uuid}/reopen`, {}); }
  deleteLead(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/leads/${uuid}`); }
  getLeadCrossReferences(uuid: string): Observable<ApiResponse<CrossReferenceResult>> {
    return this.http.get<ApiResponse<CrossReferenceResult>>(`${this.base}/leads/${uuid}/cross-references`);
  }
  getLeadTimeline(uuid: string, page = 1, pageSize = 50, search?: string): Observable<ApiResponse<TimelineResponse>> {
    return this.http.get<ApiResponse<TimelineResponse>>(`${this.base}/leads/${uuid}/timeline`, { params: toParams({ page, pageSize, search: search || undefined }) });
  }
  exportLeadTimeline(uuid: string): Observable<Blob> {
    return this.http.get(`${this.base}/leads/${uuid}/timeline/export`, { params: toParams({ format: 'xlsx' }), responseType: 'blob' });
  }
  getAccountMatches(leadUuid: string, search?: string): Observable<ApiResponse<CrmAccount[]>> {
    return this.http.get<ApiResponse<CrmAccount[]>>(`${this.base}/leads/${leadUuid}/account-matches`, { params: toParams({ search }) });
  }
  convertLead(uuid: string, req: LeadConvertRequest): Observable<ApiResponse<LeadConversionResult>> {
    return this.http.post<ApiResponse<LeadConversionResult>>(`${this.base}/leads/${uuid}/convert`, req);
  }

  // §4 opportunities
  getOpportunities(filter: OpportunityListFilter = {}): Observable<ApiResponse<PaginatedResponse<OpportunityListItem>>> {
    return this.http.get<ApiResponse<PaginatedResponse<OpportunityListItem>>>(`${this.base}/opportunities`,
      { params: toParams({ page: 1, pageSize: 25, ...filter }) });
  }
  getOpportunityKanban(filter: KanbanFilter = {}): Observable<ApiResponse<KanbanBoard<OpportunityListItem>>> {
    return this.http.get<ApiResponse<KanbanBoard<OpportunityListItem>>>(`${this.base}/opportunities/kanban`, { params: toParams(filter) });
  }
  getOpportunity(uuid: string): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.get<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}`);
  }
  updateOpportunity(uuid: string, req: OpportunityUpdate): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.put<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}`, req);
  }
  moveOpportunityStage(uuid: string, stageUuid: string): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.patch<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}/stage`, { stageUuid });
  }
  markWon(uuid: string): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.post<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}/won`, {});
  }
  markLost(uuid: string, reason: string, competitor?: string | null): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.post<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}/lost`, { reason, competitor });
  }
  reopenOpportunity(uuid: string): Observable<ApiResponse<OpportunityDetail>> {
    return this.http.post<ApiResponse<OpportunityDetail>>(`${this.base}/opportunities/${uuid}/reopen`, {});
  }
  createInquiry(uuid: string): Observable<ApiResponse<LinkedSalesDocument>> {
    return this.http.post<ApiResponse<LinkedSalesDocument>>(`${this.base}/opportunities/${uuid}/create-inquiry`, {});
  }
  createQuotation(uuid: string): Observable<ApiResponse<LinkedSalesDocument>> {
    return this.http.post<ApiResponse<LinkedSalesDocument>>(`${this.base}/opportunities/${uuid}/create-quotation`, {});
  }
  createOrder(uuid: string): Observable<ApiResponse<SaleOrderPrefill>> {
    return this.http.post<ApiResponse<SaleOrderPrefill>>(`${this.base}/opportunities/${uuid}/create-order`, {});
  }
  getOpportunityTimeline(uuid: string, page = 1, pageSize = 50, search?: string): Observable<ApiResponse<TimelineResponse>> {
    return this.http.get<ApiResponse<TimelineResponse>>(`${this.base}/opportunities/${uuid}/timeline`, { params: toParams({ page, pageSize, search: search || undefined }) });
  }
  exportOpportunityTimeline(uuid: string): Observable<Blob> {
    return this.http.get(`${this.base}/opportunities/${uuid}/timeline/export`, { params: toParams({ format: 'xlsx' }), responseType: 'blob' });
  }

  // §5 activities & documents
  getActivities(filter: ActivityListFilter = {}): Observable<ApiResponse<PaginatedResponse<Activity>>> {
    return this.http.get<ApiResponse<PaginatedResponse<Activity>>>(`${this.base}/activities`, { params: toParams({ page: 1, pageSize: 25, ...filter }) });
  }
  getPendingActivities(filter: { userId?: number; mine?: boolean; dueBy?: string } = {}): Observable<ApiResponse<PendingActivity[]>> {
    return this.http.get<ApiResponse<PendingActivity[]>>(`${this.base}/activities/pending`, { params: toParams(filter) });
  }
  getActivity(uuid: string): Observable<ApiResponse<Activity>> { return this.http.get<ApiResponse<Activity>>(`${this.base}/activities/${uuid}`); }
  createActivity(req: ActivityUpsert): Observable<ApiResponse<Activity>> { return this.http.post<ApiResponse<Activity>>(`${this.base}/activities`, req); }
  updateActivity(uuid: string, req: ActivityUpsert): Observable<ApiResponse<Activity>> {
    return this.http.put<ApiResponse<Activity>>(`${this.base}/activities/${uuid}`, req);
  }
  completeActivity(uuid: string): Observable<ApiResponse<Activity>> {
    return this.http.patch<ApiResponse<Activity>>(`${this.base}/activities/${uuid}/complete`, {});
  }
  deleteActivity(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/activities/${uuid}`); }
  attachDocument(activityUuid: string, file: File, meta: { documentType?: string; direction: string; notes?: string; documentName?: string }):
    Observable<ApiResponse<ActivityDocument>> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('direction', meta.direction);
    if (meta.documentType) form.append('documentType', meta.documentType);
    if (meta.notes) form.append('notes', meta.notes);
    if (meta.documentName) form.append('documentName', meta.documentName);
    return this.http.post<ApiResponse<ActivityDocument>>(`${this.base}/activities/${activityUuid}/documents`, form);
  }
  downloadDocument(uuid: string): Observable<Blob> {
    return this.http.get(`${this.base}/documents/${uuid}/content`, { responseType: 'blob' });
  }
  deleteDocument(uuid: string): Observable<ApiResponse> { return this.http.delete<ApiResponse>(`${this.base}/documents/${uuid}`); }
}

/** Saves a downloaded blob under a file name (the browser's download). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
