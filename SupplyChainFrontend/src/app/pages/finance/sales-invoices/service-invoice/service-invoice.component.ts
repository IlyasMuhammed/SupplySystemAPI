import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import { FLOW } from '../../../../shared/flow';
import { HrmApi, rowsOf } from '../../../hrm/core/hrm.api';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem } from '../../../hrm/core/hrm-form.component';
import { Opt, hrmError, money, today } from '../../../hrm/core/hrm.shared';

const FIELDS: Field[] = [
  { key: 'invoiceDate', label: 'Invoice date', type: 'date', required: true, def: today() },
  { key: 'dueDate', label: 'Due date', type: 'date', check: (d, v) => d && v['invoiceDate'] && d < v['invoiceDate'] ? 'The due date cannot be before the invoice date.' : null },
  { key: 'currencyCode', label: 'Currency', maxLength: 3, hint: 'Empty uses the organization currency.' },
  { key: 'lines', label: 'Lines', type: 'rows', required: true, def: [{ description: null, quantity: 1, unitPrice: null, taxCode: null }], rows: [
    { key: 'description', label: 'Description', required: true, maxLength: 300 }, { key: 'quantity', label: 'Qty', type: 'number', required: true, min: 0.0001, def: 1 },
    { key: 'unitPrice', label: 'Unit price', type: 'number', required: true, min: 0 }, { key: 'taxCode', label: 'Tax code', maxLength: 20 }
  ] }
];

/**
 * A49 E-13 (A-14, contract §10.9) — an invoice with no sale order and no delivery, for an organization that has no sales module (P2-S).
 * The server creates the normal invoice and posts it the usual way; this page then opens it.
 */
@Component({
  selector: 'app-service-invoice',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, SelectModule, HrmFormComponent, ...FLOW],
  styleUrls: ['../../gl/gl.scss', '../../../hrm/core/hrm.scss'],
  template: `
<sf-page [crumbs]="['Finance', 'Sales invoices', 'New service invoice']">
  <header class="sf-hdr sf-compact"><h1>New service invoice</h1></header>
  <div class="sf-body">
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <section class="sf-card">
      <div class="sf-fld" style="max-width:28rem;margin-bottom:0.85rem"><label for="si-customer" class="sf-req">Customer</label>
        <p-select inputId="si-customer" [options]="customers" optionLabel="label" optionValue="value" [(ngModel)]="customerUuid" [filter]="true" placeholder="Select" appendTo="body" data-testid="customer" [style]="{ width: '100%' }"></p-select></div>
      <hrm-form [fields]="fields" [values]="values" idPrefix="si"></hrm-form>
    </section>
  </div>
  <div class="sf-fbar">Total before tax <b data-testid="total">{{ total }}</b>
    <span class="sf-end">
      <a class="p-button p-button-text" [routerLink]="listRoute">Cancel</a>
      <p-button label="Create invoice" icon="pi pi-check" [loading]="busy" [disabled]="busy" (onClick)="save()" data-testid="save"></p-button>
    </span>
  </div>
</sf-page>`
})
export class ServiceInvoiceComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly router = inject(Router);

  readonly fields = FIELDS;
  readonly listRoute = '/portal/pages/finance/sales-invoices';
  customers: Opt[] = [];
  customerUuid: string | null = null;
  values = formDefaults(FIELDS);
  busy = false;
  error = '';

  get total(): string {
    return money(((this.values['lines'] ?? []) as any[]).reduce((s, l) => s + Number(l.quantity || 0) * Number(l.unitPrice || 0), 0));
  }

  ngOnInit(): void {
    this.api.get<any>('partners', { isCustomer: true, pageSize: 200 }).subscribe({
      next: r => { this.customers = rowsOf<any>(r).map(p => ({ label: p.companyName, value: p.uuid })); },
      error: err => { this.error = hrmError(err, 'The customers could not be loaded.'); }
    });
  }

  save(): void {
    this.error = !this.customerUuid ? 'Customer is required.' : (formProblem(FIELDS, this.values) ?? '');
    if (this.error) return;
    this.busy = true;
    this.api.post<any>('sales-invoices/service', { customerUuid: this.customerUuid, ...cleanBody(FIELDS, this.values) }).subscribe({
      next: inv => { this.busy = false; this.router.navigate([this.listRoute, inv.uuid]); },
      error: err => { this.busy = false; this.error = hrmError(err, 'The invoice could not be created.'); }
    });
  }
}
