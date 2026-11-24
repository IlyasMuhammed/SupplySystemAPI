import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { clone } from '../../../onboarding/onboarding.shared';

/** One node of the PRODUCT_CATEGORIES payload (`categories[]`, children nest). Deeper than two levels are flattened by the seeder (R-2). */
export interface CategoryNode { code: string; name: string; description?: string | null; children?: CategoryNode[]; }

export const CATEGORY_MAX_DEPTH = 2;
const CODE_RE = /^[A-Za-z0-9][A-Za-z0-9_.\-]*$/;

/** Every node, depth first. */
export function allCategories(nodes: CategoryNode[] | null | undefined): CategoryNode[] {
  return (nodes ?? []).flatMap(n => [n, ...allCategories(n.children)]);
}

/** The first thing wrong with the tree (mirrors the PRODUCT_CATEGORIES schema and the seeder's natural key = code), or null. */
export function categoryProblem(nodes: CategoryNode[] | null | undefined): string | null {
  const seen = new Set<string>();
  const check = (list: CategoryNode[], depth: number): string | null => {
    for (const n of list) {
      const code = (n.code ?? '').trim();
      if (!code) return 'Every category needs a code.';
      if (code.length > 20 || !CODE_RE.test(code)) return `Code "${code}": letters, digits, - _ . only, at most 20 characters.`;
      if (!(n.name ?? '').trim()) return `Category ${code} needs a name.`;
      if (seen.has(code.toLowerCase())) return `Code "${code}" is used twice.`;
      seen.add(code.toLowerCase());
      if (depth > CATEGORY_MAX_DEPTH) return `Category ${code} is deeper than ${CATEGORY_MAX_DEPTH} levels.`;
      const r = check(n.children ?? [], depth + 1);
      if (r) return r;
    }
    return null;
  };
  if (!nodes?.length) return 'Add at least one category.';
  return check(nodes, 1);
}

/** Removes `node` from wherever it is and appends it under `parent` (null = top level). Refuses a move under itself or too deep. */
export function moveCategory(roots: CategoryNode[], node: CategoryNode, parent: CategoryNode | null): boolean {
  if (parent && (parent === node || allCategories(node.children).includes(parent))) return false;
  const subDepth = (n: CategoryNode): number => 1 + Math.max(0, ...(n.children ?? []).map(subDepth));
  if (subDepth(node) + (parent ? 1 : 0) > CATEGORY_MAX_DEPTH) return false;
  const detach = (list: CategoryNode[]): boolean => {
    const i = list.indexOf(node);
    if (i >= 0) { list.splice(i, 1); return true; }
    return list.some(n => n.children ? detach(n.children) : false);
  };
  if (!detach(roots)) return false;
  if (parent) { parent.children = [...(parent.children ?? []), node]; } else roots.push(node);
  return true;
}

/**
 * A51 P5 — PRODUCT_CATEGORIES as an editable tree: add (a category, or a sub-category under a top-level one), rename (code, name,
 * description), remove (with its children) and "Move under…" (any other top-level category, or the top level). The seeder
 * keeps two levels, so a sub-category cannot have children. Emits the whole payload on every change.
 */
@Component({
  selector: 'app-product-categories-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, SelectModule],
  styles: [`
    .pc-row { display:flex; gap:.5rem; align-items:center; padding:.25rem 0; flex-wrap:wrap; }
    .pc-sub { margin-left:1.75rem; border-left:2px solid var(--sms-border, #d9dde3); padding-left:.75rem; }
    .pc-code { font-family: ui-monospace, monospace; min-width:5rem; font-weight:600; }
    .pc-in { max-width:16rem; }
    .pc-note { color: var(--sms-muted, #6b7280); font-size:.85rem; }
  `],
  template: `
<div data-testid="pc-editor">
  <div class="pc-row">
    <span class="pc-note" data-testid="pc-count">{{ count }} categories</span>
    <span style="flex:1"></span>
    <p-button label="Add category" icon="pi pi-plus" size="small" [text]="true" (onClick)="addRoot()" data-testid="pc-add-root"></p-button>
  </div>
  <div class="pc-note" style="margin-bottom:.5rem">Two levels: categories and their sub-categories. The code is the key, so keep it stable.</div>
  <div class="ob-note er" *ngIf="problem" data-testid="pc-problem"><i class="pi pi-exclamation-triangle"></i><div>{{ problem }}</div></div>
  <div class="sf-card sf-empty" *ngIf="!roots.length" data-testid="pc-empty">No categories yet.</div>

  <ng-template #nodeTpl let-node let-depth="depth">
    <div class="pc-row" [attr.data-testid]="'pc-node-' + node.code">
      <ng-container *ngIf="editing !== node; else editTpl">
        <span class="pc-code">{{ node.code }}</span>
        <span>{{ node.name }}</span>
        <span class="pc-note" *ngIf="node.description">{{ node.description }}</span>
        <span style="flex:1"></span>
        <p-button icon="pi pi-pencil" [text]="true" size="small" ariaLabel="Edit" (onClick)="edit(node)" [attr.data-testid]="'pc-edit-' + node.code"></p-button>
        <p-button *ngIf="depth < maxDepth" icon="pi pi-plus" [text]="true" size="small" ariaLabel="Add sub-category" (onClick)="addChild(node)"
                  [attr.data-testid]="'pc-add-' + node.code"></p-button>
        <p-button icon="pi pi-arrows-alt" [text]="true" size="small" ariaLabel="Move under" (onClick)="startMove(node)" [attr.data-testid]="'pc-move-' + node.code"></p-button>
        <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" ariaLabel="Remove" (onClick)="remove(node)" [attr.data-testid]="'pc-remove-' + node.code"></p-button>
      </ng-container>
      <ng-template #editTpl>
        <input pInputText class="pc-in" style="max-width:8rem" [(ngModel)]="node.code" maxlength="20" aria-label="Category code" data-testid="pc-edit-code" />
        <input pInputText class="pc-in" [(ngModel)]="node.name" maxlength="200" aria-label="Category name" data-testid="pc-edit-name" />
        <input pInputText class="pc-in" [(ngModel)]="node.description" maxlength="500" aria-label="Description" placeholder="Description" />
        <p-button icon="pi pi-check" size="small" [text]="true" ariaLabel="Done" (onClick)="commit()" data-testid="pc-commit"></p-button>
      </ng-template>
    </div>
    <div class="pc-row" *ngIf="moving === node">
      <p-select [options]="moveTargets(node)" optionLabel="label" optionValue="value" [(ngModel)]="moveTarget" placeholder="Move under…" appendTo="body"
                [style]="{ minWidth: '14rem' }" data-testid="pc-move-target"></p-select>
      <p-button label="Move" size="small" [disabled]="moveTarget === undefined" (onClick)="move(node)" data-testid="pc-move-go"></p-button>
      <p-button label="Cancel" size="small" [text]="true" (onClick)="moving = null"></p-button>
    </div>
    <div class="pc-sub" *ngIf="node.children?.length">
      <ng-container *ngFor="let child of node.children">
        <ng-container *ngTemplateOutlet="nodeTpl; context: { $implicit: child, depth: depth + 1 }"></ng-container>
      </ng-container>
    </div>
  </ng-template>
  <ng-container *ngFor="let n of roots">
    <ng-container *ngTemplateOutlet="nodeTpl; context: { $implicit: n, depth: 1 }"></ng-container>
  </ng-container>
</div>`
})
export class ProductCategoriesEditorComponent implements OnChanges {
  /** The PRODUCT_CATEGORIES payload ({ categories: [...] }). A copy is edited. */
  @Input() payload: any;
  @Input() readonly = false;
  @Output() payloadChange = new EventEmitter<any>();

