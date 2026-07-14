import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './demand.service';

const BASE = 'https://localhost:51800/api/workflow';

// ── Inbox ─────────────────────────────────────────────────────────────────────

export interface InboxFilter {
  interfaceCode?: string;
  fromDate?: string;
  toDate?: string;
  page?: number;
  pageSize?: number;
}

export interface InboxItemDto {
  approvalUUID: string;
  stepUUID: string;
  interfaceCode: string;
  documentId: string;
  documentNumber: string;
  stepName: string;
  stepNumber: number;
  totalSteps: number;
  approvalMode: string;
  canReject: boolean;
  submittedBy: number;
  submittedAt: string;
  dueAt: string | null;
  slaDeadline: string | null;
  isEscalated: boolean;
  remarks: string | null;
}

export interface ApprovalDetailDto {
  uuid: string;
  interfaceCode: string;
  documentId: string;
  documentNumber: string;
  workflowName: string | null;
  revisionNo: number;
  status: string;
  totalSteps: number;
  currentStepNumber: number;
  allowRecall: boolean;
  allowReissue: boolean;
  isEscalated: boolean;
  initiatedBy: number;
  initiatedByName: string | null;
  initiatedAt: string;
  slaDeadline: string | null;
  completedAt: string | null;
  remarks: string | null;
  steps: StepDetailDto[];
}

export interface StepDetailDto {
  uuid: string;
  stepNumber: number;
  stepName: string;
  stepType: string;
  approvalMode: string;
  canReject: boolean;
  assignedTo: number | null;
  resolvedApproverName: string | null;
  assignedRole: string | null;
  status: string;
  actedBy: number | null;
  actedAt: string | null;
  comments: string | null;
  dueAt: string | null;
  isEscalated: boolean;
  isCurrentStep: boolean;
}

export interface AuditLogEntryDto {
  uuid: string;
  stepNumber: number | null;
  action: string;
  performedBy: number;
  performedByName: string | null;
  performedAt: string;
  fromStatus: string | null;
  toStatus: string | null;
  remarks: string | null;
}

// ── Definitions (Admin) ───────────────────────────────────────────────────────

export interface WorkflowDefinitionListFilter {
  interfaceCode?: string;
  isActive?: boolean;
  page?: number;
  pageSize?: number;
}

export interface WorkflowDefinitionListItemModel {
  uuid: string;
  interfaceCode: string;
  name: string;
  version: number;
  isActive: boolean;
  requiresSequentialApproval: boolean;
  allowRecall: boolean;
  allowReissue: boolean;
  slaHours: number;
  escalationAdminId: number | null;
  conditionField: string | null;
  conditionOperator: string | null;
  conditionValue: number | null;
  conditionValueMin: number | null;
  conditionValueMax: number | null;
  stepCount: number;
  createdDate: string;
}

export interface WorkflowStepModel {
  uuid: string;
  stepNumber: number;
  stepName: string;
  stepType: string;
  approverType: string;
  approverRefId: number | null;
  approverRole: string | null;
  approverRefName: string;
  isMandatory: boolean;
  isFinalStep: boolean;
  slaHoursOverride: number | null;
  escalationUserId: number | null;
  skipCondition: string | null;
  stepInstructions: string | null;
  approvalMode: string;
  canReject: boolean;
  canRecall: boolean;
}

export interface WorkflowDefinitionDetailModel {
  uuid: string;
  interfaceCode: string;
  name: string;
  description: string | null;
  version: number;
  isActive: boolean;
  requiresSequentialApproval: boolean;
  allowRecall: boolean;
  allowReissue: boolean;
  slaHours: number;
  escalationAdminId: number | null;
  conditionField: string | null;
  conditionOperator: string | null;
  conditionValue: number | null;
  conditionValueMin: number | null;
  conditionValueMax: number | null;
  createdBy: number;
  createdDate: string;
  steps: WorkflowStepModel[];
}

export interface CreateWorkflowDefinitionRequest {
  interfaceCode: string;
  name: string;
  description?: string;
  requiresSequentialApproval?: boolean;
  allowRecall?: boolean;
  allowReissue?: boolean;
  slaHours?: number;
  escalationAdminId?: number | null;
  conditionField?: string | null;
  conditionOperator?: string | null;
  conditionValue?: number | null;
  conditionValueMin?: number | null;
  conditionValueMax?: number | null;
  steps: AddWorkflowStepRequest[];
}

