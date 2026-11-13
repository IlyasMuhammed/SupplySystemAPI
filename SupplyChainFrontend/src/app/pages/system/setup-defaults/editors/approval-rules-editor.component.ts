import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { CheckboxModule } from 'primeng/checkbox';
import { clone } from '../../../onboarding/onboarding.shared';

/**
 * A50 task 161 — APPROVAL_RULES payload: per document type, the approval levels (min / max amount, approver role
 * code, label, SLA hours). Level numbers follow the row order. An `afterA49` flag from an older pack is ignored (D-91).
 */
@Component({
  selector: 'app-approval-rules-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, InputNumberModule, CheckboxModule],
  styleUrls: ['../../../onboarding/onboarding.scss'],
  template: `
<div data-testid="approval-editor">
  <div class="sf-empty" *ngIf="!rules.length" data-testid="approval-empty">No rules. Add a document type.</div>
  <div class="sf-card" *ngFor="let r of rules; let ri = index" style="margin-bottom:.6rem" [attr.data-testid]="'rule-' + ri">
    <div class="ob-row">
      <label class="ob-sm" [for]="'dt-' + ri">Document type</label>
      <input pInputText [id]="'dt-' + ri" [(ngModel)]="r.documentType" (ngModelChange)="changed()" style="width:12rem" />
      <label class="ob-sm" [for]="'ic-' + ri">Interface code</label>
      <input pInputText [id]="'ic-' + ri" [(ngModel)]="r.interfaceCode" (ngModelChange)="changed()" style="width:6rem" />
      
      <span class="ob-grow"></span>
      <p-button icon="pi pi-trash" label="Remove rule" [text]="true" severity="danger" size="small" (onClick)="removeRule(ri)"></p-button>
    </div>
    <div class="ob-scroll" style="margin-top:.5rem">
      <table class="ob-matrix" style="width:100%;min-width:40rem">
        <thead><tr><th>Level</th><th>Min amount</th><th>Max amount</th><th>Approver role</th><th>Label</th><th>SLA h</th><th></th></tr></thead>
        <tbody>
          <tr *ngFor="let l of r.levels; let li = index" [attr.data-testid]="'level-' + ri + '-' + li">
            <td class="c">{{ li + 1 }}</td>
            <td><p-inputNumber [(ngModel)]="l.minAmount" [min]="0" (ngModelChange)="changed()" ariaLabel="Min amount" [inputStyle]="{ width: '7rem' }"></p-inputNumber></td>
            <td><p-inputNumber [(ngModel)]="l.maxAmount" [min]="0" (ngModelChange)="changed()" ariaLabel="Max amount" placeholder="no limit" [inputStyle]="{ width: '7rem' }"></p-inputNumber></td>
            <td><input pInputText [(ngModel)]="l.approverRole" (ngModelChange)="changed()" aria-label="Approver role" style="width:11rem" /></td>
            <td><input pInputText [(ngModel)]="l.label" (ngModelChange)="changed()" aria-label="Label" style="width:100%" /></td>
            <td><p-inputNumber [(ngModel)]="l.slaHours" [min]="0" (ngModelChange)="changed()" ariaLabel="SLA hours" [inputStyle]="{ width: '4rem' }"></p-inputNumber></td>
            <td><p-button icon="pi pi-trash" [text]="true" severity="danger" size="small" ariaLabel="Remove level" (onClick)="removeLevel(ri, li)"></p-button></td>
          </tr>
        </tbody>
      </table>
    </div>
    <p-button label="Add level" icon="pi pi-plus" [text]="true" size="small" (onClick)="addLevel(ri)" [attr.data-testid]="'level-add-' + ri"></p-button>
  </div>
  <p-button label="Add document type" icon="pi pi-plus" [text]="true" (onClick)="addRule()" data-testid="rule-add"></p-button>
  <div class="ob-note er" *ngIf="problem" data-testid="approval-problem"><i class="pi pi-exclamation-triangle"></i><div>{{ problem }}</div></div>
</div>`
})
export class ApprovalRulesEditorComponent implements OnChanges {
  @Input() payload: any;
  @Output() payloadChange = new EventEmitter<any>();

  rules: any[] = [];
  problem: string | null = null;
  private emitted: any = null;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.rules = clone(Array.isArray(this.payload?.rules) ? this.payload.rules : []).map((r: any) => ({ ...r, levels: Array.isArray(r.levels) ? r.levels : [] }));
    }
  }

  addRule(): void { this.rules = [...this.rules, { documentType: '', levels: [] }]; this.changed(); }
  removeRule(i: number): void { this.rules = this.rules.filter((_, x) => x !== i); this.changed(); }

  addLevel(ri: number): void {
    const levels = this.rules[ri].levels;
    const prevMax = levels.length ? levels[levels.length - 1].maxAmount : null;
    levels.push({ level: levels.length + 1, minAmount: levels.length ? (prevMax ?? 0) : 0, maxAmount: null, approverRole: '', label: '', slaHours: 48 });
    this.changed();
  }

  removeLevel(ri: number, li: number): void { this.rules[ri].levels.splice(li, 1); this.changed(); }

  /** PAYLOAD-SCHEMAS: level 1 starts at 0, every level has an approver, minimums never go down. */
  static problemOf(rules: any[]): string | null {
    for (const r of rules) {
      if (!(r.documentType ?? '').trim()) return 'Every rule needs a document type.';
      let prev = -1;
      for (const [i, l] of (r.levels ?? []).entries()) {
        if (!(l.approverRole ?? '').trim()) return `${r.documentType} level ${i + 1} needs an approver role.`;
        const min = Number(l.minAmount ?? 0);
        if (i === 0 && min !== 0) return `${r.documentType}: level 1 must start at 0.`;
        if (min < prev) return `${r.documentType}: level ${i + 1} starts below level ${i}.`;
        if (l.maxAmount !== null && l.maxAmount !== undefined && Number(l.maxAmount) < min) return `${r.documentType} level ${i + 1}: max is below min.`;
        prev = min;
      }
    }
    return null;
  }

  changed(): void {
    this.rules.forEach(r => r.levels.forEach((l: any, i: number) => (l.level = i + 1)));
    this.problem = ApprovalRulesEditorComponent.problemOf(this.rules);
    this.emitted = { ...(this.payload ?? {}), rules: clone(this.rules) };
    this.payload = this.emitted;
    this.payloadChange.emit(this.emitted);
  }
}
