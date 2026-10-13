import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { forkJoin, of, switchMap, catchError, map } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';

import { Activity, ActivityParticipant, ActivityUpsert, CrmContact, CrmService, crmLabel } from '../../../services/crm.service';
import { toDateOnly, fromDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from './crm-lookups.service';
import { CrmRichTextComponent } from './crm-rich-text.component';

interface PendingFile { file: File; documentType: string; direction: string; notes: string; }

/**
 * A41 §15.6 — log or edit an activity on a lead or an opportunity: type, subject, when, duration, the customer contact,
 * rich-text notes, outcome, participants, documents (uploaded once the activity is saved) and a follow-up.
 * The server's 400 message is shown as it comes. A conversation logged on a NEW lead makes it CONTACTED (server side).
 */
@Component({
  selector: 'app-activity-form-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CalendarModule, CheckboxModule, DialogModule, DropdownModule, InputNumberModule,
    InputTextModule, CrmRichTextComponent],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '52rem' }"
          [breakpoints]="{ '820px': '96vw' }" [header]="header" data-testid="activity-dialog">
  <ng-container *ngIf="visible">
    <div class="sf-pill er af-error" *ngIf="error" role="alert" data-testid="activity-error">{{ error }}</div>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="af-type" class="sf-req">Type</label>
        <p-dropdown inputId="af-type" [options]="typeOptions" [(ngModel)]="form.activityType" optionLabel="label" optionValue="value"
                    appendTo="body" styleClass="w-full" [disabled]="!!activity" data-testid="activity-type"></p-dropdown>
      </div>
      <div class="sf-fld span-2">
        <label for="af-subject" class="sf-req">Subject</label>
        <input pInputText id="af-subject" [(ngModel)]="form.subject" maxlength="200" data-testid="activity-subject" />
      </div>
      <div class="sf-fld" *ngIf="form.activityType !== 'TASK'">
        <label for="af-date">Date &amp; time</label>
        <p-calendar inputId="af-date" [(ngModel)]="activityDate" [showTime]="true" hourFormat="24" dateFormat="dd M yy" [showIcon]="true"
                    appendTo="body" styleClass="w-full" data-testid="activity-date"></p-calendar>
      </div>
      <div class="sf-fld" *ngIf="form.activityType === 'TASK'">
        <label for="af-due">Due</label>
        <p-calendar inputId="af-due" [(ngModel)]="dueDate" [showTime]="true" hourFormat="24" dateFormat="dd M yy" [showIcon]="true"
                    appendTo="body" styleClass="w-full" data-testid="activity-due"></p-calendar>
      </div>
      <div class="sf-fld" *ngIf="hasDuration">
        <label for="af-duration">Duration (minutes)</label>
        <p-inputnumber inputId="af-duration" [(ngModel)]="form.durationMinutes" [min]="0" [max]="10080" [useGrouping]="false" styleClass="w-full"
                       data-testid="activity-duration"></p-inputnumber>
      </div>
      <div class="sf-fld">
        <label for="af-contact">Contact</label>
        <p-dropdown inputId="af-contact" [options]="contactOptions" [(ngModel)]="form.contactPersonId" optionLabel="label" optionValue="value"
                    [showClear]="true" [placeholder]="contactOptions.length ? 'Choose a contact' : 'No linked contacts yet'"
                    [disabled]="!contactOptions.length" appendTo="body" styleClass="w-full" data-testid="activity-contact"></p-dropdown>
      </div>
      <div class="sf-fld" *ngIf="canAssignOthers">
        <label for="af-by">Performed by</label>
        <p-dropdown inputId="af-by" [options]="userOptions" [(ngModel)]="form.performedByUserId" optionLabel="label" optionValue="value"
                    [filter]="true" filterBy="label" appendTo="body" styleClass="w-full"></p-dropdown>
      </div>
      <div class="sf-fld" *ngIf="form.activityType === 'TASK'">
        <label>&nbsp;</label>
        <div class="af-check"><p-checkbox [(ngModel)]="form.isCompleted" [binary]="true" inputId="af-done"></p-checkbox>
          <label for="af-done">Already done</label></div>
      </div>
    </div>

    <div class="sf-fld af-block">
      <label>Description / meeting notes</label>
      <app-crm-rich-text [value]="form.description ?? ''" (valueChange)="form.description = $event"></app-crm-rich-text>
    </div>

    <div class="sf-fields">
      <div class="sf-fld">
        <label for="af-outcome">Outcome</label>
        <p-dropdown inputId="af-outcome" [options]="outcomeOptions" [(ngModel)]="form.outcome" optionLabel="label" optionValue="value"
                    [showClear]="true" placeholder="—" appendTo="body" styleClass="w-full" data-testid="activity-outcome"></p-dropdown>
      </div>
      <div class="sf-fld span-2">
        <label for="af-outcome-notes">Outcome notes</label>
        <input pInputText id="af-outcome-notes" [(ngModel)]="form.outcomeNotes" maxlength="2000" />
      </div>
    </div>

    <!-- Participants -->
    <div class="af-section">
      <div class="af-section-head"><strong>Participants</strong>
        <span class="sf-end">
          <p-button size="small" [text]="true" icon="pi pi-user-plus" label="Colleague" (onClick)="addParticipant('INTERNAL_USER')" data-testid="add-internal"></p-button>
          <p-button size="small" [text]="true" icon="pi pi-id-card" label="Customer contact" [disabled]="!contactOptions.length"
                    (onClick)="addParticipant('CUSTOMER_CONTACT')"></p-button>
          <p-button size="small" [text]="true" icon="pi pi-user" label="Outsider" (onClick)="addParticipant('EXTERNAL')" data-testid="add-external"></p-button>
        </span>
      </div>
      <div class="af-row" *ngFor="let p of participants; let i = index" data-testid="participant">
        <ng-container [ngSwitch]="p.participantType">
          <p-dropdown *ngSwitchCase="'INTERNAL_USER'" [options]="userOptions" [(ngModel)]="p.userId" optionLabel="label" optionValue="value"
                      [filter]="true" filterBy="label" placeholder="Colleague" appendTo="body" styleClass="af-grow"></p-dropdown>
          <p-dropdown *ngSwitchCase="'CUSTOMER_CONTACT'" [options]="contactOptions" [(ngModel)]="p.contactPersonId" optionLabel="label"
                      optionValue="value" placeholder="Contact" appendTo="body" styleClass="af-grow"></p-dropdown>
          <ng-container *ngSwitchDefault>
            <input pInputText [(ngModel)]="p.externalName" placeholder="Name" maxlength="200" class="af-grow" />
            <input pInputText [(ngModel)]="p.externalEmail" placeholder="Email" maxlength="200" class="af-grow" />
          </ng-container>
        </ng-container>
        <p-dropdown [options]="roleOptions" [(ngModel)]="p.role" optionLabel="label" optionValue="value" [showClear]="true" placeholder="Role"
                    appendTo="body"></p-dropdown>
        <p-button icon="pi pi-times" [text]="true" severity="secondary" (onClick)="participants.splice(i, 1)" ariaLabel="Remove"></p-button>
      </div>
      <div class="sf-mu sf-sm" *ngIf="!participants.length">Nobody else.</div>
    </div>

    <!-- Documents -->
    <div class="af-section">
      <div class="af-section-head"><strong>Documents</strong>
        <span class="sf-end">
          <label class="af-file p-button p-button-text p-button-sm">
            <i class="pi pi-paperclip"></i>&nbsp;Attach files
            <input type="file" multiple (change)="pickFiles($event)" data-testid="activity-files" />
          </label>
        </span>
      </div>
      <div class="af-row" *ngFor="let f of files; let i = index" data-testid="pending-file">
        <span class="af-grow af-fname"><i class="pi pi-file"></i> {{ f.file.name }}</span>
        <p-dropdown [options]="documentTypeOptions" [(ngModel)]="f.documentType" optionLabel="label" optionValue="value" appendTo="body"></p-dropdown>
        <p-dropdown [options]="directionOptions" [(ngModel)]="f.direction" optionLabel="label" optionValue="value" appendTo="body"></p-dropdown>
        <p-button icon="pi pi-times" [text]="true" severity="secondary" (onClick)="files.splice(i, 1)" ariaLabel="Remove"></p-button>
      </div>
      <div class="af-row" *ngFor="let d of activity?.documents ?? []">
        <span class="af-grow af-fname"><i class="pi pi-check-circle"></i> {{ d.documentName }}</span>
        <span class="sf-mu">{{ label(d.documentType) }} · {{ label(d.direction) }}</span>
      </div>
      <div class="sf-mu sf-sm" *ngIf="!files.length && !(activity?.documents?.length)">PDFs, images, Office files — 20 MB each.</div>
    </div>

    <!-- Follow-up -->
    <div class="af-section">
      <div class="af-check"><p-checkbox [(ngModel)]="followUp" [binary]="true" inputId="af-fu" data-testid="follow-up-toggle"></p-checkbox>
        <label for="af-fu"><strong>Schedule a follow-up</strong></label></div>
      <div class="sf-fields" *ngIf="followUp">
        <div class="sf-fld">
          <label for="af-fu-date" class="sf-req">Date</label>
          <p-calendar inputId="af-fu-date" [(ngModel)]="followUpDate" dateFormat="dd M yy" [showIcon]="true" appendTo="body" styleClass="w-full"
                      data-testid="follow-up-date"></p-calendar>
        </div>
        <div class="sf-fld">
          <label for="af-fu-type">Type</label>
          <p-dropdown inputId="af-fu-type" [options]="typeOptions" [(ngModel)]="form.followUpType" optionLabel="label" optionValue="value"
                      [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown>
        </div>
        <div class="sf-fld">
          <label for="af-fu-notes">Notes</label>
          <input pInputText id="af-fu-notes" [(ngModel)]="form.followUpNotes" maxlength="500" />
        </div>
      </div>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button [label]="activity ? 'Save' : 'Log activity'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="activity-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .af-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .af-block { margin: .75rem 0; }
    .af-section { border-top: 1px solid var(--sms-border-soft); padding-top: .6rem; margin-top: .6rem; }
    .af-section-head { display: flex; align-items: center; margin-bottom: .35rem; }
    .af-row { display: flex; gap: .5rem; align-items: center; margin-bottom: .35rem; flex-wrap: wrap; }
    :host ::ng-deep .af-grow { flex: 1 1 12rem; min-width: 10rem; }
    .af-fname { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .af-check { display: flex; align-items: center; gap: .5rem; }
    .af-file { cursor: pointer; position: relative; overflow: hidden; }
    .af-file input { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class ActivityFormDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  @Input() leadUuid: string | null = null;
  @Input() opportunityUuid: string | null = null;
  /** The lead's / opportunity's account — its contacts fill the pickers. */
  @Input() accountUuid: string | null = null;
  /** The type the quick-add button chose (DOCUMENT = a DOCUMENT_SHARED activity with files). */
  @Input() initialType: string | null = null;
  /** Editing an existing activity (null = new). */
  @Input() activity: Activity | null = null;
  /** CRM_ASSIGN_LEAD — may log an activity for a colleague. */
  @Input() canAssignOthers = false;
  @Output() saved = new EventEmitter<Activity>();

  form: ActivityUpsert = {};
  participants: ActivityParticipant[] = [];
  files: PendingFile[] = [];
  activityDate: Date | null = null;
  dueDate: Date | null = null;
  followUp = false;
  followUpDate: Date | null = null;
  saving = false;
  error: string | null = null;

  typeOptions: Option[] = [];
  outcomeOptions: Option[] = [];
  roleOptions: Option[] = [];
  documentTypeOptions: Option[] = [];
  directionOptions: Option[] = [];
  userOptions: Option<number>[] = [];
  contactOptions: Option<number>[] = [];
  label = crmLabel;

  constructor(private crm: CrmService, private lookups: CrmLookupsService) {}

  get header(): string { return this.activity ? 'Edit activity' : 'Log activity'; }
  get hasDuration(): boolean { return ['PHONE_CALL', 'MEETING', 'SITE_VISIT', 'DEMO', 'PRESENTATION'].includes(this.form.activityType ?? ''); }

  ngOnChanges(changes: SimpleChanges): void {
    if ((changes['visible'] || changes['activity']) && this.visible) this.reset();
  }

  private reset(): void {
    this.error = null;
    this.files = [];
    const a = this.activity;
    const type = a?.activityType ?? (this.initialType === 'DOCUMENT' ? 'DOCUMENT_SHARED' : this.initialType) ?? 'PHONE_CALL';
    this.form = {
      activityType: type,
      subject: a?.subject ?? (type === 'DOCUMENT_SHARED' ? 'Documents shared' : ''),
      description: a?.description ?? '',
      outcome: a?.outcome ?? null, outcomeNotes: a?.outcomeNotes ?? null,
      durationMinutes: a?.durationMinutes ?? null, contactPersonId: a?.contactPersonId ?? null,
      performedByUserId: a?.performedByUserId ?? null, isCompleted: a?.isCompleted ?? false,
      followUpType: a?.followUpType ?? null, followUpNotes: a?.followUpNotes ?? null
    };
    this.participants = (a?.participants ?? []).map(p => ({ ...p }));
    this.activityDate = a ? new Date(a.activityDate) : new Date();
    this.dueDate = a?.dueDate ? new Date(a.dueDate) : null;
    this.followUp = !!a?.followUpDate;
    this.followUpDate = a?.followUpDate ? fromDateOnly(a.followUpDate) : null;

    this.lookups.meta().subscribe(m => {
      const types = (m?.activityTypes ?? []).filter(t => t !== 'STATUS_CHANGE');
      this.typeOptions = CrmLookupsService.options(types);
      this.outcomeOptions = CrmLookupsService.options(m?.activityOutcomes);
      this.roleOptions = CrmLookupsService.options(m?.participantRoles);
      this.documentTypeOptions = CrmLookupsService.options(m?.documentTypes);
      this.directionOptions = CrmLookupsService.options(m?.documentDirections);
    });
    this.lookups.users().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
    this.contactOptions = [];
    if (this.accountUuid)
      this.crm.getAccountContacts(this.accountUuid).subscribe({
        next: r => this.contactOptions = (r.result ?? []).map((c: CrmContact) => ({ value: c.id, label: c.title ? `${c.name} — ${c.title}` : c.name })),
        error: () => this.contactOptions = []
      });
  }

  addParticipant(type: ActivityParticipant['participantType']): void {
    this.participants.push({ participantType: type, role: type === 'INTERNAL_USER' && !this.participants.length ? 'ORGANIZER' : 'ATTENDEE' });
  }

  pickFiles(event: Event): void {
    const input = event.target as HTMLInputElement;
    for (const file of Array.from(input.files ?? []))
      this.files.push({ file, documentType: 'OTHER', direction: this.form.activityType === 'DOCUMENT_SHARED' ? 'SENT_TO_CUSTOMER' : 'INTERNAL', notes: '' });
    input.value = '';
  }

  close(visible: boolean): void {
    if (!visible) { this.visible = false; this.visibleChange.emit(false); }
  }

  save(): void {
    this.error = null;
    if (!this.form.subject?.trim()) { this.error = 'Subject is required.'; return; }
    if (this.followUp && !this.followUpDate) { this.error = 'Give the follow-up date.'; return; }
    if (this.form.activityType === 'DOCUMENT_SHARED' && !this.files.length && !this.activity?.documents?.length) {
      this.error = 'Attach the document that was shared.'; return;
    }
    const req: ActivityUpsert = {
      ...this.form,
      leadUuid: this.activity ? undefined : this.leadUuid,
      opportunityUuid: this.activity ? undefined : this.opportunityUuid,
      activityDate: this.form.activityType === 'TASK' ? (this.activity?.activityDate ?? new Date().toISOString()) : (this.activityDate ?? new Date()).toISOString(),
      dueDate: this.form.activityType === 'TASK' && this.dueDate ? this.dueDate.toISOString() : null,
      followUpDate: this.followUp && this.followUpDate ? toDateOnly(this.followUpDate) : null,
      followUpType: this.followUp ? this.form.followUpType : null,
      followUpNotes: this.followUp ? this.form.followUpNotes : null,
      participants: this.participants.filter(p => p.userId || p.contactPersonId || p.externalName?.trim())
    };
    this.saving = true;
    const call = this.activity ? this.crm.updateActivity(this.activity.uuid, req) : this.crm.createActivity(req);
    call.pipe(
      switchMap(res => {
        const saved = res.result;
        if (!this.files.length) return of(saved);
        return forkJoin(this.files.map(f => this.crm.attachDocument(saved.uuid, f.file, { documentType: f.documentType, direction: f.direction })))
          .pipe(map(() => saved), catchError(err => { this.error = this.message(err, 'The activity was saved but a document could not be attached.'); return of(saved); }));
      })
    ).subscribe({
      next: saved => {
        this.saving = false;
        this.saved.emit(saved);
        if (!this.error) this.close(false);
      },
      error: err => { this.saving = false; this.error = this.message(err, 'The activity could not be saved.'); }
    });
  }

  private message(err: unknown, fallback: string): string {
    return (err as { error?: { message?: string } })?.error?.message || fallback;
  }
}