  readonly maxDepth = CATEGORY_MAX_DEPTH;
  roots: CategoryNode[] = [];
  problem: string | null = null;
  count = 0;
  editing: CategoryNode | null = null;
  moving: CategoryNode | null = null;
  /** The chosen parent's code; '' = the top level; undefined = nothing chosen yet. */
  moveTarget: string | undefined;
  private emitted: any = null;
  private seq = 0;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['payload'] && this.payload !== this.emitted) {
      this.roots = clone(this.payload?.categories ?? []);
      this.editing = null;
      this.moving = null;
      this.problem = null;
      this.count = allCategories(this.roots).length;
    }
  }

  addRoot(): void {
    const node: CategoryNode = { code: this.freshCode('CAT'), name: 'New category', description: null, children: [] };
    this.roots = [...this.roots, node];
    this.editing = node;
    this.changed();
  }

  addChild(parent: CategoryNode): void {
    const node: CategoryNode = { code: this.freshCode(parent.code + '-'), name: 'New sub-category', description: null, children: [] };
    parent.children = [...(parent.children ?? []), node];
    this.editing = node;
    this.changed();
  }

  edit(node: CategoryNode): void { this.editing = node; }

  commit(): void {
    if (this.editing) {
      this.editing.code = (this.editing.code ?? '').trim();
      this.editing.name = (this.editing.name ?? '').trim();
      if (!this.editing.description) this.editing.description = null;
    }
    this.editing = null;
    this.changed();
  }

  remove(node: CategoryNode): void {
    const strip = (list: CategoryNode[]): CategoryNode[] =>
      list.filter(n => n !== node).map(n => (n.children ? { ...n, children: strip(n.children) } : n));
    this.roots = strip(this.roots);
    if (this.editing === node) this.editing = null;
    this.changed();
  }

  startMove(node: CategoryNode): void { this.moving = node; this.moveTarget = undefined; }

  /** The top level, plus every top-level category that is not the node (or its descendant) and leaves the tree within the depth limit. */
  moveTargets(node: CategoryNode): { label: string; value: string }[] {
    const out: { label: string; value: string }[] = [{ label: '(top level)', value: '' }];
    for (const r of this.roots) {
      if (r === node) continue;
      const probe = clone(this.roots);
      const pn = allCategories(probe).find(n => n.code === node.code);
      const pp = probe.find(n => n.code === r.code) ?? null;
      if (pn && pp && moveCategory(probe, pn, pp)) out.push({ label: `${r.code} ${r.name}`, value: r.code });
    }
    return out;
  }

  move(node: CategoryNode): void {
    if (this.moveTarget === undefined) return;
    const parent = this.moveTarget === '' ? null : this.roots.find(r => r.code === this.moveTarget) ?? null;
    if (!moveCategory(this.roots, node, parent)) { this.problem = 'That move would break the two-level limit.'; return; }
    this.roots = [...this.roots];
    this.moving = null;
    this.changed();
  }

  private freshCode(prefix: string): string {
    const used = new Set(allCategories(this.roots).map(n => n.code.toLowerCase()));
    let code = '';
    do { code = `${prefix}${++this.seq}`.slice(0, 20); } while (used.has(code.toLowerCase()));
    return code;
  }

  private changed(): void {
    this.problem = categoryProblem(this.roots);
    this.count = allCategories(this.roots).length;
    this.emitted = { ...(this.payload ?? {}), categories: clone(this.roots) };
    this.payloadChange.emit(this.emitted);
  }
}
