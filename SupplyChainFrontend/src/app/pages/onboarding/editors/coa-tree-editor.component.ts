import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TreeModule } from 'primeng/tree';
import { TreeDragDropService, TreeNode } from 'primeng/api';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TooltipModule } from 'primeng/tooltip';
import {
  COA_MAX_LEVEL, COA_ROLE_KEYS, CoaAccount, allCoa, clone, coaProblem, coaRoleKeys, coaToTree, suggestChildCode, treeToCoa
} from '../onboarding.shared';

/**
 * A50 tasks 128–130 / 158 — the chart of accounts of a COA payload (A42 template JSON) as an editable tree:
 * inline rename, add (under a group), remove (not roots, not accounts carrying a DEFAULT_* role), role badges and
 * reorganising by PrimeNG tree drag-and-drop. HTML5 drag-and-drop does not work on touch screens, so every node also
 * has "Move to…" (a group at the same level of the same root) — D-76. Used by the setup wizard (Step 3) and the super
 * admin COA editor. Emits the whole payload (with `accounts` rebuilt) on every change.
 */
@Component({
  selector: 'app-coa-tree-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, TreeModule, ButtonModule, InputTextModule, SelectModule, TooltipModule],
  // p-tree drag-and-drop needs this service (it is not provided in root).
  providers: [TreeDragDropService],
  styleUrls: ['../onboarding.scss'],
  template: `
<div class="ob-tree" data-testid="coa-editor">
  <div class="ob-row" style="margin-bottom:.5rem">
    <span class="ob-mu ob-sm" data-testid="coa-count">{{ count }} accounts</span>
    <span class="ob-grow"></span>
    <p-button label="Expand all" icon="pi pi-plus" [text]="true" size="small" (onClick)="expandAll(true)" data-testid="coa-expand"></p-button>
    <p-button label="Collapse" icon="pi pi-minus" [text]="true" size="small" (onClick)="expandAll(false)"></p-button>
  </div>
  <div class="ob-note er" *ngIf="problem" data-testid="coa-problem"><i class="pi pi-exclamation-triangle"></i><div>{{ problem }}</div></div>
  <div class="sf-card sf-empty" *ngIf="!nodes.length" data-testid="coa-empty">This payload has no accounts.</div>
  <div class="ob-scroll">
    <p-tree *ngIf="nodes.length" [value]="nodes" [draggableNodes]="!readonly" [droppableNodes]="!readonly" [validateDrop]="true"
            draggableScope="coa" droppableScope="coa" (onNodeDrop)="onDrop($event)" [filter]="true" filterBy="label"
            filterPlaceholder="Search code or name">
      <ng-template let-node pTemplate="default">
        <span class="ob-node" [class.ob-grp]="node.data.is_group" [attr.data-testid]="'coa-node-' + node.data.code">
          <ng-container *ngIf="editingKey !== node.key; else editTpl">
            <span class="ob-code">{{ node.data.code }}</span>
            <span class="ob-name">{{ node.data.name }}</span>
          </ng-container>
          <ng-template #editTpl>
            <input pInputText *ngIf="isNew(node)" class="ob-in" [(ngModel)]="node.data.code" maxlength="20" aria-label="Account code"
                   style="max-width:7rem" data-testid="coa-edit-code" (click)="$event.stopPropagation()" />
            <input pInputText class="ob-in" [(ngModel)]="node.data.name" maxlength="200" aria-label="Account name" data-testid="coa-edit-name"
                   (keydown.enter)="commit(node)" (keydown.escape)="commit(node)" (click)="$event.stopPropagation()" />
            <p-button icon="pi pi-check" [text]="true" size="small" ariaLabel="Done" (onClick)="commit(node)" data-testid="coa-commit"></p-button>
          </ng-template>
          <span class="ob-badge" *ngIf="node.data.default_account_key && !(allowRoleEdit && !readonly && !node.data.is_group)"
                [attr.data-testid]="'coa-role-' + node.data.code">{{ node.data.default_account_key }}</span>
          <span *ngIf="allowRoleEdit && !readonly && !node.data.is_group && editingKey !== node.key" (click)="$event.stopPropagation()">
            <p-select [options]="roleOptions" optionLabel="label" optionValue="value" [ngModel]="node.data.default_account_key ?? ''"
                      (ngModelChange)="setRole(node, $event)" appendTo="body" [style]="{ minWidth: '12rem' }" size="small" aria-label="Account role"
                      [attr.data-testid]="'coa-role-select-' + node.data.code"></p-select>
          </span>
          <span class="ob-acts" *ngIf="!readonly && editingKey !== node.key">
            <p-button icon="pi pi-pencil" [text]="true" size="small" ariaLabel="Rename" pTooltip="Rename" (onClick)="edit(node, $event)"
                      [attr.data-testid]="'coa-rename-' + node.data.code"></p-button>
            <p-button *ngIf="canAddUnder(node)" icon="pi pi-plus" [text]="true" size="small" ariaLabel="Add account" pTooltip="Add account below"
                      (onClick)="add(node, $event)" [attr.data-testid]="'coa-add-' + node.data.code"></p-button>
            <p-button *ngIf="depth(node) > 1" icon="pi pi-arrows-alt" [text]="true" size="small" ariaLabel="Move to" pTooltip="Move to…"
                      (onClick)="startMove(node, $event)" [attr.data-testid]="'coa-move-' + node.data.code"></p-button>
            <p-button *ngIf="depth(node) > 1" icon="pi pi-trash" [text]="true" size="small" severity="danger" ariaLabel="Remove" pTooltip="Remove"
                      (onClick)="remove(node, $event)" [attr.data-testid]="'coa-remove-' + node.data.code"></p-button>
          </span>
          <span *ngIf="movingKey === node.key" class="ob-row" (click)="$event.stopPropagation()">
            <p-select [options]="moveTargets(node)" optionLabel="label" optionValue="value" [(ngModel)]="moveTarget" placeholder="Move under…"
                      appendTo="body" [style]="{ minWidth: '12rem' }" data-testid="coa-move-target"></p-select>
            <p-button label="Move" size="small" [disabled]="!moveTarget" (onClick)="move(node)" data-testid="coa-move-go"></p-button>
            <p-button label="Cancel" size="small" [text]="true" (onClick)="movingKey = null"></p-button>
          </span>
        </span>
      </ng-template>
    </p-tree>
  </div>
</div>`
})
export class CoaTreeEditorComponent implements OnChanges {
  /** The COA payload ({ accounts: [...] , template fields… }). Not mutated: a copy is edited. */
  @Input() payload: any;
  @Input() readonly = false;
  /** A51 P5 — show a role (default-account key) dropdown on every postable account (the super admin COA editor; off in the setup wizard). */
  @Input() allowRoleEdit = false;
  @Output() payloadChange = new EventEmitter<any>();

