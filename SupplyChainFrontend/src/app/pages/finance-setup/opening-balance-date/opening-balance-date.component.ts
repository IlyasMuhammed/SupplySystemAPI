import { Component, EventEmitter, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import { ImportService } from '../../../services/import.service';
import { AuthService } from '../../service/auth.service';
import { FINANCE_SETUP_MANAGE, fromIsoDate, setupErrorMessage, toIsoDate } from '../finance-setup.shared';

/**
 * A40 D-7 — the organization's opening balance date (GET/PUT api/finance/opening-balance-date), a card on Settings › Finance
 * Setup › Currency configuration. Opening stock and customer / supplier opening balances are imported as at this date and
 * are refused until it is set. FINANCE_SETUP_MANAGE changes it; anyone who opens the page sees it.
 * Sub-component (no sf-page); toasts go to the host page's MessageService.
 */
@Component({
  selector: 'app-opening-balance-date',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, DatePickerModule],
  template: `
<section class="sf-card" id="opening-balance-date" data-testid="opening-date-card">
  <div class="sf-ch">
    <div>
      <h2>Opening balance date</h2>
      <div class="sf-mu sf-sm">
        The day your balances are taken over from the previous system. Imported opening stock and open customer / supplier
        balances are dated on it, and they cannot be imported until it is set (Settings › Data import).
      </div>
    </div>
  </div>
  <div class="sf-mu sf-sm" *ngIf="loadFailed" data-testid="opening-date-failed">The opening balance date could not be loaded.</div>
  <div class="sf-fields" *ngIf="!loadFailed">
    <div class="sf-fld">
      <label for="openingDate">Opening balance date</label>
      <p-datepicker inputId="openingDate" [(ngModel)]="date" dateFormat="dd M yy" [showIcon]="true" [disabled]="!canManage || saving"
                    appendTo="body" data-testid="opening-date-input"></p-datepicker>
      <small class="sf-mu" *ngIf="!saved" data-testid="opening-date-unset">Not set — opening balance imports are refused until it is.</small>
    </div>
    <div class="sf-fld" *ngIf="canManage" style="align-self:end">
      <p-button label="Save date" icon="pi pi-check" [disabled]="!dirty || saving" [loading]="saving" (onClick)="save()"
                data-testid="opening-date-save"></p-button>
      <p-button *ngIf="saved" label="Clear" [text]="true" severity="danger" [disabled]="saving" (onClick)="clear()"
                data-testid="opening-date-clear"></p-button>
    </div>
  </div>
</section>
  `
})
export class OpeningBalanceDateComponent implements OnInit {
  private readonly api = inject(ImportService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService, { optional: true });

  /** A42 — the date was saved or cleared (the GL opening balances page re-reads its conversion date). */
  @Output() changed = new EventEmitter<string | null>();

  date: Date | null = null;
  saved: string | null = null;
  loadFailed = false;
  saving = false;
  canManage = false;

  ngOnInit(): void {
    // A42 — GL setup managers may set it too (the API's PUT accepts either code).
    this.canManage = this.auth.hasPermission(FINANCE_SETUP_MANAGE) || this.auth.hasPermission('GL_SETUP_MANAGE');
    this.api.getOpeningBalanceDate().subscribe({
      next: res => { this.apply(res?.result?.date ?? null); },
      error: () => { this.loadFailed = true; }
    });
  }

  private apply(iso: string | null): void {
    this.saved = iso ? iso.substring(0, 10) : null;
    this.date = fromIsoDate(this.saved);
  }

  get dirty(): boolean {
    return !!this.date && toIsoDate(this.date) !== this.saved;
  }

  save(): void {
    if (!this.canManage || !this.date || !this.dirty || this.saving) return;
    this.put(toIsoDate(this.date), 'The opening balance date is saved.');
  }

  /** Removes the date (opening imports are then refused again). */
  clear(): void {
    if (!this.canManage || !this.saved || this.saving) return;
    this.put(null, 'The opening balance date is cleared.');
  }

  /**
   * The server refuses (409) clearing the date or moving it before existing opening documents — its sentence is shown as
   * it is, and the field goes back to the saved date.
   */
  private put(iso: string | null, done: string): void {
    this.saving = true;
    this.api.setOpeningBalanceDate(iso).subscribe({
      next: res => {
        this.saving = false;
        this.apply(res?.result ? res.result.date ?? null : iso);
        this.changed.emit(this.saved);
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: done, life: 4000 });
      },
      error: err => {
        this.saving = false;
        this.date = fromIsoDate(this.saved);
        this.messages?.add({ severity: 'error', summary: 'Not saved', detail: setupErrorMessage(err, 'The opening balance date could not be saved.'), life: 8000 });
      }
    });
  }
}
