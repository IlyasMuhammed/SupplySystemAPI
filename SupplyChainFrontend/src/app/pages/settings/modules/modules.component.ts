import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DrawerModule } from 'primeng/drawer';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { FLOW, FlowTone } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';
import { ModuleAdminService, ModuleService } from '../../../services/module.service';
import { ModuleCard, ModuleFeature, ModuleHistoryEntry, ModuleImpact, daysLeft, moduleName } from '../../../services/module.models';

export interface FeatureVm {
  f: ModuleFeature;
  /** Bound to the switch; set back on refusal so the switch flips back. */
  on: boolean;
  disabled: boolean;
  locked: boolean;
  availability: string;
  availabilityTone: FlowTone;
  note: string | null;
  requiresName: string | null;
  requiredByNames: string[];
}

export interface CardVm {
  card: ModuleCard;
  on: boolean;
  /** Has an on/off switch at all (not core, not coming soon, licensed). */
  toggleable: boolean;
  badge: string;
  tone: FlowTone;
  requires: string;
  featuresText: string;
  graceDaysLeft: number | null;
  expanded: boolean;
  features: FeatureVm[];
}

export interface SectionVm {
  key: 'core' | 'active' | 'available' | 'soon';
  title: string;
  sub: string;
  cards: CardVm[];
}

export interface HistoryRow {
  when: string;
  action: string;
  tone: FlowTone;
  target: string | null;
  by: string;
  graceDays: number | null;
  notes: string | null;
}

const ACTION_LABELS: Record<string, [string, FlowTone]> = {
  ENABLED: ['Switched on', 'ok'],
  DISABLED: ['Switched off', 'wn'],
  FEATURE_ENABLED: ['Feature switched on', 'ok'],
  FEATURE_DISABLED: ['Feature switched off', 'wn'],
  LICENSED: ['Added to plan', 'in'],
  UNLICENSED: ['Removed from plan', 'er'],
  GRACE_EXPIRED: ['Grace period ended', 'er'],
  AUTO_ENABLED: ['Switched on automatically', 'te'],
  AUTO_DISABLED: ['Switched off automatically', '']
};

/**
 * A37 Settings › Modules (spec §11.3 / §15.1, API-CONTRACT §1.1). The org admin switches licensed modules and their
 * features on and off; licensing stays with the super admin (D-4). View models are built once per load — never in a
 * getter (a getter handing *ngFor new objects every pass re-creates every switch and loops change detection).
 */
@Component({
  selector: 'app-modules',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, DialogModule, DrawerModule, InputNumberModule, TextareaModule,
    ToastModule, ToggleSwitchModule, TooltipModule, ...FLOW
  ],
  templateUrl: './modules.component.html',
  styleUrls: ['./modules.component.scss'],
  providers: [MessageService]
})
export class ModulesComponent implements OnInit {
  cards: ModuleCard[] = [];
  sections: SectionVm[] = [];
  isLoading = true;
  /** Code of the module an action is running for (its switches are disabled meanwhile). */
  busy: string | null = null;
  canManage = false;
  orgName = '';

  // ── Disable dialog ──────────────────────────────────────────────────────
  showDisable = false;
  disableVm: CardVm | null = null;
  impact: ModuleImpact | null = null;
  graceDays: number | null = 30;
  notes = '';

  // ── Blocking dialog (enabled dependents) ────────────────────────────────
  showBlocked = false;
  blockedName = '';
  blockedBy: { code: string; name: string }[] = [];

  // ── History drawer ──────────────────────────────────────────────────────
  showHistory = false;
  historyName = '';
  historyLoading = false;
  historyRows: HistoryRow[] = [];

  private expanded = new Set<string>();

  constructor(
    private api: ModuleAdminService,
    private modules: ModuleService,
    private auth: AuthService,
    private tenant: TenantService,
    private messages: MessageService
  ) {}

  ngOnInit(): void {
    this.canManage = this.auth.hasPermission('MODULES_MANAGE');
    this.orgName = this.tenant.tenant()?.orgName ?? '';
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.api.list().subscribe({
      next: res => {
        this.isLoading = false;
        this.cards = res?.result ?? [];
        this.build();
      },
      error: err => {
        this.isLoading = false;
        this.toastError(err, 'Could not load the modules.');
      }
    });
  }

  get activeCount(): number {
    return this.cards.filter(c => c.status === 'ACTIVE' || c.status === 'ALWAYS_ON').length;
  }

  // ── View models ─────────────────────────────────────────────────────────

  private build(): void {
    const names = new Map<string, string>();
    for (const c of this.cards) {
      names.set(c.code, c.name);
      for (const f of c.features ?? []) names.set(f.code, f.name);
    }
    const nameOf = (code: string) => names.get(code) ?? moduleName(code);

    const vms = this.cards.map(c => this.cardVm(c, nameOf));
    const pick = (...statuses: string[]) => vms.filter(v => statuses.includes(v.card.status));
    this.sections = ([
      { key: 'core', title: 'Core modules', sub: 'Always on — the base every other module builds on.', cards: pick('ALWAYS_ON') },
      { key: 'active', title: 'Active modules', sub: 'Switched on for your organization.', cards: pick('ACTIVE') },
      { key: 'available', title: 'Available modules', sub: 'Switched off, or not included in your plan.', cards: pick('GRACE', 'DISABLED', 'NOT_LICENSED') },
      { key: 'soon', title: 'Coming soon', sub: 'Planned modules — not available yet.', cards: pick('COMING_SOON') }
    ] as SectionVm[]).filter(s => s.cards.length > 0);
  }