  readonly roleOptions = [{ label: '(no role)', value: '' }, ...COA_ROLE_KEYS.map(k => ({ label: k.replace(/^DEFAULT_/, '').replace(/_/g, ' ').toLowerCase().replace(/^./, c => c.toUpperCase()), value: k }))];

  nodes: TreeNode<CoaAccount>[] = [];
  problem: string | null = null;
  editingKey: string | null = null;
  movingKey: string | null = null;
  moveTarget: string | null = null;
  count = 0;
  private newKeys = new Set<string>();
  private seq = 0;
  /** The last payload this editor emitted, so its own echo does not rebuild the tree. */
  private emitted: any = null;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.nodes = coaToTree(clone(this.payload?.accounts ?? []), 1);
      this.newKeys.clear();
      this.editingKey = null;
      this.movingKey = null;
      this.recount();
      this.problem = null;
    }
  }

  depth(node: TreeNode<CoaAccount>): number {
    let d = 1;
    let p = this.parentOf(node);
    while (p) { d++; p = this.parentOf(p); }
    return d;
  }

  isNew(node: TreeNode<CoaAccount>): boolean { return this.newKeys.has(node.key!); }

  canAddUnder(node: TreeNode<CoaAccount>): boolean { return !!node.data?.is_group && this.depth(node) < COA_MAX_LEVEL; }

  expandAll(open: boolean): void {
    const walk = (ns: TreeNode[]) => ns.forEach(n => { n.expanded = open; walk(n.children ?? []); });
    walk(this.nodes);
    this.nodes = [...this.nodes];
  }

  edit(node: TreeNode<CoaAccount>, ev?: Event): void {
    ev?.stopPropagation?.();
    this.editingKey = node.key!;
  }

  commit(node: TreeNode<CoaAccount>): void {
    node.data!.code = (node.data!.code ?? '').trim();
    node.data!.name = (node.data!.name ?? '').trim();
    node.label = `${node.data!.code} ${node.data!.name}`;
    this.editingKey = null;
    this.changed();
  }

  add(parent: TreeNode<CoaAccount>, ev?: Event): void {
    ev?.stopPropagation?.();
    if (!this.canAddUnder(parent)) return;
    const level = this.depth(parent) + 1;
    const siblings = (parent.children ?? []).map(c => c.data!);
    const code = suggestChildCode(parent.data!, siblings, allCoa(treeToCoa(this.nodes)).map(a => a.code));
    const key = `new-${++this.seq}`;
    const data: CoaAccount = {
      code, name: level < COA_MAX_LEVEL ? 'New group' : 'New account', root_type: parent.data!.root_type, level,
      is_group: level < COA_MAX_LEVEL
    };
    const node: TreeNode<CoaAccount> = {
      key, label: `${code} ${data.name}`, data, draggable: true, droppable: data.is_group, leaf: !data.is_group, children: [], parent
    };
    parent.children = [...(parent.children ?? []), node];
    parent.expanded = true;
    this.newKeys.add(key);
    this.editingKey = key;
    this.nodes = [...this.nodes];
    this.changed();
  }

  remove(node: TreeNode<CoaAccount>, ev?: Event): void {
    ev?.stopPropagation?.();
    const roles = coaRoleKeys(node);
    if (roles.length) {
      this.problem = `${node.data!.code} ${node.data!.name} carries ${roles.join(', ')}. Default roles must stay assigned — keep this account.`;
      return;
    }
    const parent = this.parentOf(node);
    if (!parent) return;
    parent.children = (parent.children ?? []).filter(c => c !== node);
    this.nodes = [...this.nodes];
    this.changed();
  }

  /** Gives the account a role ('' clears it). A role is held by one account, so it moves from its previous holder. */
  setRole(node: TreeNode<CoaAccount>, key: string | null): void {
    if (!node.data || node.data.is_group) return;
    const clear = (n: TreeNode<CoaAccount>) => {
      delete n.data!.default_account_key;
      delete n.data!.is_default_account;
    };
    if (key) {
      const walk = (ns: TreeNode<CoaAccount>[]) => ns.forEach(n => {
        if (n !== node && n.data?.default_account_key === key) clear(n);
        walk(n.children ?? []);
      });
      walk(this.nodes);
      node.data.default_account_key = key;
      node.data.is_default_account = true;
    } else {
      clear(node);
    }
    this.nodes = [...this.nodes];
    this.changed();
  }

  startMove(node: TreeNode<CoaAccount>, ev?: Event): void {
    ev?.stopPropagation?.();
    this.movingKey = node.key!;
    this.moveTarget = null;
  }

  /** Groups at the node's parent level, in the same root, other than the current parent. */
  moveTargets(node: TreeNode<CoaAccount>): { label: string; value: string }[] {
    const parent = this.parentOf(node);
    const want = this.depth(node) - 1;
    const root = this.rootOf(node);
    const out: { label: string; value: string }[] = [];
    const walk = (ns: TreeNode<CoaAccount>[], d: number) => ns.forEach(n => {
      if (d === want && n.data?.is_group && n !== parent) out.push({ label: `${n.data.code} ${n.data.name}`, value: n.key! });
      if (d < want) walk(n.children ?? [], d + 1);
    });
    walk(root ? [root] : [], 1);
    return out;
  }

  move(node: TreeNode<CoaAccount>): void {
    const target = this.find(this.moveTarget);
    const parent = this.parentOf(node);
    if (!target || !parent) return;
    parent.children = (parent.children ?? []).filter(c => c !== node);
    target.children = [...(target.children ?? []), node];
    node.parent = target;
    target.expanded = true;
    this.movingKey = null;
    this.nodes = [...this.nodes];
    this.changed();
  }

  /** p-tree drop (validateDrop): accept, then check the A42 rules on the result and undo when they break. */
  onDrop(event: any): void {
    const before = treeToCoa(this.nodes);
    const keysBefore = new Set(this.newKeys);
    event?.accept?.();
    this.relink();
    const after = treeToCoa(this.nodes);
    const p = coaProblem(after);
    if (p) {
      this.nodes = coaToTree(before, 1);
      this.newKeys = keysBefore;
      this.problem = `That move was undone: ${p}`;
      return;
    }
    this.nodes = [...this.nodes];
    this.changed();
  }

  private changed(): void {
    const accounts = treeToCoa(this.nodes);
    this.problem = coaProblem(accounts);
    this.recount();
    this.emitted = { ...(this.payload ?? {}), accounts };
    this.payloadChange.emit(this.emitted);
  }

  private recount(): void { this.count = allCoa(treeToCoa(this.nodes)).length; }

  /** Re-sets every node's parent after p-tree moved nodes around. */
  private relink(): void {
    const walk = (ns: TreeNode[], parent?: TreeNode) => ns.forEach(n => { n.parent = parent; walk(n.children ?? [], n); });
    walk(this.nodes);
  }

  private parentOf(node: TreeNode<CoaAccount>): TreeNode<CoaAccount> | null {
    let found: TreeNode<CoaAccount> | null = null;
    const walk = (ns: TreeNode<CoaAccount>[], parent: TreeNode<CoaAccount> | null) => {
      for (const n of ns) {
        if (found) return;
        if (n === node) { found = parent; return; }
        walk(n.children ?? [], n);
      }
    };
    walk(this.nodes, null);
    return found;
  }

  private rootOf(node: TreeNode<CoaAccount>): TreeNode<CoaAccount> | null {
    let cur: TreeNode<CoaAccount> | null = node;
    let p = this.parentOf(node);
    while (p) { cur = p; p = this.parentOf(p); }
    return cur;
  }

  private find(key: string | null): TreeNode<CoaAccount> | null {
    if (!key) return null;
    let found: TreeNode<CoaAccount> | null = null;
    const walk = (ns: TreeNode<CoaAccount>[]) => ns.forEach(n => { if (!found) { if (n.key === key) found = n; else walk(n.children ?? []); } });
    walk(this.nodes);
    return found;
  }
}
