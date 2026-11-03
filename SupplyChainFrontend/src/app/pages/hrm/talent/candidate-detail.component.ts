import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import { FLOW } from '../../../shared/flow';
import { saveDownload } from '../../../services/import.service';
import { AuthService } from '../../service/auth.service';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf, markFilesSaved } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { HrmPdfViewerComponent } from '../core/hrm-pdf-viewer.component';
import { HRM_ROUTE } from '../core/hrm.nav';
import { ENUMS, hrmError, hrmErrorAsync, hrmLabel, hrmTone, money } from '../core/hrm.shared';
import { CANDIDATE_FIELDS } from '../configs/tal.config';

const INTERVIEW_FIELDS: Field[] = [
  { key: 'scheduledAt', label: 'When', type: 'datetime', required: true }, { key: 'interviewerUuid', label: 'Interviewer', type: 'lookup', lookup: 'employees' },
  { key: 'result', label: 'Result', type: 'select', options: ENUMS.interviewResult, required: true, def: 'Pending' }, { key: 'score', label: 'Score', type: 'number', min: 0 }, { key: 'feedback', label: 'Feedback', type: 'textarea', maxLength: 2000 }
];
const OFFER_FIELDS: Field[] = [
  { key: 'offeredSalary', label: 'Offered salary', type: 'number', min: 0 }, { key: 'proposedJoinDate', label: 'Proposed join date', type: 'date' }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 1000 }
];
const OFFER_STATUS: Field[] = [{ key: 'status', label: 'Status', type: 'select', options: ENUMS.offerStatus, required: true }];

type Dlg = 'edit' | 'resume' | 'interview' | 'offer' | 'offerStatus';

