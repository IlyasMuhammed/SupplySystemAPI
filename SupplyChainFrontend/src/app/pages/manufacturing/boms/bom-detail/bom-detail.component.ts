import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { MessageModule } from 'primeng/message';
import { TableModule } from 'primeng/table';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  BomComparison, BomCost, BomDetail, BomService, BomVersion, bomStatusSeverity
} from '../../../../services/bom.service';
import { AuthService } from '../../../service/auth.service';
import { ApiResponse } from '../../../../services/inventory.service';

/**
 * A30 §29.2 — one recipe: its inputs, the approval walk (submit → approve → activate, or reject),
 * its cost rolled up from what the inputs cost, its versions and what changed between them.
 */
@Component({
  selector: 'app-bom-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, ConfirmDialogModule, DialogModule, DropdownModule, MessageModule, TableModule, TabViewModule, TagModule,
    TextareaModule, ToastModule, TooltipModule
  ],
  templateUrl: './bom-detail.component.html',
  styleUrls: ['./bom-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class BomDetailComponent implements OnInit {
  readonly severity = bomStatusSeverity;

  uuid = '';
  bom: BomDetail | null = null;
  isLoading = true;
  loadFailed = false;
  busy = false;

  activeTab = 0;
  cost: BomCost | null = null;
  isLoadingCost = false;
  versions: BomVersion[] = [];
  isLoadingVersions = false;
  compareWith: string | null = null;
  comparison: BomComparison | null = null;
  isComparing = false;

  rejectVisible = false;
  rejectReason = '';
  obsoleteVisible = false;
  obsoleteReason = '';

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private service: BomService,
    private authService: AuthService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  // ── Permissions × status ──────────────────────────────────────────────────

  private has(code: string): boolean { return this.authService.hasPermission(code); }
  private is(...statuses: string[]): boolean { return !!this.bom && statuses.includes(this.bom.status); }

  get canEdit(): boolean     { return this.has('BOM_EDIT')     && this.is('DRAFT', 'REJECTED'); }
  get canDelete(): boolean   { return this.has('BOM_EDIT')     && this.is('DRAFT', 'REJECTED'); }
  get canSubmit(): boolean   { return this.has('BOM_SUBMIT')   && this.is('DRAFT', 'REJECTED') && (this.bom?.lines.length ?? 0) > 0; }
  get canApprove(): boolean  { return this.has('BOM_APPROVE')  && this.is('SUBMITTED'); }
  get canActivate(): boolean { return this.has('BOM_ACTIVATE') && this.is('APPROVED'); }
  get canObsolete(): boolean { return this.has('BOM_OBSOLETE') && this.is('ACTIVE', 'APPROVED'); }
  get canVersion(): boolean  { return this.has('BOM_CREATE')   && this.is('APPROVED', 'ACTIVE', 'OBSOLETE', 'REJECTED'); }

  get compareOptions(): { label: string; value: string }[] {
    return this.versions.filter(v => v.uuid !== this.uuid).map(v => ({ label: `v${v.version} — ${v.bomNumber} (${v.status})`, value: v.uuid }));
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getBom(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.bom = res.result ?? null;
        if (!this.bom) this.loadFailed = true;
        this.cost = null;
        this.versions = [];
        this.comparison = null;
        if (this.activeTab === 1) this.loadCost();
        if (this.activeTab === 2) this.loadVersions();
      },
      error: () => { this.isLoading = false; this.loadFailed = true; }
    });
  }

  onTabChange(index: number): void {
    this.activeTab = index;
    if (index === 1 && !this.cost) this.loadCost();
    if (index === 2 && this.versions.length === 0) this.loadVersions();
  }

  loadCost(): void {
    this.isLoadingCost = true;
    this.service.getCost(this.uuid).subscribe({
      next: (res) => { this.isLoadingCost = false; this.cost = res.result ?? null; },
      error: () => { this.isLoadingCost = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The cost could not be calculated.' }); }
    });
  }

  loadVersions(): void {
    if (!this.bom) return;
    this.isLoadingVersions = true;
    this.service.getVersions(this.bom.productUuid, this.bom.productVariantUuid).subscribe({
      next: (res) => { this.isLoadingVersions = false; this.versions = res.result ?? []; },
      error: () => { this.isLoadingVersions = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The versions could not be loaded.' }); }
    });
  }

  compare(): void {
    if (!this.compareWith) return;
    this.isComparing = true;
    this.service.compare(this.compareWith, this.uuid).subscribe({
      next: (res) => { this.isComparing = false; this.comparison = res.result ?? null; },
      error: (err) => {
        this.isComparing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'The versions could not be compared.' });
      }
    });
  }

  // ── Actions ───────────────────────────────────────────────────────────────

  edit(): void { this.router.navigate(['/portal/pages/manufacturing/boms', this.uuid, 'edit']); }

  submit(): void   { this.run(this.service.submit(this.uuid), 'Submitted for approval.'); }
  approve(): void  { this.run(this.service.approve(this.uuid), 'Approved. It can now be activated.'); }
  activate(): void { this.run(this.service.activate(this.uuid), 'Activated. Production orders will use this version.'); }

  openReject(): void { this.rejectReason = ''; this.rejectVisible = true; }
  confirmReject(): void {
    if (!this.rejectReason.trim()) return;
    this.run(this.service.reject(this.uuid, this.rejectReason.trim()), 'Rejected. The author can revise and resubmit.', () => this.rejectVisible = false);
  }

  openObsolete(): void { this.obsoleteReason = ''; this.obsoleteVisible = true; }
  confirmObsolete(): void {
    this.run(this.service.obsolete(this.uuid, this.obsoleteReason.trim() || undefined), 'Made obsolete.', () => this.obsoleteVisible = false);
  }

  newVersion(): void {
    if (this.busy) return;
    this.busy = true;
    this.service.newVersion(this.uuid).subscribe({
      next: (res) => {
        this.busy = false;
        this.messageService.add({ severity: 'success', summary: 'Drafted', detail: 'A new version was drafted from this one.' });
        if (res.result) this.router.navigate(['/portal/pages/manufacturing/boms', res.result, 'edit']);
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  delete(): void {
    this.confirmationService.confirm({
      header: 'Delete this draft?',
      message: `${this.bom?.bomNumber} will be removed. This cannot be undone.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => {
        this.busy = true;
        this.service.deleteBom(this.uuid).subscribe({
          next: () => {
            this.busy = false;
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'The draft was removed.' });
            this.router.navigate(['/portal/pages/manufacturing/boms']);
          },
          error: (err) => { this.busy = false; this.fail(err); }
        });
      }
    });
  }

  private run(call: Observable<ApiResponse>, detail: string, then?: () => void): void {
    if (this.busy) return;
    this.busy = true;
    call.subscribe({
      next: () => {
        this.busy = false;
        then?.();
        this.messageService.add({ severity: 'success', summary: 'Done', detail });
        this.load();
      },
      error: (err) => { this.busy = false; this.fail(err); }
    });
  }

  private fail(err: { error?: { message?: string } }): void {
    this.messageService.add({ severity: 'error', summary: 'Not done', detail: err.error?.message || 'The action failed.' });
  }

  costSourceLabel(source: string): string {
    switch (source) {
      case 'LAST_PURCHASE_PRICE': return 'Last purchase price';
      case 'PURCHASE_PRICE':      return 'List purchase price';
      case 'BOM_ROLLUP':          return 'Its own recipe';
      default:                    return 'No cost recorded';
    }
  }
}
