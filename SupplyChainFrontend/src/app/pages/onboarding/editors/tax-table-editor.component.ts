import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { SelectModule } from 'primeng/select';
import { CheckboxModule } from 'primeng/checkbox';
import { TAX_TYPES, TAX_USAGES, TaxRow, clone, taxProblem } from '../onboarding.shared';

/**
 * A50 tasks 131 / 159 — the tax codes of a TAX payload as an editable table (code, name, rate, usage, type, default),
 * with add / remove rows. `verified: false` packs show the "check with your tax adviser" banner (D-13). Used by the
 * wizard (Step 4) and the super admin TAX editor (which may also tick "verified").
 */
@Component({
  selector: 'app-tax-table-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule, InputNumberModule, SelectModule, CheckboxModule],
  styleUrls: ['../onboarding.scss'],
  template: `
<div data-testid="tax-editor">
  <div class="ob-note wn" *ngIf="payload?.verified === false" data-testid="tax-unverified">
    <i class="pi pi-exclamation-triangle"></i>
    <div><strong>verified: false — check with your tax adviser.</strong>
      These rates are indicative and have not been confirmed for your business.
      <span *ngIf="payload?.verificationNote" class="ob-mu"> {{ payload.verificationNote }}</span></div>
  </div>
  <div class="ob-row" *ngIf="allowVerify" style="margin-bottom:.5rem">
    <p-checkbox [binary]="true" [ngModel]="payload?.verified === true" (ngModelChange)="setVerified($event)" inputId="tax-verified"
                data-testid="tax-verify"></p-checkbox>
    <label for="tax-verified">A tax adviser has confirmed these rates (verified)</label>
  </div>
  <div class="ob-note er" *ngIf="problem" data-testid="tax-problem"><i class="pi pi-exclamation-triangle"></i><div>{{ problem }}</div></div>
  <div class="sf-card p0 ob-scroll">
    <p-table [value]="rows" [tableStyle]="{ 'min-width': '44rem' }" dataKey="code">
      <ng-template pTemplate="header">
        <tr><th style="width:8rem">Code</th><th>Name</th><th style="width:8rem" class="ob-num">Rate %</th><th style="width:11rem">Used on</th>
          <th style="width:9rem">Type</th><th style="width:5rem">Default</th><th style="width:3rem"></th></tr>
      </ng-template>
      <ng-template pTemplate="body" let-r let-i="rowIndex">
        <tr [attr.data-testid]="'tax-row-' + i">
          <td><input pInputText [(ngModel)]="r.code" maxlength="20" (ngModelChange)="changed()" [readonly]="readonly" aria-label="Code" style="width:100%" /></td>
          <td><input pInputText [(ngModel)]="r.name" maxlength="100" (ngModelChange)="changed()" [readonly]="readonly" aria-label="Name" style="width:100%" /></td>
          <td><p-inputNumber [(ngModel)]="r.rate" [min]="0" [max]="100" [maxFractionDigits]="2" (ngModelChange)="changed()" [readonly]="readonly"
                             ariaLabel="Rate" [inputStyle]="{ width: '100%', textAlign: 'right' }" [attr.data-testid]="'tax-rate-' + i"></p-inputNumber></td>
          <td><p-select [options]="usages" optionLabel="label" optionValue="value" [(ngModel)]="r.usage" (ngModelChange)="changed()" [readonly]="readonly"
                        appendTo="body" [style]="{ width: '100%' }" ariaLabel="Used on"></p-select></td>
          <td><p-select [options]="types" [(ngModel)]="r.type" (ngModelChange)="changed()" [readonly]="readonly" [showClear]="true"
                        appendTo="body" [style]="{ width: '100%' }" ariaLabel="Type"></p-select></td>
          <td class="c"><p-checkbox [binary]="true" [(ngModel)]="r.isDefault" (ngModelChange)="changed()" [readonly]="readonly" ariaLabel="Default"></p-checkbox></td>
          <td><p-button *ngIf="!readonly" icon="pi pi-trash" [text]="true" severity="danger" size="small" ariaLabel="Remove" (onClick)="remove(i)"
                        [attr.data-testid]="'tax-remove-' + i"></p-button></td>
        </tr>
      </ng-template>
      <ng-template pTemplate="emptymessage"><tr><td colspan="7" class="sf-empty" data-testid="tax-empty">No tax codes. Add one, or skip this category.</td></tr></ng-template>
    </p-table>
  </div>
  <p-button *ngIf="!readonly" label="Add tax code" icon="pi pi-plus" [text]="true" (onClick)="add()" data-testid="tax-add"></p-button>
</div>`
})
export class TaxTableEditorComponent implements OnChanges {
  @Input() payload: any;
  @Input() readonly = false;
  /** Super admin: show the "verified" tick box. */
  @Input() allowVerify = false;
  @Output() payloadChange = new EventEmitter<any>();

  readonly usages = TAX_USAGES;
  readonly types = TAX_TYPES;
  rows: TaxRow[] = [];
  problem: string | null = null;
  private emitted: any = null;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.rows = clone(Array.isArray(this.payload?.taxCodes) ? this.payload.taxCodes : []);
      this.problem = null;
    }
  }

  add(): void {
    this.rows = [...this.rows, { code: '', name: '', rate: 0, usage: 'SALES', type: 'Output', isDefault: false }];
    this.changed();
  }

  remove(i: number): void {
    this.rows = this.rows.filter((_, x) => x !== i);
    this.changed();
  }

  setVerified(v: boolean): void {
    this.emit({ ...(this.payload ?? {}), verified: !!v });
  }

  changed(): void {
    this.rows.forEach(r => { if (typeof r.code === 'string') r.code = r.code.toUpperCase(); });
    this.problem = taxProblem(this.rows);
    this.emit({ ...(this.payload ?? {}), taxCodes: this.rows.map(r => ({ ...r })) });
  }

  private emit(p: any): void {
    this.emitted = p;
    this.payload = p;
    this.payloadChange.emit(p);
  }
}