export interface UpdateWorkflowDefinitionRequest {
  name: string;
  description?: string;
  requiresSequentialApproval?: boolean;
  allowRecall?: boolean;
  allowReissue?: boolean;
  slaHours?: number;
  escalationAdminId?: number | null;
  conditionField?: string | null;
  conditionOperator?: string | null;
  conditionValue?: number | null;
  conditionValueMin?: number | null;
  conditionValueMax?: number | null;
  steps: AddWorkflowStepRequest[];
}

export interface AddWorkflowStepRequest {
  stepNumber: number;
  stepName: string;
  stepType: string;
  approverType: string;
  approverRefId?: number | null;
  approverRole?: string | null;
  approverRefName?: string | null;
  isMandatory?: boolean;
  isFinalStep?: boolean;
  slaHoursOverride?: number | null;
  escalationUserId?: number | null;
  skipCondition?: string | null;
  stepInstructions?: string | null;
  approvalMode: string;
  canReject?: boolean;
  canRecall?: boolean;
}

export interface UpdateWorkflowStepRequest {
  stepName: string;
  stepType: string;
  approverType: string;
  approverRefId?: number | null;
  approverRole?: string | null;
  approverRefName?: string | null;
  isMandatory?: boolean;
  isFinalStep?: boolean;
  slaHoursOverride?: number | null;
  escalationUserId?: number | null;
  skipCondition?: string | null;
  stepInstructions?: string | null;
  approvalMode: string;
  canReject?: boolean;
  canRecall?: boolean;
}

// ── Config (Admin) ────────────────────────────────────────────────────────────

export interface InterfaceSummaryDto {
  interfaceCode: string;
  activeWorkflowName: string | null;
  activeVersion: number | null;
  stepCount: number | null;
  hasActiveWorkflow: boolean;
}

export interface InterfaceWorkflowVersionDto {
  uuid: string;
  name: string;
  description: string | null;
  version: number;
  isActive: boolean;
  stepCount: number;
  createdDate: string;
  steps: WorkflowStepModel[];
}

export interface ApproverOptionsDto {
  users: UserOptionDto[];
  roles: RoleOptionDto[];
  groups: GroupOptionDto[];
}

export interface UserOptionDto  { id: number; fullName: string; email: string; }
export interface RoleOptionDto  { id: number; name: string; }
export interface GroupOptionDto { uuid: string; id: number; name: string; type: string; }

export interface ConditionFieldDto {
  field: string;
  label: string;
  dataType: string;
}

// ── History ───────────────────────────────────────────────────────────────────

export interface DocumentApprovalHistoryItem {
  uuid: string;
  revisionNo: number;
  documentId: string;
  documentNumber: string;
  interfaceCode: string;
  status: string;
  totalSteps: number;
  currentStepNumber: number;
  initiatedBy: number;
  initiatedAt: string;
  completedAt: string | null;
  remarks: string | null;
  createdDate: string;
  steps: RevisionStepSummary[];
}

export interface RevisionStepSummary {
  stepNumber: number;
  stepName: string;
  resolvedApproverName: string | null;
  status: string;
  actedBy: number | null;
  actedAt: string | null;
  comments: string | null;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class WorkflowService {
  constructor(private http: HttpClient) {}

  // ── Inbox ──────────────────────────────────────────────────────────────────
  getInbox(filter: InboxFilter): Observable<ApiResponse<PaginatedResponse<InboxItemDto>>> {
    let params = new HttpParams()
      .set('page', filter.page ?? 1)
      .set('pageSize', filter.pageSize ?? 20);
    if (filter.interfaceCode) params = params.set('interfaceCode', filter.interfaceCode);
    if (filter.fromDate)      params = params.set('fromDate', filter.fromDate);
    if (filter.toDate)        params = params.set('toDate', filter.toDate);
    return this.http.get<ApiResponse<PaginatedResponse<InboxItemDto>>>(`${BASE}/inbox`, { params });
  }

  getInboxCount(): Observable<ApiResponse<{ count: number }>> {
    return this.http.get<ApiResponse<{ count: number }>>(`${BASE}/inbox/count`);
  }

  // ── Approval actions ───────────────────────────────────────────────────────
  approve(approvalUUID: string, remarks?: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/approve`, { approvalUUID, remarks });
  }

  reject(approvalUUID: string, rejectionReason: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/approvals/${approvalUUID}/reject`, { rejectionReason });
  }

