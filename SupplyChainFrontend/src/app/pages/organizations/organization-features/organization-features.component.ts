import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FLOW, FlowTone } from '../../../shared/flow';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  OrganizationsService, OrganizationDetailModel, OrganizationFeatureModel, OrganizationFeatureHistoryEntry
} from '../../../services/organizations.service';
import { moduleName } from '../../../services/module.models';

/** One feature row: the model (its checkbox binds `f.isEnabled` = the licence) plus labels computed once per load. */
export interface FeatureRowVm {
  f: OrganizationFeatureModel;
  statusLabel: string;
  statusTone: FlowTone;
  comingSoon: boolean;
  alwaysOn: boolean;
}

/** A module with the screens and features that belong to it (parentModuleCode). `module` is null for "Other". */
export interface FeatureGroupVm {
  key: string;
  title: string;
  module: FeatureRowVm | null;
  items: FeatureRowVm[];
}

export interface FeatureHistoryRow {
  when: string;
  action: string;
  tone: FlowTone;
  target: string;
  by: string;
  graceDays: number | null;
  notes: string | null;
}

const STATUS: Record<string, [string, FlowTone]> = {
  ALWAYS_ON: ['Always on', 'ok'],
  ACTIVE: ['Active', 'ok'],
  GRACE: ['Grace period', 'wn'],
  DISABLED: ['Switched off by org', ''],
  NOT_LICENSED: ['Not licensed', 'er'],
  COMING_SOON: ['Coming soon', 'in']
};

const ACTIONS: Record<string, [string, FlowTone]> = {
  ENABLED: ['Switched on', 'ok'], DISABLED: ['Switched off', 'wn'],
  FEATURE_ENABLED: ['Feature on', 'ok'], FEATURE_DISABLED: ['Feature off', 'wn'],
  LICENSED: ['Licensed', 'in'], UNLICENSED: ['Licence removed', 'er'],
  GRACE_EXPIRED: ['Grace ended', 'er'], AUTO_ENABLED: ['Auto on', 'te'], AUTO_DISABLED: ['Auto off', '']
};

/**
 * Super admin › Organization › Manage features. A37 D-4: ticking a row here grants the **licence** (licensed + enabled
 * together); the organization's own admin then switches licensed modules on and off in Settings › Modules.
 */
