import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import {
  DocumentEmailResult, EmailableDocument, OnboardingTemplatesService
} from '../../../services/onboarding-templates.service';

/** Loose: one address, or several split by , or ; — each "x@y.z". */
const EMAIL = /^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/;
export function validEmailList(value: string | null | undefined, required: boolean): boolean {
  const parts = (value ?? '').split(/[;,]/).map(p => p.trim()).filter(Boolean);
  if (!parts.length) return !required;
  return parts.every(p => EMAIL.test(p));
}

export function describeFailures(failed: { email: string; error?: string | null }[] | null | undefined): string {
  return (failed ?? []).map(f => f.error ? `${f.email}: ${f.error}` : f.email).join('; ');
}

/** The toast after a send: who got it, and who did not. */
export function emailSentToast(r: DocumentEmailResult | null): { severity: 'success' | 'warn'; summary: string; detail: string } {
  const to = [...(r?.sentTo ?? []), ...(r?.cc ?? [])];
  const failed = r?.failed ?? [];
  if (failed.length) {
    return { severity: 'warn', summary: 'Partly sent',
      detail: `Sent to ${to.join(', ') || 'some recipients'}. Not sent to ${describeFailures(failed)}.` };
  }
  return { severity: 'success', summary: 'Email sent', detail: to.length ? `Sent to ${to.join(', ')}.` : 'The email has been sent.' };
}

/**
 * A50 D-26: "Email to customer" (sales invoice, quotation) / "Email to supplier" (purchase order). To / Cc / a short
 * message, prefilled from the document; the server renders the org's email template and attaches the PDF.
 * To may be left empty: the server then uses the party's email on file.
 */
@Component({
  selector: 'app-document-email-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, DialogModule, ButtonModule, InputTextModule, TextareaModule],
  template: `
    <p-dialog [visible]="visible" (visibleChange)="onVisibleChange($event)" [modal]="true" [draggable]="false"
              [header]="header" styleClass="sdlg" [style]="{ width: '520px' }"
              [breakpoints]="{ '640px': '94vw' }" data-testid="email-dialog">
      <div class="ede-body">
        <p class="ede-hint">
          The {{ documentLabel || 'document' }} PDF is attached automatically, and the email uses your
          organization's email template.
        </p>
        <div class="ede-fld">
          <label for="ede-to">To</label>
          <input pInputText id="ede-to" type="text" [(ngModel)]="to" maxlength="500" class="w-full"
                 placeholder="Defaults to the {{ partyLabel }}'s email" data-testid="email-to" />
          <small class="ede-err" *ngIf="!toValid" data-testid="email-to-error">Enter a valid email address.</small>
          <small class="ede-mu" *ngIf="toValid && !to.trim()" data-testid="email-to-hint">Left empty, it defaults to the {{ partyLabel }}'s email on file. Separate several addresses with commas.</small>
        </div>
        <div class="ede-fld">
          <label for="ede-cc">Cc <span class="ede-mu">(optional)</span></label>
          <input pInputText id="ede-cc" type="text" [(ngModel)]="cc" maxlength="500" class="w-full"
                 placeholder="Separate several addresses with commas" data-testid="email-cc" />
          <small class="ede-err" *ngIf="!ccValid" data-testid="email-cc-error">Enter valid email addresses.</small>
        </div>
        <div class="ede-fld">
          <label for="ede-msg">Message <span class="ede-mu">(optional)</span></label>
          <textarea pTextarea id="ede-msg" rows="4" [(ngModel)]="message" maxlength="2000" class="w-full"
                    placeholder="Added to the email above the template text" data-testid="email-message"></textarea>
        </div>
      </div>
      <ng-template pTemplate="footer">
        <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="onVisibleChange(false)"></p-button>
        <p-button label="Send email" icon="pi pi-send" [loading]="sending" [disabled]="!canSend"
                  (onClick)="send()" data-testid="email-send"></p-button>
      </ng-template>
    </p-dialog>
  `,
  styles: [`
    .ede-body { display: flex; flex-direction: column; gap: 1rem; }
    .ede-hint { margin: 0; font-size: .82rem; color: var(--sms-text-muted); line-height: 1.5; }
    .ede-fld { display: flex; flex-direction: column; gap: .3rem; }
    .ede-fld label { font-size: .86rem; color: var(--sms-text-muted); }
    .ede-mu { color: var(--sms-text-faint); font-size: .76rem; }
    .ede-err { color: var(--sms-danger); font-size: .76rem; }
  `]
})
export class DocumentEmailDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();

  @Input({ required: true }) kind!: EmailableDocument;
  @Input() uuid: string | null | undefined = null;
  /** "invoice SI-0001" — used in the header and hint. */
  @Input() documentLabel = '';
  /** customer | supplier */
  @Input() partyLabel = 'customer';
  /** The party's email the document knows, if any. */
  @Input() defaultTo: string | null | undefined = null;

  /** Sent: the toast is the host's (it owns the MessageService). */
  @Output() sent = new EventEmitter<DocumentEmailResult | null>();
  @Output() failed = new EventEmitter<string>();

  to = '';
  cc = '';
  message = '';
  sending = false;

  constructor(private api: OnboardingTemplatesService) {}

  ngOnChanges(changes: SimpleChanges): void {
    // Every time the dialog opens, start from the document's own values.
    if (changes['visible'] && this.visible) this.reset();
  }

  get header(): string {
    return `Email to ${this.partyLabel}`;
  }

  get toValid(): boolean { return validEmailList(this.to, false); }
  get ccValid(): boolean { return validEmailList(this.cc, false); }
  get canSend(): boolean { return !!this.uuid && this.toValid && this.ccValid && !this.sending; }

  reset(): void {
    this.to = this.defaultTo ?? '';
    this.cc = '';
    this.message = '';
    this.sending = false;
  }

  onVisibleChange(v: boolean): void {
    if (this.sending && !v) return;
    this.visible = v;
    this.visibleChange.emit(v);
  }

  send(): void {
    if (!this.canSend || !this.uuid) return;
    this.sending = true;
    this.api.emailDocument(this.kind, this.uuid, { to: this.to, cc: this.cc, message: this.message }).subscribe({
      next: res => {
        this.sending = false;
        if (res && res.success === false) {
          this.failed.emit(res.message || 'The email could not be sent.');
          return;
        }
        const r = res?.result ?? null;
        // A send failure is still 200: `failed` lists who did not get it. Nobody reached = a failure; some = partial.
        const failedList = r?.failed ?? [];
        if (r && r.sent === false && !(r.sentTo ?? []).length) {
          this.failed.emit(describeFailures(failedList) || 'The email could not be sent.');
          return;
        }
        this.sent.emit(r);
        this.onVisibleChange(false);
      },
      error: err => {
        this.sending = false;
        this.failed.emit(err?.error?.message || 'The email could not be sent.');
      }
    });
  }
}
