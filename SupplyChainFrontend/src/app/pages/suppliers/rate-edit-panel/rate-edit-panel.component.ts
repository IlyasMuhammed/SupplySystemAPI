import { Component, EventEmitter, HostListener, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SidebarModule } from 'primeng/sidebar';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { InputSwitchModule } from 'primeng/inputswitch';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import {
  RateCardService, VariantSupplierDetail, RateHistoryEntry, UpdateRateCardRequest, DiscountTierDto
} from '../../../services/rate-card.service';
import { CurrenciesService, CurrencyModel } from '../../../services/currencies.service';
import { isPrimeOverlayClick } from '../../../shared/prime-overlay.util';

const CHANGE_REASON_THRESHOLD = 0.10;

interface TierPreviewRow {
  label: string;
  discountPct: number;
  price: number;
}

@Component({
  selector: 'app-rate-edit-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule, SidebarModule, ButtonModule, InputTextModule, TextareaModule,
    CalendarModule, DropdownModule, InputSwitchModule, TagModule, TooltipModule, ToastModule
  ],
  templateUrl: './rate-edit-panel.component.html',
  styleUrls: ['./rate-edit-panel.component.scss'],
  providers: [MessageService]
})
export class RateEditPanelComponent implements OnChanges {
  @Input() variantSupplierUuid: string | null = null;
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  // Emitted after a successful save so the parent grid can refresh its row.
  @Output() saved = new EventEmitter<void>();

  isLoading  = false;
  loadFailed = false;
  detail: VariantSupplierDetail | null = null;

  isLoadingHistory = false;
  history: RateHistoryEntry[] = [];

  currencies: CurrencyModel[] = [];
  currencyOptions: { label: string; value: string }[] = [];

  isSaving = false;

  // RC-007 — mark as reviewed.
  isMarkingReviewed = false;

  // ── Section 1/2/4 form state — plain object bound via ngModel, matching this app's convention
  // for forms of this size (see mir-detail's editData/editLines) rather than a reactive FormGroup.
  form = {
    vendorUnitCost: 0,
    currencyId: '',
    effectiveFrom: null as Date | null,
    effectiveTo: null as Date | null,
    quotationRef: '',
    changeReason: '',
    leadTimeDays: null as number | null,
    minOrderQty: null as number | null,
    minOrderValue: null as number | null,
    isPreferred: false,
    vendorPartNo: '',
    notes: '',
    isActive: true
  };

  // ── Section 3 — discount tiers ───────────────────────────────────────────
  tiers: DiscountTierDto[] = [];
  tierError: string | null = null;