/** Tasks 210, 211 — a candidate: details, resume in the page, applications, interview schedule and feedback, offers with their letter. */
@Component({
  selector: 'app-hrm-candidate-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ToastModule, HrmDialogComponent, HrmFormComponent, HrmPdfViewerComponent, ...FLOW],
  providers: [MessageService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['HR', 'Recruitment', 'Candidates', name]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <ng-container *ngIf="c">
      <p-button *ngIf="canManage" label="Edit" icon="pi pi-pencil" (onClick)="openEdit()" data-testid="edit"></p-button>
      <p-button *ngIf="canManage" label="Upload resume" icon="pi pi-upload" [text]="true" (onClick)="openResume()" data-testid="upload"></p-button>
      <p-button *ngIf="c.resumeAttachmentUuid" label="View resume" icon="pi pi-file-pdf" [text]="true" (onClick)="viewerOpen = true" data-testid="resume"></p-button>
    </ng-container>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="back"><i class="pi pi-arrow-left"></i>&nbsp;Back</a></span>
  </div>
  <div class="sf-body" *ngIf="error"><div class="gl-note er" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div></div>
  <ng-container *ngIf="c">
    <header class="sf-hdr"><div class="sf-t"><div><div class="sf-kicker">Candidate</div><h1>{{ name }}</h1><div class="sf-meta">{{ c.email || 'no email' }} · {{ c.phone || 'no phone' }}<ng-container *ngIf="c.source"> · via {{ c.source }}</ng-container></div></div></div></header>
    <div class="sf-body">
      <section class="sf-card" *ngIf="c.notes"><div class="sf-ch"><h2>Notes</h2></div><p style="margin:0;white-space:pre-wrap">{{ c.notes }}</p></section>
      <section class="sf-card p0" *ngFor="let a of c.applications ?? []" [attr.data-testid]="'app-' + a.uuid">
        <div class="sf-ch sf-pad"><h2>{{ a.vacancyTitle }} <span class="sf-pill" [ngClass]="hrmTone(a.stage)">{{ hrmLabel(a.stage) }}</span></h2>
          <span class="sf-ch-actions" *ngIf="canManage">
            <p-button label="Schedule interview" icon="pi pi-calendar-plus" [text]="true" size="small" (onClick)="openInterview(a)" [attr.data-testid]="'interview-' + a.uuid"></p-button>
            <p-button label="New offer" icon="pi pi-file-edit" [text]="true" size="small" (onClick)="openOffer(a)" [attr.data-testid]="'offer-' + a.uuid"></p-button>
          </span></div>
        <div class="gl-scroll"><table class="hrm-table hrm-stack">
          <thead><tr><th>Interview</th><th>Interviewer</th><th>Score</th><th>Result</th><th>Feedback</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let i of interviews[a.uuid] ?? []"><td data-label="When">{{ i.scheduledAt | date:'dd MMM yyyy, HH:mm' }}</td><td data-label="Interviewer">{{ i.interviewerName || '—' }}</td><td data-label="Score">{{ i.score ?? '—' }}</td><td data-label="Result"><span class="sf-pill" [ngClass]="hrmTone(i.result)">{{ hrmLabel(i.result) }}</span></td><td data-label="Feedback">{{ i.feedback || '—' }}</td>
              <td class="hrm-acts"><div class="hrm-actions"><p-button *ngIf="canManage" label="Feedback" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openFeedback(a, i)" [attr.data-testid]="'feedback-' + i.uuid"></p-button></div></td></tr>
            <tr *ngIf="!(interviews[a.uuid] ?? []).length"><td class="hrm-empty sf-mu" colspan="6">No interviews yet.</td></tr>
          </tbody>
        </table></div>
        <div class="gl-scroll" *ngIf="offers[a.uuid]?.length"><table class="hrm-table hrm-stack">
          <thead><tr><th>Offer</th><th>Join date</th><th>Status</th><th></th></tr></thead>
          <tbody><tr *ngFor="let o of offers[a.uuid]"><td data-label="Salary">{{ o.offeredSalary === null || o.offeredSalary === undefined ? 'Hidden' : money(o.offeredSalary) }}</td><td data-label="Join date">{{ o.proposedJoinDate ? (o.proposedJoinDate | date:'dd MMM yyyy') : '—' }}</td><td data-label="Status"><span class="sf-pill" [ngClass]="hrmTone(o.status)">{{ hrmLabel(o.status) }}</span></td>
            <td class="hrm-acts"><div class="hrm-actions" *ngIf="canManage"><p-button label="Status" icon="pi pi-flag" [text]="true" size="small" (onClick)="openOfferStatus(o)"></p-button><p-button label="Make letter" icon="pi pi-file-edit" [text]="true" size="small" (onClick)="makeLetter(o)"></p-button><p-button *ngIf="o.letterAttachmentUuid" label="Letter" icon="pi pi-download" [text]="true" size="small" (onClick)="letter(o)"></p-button></div></td></tr></tbody>
        </table></div>
      </section>
      <div class="sf-card sf-empty" *ngIf="!(c.applications ?? []).length">No applications. Add the candidate from a vacancy's board.</div>
    </div>
  </ng-container>
</sf-page>
<hrm-dialog [visible]="dlg !== null" (visibleChange)="$event ? null : (dlg = null)" [title]="dlgTitle" [subtitle]="name" [icon]="dlg === 'interview' ? 'pi-calendar-plus' : 'pi-pencil'" [busy]="busy" [error]="dlgError" (save)="saveDlg()">
  <hrm-form *ngIf="dlg" [fields]="dlgFields" [values]="dlgValues" [lookups]="lookups" idPrefix="cand"></hrm-form>
</hrm-dialog>
<hrm-pdf-viewer [(visible)]="viewerOpen" [attachmentUuid]="c?.resumeAttachmentUuid ?? null" [fileName]="name + ' resume'"></hrm-pdf-viewer>`
})
export class CandidateDetailComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);
  readonly lookups = inject(HrmLookups);

  readonly back = `${HRM_ROUTE}/recruitment/candidates`;
  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;
  readonly money = money;

  uuid = '';
  c: any = null;
  error = '';
  interviews: Record<string, any[]> = {};
  offers: Record<string, any[]> = {};
  viewerOpen = false;

  dlg: Dlg | null = null;
  dlgFields: Field[] = [];
  dlgValues: Record<string, any> = {};
  dlgRow: any = null;
  dlgOffer: any = null;
  dlgError = '';
  busy = false;

  get canManage(): boolean { return this.auth.hasPermission('HRM_RECRUITMENT_MANAGE'); }
  get name(): string { return this.c ? `${this.c.firstName} ${this.c.lastName ?? ''}`.trim() : ''; }
  get dlgTitle(): string { return { edit: 'Edit candidate', resume: 'Upload resume', interview: this.dlgOffer ? 'Interview feedback' : 'Schedule interview', offer: 'New offer', offerStatus: 'Offer status' }[this.dlg ?? 'edit']; }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load(): void {
    this.api.get<any>(`hrm/candidates/${this.uuid}`).subscribe({
      next: c => {
        this.c = c;
        for (const a of c?.applications ?? []) {
          this.api.get<any>(`hrm/applications/${a.uuid}/interviews`).subscribe({ next: r => { this.interviews[a.uuid] = rowsOf(r); }, error: () => { this.interviews[a.uuid] = []; } });
          this.api.get<any>(`hrm/applications/${a.uuid}/offers`).subscribe({ next: r => { this.offers[a.uuid] = rowsOf(r); }, error: () => { this.offers[a.uuid] = []; } });
        }
      },
      error: err => { this.error = hrmError(err, 'The candidate could not be loaded.'); }
    });
  }

  private open(kind: Dlg, fields: Field[], values?: Record<string, any>, row: any = null, offer: any = null): void {
    this.dlg = kind;
    this.dlgFields = fields;
    this.dlgValues = { ...formDefaults(fields), ...(values ?? {}) };
    this.dlgRow = row;
    this.dlgOffer = offer;
    this.dlgError = '';
    this.lookups.need(lookupsOf(fields)).subscribe();
  }

  openEdit(): void { this.open('edit', CANDIDATE_FIELDS, this.c); }
  openResume(): void { this.open('resume', [{ key: 'resumeAttachmentUuid', label: 'Resume file', type: 'file', required: true, interfaceCode: 'HRM_CANDIDATE', docId: () => this.uuid }]); }
  openInterview(a: any): void { this.open('interview', INTERVIEW_FIELDS, undefined, a); }
  /** `dlgOffer` here holds the interview being updated. */
  openFeedback(a: any, i: any): void { this.open('interview', INTERVIEW_FIELDS, { ...i, scheduledAt: (i.scheduledAt ?? '').slice(0, 16) }, a, i); }
  openOffer(a: any): void { this.open('offer', OFFER_FIELDS, undefined, a); }
  openOfferStatus(o: any): void { this.open('offerStatus', OFFER_STATUS, { status: o.status }, null, o); }

  saveDlg(): void {
    this.dlgError = formProblem(this.dlgFields, this.dlgValues) ?? '';
    if (this.dlgError) return;
    const v = cleanBody(this.dlgFields, this.dlgValues);
    let call: Observable<any>;
    switch (this.dlg) {
      case 'edit': call = this.api.put(`hrm/candidates/${this.uuid}`, { ...this.c, ...v, applications: undefined }); break;
      case 'resume': call = this.api.put(`hrm/candidates/${this.uuid}`, { ...this.c, resumeAttachmentUuid: v['resumeAttachmentUuid'], applications: undefined }); break;
      case 'interview': call = this.dlgOffer ? this.api.put(`hrm/interviews/${this.dlgOffer.uuid}`, v) : this.api.post(`hrm/applications/${this.dlgRow.uuid}/interviews`, v); break;
      case 'offer': call = this.api.post(`hrm/applications/${this.dlgRow.uuid}/offers`, v); break;
      case 'offerStatus': call = this.api.patch(`hrm/offers/${this.dlgOffer.uuid}/status`, v); break;
      default: return;
    }
    this.busy = true;
    call.subscribe({
      next: () => { markFilesSaved(this.dlgValues); this.busy = false; this.dlg = null; this.messages.add({ severity: 'success', summary: 'Saved', detail: 'Saved.', life: 3000 }); this.load(); },
      error: err => { this.busy = false; this.dlgError = hrmError(err, 'It could not be saved.'); }
    });
  }

  makeLetter(o: any): void {
    this.api.post(`hrm/offers/${o.uuid}/letter`).subscribe({
      next: () => { this.messages.add({ severity: 'success', summary: 'Done', detail: 'The offer letter is made.', life: 3000 }); this.load(); },
      error: err => this.messages.add({ severity: 'error', summary: 'Not done', detail: hrmError(err, 'The letter could not be made.'), life: 8000 })
    });
  }

  letter(o: any): void {
    this.api.blob(`hrm/offers/${o.uuid}/letter`).subscribe({
      next: res => saveDownload(res, `offer-${this.name}.pdf`),
      error: async err => this.messages.add({ severity: 'error', summary: 'Not downloaded', detail: await hrmErrorAsync(err, 'The letter could not be downloaded.'), life: 8000 })
    });
  }
}