  delegate(approvalUUID: string, delegateUserId: number, remarks?: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/approvals/${approvalUUID}/delegate`, { delegateUserId, remarks });
  }

  recall(approvalUUID: string, recallReason?: string): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/approvals/${approvalUUID}/recall`, { recallReason });
  }

  reissue(approvalUUID: string, conditionValue?: number): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/approvals/${approvalUUID}/reissue`, { conditionValue });
  }

  // ── Approval detail ────────────────────────────────────────────────────────
  getApprovalDetail(uuid: string): Observable<ApiResponse<ApprovalDetailDto>> {
    return this.http.get<ApiResponse<ApprovalDetailDto>>(`${BASE}/approvals/${uuid}`);
  }

  getApprovalAudit(uuid: string): Observable<ApiResponse<AuditLogEntryDto[]>> {
    return this.http.get<ApiResponse<AuditLogEntryDto[]>>(`${BASE}/approvals/${uuid}/audit`);
  }

  // ── Definitions (Admin) ────────────────────────────────────────────────────
  getDefinitions(filter: WorkflowDefinitionListFilter): Observable<ApiResponse<PaginatedResponse<WorkflowDefinitionListItemModel>>> {
    let params = new HttpParams()
      .set('page', filter.page ?? 1)
      .set('pageSize', filter.pageSize ?? 20);
    if (filter.interfaceCode !== undefined && filter.interfaceCode !== '')
      params = params.set('interfaceCode', filter.interfaceCode);
    if (filter.isActive !== undefined)
      params = params.set('isActive', filter.isActive);
    return this.http.get<ApiResponse<PaginatedResponse<WorkflowDefinitionListItemModel>>>(`${BASE}/definitions`, { params });
  }

  getDefinitionById(id: string): Observable<ApiResponse<WorkflowDefinitionDetailModel>> {
    return this.http.get<ApiResponse<WorkflowDefinitionDetailModel>>(`${BASE}/definitions/${id}`);
  }

  createDefinition(req: CreateWorkflowDefinitionRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/definitions`, req);
  }

  updateDefinition(id: string, req: UpdateWorkflowDefinitionRequest): Observable<ApiResponse<string>> {
    return this.http.put<ApiResponse<string>>(`${BASE}/definitions/${id}`, req);
  }

  updateDefinitionInPlace(id: string, req: UpdateWorkflowDefinitionRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/definitions/${id}`, req);
  }

  activateDefinition(id: string): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/definitions/${id}/activate`, {});
  }

  deactivateDefinition(id: string): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`${BASE}/definitions/${id}/deactivate`, {});
  }

  addStep(definitionId: string, req: AddWorkflowStepRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/definitions/${definitionId}/steps`, req);
  }

  updateStep(defId: string, stepId: string, req: UpdateWorkflowStepRequest): Observable<ApiResponse<null>> {
    return this.http.put<ApiResponse<null>>(`${BASE}/definitions/${defId}/steps/${stepId}`, req);
  }

  deleteStep(defId: string, stepId: string): Observable<ApiResponse<null>> {
    return this.http.delete<ApiResponse<null>>(`${BASE}/definitions/${defId}/steps/${stepId}`);
  }

  // ── Config (Admin) ─────────────────────────────────────────────────────────
  getInterfaceSummaries(): Observable<ApiResponse<InterfaceSummaryDto[]>> {
    return this.http.get<ApiResponse<InterfaceSummaryDto[]>>(`${BASE}/config/interfaces`);
  }

  getInterfaceWorkflows(code: string): Observable<ApiResponse<InterfaceWorkflowVersionDto[]>> {
    return this.http.get<ApiResponse<InterfaceWorkflowVersionDto[]>>(`${BASE}/config/interfaces/${code}/workflows`);
  }

  getApproverOptions(): Observable<ApiResponse<ApproverOptionsDto>> {
    return this.http.get<ApiResponse<ApproverOptionsDto>>(`${BASE}/config/approver-options`);
  }

  getConditionFields(interfaceCode: string): Observable<ApiResponse<ConditionFieldDto[]>> {
    return this.http.get<ApiResponse<ConditionFieldDto[]>>(`${BASE}/config/condition-fields/${interfaceCode}`);
  }

  // ── History ────────────────────────────────────────────────────────────────
  getHistory(params: { documentId?: string; documentNumber?: string; interfaceCode?: string }):
    Observable<ApiResponse<DocumentApprovalHistoryItem[]>> {
    let hp = new HttpParams();
    if (params.documentId)     hp = hp.set('documentId', params.documentId);
    if (params.documentNumber) hp = hp.set('documentNumber', params.documentNumber);
    if (params.interfaceCode)  hp = hp.set('interfaceCode', params.interfaceCode);
    return this.http.get<ApiResponse<DocumentApprovalHistoryItem[]>>(`${BASE}/history`, { params: hp });
  }
}
