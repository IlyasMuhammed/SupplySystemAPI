import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  AR_AP_PERMS, AP_ROUTE, ArApService, PartnerBalance, StatementQuery, StatementResult, StatementType, VENDOR_STATEMENT_ROUTE,
  VendorClaimedItem, VendorReconciliationResult
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { saveDownload } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { STATEMENT_TYPES, statementPeriodProblem } from '../customer-statement/customer-statement.component';
import { StatementViewComponent } from '../statement-view/statement-view.component';
import { addDays, arApError, arApErrorAsync, labelOf, money, signedMoney, today } from '../ar-ap.shared';

export function reconciliationProblem(asOf: string, claimed: number | null | undefined, items: VendorClaimedItem[]): string | null {
  if (!asOf) return 'Choose the date of the vendor\'s statement.';
  if (claimed === null || claimed === undefined || !isFinite(claimed)) return 'Enter the balance the vendor claims.';
  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    if (!(it.reference ?? '').trim()) return `Line ${i + 1}: enter the vendor's reference.`;
    if (!it.date) return `Line ${i + 1}: enter the date.`;
    if (it.amount === null || it.amount === undefined || !isFinite(it.amount)) return `Line ${i + 1}: enter the amount.`;
  }
  return null;
}

/**
 * A48 C13 — `/finance/ap/vendor-statement/:uuid`: the vendor's balance, its statement (Activity / Open items, per
 * currency, PDF), and the reconciliation against the vendor's own statement — their claimed balance and, optionally,
 * their list of documents — showing what is in SMS but not with the vendor, the other way round, and amount differences.
 * Reconciling needs AP_EXECUTE.
 */