  constructor(
    private rateCardService: RateCardService,
    private currenciesService: CurrenciesService,
    private messageService: MessageService,
    private router: Router
  ) {
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        if (res.success && res.result) {
          this.currencies = res.result;
          this.currencyOptions = res.result.map(c => ({ label: c.code || c.name, value: c.id }));
        }
      }
    });
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['visible'] && this.visible && this.variantSupplierUuid) {
      this.load();
    }
  }

  // PrimeNG's p-sidebar moves its rendered DOM out of this component's own subtree (for stacking),
  // so a containment check against this component's host element sees even the sidebar's own
  // content (calendars/dropdowns overlays, etc.) as "outside". Query for the sidebar's actual
  // rendered root by its styleClass instead. [dismissible] on p-sidebar is off; this replaces it.
  @HostListener('document:mousedown', ['$event'])
  onDocumentMouseDown(event: MouseEvent) {
    if (!this.visible) return;
    const target = event.target as HTMLElement;
    const sidebarEl = document.querySelector('.rate-edit-panel-sidebar');
    if (sidebarEl?.contains(target)) return;
    if (isPrimeOverlayClick(target)) return;
    this.close();
  }

  onVisibleChange(v: boolean) {
    this.visible = v;
    this.visibleChange.emit(v);
  }

  close() {
    this.visible = false;
    this.visibleChange.emit(false);
  }

  load() {
    if (!this.variantSupplierUuid) return;
    this.isLoading  = true;
    this.loadFailed = false;
    this.detail     = null;

    this.rateCardService.getDetail(this.variantSupplierUuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.detail = res.result;
          this.populateForm(res.result);
          this.loadHistory();
        } else {
          this.loadFailed = true;
        }
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  private populateForm(d: VariantSupplierDetail) {
    this.form = {
      vendorUnitCost: d.vendorUnitCost,
      currencyId:     d.currencyId,
      effectiveFrom:  d.effectiveFrom ? new Date(d.effectiveFrom) : null,
      effectiveTo:    d.effectiveTo ? new Date(d.effectiveTo) : null,
      quotationRef:   d.quotationRef ?? '',
      changeReason:   '',
      leadTimeDays:   d.leadTimeDays ?? null,
      minOrderQty:    d.minOrderQty ?? null,
      minOrderValue:  d.minOrderValue ?? null,
      isPreferred:    d.isPreferred,
      vendorPartNo:   d.vendorPartNo ?? '',
      notes:          d.notes ?? '',
      isActive:       d.isActive
    };
    this.tiers = (d.discountTiers ?? []).map(t => ({ ...t }));
    this.tierError = null;
  }

  loadHistory() {
    if (!this.variantSupplierUuid) return;
    this.isLoadingHistory = true;
    this.rateCardService.getHistory(this.variantSupplierUuid).subscribe({
      next: (res) => {
        this.isLoadingHistory = false;
        this.history = res.success && res.result ? res.result.data : [];
      },
      error: () => { this.isLoadingHistory = false; }
    });
  }

  currencyCode(currencyId: string): string {
    const c = this.currencies.find(x => x.id === currencyId);
    return c?.code || c?.name || '—';
  }

  // ── Change-reason gate (>10% on vendorUnitCost) — mirrors the grid's inline-edit check ────────

  get needsChangeReason(): boolean {
    const original = this.detail?.vendorUnitCost;
    if (original == null || original === 0) return false;
    if (this.form.vendorUnitCost === original) return false;
    return Math.abs(this.form.vendorUnitCost - original) / original > CHANGE_REASON_THRESHOLD;
  }

  // ── Section 3: discount tiers ────────────────────────────────────────────

  addTier() {
    this.tiers.push({ qtyFrom: 0, qtyTo: null, discountPct: 0 });
    this.validateTiers();
  }

  removeTier(index: number) {
    this.tiers.splice(index, 1);
    this.validateTiers();
  }

  validateTiers(): boolean {
    this.tierError = null;
    if (this.tiers.length === 0) return true;

    const sorted = [...this.tiers].sort((a, b) => a.qtyFrom - b.qtyFrom);
    for (let i = 0; i < sorted.length; i++) {
      const t = sorted[i];
      if (t.discountPct < 0 || t.discountPct > 100) {
        this.tierError = `Discount tier (${t.qtyFrom}-${t.qtyTo ?? '∞'}) has an invalid discount % — must be between 0 and 100.`;
        return false;
      }
      if (t.qtyTo != null && t.qtyFrom >= t.qtyTo) {
        this.tierError = `Discount tier qty_from (${t.qtyFrom}) must be less than qty_to (${t.qtyTo}).`;
        return false;
      }
      if (i < sorted.length - 1) {
        const next = sorted[i + 1];
        if (t.qtyTo == null || t.qtyTo >= next.qtyFrom) {
          this.tierError = `Discount tiers overlap: (${t.qtyFrom}-${t.qtyTo ?? '∞'}) overlaps (${next.qtyFrom}-${next.qtyTo ?? '∞'}).`;
          return false;
        }
      }
    }
    return true;
  }

  get tierPreview(): TierPreviewRow[] {
    const base = this.form.vendorUnitCost;
    return [...this.tiers]
      .sort((a, b) => a.qtyFrom - b.qtyFrom)
      .map(t => ({
        label: t.qtyTo != null ? `${t.qtyFrom}-${t.qtyTo}` : `${t.qtyFrom}+`,
        discountPct: t.discountPct,
        price: base * (1 - t.discountPct / 100)
      }));
  }

  // ── Save (Sections 1/2/3/4 all submit through the same full-replace PUT) ────────────────────

  save() {
    if (!this.variantSupplierUuid || !this.detail) return;
    if (!this.validateTiers()) return;

    if (this.needsChangeReason && !this.form.changeReason.trim()) {
      this.messageService.add({
        severity: 'warn', summary: 'Change Reason Required',
        detail: 'This rate change is greater than 10%. Please provide a reason before saving.'
      });
      return;
    }

    const req: UpdateRateCardRequest = {
      vendorUnitCost: this.form.vendorUnitCost,
      leadTimeDays:   this.form.leadTimeDays,
      effectiveFrom:  (this.form.effectiveFrom ?? new Date()).toISOString(),
      effectiveTo:    this.form.effectiveTo ? this.form.effectiveTo.toISOString() : null,
      currencyId:     this.form.currencyId,
      minOrderValue:  this.form.minOrderValue,
      minOrderQty:    this.form.minOrderQty,
      discountTiers:  this.tiers,
      quotationRef:   this.form.quotationRef || null,
      notes:          this.form.notes || null,
      vendorPartNo:   this.form.vendorPartNo || null,
      isActive:       this.form.isActive,
      lastReviewedAt: this.detail.lastReviewedAt,
      lastReviewedBy: this.detail.lastReviewedBy,
      changeReason:   this.form.changeReason || null
    };

    this.isSaving = true;
    this.rateCardService.updateRateCard(this.variantSupplierUuid, req).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Rate card updated.' });
          this.form.changeReason = '';
          this.load(); // refreshes detail + history timeline in place
          this.saved.emit();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed.' });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed.' });
      }
    });
  }

  // ── RC-007: Mark as Reviewed (separate from Save — does NOT touch the rate) ──

  markReviewed() {
    if (!this.variantSupplierUuid) return;
    this.isMarkingReviewed = true;
    this.rateCardService.markReviewed(this.variantSupplierUuid).subscribe({
      next: (res) => {
        this.isMarkingReviewed = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Reviewed', detail: 'Marked as reviewed.' });
          this.load();
          this.saved.emit();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to mark as reviewed.' });
        }
      },
      error: (err) => {
        this.isMarkingReviewed = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to mark as reviewed.' });
      }
    });
  }

  // ── Section 5: history timeline ──────────────────────────────────────────

  initials(name: string | null | undefined): string {
    if (!name) return '?';
    const parts = name.trim().split(/\s+/);
    return parts.length === 1 ? parts[0].slice(0, 2).toUpperCase() : (parts[0][0] + parts[1][0]).toUpperCase();
  }

  fieldLabel(field: string): string {
    return field.replace(/([a-z])([A-Z])/g, '$1 $2');
  }

  // ── Section 6: PO reference ──────────────────────────────────────────────

  goToLastPo() {
    if (!this.detail?.poReference) return;
    this.close();
    this.router.navigate(['/portal/pages/demand/purchase-orders', this.detail.poReference.lastPoUuid]);
  }
}
