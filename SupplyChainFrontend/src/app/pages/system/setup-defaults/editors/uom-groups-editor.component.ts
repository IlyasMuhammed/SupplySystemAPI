import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { RadioButtonModule } from 'primeng/radiobutton';
import { clone } from '../../../onboarding/onboarding.shared';

/**
 * A50 task 162 — UOM payload as groups of units: code, name, the group's base unit (exactly one) and the conversion
 * factor to the base (stored only, R-1).
 */
@Component({
  selector: 'app-uom-groups-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, InputNumberModule, RadioButtonModule],
  styleUrls: ['../../../onboarding/onboarding.scss'],
  template: `
<div data-testid="uom-editor">
  <div class="sf-empty" *ngIf="!groups.length" data-testid="uom-empty">No unit groups.</div>
  <div class="sf-card" *ngFor="let g of groups; let gi = index" style="margin-bottom:.6rem" [attr.data-testid]="'uom-group-' + gi">
    <div class="ob-row">
      <label class="ob-sm" [for]="'g-' + gi">Group</label>
      <input pInputText [id]="'g-' + gi" [(ngModel)]="g.name" (ngModelChange)="changed()" style="width:12rem" />
      <span class="ob-grow"></span>
      <p-button icon="pi pi-trash" label="Remove group" [text]="true" severity="danger" size="small" (onClick)="removeGroup(gi)"></p-button>
    </div>
    <div class="ob-scroll" style="margin-top:.5rem">
      <table class="ob-matrix" style="width:100%;min-width:30rem">
        <thead><tr><th>Code</th><th>Name</th><th>Base</th><th>Base units per unit</th><th></th></tr></thead>
        <tbody>
          <tr *ngFor="let u of g.units; let ui = index" [attr.data-testid]="'uom-' + gi + '-' + ui">
            <td><input pInputText [(ngModel)]="u.code" (ngModelChange)="changed()" maxlength="20" aria-label="Code" style="width:6rem" /></td>
            <td><input pInputText [(ngModel)]="u.name" (ngModelChange)="changed()" maxlength="150" aria-label="Name" style="width:100%" /></td>
            <td class="c"><p-radiobutton [name]="'base-' + gi" [value]="ui" [ngModel]="baseIndex(g)" (ngModelChange)="setBase(g, $event)" [ariaLabel]="'Base ' + u.code"></p-radiobutton></td>
            <td><p-inputNumber [(ngModel)]="u.conversionFactor" [min]="0" [maxFractionDigits]="6" [disabled]="!!u.isBase" (ngModelChange)="changed()"
                               ariaLabel="Conversion factor" [inputStyle]="{ width: '8rem' }"></p-inputNumber></td>
            <td><p-button icon="pi pi-trash" [text]="true" severity="danger" size="small" ariaLabel="Remove unit" (onClick)="removeUnit(gi, ui)"></p-button></td>
          </tr>
        </tbody>
      </table>
    </div>
    <p-button label="Add unit" icon="pi pi-plus" [text]="true" size="small" (onClick)="addUnit(gi)" [attr.data-testid]="'uom-add-' + gi"></p-button>
  </div>
  <p-button label="Add group" icon="pi pi-plus" [text]="true" (onClick)="addGroup()" data-testid="uom-group-add"></p-button>
  <div class="ob-note er" *ngIf="problem" data-testid="uom-problem"><i class="pi pi-exclamation-triangle"></i><div>{{ problem }}</div></div>
</div>`
})
export class UomGroupsEditorComponent implements OnChanges {
  @Input() payload: any;
  @Output() payloadChange = new EventEmitter<any>();

  groups: { name: string; units: any[] }[] = [];
  problem: string | null = null;
  private emitted: any = null;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.groups = clone(Array.isArray(this.payload?.groups) ? this.payload.groups : []).map((g: any) => ({ ...g, units: Array.isArray(g.units) ? g.units : [] }));
    }
  }

  baseIndex(g: { units: any[] }): number { return g.units.findIndex(u => u.isBase); }

  setBase(g: { units: any[] }, index: number): void {
    g.units.forEach((u, i) => {
      if (i === index) { u.isBase = true; delete u.conversionFactor; } else { delete u.isBase; if (u.conversionFactor === undefined) u.conversionFactor = 1; }
    });
    this.changed();
  }

  addGroup(): void { this.groups = [...this.groups, { name: '', units: [{ code: '', name: '', isBase: true }] }]; this.changed(); }
  removeGroup(i: number): void { this.groups = this.groups.filter((_, x) => x !== i); this.changed(); }
  addUnit(gi: number): void { this.groups[gi].units.push({ code: '', name: '', conversionFactor: 1 }); this.changed(); }
  removeUnit(gi: number, ui: number): void { this.groups[gi].units.splice(ui, 1); this.changed(); }

  static problemOf(groups: { name: string; units: any[] }[]): string | null {
    const codes = new Set<string>();
    for (const g of groups) {
      if (!(g.name ?? '').trim()) return 'Every group needs a name.';
      if (g.units.filter(u => u.isBase).length !== 1) return `Group ${g.name} needs exactly one base unit.`;
      for (const u of g.units) {
        const c = (u.code ?? '').trim().toUpperCase();
        if (!/^[A-Za-z0-9_.-]{1,20}$/.test(c)) return `Unit code "${u.code ?? ''}" in ${g.name} is not valid.`;
        if (codes.has(c)) return `Unit code ${c} is used twice.`;
        codes.add(c);
        if (!(u.name ?? '').trim()) return `Unit ${c} needs a name.`;
        if (!u.isBase && !(Number(u.conversionFactor) > 0)) return `Unit ${c} needs a conversion factor above 0.`;
      }
    }
    return null;
  }

  changed(): void {
    this.problem = UomGroupsEditorComponent.problemOf(this.groups);
    this.emitted = { ...(this.payload ?? {}), groups: clone(this.groups) };
    this.payload = this.emitted;
    this.payloadChange.emit(this.emitted);
  }
}