@Component({
  selector: 'app-vendor-statement',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, InputNumberModule, InputTextModule, SelectModule, ToastModule,
    StatementViewComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Payables', 'Vendor statement', balance?.partnerName]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Generate" icon="pi pi-play" [disabled]="!uuid || generating" [loading]="generating" (onClick)="generate()" data-testid="generate"></p-button>
    <p-button label="Download PDF" icon="pi pi-file-pdf" [text]="true" [disabled]="!uuid || downloading" [loading]="downloading" (onClick)="downloadPdf()" data-testid="pdf"></p-button>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="apRoute"><i class="pi pi-arrow-left"></i>&nbsp;AP dashboard</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Vendor statement<ng-container *ngIf="balance"> — {{ balance.partnerName }}</ng-container></h1>
      <div class="sf-sub">What SMS says is owed to the vendor, and how it compares with the vendor's own statement</div></div></div>

    <section class="sf-card">
      <div class="sf-fields">
        <div class="sf-fld"><label for="vs-vendor" class="sf-req">Vendor</label>
          <p-autoComplete inputId="vs-vendor" [(ngModel)]="vendor" [suggestions]="suggestions" optionLabel="companyName" (completeMethod)="search($event)"
                          (onSelect)="choose($event.value)" [minLength]="1" appendTo="body" placeholder="Search vendors…" data-testid="vendor"></p-autoComplete></div>
        <div class="sf-fld"><label for="vs-type">Type</label>
          <p-select inputId="vs-type" [options]="types" [(ngModel)]="type" optionLabel="label" optionValue="value" appendTo="body" data-testid="type"></p-select></div>
        <div class="sf-fld"><label for="vs-from">From</label><input pInputText id="vs-from" type="date" [(ngModel)]="from" data-testid="from" /></div>
        <div class="sf-fld"><label for="vs-to">To</label><input pInputText id="vs-to" type="date" [(ngModel)]="to" data-testid="to" /></div>
      </div>
    </section>
    <div class="gl-note wn" *ngIf="formError" data-testid="form-error"><i class="pi pi-info-circle"></i><div>{{ formError }}</div></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <div class="sf-grid sf-tiles" *ngIf="balance" data-testid="balance">
      <div class="sf-tile"><span class="l">Open</span><span class="v">{{ amount(balance.totalOpen) }}</span><span class="s">{{ balance.openItemCount }} documents</span></div>
      <div class="sf-tile"><span class="l">Credits</span><span class="v">{{ amount(balance.totalCredits) }}</span></div>
      <div class="sf-tile"><span class="l">Net balance</span><span class="v" data-testid="net">{{ amount(balance.netBalance) }}</span><span class="s">{{ balance.baseCurrencyCode || '' }}</span></div>
      <div class="sf-tile"><span class="l">Oldest</span><span class="v">{{ balance.daysOldest ?? '—' }}</span><span class="s">days</span></div>
    </div>

    <app-statement-view *ngIf="statement" [statement]="statement"></app-statement-view>

    <!-- C13 — reconciliation -->
    <section class="sf-card" *ngIf="uuid && canReconcile" data-testid="reconcile">
      <div class="sf-ch"><h2>Reconcile with the vendor's statement</h2></div>
      <div class="sf-fields">
        <div class="sf-fld"><label for="vs-asof" class="sf-req">Their statement date</label><input pInputText id="vs-asof" type="date" [(ngModel)]="asOf" data-testid="rec-asof" /></div>
        <div class="sf-fld"><label for="vs-claim" class="sf-req">Balance they claim</label>
          <p-inputNumber inputId="vs-claim" [(ngModel)]="claimed" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" data-testid="rec-claimed"></p-inputNumber></div>
      </div>
      <div class="sf-ch" style="margin-top:.75rem"><h2>Their documents (optional)</h2>
        <span class="sf-end"><p-button label="Add line" icon="pi pi-plus" [text]="true" size="small" (onClick)="addItem()" data-testid="add-item"></p-button></span></div>
      <div class="gl-scroll" *ngIf="items.length">
        <table class="aa-table" style="min-width:34rem"><thead><tr><th>Reference</th><th>Date</th><th class="gl-num">Amount</th><th></th></tr></thead><tbody>
          <tr *ngFor="let it of items; let i = index" [attr.data-testid]="'claim-' + i">
            <td><input pInputText [(ngModel)]="it.reference" maxlength="100" [attr.aria-label]="'Reference ' + (i + 1)" /></td>
            <td><input pInputText type="date" [(ngModel)]="it.date" [attr.aria-label]="'Date ' + (i + 1)" /></td>
            <td class="gl-num"><p-inputNumber [(ngModel)]="it.amount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [ariaLabel]="'Amount ' + (i + 1)"></p-inputNumber></td>
            <td><p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" (onClick)="removeItem(i)" ariaLabel="Remove line"></p-button></td>
          </tr></tbody></table>
      </div>
      <div class="gl-note wn" *ngIf="recError" style="margin-top:.75rem" data-testid="rec-error"><i class="pi pi-info-circle"></i><div>{{ recError }}</div></div>
      <div class="aa-actions" style="margin-top:.75rem"><p-button label="Reconcile" icon="pi pi-link" [disabled]="reconciling" [loading]="reconciling" (onClick)="reconcile()" data-testid="run-reconcile"></p-button></div>

      <ng-container *ngIf="result as r">
        <div class="gl-note" [ngClass]="r.isReconciled ? 'ok' : 'wn'" style="margin-top:.75rem" data-testid="rec-result"><i class="pi" [ngClass]="r.isReconciled ? 'pi-check' : 'pi-exclamation-triangle'"></i>
          <div>SMS {{ amount(r.smsBalance) }} · vendor {{ amount(r.vendorClaimedBalance) }} {{ r.currencyCode }} · difference {{ signed(r.difference) }} · {{ r.matchedCount }} matched{{ r.isReconciled ? ' — reconciled.' : '' }}</div></div>
        <div class="gl-scroll" *ngIf="r.differences.length">
          <table class="aa-table" style="min-width:44rem;margin-top:.5rem" data-testid="differences">
            <thead><tr><th>Difference</th><th>Document</th><th>Reference</th><th>Date</th><th class="gl-num">SMS</th><th class="gl-num">Vendor</th><th class="gl-num">Difference</th></tr></thead>
            <tbody><tr *ngFor="let d of r.differences">
              <td>{{ diffLabel(d.type) }}</td><td class="sf-mono">{{ d.entryNumber || '' }}</td><td>{{ d.reference || '' }}</td>
              <td>{{ d.date ? (d.date | date:'dd MMM yyyy') : '' }}</td>
              <td class="gl-num">{{ d.smsAmount !== null && d.smsAmount !== undefined ? amount(d.smsAmount) : '' }}</td>
              <td class="gl-num">{{ d.vendorAmount !== null && d.vendorAmount !== undefined ? amount(d.vendorAmount) : '' }}</td>
              <td class="gl-num">{{ d.difference !== null && d.difference !== undefined ? signed(d.difference) : '' }}</td></tr></tbody>
          </table>
        </div>
      </ng-container>
    </section>
  </div>
</sf-page>`
})
export class VendorStatementComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly apRoute = AP_ROUTE;
  readonly types = STATEMENT_TYPES;

  uuid = '';
  vendor: BusinessPartnerModel | { uuid: string; companyName: string } | string | null = null;
  suggestions: BusinessPartnerModel[] = [];
  type: StatementType = 'OpenItems';
  from = addDays(today(), -90);
  to = today();
  formError = '';
  loadError = '';
  balance: PartnerBalance | null = null;
  statement: StatementResult | null = null;
  generating = false;
  downloading = false;

  asOf = today();
  claimed: number | null = null;
  items: VendorClaimedItem[] = [];
  recError = '';
  reconciling = false;
  result: VendorReconciliationResult | null = null;

  get canReconcile(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AP_EXECUTE); }

  ngOnInit(): void {
    this.route?.paramMap?.subscribe(pm => {
      this.uuid = pm.get('uuid') ?? '';
      this.statement = null;
      this.result = null;
      if (this.uuid) {
        this.loadBalance();
        this.generate();
      }
    });
  }

  search(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isVendor: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.suggestions = r?.result?.data ?? []);
  }

  choose(p: BusinessPartnerModel): void { if (p?.uuid) this.router.navigate([VENDOR_STATEMENT_ROUTE, p.uuid]); }

  loadBalance(): void {
    this.api.getVendorBalance(this.uuid).subscribe({
      next: b => { this.balance = b; if (!this.vendor && b) this.vendor = { uuid: b.partnerId, companyName: b.partnerName ?? '' }; },
      error: err => { this.balance = null; this.loadError = arApError(err, 'The vendor balance could not be loaded.'); }
    });
  }

  private query(): StatementQuery | null {
    this.formError = (!this.uuid ? 'Choose the vendor.' : statementPeriodProblem(this.from, this.to)) ?? '';
    return this.formError ? null : { fromDate: this.from, toDate: this.to, type: this.type };
  }

  generate(): void {
    const q = this.query();
    if (!q) return;
    this.generating = true;
    this.loadError = '';
    this.api.getVendorStatement(this.uuid, q).subscribe({
      next: s => { this.generating = false; this.statement = s; },
      error: err => { this.generating = false; this.statement = null; this.loadError = arApError(err, 'The statement could not be generated.'); }
    });
  }

  downloadPdf(): void {
    const q = this.query();
    if (!q) return;
    this.downloading = true;
    this.api.downloadVendorStatementPdf(this.uuid, q).subscribe({
      next: res => { this.downloading = false; saveDownload(res, `vendor-statement-${this.to}.pdf`); },
      error: async err => { this.downloading = false; this.messages.add({ severity: 'error', summary: 'Error', detail: await arApErrorAsync(err, 'The PDF could not be made.'), life: 8000 }); }
    });
  }

  addItem(): void { this.items = [...this.items, { reference: '', date: this.asOf, amount: 0 }]; }
  removeItem(i: number): void { this.items = this.items.filter((_, j) => j !== i); }

  reconcile(): void {
    this.recError = reconciliationProblem(this.asOf, this.claimed, this.items) ?? '';
    if (this.recError) return;
    this.reconciling = true;
    this.api.reconcileVendor(this.uuid, {
      asOfDate: this.asOf, vendorClaimedBalance: this.claimed!, vendorItems: this.items.length ? this.items.map(i => ({ ...i, reference: i.reference.trim() })) : null
    }).subscribe({
      next: r => { this.reconciling = false; this.result = r; },
      error: err => { this.reconciling = false; this.recError = arApError(err, 'The reconciliation could not be run.'); }
    });
  }

  diffLabel(t: string): string {
    switch (t) {
      case 'InSMSNotVendor': return 'In SMS, not on their statement';
      case 'InVendorNotSMS': return 'On their statement, not in SMS';
      case 'AmountDifference': return 'Amounts differ';
      default: return labelOf(t);
    }
  }

  amount(n: number | null | undefined): string { return money(n); }
  signed(n: number | null | undefined): string { return signedMoney(n); }
}
