import { Component, EventEmitter, Input, OnInit, Output, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { Menu, MenuModule } from 'primeng/menu';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MenuItem, MessageService } from 'primeng/api';

import {
  Account, AccountDetail, AccountTreeNode, AccountingService, CoaSummary, FileFormat, GL_PERMS
} from '../../../../services/accounting.service';
import { importErrorMessage, saveDownload } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { TreeRow, accountTypeLabel, allNodes, flattenTree, glError } from '../gl.shared';
import { GlAccountDialogComponent } from './gl-account-dialog.component';

/** The reason a delete is refused, in the server's words (BR-COA-005/006/007); null when it may go. */
export function deleteBlockedReason(n: AccountTreeNode): string | null {
  if (n.canDelete === true) return null;
  if (n.canDelete === false) return n.deleteBlockedReason || 'This account cannot be deleted.';
  if (n.deleteBlockedReason) return n.deleteBlockedReason;
  if (n.isSystemAccount) return 'Cannot delete a system account.';
  if (n.children?.length) return 'Cannot delete an account that has child accounts.';
  if (n.hasTransactions) return 'Cannot delete an account with posted transactions.';
  return null;
}

/**
 * A42 (0264–0284) — `/gl/accounts`: the chart of accounts as a collapsible tree (code, name, type, balance on leaves),
 * a ⋯ menu per row (Edit, Delete — disabled with the reason), + Add Account, Export CSV/JSON, search, level filter and a
 * footer with totals. Also step 2 of the wizard (`embedded`: no sf-page, same content).
 */
@Component({
  selector: 'app-gl-accounts',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, IconFieldModule, InputIconModule, InputTextModule, MenuModule,
    SelectModule, TableModule, ToastModule, ...FLOW, GlAccountDialogComponent],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast *ngIf="!embedded"></p-toast>
<p-confirmDialog key="gl-acc"></p-confirmDialog>

<ng-template #actions>
  <p-button *ngIf="canManage" label="Add account" icon="pi pi-plus" (onClick)="openNew()" data-testid="add-account"></p-button>
  <p-button label="Export" icon="pi pi-download" iconPos="left" [text]="true" (onClick)="exportMenu.toggle($event)" data-testid="export"></p-button>
  <span class="sf-sep"></span>
  <p-button label="Expand all" icon="pi pi-angle-double-down" [text]="true" (onClick)="expandAll()" data-testid="expand-all"></p-button>
  <p-button label="Collapse" icon="pi pi-angle-double-up" [text]="true" (onClick)="collapseAll()" data-testid="collapse-all"></p-button>
  <p-button icon="pi pi-refresh" [text]="true" (onClick)="load()" ariaLabel="Refresh"></p-button>
</ng-template>

<ng-template #content>
  <div class="sf-filters">
    <p-iconfield class="sf-search">
      <p-inputicon styleClass="pi pi-search"></p-inputicon>
      <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="rebuild()" placeholder="Search code or name" data-testid="search" />
    </p-iconfield>
    <p-select [options]="levelOptions" [(ngModel)]="maxLevel" optionLabel="label" optionValue="value" (onChange)="rebuild()"
              appendTo="body" data-testid="level-filter"></p-select>
  </div>

  <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

  <div class="sf-card p0">
    <div class="gl-scroll">
      <p-table [value]="rows" [loading]="loading" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '44rem' }" data-testid="tree">
        <ng-template pTemplate="header">
          <tr><th>Code / name</th><th>Type</th><th class="gl-num">Balance</th><th style="width:3rem"></th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [class.gl-group]="r.node.isGroup" [class.gl-root]="r.node.level === 1" [class.gl-inactive]="!r.node.isActive"
              [attr.data-testid]="'row-' + r.node.accountCode">
            <td>
              <span class="gl-tree-name" [style.padding-left.rem]="r.depth * 1.25">
                <button *ngIf="r.hasChildren" type="button" class="gl-toggle" (click)="toggle(r)" [attr.aria-expanded]="r.expanded"
                        [attr.aria-label]="(r.expanded ? 'Collapse ' : 'Expand ') + r.node.accountCode" [attr.data-testid]="'toggle-' + r.node.accountCode">
                  <i class="pi" [ngClass]="r.expanded ? 'pi-chevron-down' : 'pi-chevron-right'"></i>
                </button>
                <span *ngIf="!r.hasChildren" class="gl-toggle-sp"></span>
                <span class="sf-mono">{{ r.node.accountCode }}</span>
                <span>{{ r.node.accountName }}</span>
                <i *ngIf="r.node.isSystemAccount" class="pi pi-lock sf-mu" title="System account" style="font-size:.75rem"></i>
                <span *ngIf="!r.node.isActive" class="sf-pill">Inactive</span>
              </span>
            </td>
            <td class="sf-sm">{{ typeLabel(r.node.accountType) }}</td>
            <td class="gl-num">{{ r.node.isGroup ? '' : (r.node.currentBalance | number:'1.2-2') }}</td>
            <td>
              <p-button icon="pi pi-ellipsis-h" [text]="true" size="small" (onClick)="openRowMenu($event, r.node)"
                        [ariaLabel]="'Actions for ' + r.node.accountCode" [attr.data-testid]="'menu-' + r.node.accountCode"></p-button>
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="4" class="sf-empty">{{ search ? 'No account matches the search.' : 'No accounts yet — apply a template or upload a chart in the setup wizard.' }}</td></tr>
        </ng-template>
      </p-table>
    </div>
    <div class="gl-foot" data-testid="footer">
      <span>Total accounts <strong data-testid="total-accounts">{{ summary.totalAccounts | number }}</strong></span>
      <span>Postable <strong data-testid="postable-accounts">{{ summary.postableAccounts | number }}</strong></span>
      <span *ngIf="summary.lastModifiedAt">Last modified <strong>{{ summary.lastModifiedAt | date:'d MMM y, HH:mm' }}</strong></span>
    </div>
  </div>
