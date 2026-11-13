import { Component, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { CheckboxModule } from 'primeng/checkbox';
import { MultiSelectModule } from 'primeng/multiselect';
import { catchError, of, switchMap } from 'rxjs';
import { AuthService } from '../../../service/auth.service';
import { MODULE_NAMES } from '../../../../services/module.models';
import { clone } from '../../../onboarding/onboarding.shared';

interface PermRow { code: string; group: string; }

/**
 * A50 task 160 — ROLES payload as a role × permission matrix. Columns = the pack's roles (code, name, required
 * modules); rows = permission codes, grouped by module, from the existing role catalog (`api/roles` → a role's
 * permissionGroups), plus any code already in the payload. "ALL" on a role ticks everything (ORG_ADMIN).
 */
@Component({
  selector: 'app-role-matrix-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, CheckboxModule, MultiSelectModule],
  styleUrls: ['../../../onboarding/onboarding.scss'],
  template: `
<div data-testid="roles-editor">
  <div class="ob-row" style="margin-bottom:.5rem">
    <input pInputText [(ngModel)]="search" placeholder="Filter permissions" aria-label="Filter permissions" data-testid="perm-search" />
    <span class="ob-mu ob-sm">{{ roles.length }} roles · {{ visibleRows.length }} permissions</span>
    <span class="ob-grow"></span>
    <p-button label="Add role" icon="pi pi-plus" [text]="true" (onClick)="addRole()" data-testid="role-add"></p-button>
  </div>
  <div class="ob-note" *ngIf="catalogNote"><i class="pi pi-info-circle"></i><div>{{ catalogNote }}</div></div>
  <div class="ob-scroll" style="max-height:32rem;overflow-y:auto">
    <table class="ob-matrix">
      <thead>
        <tr><th style="text-align:left;min-width:14rem">Permission</th>
          <th *ngFor="let r of roles; let i = index" [attr.data-testid]="'role-col-' + i" style="min-width:9rem">
            <input pInputText [(ngModel)]="r.code" (ngModelChange)="changed()" aria-label="Role code" style="width:8.5rem" />
            <input pInputText [(ngModel)]="r.name" (ngModelChange)="changed()" aria-label="Role name" style="width:8.5rem;margin-top:.2rem" />
            <p-multiselect [options]="moduleOptions" [(ngModel)]="r.requiredModules" (ngModelChange)="changed()" placeholder="Modules"
                           appendTo="body" [style]="{ width: '8.5rem', marginTop: '.2rem' }" ariaLabel="Required modules"></p-multiselect>
            <div class="ob-row" style="justify-content:center">
              <p-checkbox [binary]="true" [ngModel]="isAll(r)" (ngModelChange)="setAll(r, $event)" [inputId]="'all-' + i"></p-checkbox>
              <label [for]="'all-' + i" class="ob-sm">ALL</label>
              <p-button icon="pi pi-trash" [text]="true" severity="danger" size="small" ariaLabel="Remove role" (onClick)="removeRole(i)"></p-button>
            </div>
          </th></tr>
      </thead>
      <tbody>
        <ng-container *ngFor="let g of groups">
          <tr class="grp" *ngIf="rowsOf(g).length"><td [attr.colspan]="roles.length + 1">{{ g }}</td></tr>
          <tr *ngFor="let p of rowsOf(g)" [attr.data-testid]="'perm-' + p.code">
            <td class="p">{{ p.code }}</td>
            <td class="c" *ngFor="let r of roles">
              <p-checkbox [binary]="true" [ngModel]="has(r, p.code)" (ngModelChange)="toggle(r, p.code, $event)" [disabled]="isAll(r)"
                          [ariaLabel]="r.code + ' ' + p.code"></p-checkbox></td>
          </tr>
        </ng-container>
      </tbody>
    </table>
  </div>
  <div class="ob-row" style="margin-top:.5rem">
    <input pInputText [(ngModel)]="newCode" placeholder="Add a permission code" aria-label="Permission code" data-testid="perm-new" />
    <p-button label="Add row" [text]="true" [disabled]="!newCode.trim()" (onClick)="addPermission()"></p-button>
  </div>
</div>`
})
export class RoleMatrixEditorComponent implements OnInit, OnChanges {
  private readonly auth = inject(AuthService, { optional: true });

  @Input() payload: any;
  @Output() payloadChange = new EventEmitter<any>();

  roles: { code: string; name: string; description?: string; permissions: string[]; requiredModules: string[]; [k: string]: any }[] = [];
  catalog: PermRow[] = [];
  extra: PermRow[] = [];
  search = '';
  newCode = '';
  catalogNote = '';
  readonly moduleOptions = Object.keys(MODULE_NAMES);
  private emitted: any = null;

  ngOnInit(): void {
    if (!this.auth) return;
    // There is no permission-catalog endpoint (D-CORE §9): one role's detail lists every permission, grouped by module.
    this.auth.getRoleList().pipe(
      switchMap(r => { const id = r?.result?.[0]?.roleId; return id ? this.auth!.getRoleDetail(id) : of(null); }),
      catchError(() => of(null))
    ).subscribe(d => {
      const groups = d?.result?.permissionGroups ?? [];
      this.catalog = groups.flatMap(g => g.permissions.map(p => ({ code: p.code, group: g.module || 'Other' })));
      if (!this.catalog.length) this.catalogNote = 'The permission catalog could not be read — only the codes already in this payload are listed.';
    });
  }

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.roles = clone(Array.isArray(this.payload?.roles) ? this.payload.roles : []).map((r: any) => ({
        ...r, permissions: Array.isArray(r.permissions) ? r.permissions : [], requiredModules: Array.isArray(r.requiredModules) ? r.requiredModules : []
      }));
    }
  }

  get allRows(): PermRow[] {
    const known = new Set(this.catalog.map(c => c.code));
    const fromPayload = new Set<string>();
    this.roles.forEach(r => r.permissions.forEach(p => { if (p !== 'ALL' && !known.has(p)) fromPayload.add(p); }));
    this.extra.forEach(e => fromPayload.add(e.code));
    return [...this.catalog, ...[...fromPayload].filter(c => !known.has(c)).map(code => ({ code, group: 'Other (not in catalog)' }))];
  }

  get visibleRows(): PermRow[] {
    const t = this.search.trim().toLowerCase();
    return this.allRows.filter(r => !t || r.code.toLowerCase().includes(t) || r.group.toLowerCase().includes(t));
  }

  get groups(): string[] { return [...new Set(this.visibleRows.map(r => r.group))]; }

  rowsOf(g: string): PermRow[] { return this.visibleRows.filter(r => r.group === g); }

  isAll(r: { permissions: string[] }): boolean { return r.permissions.includes('ALL'); }
  has(r: { permissions: string[] }, code: string): boolean { return this.isAll(r) || r.permissions.includes(code); }

  toggle(r: { permissions: string[] }, code: string, on: boolean): void {
    r.permissions = on ? [...new Set([...r.permissions, code])] : r.permissions.filter(p => p !== code);
    this.changed();
  }

  setAll(r: { permissions: string[] }, on: boolean): void {
    r.permissions = on ? ['ALL'] : [];
    this.changed();
  }

  addRole(): void {
    this.roles = [...this.roles, { code: `ROLE_${this.roles.length + 1}`, name: 'New role', permissions: [], requiredModules: [] }];
    this.changed();
  }

  removeRole(i: number): void {
    this.roles = this.roles.filter((_, x) => x !== i);
    this.changed();
  }

  addPermission(): void {
    const code = this.newCode.trim().toUpperCase();
    if (code && !this.allRows.some(r => r.code === code)) this.extra = [...this.extra, { code, group: 'Other (not in catalog)' }];
    this.newCode = '';
  }

  changed(): void {
    this.emitted = { ...(this.payload ?? {}), roles: this.roles.map(r => ({ ...r })) };
    this.payload = this.emitted;
    this.payloadChange.emit(this.emitted);
  }
}