  private cardVm(c: ModuleCard, nameOf: (code: string) => string): CardVm {
    const graceDaysLeft = c.status === 'GRACE' ? daysLeft(c.graceEndsAt) : null;
    const [badge, tone] = this.badgeFor(c, graceDaysLeft);
    const deps = c.dependsOn ?? [];
    return {
      card: c,
      on: c.isEnabled,
      toggleable: !c.isAlwaysOn && c.status !== 'ALWAYS_ON' && c.status !== 'COMING_SOON' && c.status !== 'NOT_LICENSED',
      badge,
      tone,
      requires: deps.length ? 'Requires: ' + deps.map(d => d.name + (d.isEnabled ? '' : ' (off)')).join(', ') : '',
      featuresText: c.featureCount ? `${c.enabledFeatureCount} of ${c.featureCount} features` : '',
      graceDaysLeft,
      expanded: this.expanded.has(c.code),
      features: (c.features ?? []).map(f => this.featureVm(c, f, nameOf))
    };
  }

  private badgeFor(c: ModuleCard, graceDaysLeft: number | null): [string, FlowTone] {
    switch (c.status) {
      case 'ALWAYS_ON': return ['Always active', 'ok'];
      case 'ACTIVE': return ['Active', 'ok'];
      case 'GRACE': return [`Grace period · ${graceDaysLeft} day${graceDaysLeft === 1 ? '' : 's'} left`, 'wn'];
      case 'NOT_LICENSED': return ['Not in your plan', ''];
      case 'COMING_SOON': return ['Coming soon', 'in'];
      default: return ['Disabled', ''];
    }
  }

  private featureVm(c: ModuleCard, f: ModuleFeature, nameOf: (code: string) => string): FeatureVm {
    let availability = 'Active';
    let availabilityTone: FlowTone = 'ok';
    let note: string | null = null;
    if (!f.isAvailable) { availability = 'Coming soon'; availabilityTone = 'in'; }
    else if (!f.isLicensed) { availability = 'Not in your plan'; availabilityTone = ''; }
    else if (!f.isEnabled) { availability = 'Off'; availabilityTone = ''; }

    if (f.isCore) note = 'Core feature — always on.';
    else if (f.autoManaged) note = 'Switched on and off automatically with Manufacturing and Services; switching it here overrides that.';
    else if (f.isAvailable && f.isLicensed && !c.isEnabled) note = `Switch on ${c.name} first.`;

    const coming = c.status === 'COMING_SOON';
    return {
      f,
      on: f.isCore || f.isEnabled,
      locked: f.isCore,
      disabled: !this.canManage || f.isCore || coming || !f.isAvailable || !f.isLicensed || !c.isEnabled,
      availability,
      availabilityTone,
      note,
      requiresName: f.requiresCode ? nameOf(f.requiresCode) : null,
      requiredByNames: (f.requiredBy ?? []).map(nameOf)
    };
  }

  toggleExpanded(vm: CardVm): void {
    vm.expanded = !vm.expanded;
    if (vm.expanded) this.expanded.add(vm.card.code);
    else this.expanded.delete(vm.card.code);
  }

  // ── Module on / off ─────────────────────────────────────────────────────

  onToggle(vm: CardVm, value: boolean): void {
    vm.on = value;
    if (!this.canManage || !vm.toggleable) { vm.on = !value; return; }
    if (value) this.enable(vm);
    else this.askDisable(vm);
  }

  private enable(vm: CardVm): void {
    this.busy = vm.card.code;
    this.api.enable(vm.card.code, vm.card.rowVersion).subscribe({
      next: res => {
        this.busy = null;
        if (res?.success === false) { vm.on = false; this.toastText(res.message); return; }
        this.messages.add({ severity: 'success', summary: 'Module switched on', detail: `${vm.card.name} is switched on.` });
        this.afterChange();
      },
      error: err => {
        this.busy = null;
        vm.on = false;
        this.handleActionError(err);
      }
    });
  }

  private askDisable(vm: CardVm): void {
    this.busy = vm.card.code;
    this.api.impact(vm.card.code).subscribe({
      next: res => {
        this.busy = null;
        const impact = res?.result;
        if (!impact) { vm.on = true; this.toastText(res?.message || 'Could not check what switching it off affects.'); return; }
        if (impact.dependents?.length) {
          vm.on = true;
          this.blockedName = vm.card.name;
          this.blockedBy = impact.dependents;
          this.showBlocked = true;
          return;
        }
        this.disableVm = vm;
        this.impact = impact;
        this.graceDays = impact.defaultGraceDays ?? 30;
        this.notes = '';
        this.showDisable = true;
      },
      error: err => {
        this.busy = null;
        vm.on = true;
        this.handleActionError(err);
      }
    });
  }

