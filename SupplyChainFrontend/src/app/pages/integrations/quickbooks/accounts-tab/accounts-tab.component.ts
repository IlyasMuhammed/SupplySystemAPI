import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import {
  AccountMappingRowModel, AccountMappingsModel, AmbiguousAccountModel, QuickBooksIntegrationService, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { GL_SETUP_ROUTE } from '../../../../services/accounting.service';
import { Option } from '../quickbooks.shared';
import { RefLike, refOptions } from '../a44.shared';

interface AccountRow extends AccountMappingRowModel {
  /** What the admin chose on screen (saved on "Save"). */
  draft: string | null;
}

/**
 * A44 D-16 — the general ledger's postable accounts (A42 chart) next to the QuickBooks account each maps to. QuickBooks
 * derives its ledger from items, so these mappings are not needed to send documents; they are used where a setting
 * account is empty (income, expense, discount, deposit / bank accounts) and reported on Coverage.
 */
@Component({
  selector: 'app-qbo-accounts-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, IconFieldModule, InputIconModule, InputTextModule, SelectModule,
    TableModule, TagModule, ToggleSwitchModule, TooltipModule
  ],
  templateUrl: './accounts-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './accounts-tab.component.scss']
})
export class AccountsTabComponent implements OnInit {
  @Input() canManage = false;

  readonly glSetupRoute = GL_SETUP_ROUTE;

  isLoading = true;
  loadFailed = false;
  loadError = '';
  isImporting = false;
  isAutoMapping = false;
  isSaving = false;

  /** False when the general ledger is not set up or not licensed: nothing to map. */
  glAvailable = true;
  rows: AccountRow[] = [];
  filtered: AccountRow[] = [];
  qboAccounts: RefLike[] = [];
  /** The last auto-map's accounts with several equally good QuickBooks candidates. */
  ambiguous: AmbiguousAccountModel[] = [];
  qboOpts: Option[] = [];
  search = '';
  unmappedOnly = false;

  constructor(private service: QuickBooksIntegrationService, private messages: MessageService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    forkJoin({
      accounts: this.service.getAccountMappings(),
      reference: this.service.getReference().pipe(catchError(() => of(null)))
    }).subscribe({
      next: (r) => {
        this.isLoading = false;
        this.apply(r.accounts, r.reference?.accounts ?? []);
      },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'The account mappings could not be loaded.');
      }
    });
  }

  private apply(model: AccountMappingsModel, fallbackAccounts: RefLike[]): void {
    this.glAvailable = model.glAvailable;
    if (model.qboAccounts?.length) this.qboAccounts = model.qboAccounts;
    else if (fallbackAccounts.length) this.qboAccounts = fallbackAccounts;
    this.rows = (model.accounts ?? [])
      .map(r => ({ ...r, draft: r.qboAccountId || null }))
      .sort((a, b) => a.code.localeCompare(b.code));
    this.qboOpts = refOptions(this.qboAccounts, this.rows.map(r => r.qboAccountId));
    this.applyFilter();
  }

  // ── State ───────────────────────────────────────────────────────────────────

  get unmappedCount(): number { return this.rows.filter(r => !r.draft).length; }

  /** Mapped to a QuickBooks account that is no longer active, and not changed since. */
  isInvalid(r: AccountRow): boolean { return r.status === 'Invalid' && (r.draft || null) === (r.qboAccountId || null); }

  get invalidCount(): number { return this.rows.filter(r => this.isInvalid(r)).length; }

  ambiguousNames(a: AmbiguousAccountModel): string {
    return (a.candidates ?? []).map(c => c.acctNum ? `${c.acctNum} ${c.name}` : c.name).join(', ');
  }

  get changed(): AccountRow[] { return this.rows.filter(r => (r.draft || null) !== (r.qboAccountId || null)); }

  get dirty(): boolean { return this.changed.length > 0; }

  applyFilter(): void {
    const term = this.search.trim().toLowerCase();
    this.filtered = this.rows.filter(r =>
      (!this.unmappedOnly || !r.draft) &&
      (!term || r.code.toLowerCase().includes(term) || r.name.toLowerCase().includes(term)));
  }

  qboName(id: string | null | undefined): string {
    if (!id) return '';
    return this.qboAccounts.find(a => a.id === id)?.name ?? `QuickBooks id ${id}`;
  }

  // ── Actions ─────────────────────────────────────────────────────────────────

  /** Reads the QuickBooks chart of accounts again (the reference snapshot), then reloads. */
  importAccounts(): void {
    if (!this.canManage || this.isImporting) return;
    this.isImporting = true;
    this.service.refreshReference().subscribe({
      next: (ref) => {
        this.isImporting = false;
        this.qboAccounts = ref?.accounts ?? [];
        this.qboOpts = refOptions(this.qboAccounts, this.rows.map(r => r.qboAccountId));
        this.messages.add({ severity: 'success', summary: 'Imported', detail: `${this.qboAccounts.length} QuickBooks accounts were read.` });
      },
      error: (err) => {
        this.isImporting = false;
        this.messages.add({ severity: 'error', summary: 'Not imported', detail: qboErrorMessage(err, 'The QuickBooks accounts could not be read.') });
      }
    });
  }

  /** Maps by account number, then exact name, then normalised name (server side); unsaved choices here are lost. */
  autoMap(): void {
    if (!this.canManage || this.isAutoMapping) return;
    this.isAutoMapping = true;
    this.service.autoMapAccounts({ overwrite: false }).subscribe({
      next: (res) => {
        this.isAutoMapping = false;
        const n = res?.mapped ?? 0;
        this.ambiguous = res?.ambiguous ?? [];
        const left = this.ambiguous.length;
        this.messages.add({
          severity: n ? 'success' : 'info', summary: n ? 'Mapped' : 'Nothing mapped',
          detail: (n ? `${n} ${n === 1 ? 'account was' : 'accounts were'} mapped by number or name. Check them.` : 'No unmapped account had a unique match in QuickBooks.')
            + (left ? ` ${left} matched more than one QuickBooks account; choose those yourself.` : '')
        });
        if (res?.accounts) this.apply(res.accounts, this.qboAccounts);
        else this.load();
      },
      error: (err) => {
        this.isAutoMapping = false;
        this.messages.add({ severity: 'error', summary: 'Not mapped', detail: qboErrorMessage(err, 'The accounts could not be mapped.') });
      }
    });
  }

  save(): void {
    if (!this.canManage || this.isSaving || !this.dirty) return;
    this.isSaving = true;
    const mappings = this.changed.map(r => ({ accountUuid: r.accountUuid, qboAccountId: r.draft || null }));
    this.service.saveAccountMappings({ mappings }).subscribe({
      next: (model) => {
        this.isSaving = false;
        if (model?.accounts?.length) this.apply(model, this.qboAccounts);
        else this.rows.forEach(r => (r.qboAccountId = r.draft));
        this.applyFilter();
        this.messages.add({ severity: 'success', summary: 'Saved', detail: `${mappings.length} account ${mappings.length === 1 ? 'mapping' : 'mappings'} saved.` });
      },
      error: (err) => {
        this.isSaving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The account mappings could not be saved.') });
      }
    });
  }

  discard(): void {
    this.rows.forEach(r => (r.draft = r.qboAccountId || null));
    this.applyFilter();
  }
}
