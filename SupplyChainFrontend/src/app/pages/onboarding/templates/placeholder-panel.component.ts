import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { InputTextModule } from 'primeng/inputtext';
import { TemplatePlaceholder, normalizeToken } from '../../../services/onboarding-templates.service';

interface Group { name: string; items: TemplatePlaceholder[]; }

/**
 * A50 task 308: the `{placeholders}` a template type may use, grouped, with a search box. Clicking one inserts it
 * where the cursor is (the host decides into which field).
 */
@Component({
  selector: 'app-placeholder-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, InputTextModule],
  template: `
    <section class="sf-card php" data-testid="placeholder-panel">
      <div class="sf-ch"><h2>Placeholders</h2></div>
      <p class="php-hint">Click one to insert it at the cursor{{ target ? ' in ' + target : '' }}. Each is replaced with the document's data.</p>
      <span class="sf-search php-search">
        <input pInputText type="search" [(ngModel)]="query" placeholder="Search placeholders"
               aria-label="Search placeholders" data-testid="placeholder-search" />
      </span>
      @if (loading) {
        <div class="php-mu"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      } @else if (error) {
        <div class="php-err" data-testid="placeholder-error">{{ error }}</div>
      } @else if (!groups.length) {
        <div class="php-mu" data-testid="placeholder-empty">{{ query ? 'No placeholder matches.' : 'This type has no placeholders.' }}</div>
      } @else {
        @for (g of groups; track g.name) {
          <div class="php-group">
            <div class="php-gname">{{ g.name }}</div>
            @for (p of g.items; track p.token) {
              <button type="button" class="php-item" (mousedown)="$event.preventDefault()" (click)="pick(p)"
                      [attr.data-testid]="'ph-' + bare(p.token)" [title]="p.description || p.label || p.token">
                <code>{{ token(p) }}</code>
                <span class="php-lbl">{{ p.label || '' }}@if (p.structural) { <span class="sf-pill in">block</span> }</span>
              </button>
            }
          </div>
        }
      }
    </section>
  `,
  styles: [`
    .php { display: flex; flex-direction: column; gap: .6rem; }
    .php-hint { margin: 0; font-size: .78rem; color: var(--sms-text-muted); line-height: 1.45; }
    .php-search { width: 100%; max-width: none; flex: none; }
    .php-search input { width: 100%; }
    .php-mu { font-size: .82rem; color: var(--sms-text-faint); }
    .php-err { font-size: .82rem; color: var(--sms-danger); }
    .php-group { display: flex; flex-direction: column; gap: .15rem; }
    .php-gname { font-size: .68rem; text-transform: uppercase; letter-spacing: .05em; font-weight: 700; color: var(--sms-text-faint); margin-top: .35rem; }
    .php-item { display: flex; align-items: baseline; gap: .5rem; text-align: left; border: 0; background: transparent; padding: .3rem .4rem;
                border-radius: 6px; cursor: pointer; color: var(--sms-text); font: inherit; width: 100%; flex-wrap: wrap; }
    .php-item:hover, .php-item:focus-visible { background: var(--sms-row-hover); outline: none; }
    .php-item code { font-size: .78rem; color: var(--sms-primary-text); background: var(--sms-primary-soft); padding: .05rem .3rem; border-radius: 4px; }
    .php-lbl { font-size: .76rem; color: var(--sms-text-muted); }
    .php-lbl .sf-pill { font-size: .62rem; margin-left: .3rem; }
  `]
})
export class PlaceholderPanelComponent {
  @Input() placeholders: TemplatePlaceholder[] = [];
  @Input() loading = false;
  @Input() error: string | null = null;
  /** "the body", "the subject" — where a click will insert. */
  @Input() target = '';
  @Output() insert = new EventEmitter<string>();

  query = '';

  get groups(): Group[] {
    const q = this.query.trim().toLowerCase();
    const list = (this.placeholders ?? []).filter(p =>
      !q || [p.token, p.label, p.description, p.group].some(v => (v ?? '').toLowerCase().includes(q)));
    const map = new Map<string, TemplatePlaceholder[]>();
    for (const p of list) {
      const g = p.group || 'General';
      if (!map.has(g)) map.set(g, []);
      map.get(g)!.push(p);
    }
    return [...map.entries()].map(([name, items]) => ({ name, items }));
  }

  token(p: TemplatePlaceholder): string { return normalizeToken(p.token); }
  bare(t: string): string { return normalizeToken(t).slice(1, -1); }

  pick(p: TemplatePlaceholder): void { this.insert.emit(this.token(p)); }
}