  get blockedNames(): string {
    return this.blockedBy.map(d => d.name).join(', ');
  }

  get graceValid(): boolean {
    const g = this.graceDays;
    return g !== null && g !== undefined && Number.isInteger(g) && g >= 0 && g <= 365;
  }

  /** Closing the dialog any way other than "Disable module" leaves the module on. */
  cancelDisable(): void {
    if (this.disableVm && this.showDisable) this.disableVm.on = true;
    this.showDisable = false;
    this.disableVm = null;
    this.impact = null;
  }

  onDisableHide(): void {
    if (this.disableVm) this.disableVm.on = true;
    this.disableVm = null;
    this.impact = null;
  }

  confirmDisable(): void {
    const vm = this.disableVm;
    if (!vm || !this.graceValid) return;
    this.busy = vm.card.code;
    const notes = this.notes.trim();
    this.api.disable(vm.card.code, {
      graceDays: this.graceDays!, notes: notes || undefined, rowVersion: vm.card.rowVersion
    }).subscribe({
      next: res => {
        this.busy = null;
        if (res?.success === false) { this.toastText(res.message); return; }
        this.disableVm = null;          // keep the switch off when the dialog hides
        this.showDisable = false;
        this.impact = null;
        this.messages.add({
          severity: 'success', summary: 'Module switched off',
          detail: this.graceDays ? `${vm.card.name} is switched off; open records can be finished for ${this.graceDays} days.`
                                 : `${vm.card.name} is switched off.`
        });
        this.afterChange();
      },
      error: err => {
        this.busy = null;
        if (err instanceof HttpErrorResponse && err.status === 409) {
          this.disableVm = null;
          this.showDisable = false;
          this.impact = null;
        }
        this.handleActionError(err);
      }
    });
  }

  // ── Features ────────────────────────────────────────────────────────────

  onFeatureToggle(vm: CardVm, fvm: FeatureVm, value: boolean): void {
    fvm.on = value;
    if (fvm.disabled) { fvm.on = !value; return; }
    this.busy = vm.card.code;
    this.api.setFeature(vm.card.code, fvm.f.code, value, vm.card.rowVersion).subscribe({
      next: res => {
        this.busy = null;
        if (res?.success === false) { fvm.on = !value; this.toastText(res.message); return; }
        this.messages.add({
          severity: 'success', summary: value ? 'Feature switched on' : 'Feature switched off',
          detail: `${fvm.f.name} is switched ${value ? 'on' : 'off'}.`
        });
        this.afterChange();
      },
      error: err => {
        this.busy = null;
        fvm.on = !value;
        this.handleActionError(err);
      }
    });
  }

  // ── History ─────────────────────────────────────────────────────────────

  openHistory(vm: CardVm): void {
    const names = new Map<string, string>((vm.card.features ?? []).map(f => [f.code, f.name] as [string, string]));
    this.historyName = vm.card.name;
    this.historyRows = [];
    this.historyLoading = true;
    this.showHistory = true;
    this.api.history(vm.card.code).subscribe({
      next: res => {
        this.historyLoading = false;
        this.historyRows = (res?.result ?? []).map(e => ModulesComponent.historyRow(e, names));
      },
      error: err => {
        this.historyLoading = false;
        this.toastError(err, 'Could not load the history.');
      }
    });
  }

  static historyRow(e: ModuleHistoryEntry, names: Map<string, string>): HistoryRow {
    const [action, tone] = ACTION_LABELS[e.action] ?? [e.action, '' as FlowTone];
    return {
      when: e.performedAt,
      action,
      tone,
      target: e.featureCode ? (names.get(e.featureCode) ?? moduleName(e.featureCode)) : null,
      by: e.performedByName || 'System',
      graceDays: e.graceDays ?? null,
      notes: e.notes ?? null
    };
  }

  // ── Plumbing ────────────────────────────────────────────────────────────

  /** After any change: reload the cards (dependents, counts, row versions) and the app-wide enabled set (menu). */
  private afterChange(): void {
    this.load();
    this.modules.refresh();
  }

  /** 409 → someone else changed it: reload and say so. 400 → the server's own sentence, verbatim. */
  private handleActionError(err: unknown): void {
    if (err instanceof HttpErrorResponse && err.status === 409) {
      this.messages.add({
        severity: 'warn', summary: 'Changed elsewhere',
        detail: 'This module was changed by someone else in the meantime. The page has been reloaded — please try again.'
      });
      this.load();
      return;
    }
    this.toastError(err, 'Could not update the module.');
  }

  private toastError(err: unknown, fallback: string): void {
    const message = err instanceof HttpErrorResponse ? err.error?.message : undefined;
    this.toastText(message || fallback);
  }

  private toastText(detail: string | null | undefined): void {
    this.messages.add({ severity: 'error', summary: 'Not changed', detail: detail || 'The request was refused.' });
  }

  trackSection = (_: number, s: SectionVm) => s.key;
  trackCard = (_: number, v: CardVm) => v.card.code;
  trackFeature = (_: number, v: FeatureVm) => v.f.code;
}
