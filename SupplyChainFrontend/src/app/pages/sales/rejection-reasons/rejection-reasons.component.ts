import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import { SalesPreorderService, RejectionReason } from '../../../services/sales-preorder.service';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';

export interface RejectionReasonDraft {
  code: string;
  description: string;
  displayOrder: number | null;
}

const CODE_PATTERN = /^[A-Z0-9_]{1,10}$/;
const MAX_DESCRIPTION = 200;

/**
 * A32 C5 — Sales → Rejection Reasons (SALE_REJECTION_REASON_MANAGE). Why inquiry and quotation lines are turned
 * down: code, description, active, display order. Seeded codes (isSystem) can be renamed and deactivated but never
 * deleted; a custom reason can be deleted only while unused (the API answers 409 otherwise). Deactivated reasons
 * stay on the lines that already carry them.
 */
@Component({
  selector: 'app-rejection-reasons',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, InputNumberModule,
    InputTextModule, TableModule, TagModule, ToastModule, TooltipModule, ...FLOW
  ],
  templateUrl: './rejection-reasons.component.html',
  styleUrls: ['./rejection-reasons.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class RejectionReasonsComponent implements OnInit {
  readonly maxDescription = MAX_DESCRIPTION;

  reasons: RejectionReason[] = [];
  isLoading = false;
  loadFailed = false;
  showInactive = true;
  busy: Record<string, boolean> = {};

  dialogVisible = false;
  editing: RejectionReason | null = null;
  draft: RejectionReasonDraft = { code: '', description: '', displayOrder: null };
  submitted = false;
  isSaving = false;
  saveError = '';

  constructor(
    private service: SalesPreorderService,
    private authService: AuthService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit() { this.load(); }

  get canManage(): boolean { return this.authService.hasPermission('SALE_REJECTION_REASON_MANAGE'); }

  get visibleReasons(): RejectionReason[] {
    return this.showInactive ? this.reasons : this.reasons.filter(r => r.isActive);
  }

  get inactiveCount(): number { return this.reasons.filter(r => !r.isActive).length; }

  load() {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getRejectionReasons(true).subscribe({
      next: (res) => { this.isLoading = false; this.reasons = res.result ?? []; },
      error: () => { this.isLoading = false; this.loadFailed = true; this.reasons = []; }
    });
  }

  // ── Add / edit ──────────────────────────────────────────────────────────────

  openCreate() {
    if (!this.canManage) return;
    this.editing = null;
    this.draft = { code: '', description: '', displayOrder: null };
    this.openDialog();
  }

  openEdit(reason: RejectionReason) {
    if (!this.canManage) return;
    this.editing = reason;
    this.draft = { code: reason.code, description: reason.description, displayOrder: reason.displayOrder };
    this.openDialog();
  }

  private openDialog() {
    this.submitted = false;
    this.saveError = '';
    this.isSaving = false;
    this.dialogVisible = true;
  }

  /** The first thing stopping a save, in words; null when it can be saved. Mirrors the API's rules. */
  get problem(): string | null {
    const d = this.draft;
    if (!this.editing && !CODE_PATTERN.test(d.code.trim().toUpperCase())) {
      return 'Code: 1–10 letters, digits or underscores.';
    }
    const description = d.description.trim();
    if (!description) return 'Description is required.';
    if (description.length > MAX_DESCRIPTION) return `Description: at most ${MAX_DESCRIPTION} characters.`;
    if (d.displayOrder != null && (!Number.isInteger(d.displayOrder) || d.displayOrder < 0)) return 'Order is a whole number, 0 or more.';
    if (this.editing && d.displayOrder == null) return 'Order is required.';
    return null;
  }

  save() {
    this.submitted = true;
    if (this.problem || this.isSaving || !this.canManage) return;
    this.isSaving = true;
    this.saveError = '';
    const d = this.draft;

    const call = this.editing
      ? this.service.updateRejectionReason(this.editing.uuid, { description: d.description.trim(), displayOrder: d.displayOrder! })
      : this.service.createRejectionReason({ code: d.code.trim().toUpperCase(), description: d.description.trim(), displayOrder: d.displayOrder });

    call.subscribe({
      next: () => {
        this.isSaving = false;
        this.dialogVisible = false;
        this.messages.add({ severity: 'success', summary: this.editing ? 'Reason saved' : 'Reason added' });
        this.load();
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = err?.error?.message || 'The reason could not be saved.';
      }
    });
  }

  // ── Activate / deactivate / delete ──────────────────────────────────────────

  confirmDeactivate(reason: RejectionReason) {
    this.confirmation.confirm({
      key: 'rejection-reasons',
      header: `Deactivate ${reason.code}?`,
      message: 'It can no longer be chosen for a line. Lines that already carry it keep it.',
      icon: 'pi pi-ban',
      acceptLabel: 'Deactivate', rejectLabel: 'Keep active',
      accept: () => this.deactivate(reason)
    });
  }

  deactivate(reason: RejectionReason) {
    this.run(reason, this.service.deactivateRejectionReason(reason.uuid), `${reason.code} deactivated`);
  }

  reactivate(reason: RejectionReason) {
    this.run(reason, this.service.activateRejectionReason(reason.uuid), `${reason.code} reactivated`);
  }

  confirmDelete(reason: RejectionReason) {
    if (reason.isSystem) return;
    this.confirmation.confirm({
      key: 'rejection-reasons',
      header: `Delete ${reason.code}?`,
      message: 'Only a reason no line has used can be deleted; otherwise deactivate it.',
      icon: 'pi pi-trash',
      acceptLabel: 'Delete', rejectLabel: 'Keep',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.delete(reason)
    });
  }

  delete(reason: RejectionReason) {
    // Seeded codes are kept for analytics: deactivate them instead (the API refuses too).
    if (reason.isSystem) return;
    this.run(reason, this.service.deleteRejectionReason(reason.uuid), `${reason.code} deleted`);
  }

  private run(reason: RejectionReason, call: Observable<unknown>, done: string) {
    if (!this.canManage || this.busy[reason.uuid]) return;
    this.busy[reason.uuid] = true;
    call.subscribe({
      next: () => {
        this.busy[reason.uuid] = false;
        this.messages.add({ severity: 'success', summary: done });
        this.load();
      },
      error: (err) => {
        this.busy[reason.uuid] = false;
        this.messages.add({ severity: 'error', summary: 'Not changed', detail: err?.error?.message || 'The reason could not be changed.' });
      }
    });
  }
}