</ng-template>

<sf-page *ngIf="!embedded" [crumbs]="['Finance', 'General Ledger', 'Chart of Accounts']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Chart of accounts <span class="sf-count">{{ summary.totalAccounts }}</span></h1>
        <div class="sf-sub">Groups (Levels 1–3) total the postable Level 4 accounts below them</div></div>
    </div>
    <ng-container *ngTemplateOutlet="content"></ng-container>
  </div>
</sf-page>
<div *ngIf="embedded" class="gl-embedded">
  <div class="sf-cmd" role="toolbar" aria-label="Chart commands" style="margin-bottom:.5rem"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <ng-container *ngTemplateOutlet="content"></ng-container>
</div>

<p-menu #exportMenu [model]="exportItems" [popup]="true" appendTo="body"></p-menu>
<p-menu #rowMenu [model]="rowItems" [popup]="true" appendTo="body" data-testid="row-menu"></p-menu>
<app-gl-account-dialog [(visible)]="dialogVisible" [accounts]="flat" [editing]="editing" [presetParentUuid]="presetParent"
                       (saved)="onSaved($event)"></app-gl-account-dialog>`,
  styles: [`.gl-embedded { display: flex; flex-direction: column; gap: .75rem; }`]
})
export class GlAccountsComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() embedded = false;
  /** Accounts were added, changed or deleted (the wizard re-reads the setup status). */
  @Output() changed = new EventEmitter<void>();
  @ViewChild('rowMenu') rowMenu?: Menu;

  roots: AccountTreeNode[] = [];
  flat: Account[] = [];
  rows: TreeRow[] = [];
  summary: CoaSummary = { totalAccounts: 0, postableAccounts: 0, lastModifiedAt: null };
  expanded = new Set<string>();
  search = '';
  maxLevel: number | null = null;
  loading = false;
  loadError = '';

  dialogVisible = false;
  editing: Account | null = null;
  presetParent: string | null = null;
  rowItems: MenuItem[] = [];

  readonly typeLabel = accountTypeLabel;
  readonly levelOptions = [
    { label: 'All levels', value: null }, { label: 'Level 1 only', value: 1 }, { label: 'Up to level 2', value: 2 }, { label: 'Up to level 3', value: 3 }
  ];
  readonly exportItems: MenuItem[] = [
    { label: 'CSV', icon: 'pi pi-file', command: () => this.export('csv') },
    { label: 'JSON', icon: 'pi pi-code', command: () => this.export('json') }
  ];

  get canManage(): boolean { return this.auth.hasAnyPermission(GL_PERMS.ACCOUNT_MANAGE, GL_PERMS.SETUP_MANAGE); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getChart().subscribe({
      next: t => {
        this.loading = false;
        this.roots = t.roots;
        this.summary = t.summary;
        this.flat = allNodes(t.roots);
        // First load: the five roots open, so the main groups show.
        if (!this.expanded.size) t.roots.forEach(r => this.expanded.add(r.uuid));
        this.rebuild();
      },
      error: err => { this.loading = false; this.loadError = glError(err, 'The chart of accounts could not be loaded.'); }
    });
  }

  rebuild(): void {
    this.rows = flattenTree(this.roots, this.expanded, { search: this.search, maxLevel: this.maxLevel });
  }

  toggle(r: TreeRow): void {
    if (this.search.trim()) return; // a search shows every match expanded
    if (this.expanded.has(r.node.uuid)) this.expanded.delete(r.node.uuid); else this.expanded.add(r.node.uuid);
    this.rebuild();
  }

  expandAll(): void { this.flat.filter(a => a.isGroup).forEach(a => this.expanded.add(a.uuid)); this.rebuild(); }
  collapseAll(): void { this.expanded.clear(); this.rebuild(); }

  openRowMenu(event: Event, n: AccountTreeNode): void {
    const reason = deleteBlockedReason(n);
    this.rowItems = [
      { label: 'Edit', icon: 'pi pi-pencil', disabled: !this.canManage, command: () => this.openEdit(n) },
      ...(n.isGroup && n.level < 4 ? [{ label: 'Add account under', icon: 'pi pi-plus', disabled: !this.canManage, command: () => this.openNew(n.uuid) }] : []),
      { label: 'Delete', icon: 'pi pi-trash', disabled: !this.canManage || !!reason, command: () => this.confirmDelete(n) },
      ...(reason ? [{ label: reason, icon: 'pi pi-info-circle', disabled: true, styleClass: 'gl-reason' }] : [])
    ];
    this.rowMenu?.toggle(event);
  }

  openNew(parentUuid: string | null = null): void {
    this.editing = null;
    this.presetParent = parentUuid;
    this.dialogVisible = true;
  }

  openEdit(n: Account): void {
    this.editing = n;
    this.presetParent = null;
    this.dialogVisible = true;
  }

  onSaved(a: AccountDetail): void {
    this.toast('success', `Account ${a.accountCode} saved.`);
    if (a.parentUuid) this.expanded.add(a.parentUuid);
    this.load();
    this.changed.emit();
  }

  confirmDelete(n: AccountTreeNode): void {
    this.confirm.confirm({
      key: 'gl-acc', header: 'Delete account', icon: 'pi pi-trash',
      message: `Delete ${n.accountCode} ${n.accountName}? It is deactivated and removed from the chart.`,
      acceptLabel: 'Delete', rejectLabel: 'Cancel', acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.api.deleteAccount(n.uuid).subscribe({
        next: () => { this.toast('success', `Account ${n.accountCode} deleted.`); this.load(); this.changed.emit(); },
        error: err => this.toast('error', glError(err, 'The account could not be deleted.'))
      })
    });
  }

  export(format: FileFormat): void {
    this.api.exportChart(format).subscribe({
      next: res => saveDownload(res, `chart-of-accounts.${format}`),
      error: async err => this.toast('error', await importErrorMessage(err, 'The chart could not be exported.'))
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages?.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