@Component({
  selector: 'app-organization-features',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CheckboxModule, TagModule, ToastModule, ConfirmDialogModule, TooltipModule, ...FLOW
  ],
  templateUrl: './organization-features.component.html',
  styleUrls: ['./organization-features.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class OrganizationFeaturesComponent implements OnInit {
  orgId = '';
  org: OrganizationDetailModel | null = null;
  features: OrganizationFeatureModel[] = [];
  isLoading = true;
  savingCode: string | null = null;
  isApplyingTemplate = false;

  tab: 'features' | 'history' = 'features';
  historyRows: FeatureHistoryRow[] = [];
  historyLoading = false;
  private historyLoaded = false;

  constructor(
    private route: ActivatedRoute,
    private orgsService: OrganizationsService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.orgId = this.route.snapshot.paramMap.get('id') ?? '';
    if (this.orgId) this.load();
  }

  load() {
    this.isLoading = true;
    this.orgsService.getById(this.orgId).subscribe({
      next: (res) => { this.org = res.result ?? null; },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organization' })
    });
    this.loadFeatures();
  }

  private loadFeatures() {
    this.orgsService.getFeatures(this.orgId).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.features = res.result ?? [];
        this.groupedFeatures = OrganizationFeaturesComponent.group(this.features);
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load features' });
      }
    });
  }

  /**
   * Built once per load — NOT a getter. As a getter it handed *ngFor new group objects on every change detection,
   * so every section and p-checkbox was destroyed and re-created each pass; each new ngModel schedules another pass,
   * and the page looped until the browser tab ran out of memory ("Aw, Snap!").
   */
  groupedFeatures: FeatureGroupVm[] = [];

  /**
   * A37 — modules in display order, each followed by its own screens/features (parentModuleCode). Rows whose parent is
   * unknown — or the whole list, from a server without parentModuleCode — fall back to the old MODULE/SCREEN/FEATURE
   * category sections.
   */
  static group(features: OrganizationFeatureModel[]): FeatureGroupVm[] {
    const byOrder = (a: OrganizationFeatureModel, b: OrganizationFeatureModel) => (a.displayOrder ?? 0) - (b.displayOrder ?? 0);
    const modules = features.filter(f => f.category === 'MODULE').sort(byOrder);
    const moduleCodes = new Set(modules.map(m => m.featureCode));
    const hasParents = features.some(f => !!f.parentModuleCode);

    const groups: FeatureGroupVm[] = [];
    if (hasParents) {
      for (const m of modules) {
        groups.push({
          key: m.featureCode,
          title: m.featureName,
          module: OrganizationFeaturesComponent.row(m),
          items: features.filter(f => f.category !== 'MODULE' && f.parentModuleCode === m.featureCode).sort(byOrder)
                         .map(f => OrganizationFeaturesComponent.row(f))
        });
      }
    }
    const rest = features.filter(f => hasParents
      ? f.category !== 'MODULE' && !(f.parentModuleCode && moduleCodes.has(f.parentModuleCode))
      : true);
    for (const category of ['MODULE', 'SCREEN', 'FEATURE']) {
      const items = rest.filter(f => f.category === category).sort(byOrder).map(f => OrganizationFeaturesComponent.row(f));
      if (items.length) groups.push({ key: 'cat-' + category, title: OrganizationFeaturesComponent.categoryTitle(category, hasParents), module: null, items });
    }
    return groups;
  }

  static row(f: OrganizationFeatureModel): FeatureRowVm {
    const status = f.status ?? (f.isEnabled ? 'ACTIVE' : 'NOT_LICENSED');
    const [statusLabel, statusTone] = STATUS[status] ?? [status, '' as FlowTone];
    return {
      f,
      statusLabel,
      statusTone,
      comingSoon: f.isAvailable === false,
      alwaysOn: !!f.isAlwaysOn
    };
  }

  private static categoryTitle(category: string, hasParents: boolean): string {
    if (hasParents) return category === 'MODULE' ? 'Other modules' : 'Other screens & features';
    switch (category) {
      case 'MODULE':  return 'Modules';
      case 'SCREEN':  return 'Screens';
      case 'FEATURE': return 'Capabilities';
      default:        return category;
    }
  }

  trackGroup = (_: number, g: FeatureGroupVm) => g.key;
  trackRow = (_: number, r: FeatureRowVm) => r.f.featureCode;

  onToggle(feature: OrganizationFeatureModel, checked: boolean) {
    if (feature.isCore && !checked) {
      // Revert the checkbox — core features can never be disabled. Backend also rejects this,
      // but blocking client-side avoids a round trip and an error toast for an action that can
      // never succeed.
      feature.isEnabled = true;
      this.messageService.add({
        severity: 'warn', summary: 'Core Feature',
        detail: `"${feature.featureName}" is a core feature and cannot be disabled.`
      });
      return;
    }

    this.savingCode = feature.featureCode;
    this.orgsService.updateFeatures(this.orgId, [{ featureCode: feature.featureCode, isEnabled: checked }]).subscribe({
      next: (res) => {
        this.savingCode = null;
        const result = res.result;
        if (!res.success || !result) {
          feature.isEnabled = !checked;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update feature' });
          return;
        }
        if (result.autoEnabledDependencies?.length) {
          this.messageService.add({
            severity: 'info', summary: 'Dependency Licensed',
            detail: `${result.autoEnabledDependencies.join(', ')} was also licensed — required by "${feature.featureName}".`
          });
        } else {
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: `"${feature.featureName}" ${checked ? 'licensed' : 'licence removed'}` });
        }
        // Statuses, dependencies and history change with the licence — re-read them.
        this.historyLoaded = false;
        this.loadFeatures();
      },
      error: (err) => {
        this.savingCode = null;
        feature.isEnabled = !checked;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update feature' });
      }
    });
  }

  showTab(tab: 'features' | 'history') {
    this.tab = tab;
    if (tab === 'history' && !this.historyLoaded) this.loadHistory();
  }

  loadHistory() {
    const names = new Map(this.features.map(f => [f.featureCode, f.featureName] as [string, string]));
    this.historyLoading = true;
    this.orgsService.getFeatureHistory(this.orgId).subscribe({
      next: (res) => {
        this.historyLoading = false;
        this.historyLoaded = true;
        this.historyRows = (res.result ?? []).map(e => OrganizationFeaturesComponent.historyRow(e, names));
      },
      error: (err) => {
        this.historyLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load history' });
      }
    });
  }

  static historyRow(e: OrganizationFeatureHistoryEntry, names: Map<string, string>): FeatureHistoryRow {
    const [action, tone] = ACTIONS[e.action] ?? [e.action, '' as FlowTone];
    const code = e.featureCode ?? '';
    return {
      when: e.performedAt,
      action,
      tone,
      target: names.get(code) ?? moduleName(code),
      by: e.performedByName || 'System',
      graceDays: e.graceDays ?? null,
      notes: e.notes ?? null
    };
  }

  confirmApplyPlanTemplate() {
    this.confirmationService.confirm({
      message: `Reset all feature toggles to the defaults for the <strong>${this.org?.plan}</strong> plan? Any manual overrides will be lost.`,
      header: 'Reset to Plan Defaults',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Reset',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.isApplyingTemplate = true;
        this.orgsService.applyPlanTemplate(this.orgId).subscribe({
          next: () => {
            this.isApplyingTemplate = false;
            this.messageService.add({ severity: 'success', summary: 'Reset', detail: 'Feature toggles reset to plan defaults.' });
            this.historyLoaded = false;
            this.load();
          },
          error: (err) => {
            this.isApplyingTemplate = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Reset failed' });
          }
        });
      }
    });
  }

  planSeverity(plan?: string): 'success' | 'info' | 'warn' {
    switch (plan) {
      case 'ENTERPRISE': return 'success';
      case 'STANDARD':   return 'info';
      default:           return 'warn';
    }
  }
}
