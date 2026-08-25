import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { DialogModule } from 'primeng/dialog';
import { CardModule } from 'primeng/card';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import {
  InventoryService,
  ReorderAlertModel,
  CreateAdjustmentRequest
} from '../../../services/inventory.service';

export type UrgencyLevel = 'critical' | 'low' | 'normal';

@Component({
  selector: 'app-reorder-alerts',
  standalone: true,
  imports: [
    CommonModule,
    ReactiveFormsModule,
    FormsModule,
    ButtonModule,
    TableModule,
    TagModule,
    ToastModule,
    DropdownModule,
    InputTextModule,
    InputNumberModule,
    TextareaModule,
    DialogModule,
    CardModule,
    TooltipModule
  ],
  templateUrl: './reorder-alerts.component.html',
  styleUrls: ['./reorder-alerts.component.scss'],
  providers: [MessageService]
})
export class ReorderAlertsComponent implements OnInit {

  // ── Alerts data ────────────────────────────────────────────────────────────
  alerts: ReorderAlertModel[] = [];
  isLoading = true;
  searchText = '';
  urgencyFilter: 'all' | UrgencyLevel = 'all';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  // ── Adjustment dialog ──────────────────────────────────────────────────────
  showAdjustmentDialog = false;
  adjustmentForm!: FormGroup;
  isSavingAdjustment = false;
  selectedAlert: ReorderAlertModel | null = null;

  constructor(
    private fb: FormBuilder,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.initForm();
    this.loadAlerts();
  }

  // ── Form ──────────────────────────────────────────────────────────────────
  private initForm(): void {
    this.adjustmentForm = this.fb.group({
      variantId:        [null, Validators.required],
      warehouseId:      [null, Validators.required],
      qtyAdjusted:      [null, [Validators.required, Validators.min(1)]],
      unitCost:         [null],
      adjustmentReason: ['']
    });
  }

  // ── Load reorder alerts ────────────────────────────────────────────────────
  loadAlerts(): void {
    this.isLoading = true;
    this.inventoryService.getReorderAlerts().subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          // Sort: critical first, then low, then normal
          this.alerts = res.result.sort((a, b) => {
            const rank = (x: ReorderAlertModel) => {
              if (x.qtyAvailable <= 0)                                  return 0;
              if (x.qtyAvailable <= x.reorderPoint * 0.5)               return 1;
              return 2;
            };
            return rank(a) - rank(b);
          });
        } else {
          this.alerts = [];
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({
          severity: 'error',
          summary: 'Error',
          detail: 'Failed to load reorder alerts'
        });
      }
    });
  }

  // ── Filtered alerts ────────────────────────────────────────────────────────
  get filteredAlerts(): ReorderAlertModel[] {
    let list = this.alerts;
    if (this.urgencyFilter !== 'all') {
      list = list.filter(a => this.getUrgencyLevel(a) === this.urgencyFilter);
    }
    const s = this.searchText.trim().toLowerCase();
    if (s) {
      list = list.filter(a =>
        a.productName.toLowerCase().includes(s) ||
        a.variantSku.toLowerCase().includes(s) ||
        (a.categoryName?.toLowerCase().includes(s) ?? false) ||
        a.warehouseName.toLowerCase().includes(s)
      );
    }
    return list;
  }

  setUrgencyFilter(level: 'all' | UrgencyLevel): void {
    this.urgencyFilter = this.urgencyFilter === level ? 'all' : level;
  }

  // ── Computed counts ────────────────────────────────────────────────────────
  get criticalCount(): number {
    return this.alerts.filter(a => a.qtyAvailable <= 0).length;
  }

  get lowCount(): number {
    return this.alerts.filter(a => a.qtyAvailable > 0 && a.qtyAvailable <= a.reorderPoint * 0.5).length;
  }

  // ── Stock-level gauge (visual % of reorder point currently on hand) ────────
  stockGaugePct(alert: ReorderAlertModel): number {
    if (alert.reorderPoint <= 0) return alert.qtyAvailable > 0 ? 100 : 0;
    return Math.max(0, Math.min(100, Math.round((alert.qtyAvailable / alert.reorderPoint) * 100)));
  }

  // ── Search with debounce ───────────────────────────────────────────────────
  onSearchChange(): void {
    if (this.searchTimer) { clearTimeout(this.searchTimer); }
    this.searchTimer = setTimeout(() => {
      // filteredAlerts is a getter — no action needed; binding auto-updates
    }, 300);
  }

  // ── Urgency classification ─────────────────────────────────────────────────
  getUrgencyLevel(alert: ReorderAlertModel): UrgencyLevel {
    if (alert.qtyAvailable <= 0)                                 return 'critical';
    if (alert.qtyAvailable <= alert.reorderPoint * 0.5)          return 'low';
    return 'normal';
  }

  getUrgencyLabel(alert: ReorderAlertModel): string {
    switch (this.getUrgencyLevel(alert)) {
      case 'critical': return 'Out of Stock';
      case 'low':      return 'Low Stock';
      default:         return 'Below Reorder';
    }
  }

  getUrgencySeverity(alert: ReorderAlertModel): 'danger' | 'warn' | 'info' {
    switch (this.getUrgencyLevel(alert)) {
      case 'critical': return 'danger';
      case 'low':      return 'warn';
      default:         return 'info';
    }
  }

  // ── Open create-adjustment dialog ──────────────────────────────────────────
  openAdjustmentDialog(alert: ReorderAlertModel): void {
    this.selectedAlert = alert;
    const suggestedQty = alert.reorderQty ?? alert.reorderPoint;
    this.adjustmentForm.patchValue({
      variantId:        alert.variantId,
      warehouseId:      alert.warehouseId,
      qtyAdjusted:      suggestedQty > 0 ? suggestedQty : alert.reorderPoint,
      unitCost:         null,
      adjustmentReason: `Reorder triggered for ${alert.productName}`
    });
    this.adjustmentForm.markAsUntouched();
    this.showAdjustmentDialog = true;
  }

  // ── Save adjustment from dialog ────────────────────────────────────────────
  saveAdjustment(): void {
    if (this.adjustmentForm.invalid) {
      this.adjustmentForm.markAllAsTouched();
      return;
    }

    const raw = this.adjustmentForm.value;
    const payload: CreateAdjustmentRequest = {
      variantId:   raw.variantId,
      warehouseId: raw.warehouseId,
      adjType:     'Count',
      reason:      'Count Variance',
      qtyAdjusted: raw.qtyAdjusted,
      unitCost:    raw.unitCost   || undefined,
      notes:       raw.adjustmentReason || undefined
    };

    this.isSavingAdjustment = true;
    this.inventoryService.createAdjustment(payload).subscribe({
      next: (res) => {
        this.isSavingAdjustment = false;
        if (res.success) {
          const status = res.result?.status ?? '';
          const detail = status === 'PENDING_APPROVAL'
            ? 'Adjustment submitted and awaiting approval'
            : 'Stock adjusted — reloading alerts…';

          this.messageService.add({
            severity: status === 'PENDING_APPROVAL' ? 'warn' : 'success',
            summary: status === 'PENDING_APPROVAL' ? 'Pending Approval' : 'Adjustment Recorded',
            detail
          });

          this.showAdjustmentDialog = false;
          this.loadAlerts();
        } else {
          this.messageService.add({
            severity: 'error',
            summary: 'Error',
            detail: res.message || 'Failed to create adjustment'
          });
        }
      },
      error: (err) => {
        this.isSavingAdjustment = false;
        this.messageService.add({
          severity: 'error',
          summary: 'Error',
          detail: err.error?.message || 'Failed to create adjustment'
        });
      }
    });
  }

  // ── Navigate to product detail ─────────────────────────────────────────────
  viewProduct(productId: number): void {
    this.router.navigate(['/portal/pages/inventory/products', productId]);
  }

  // ── Navigate to PO create pre-filled with this product ───────────────────
  placeOrder(alert: ReorderAlertModel): void {
    const qty = (alert.reorderQty && alert.reorderQty > 0) ? alert.reorderQty : alert.reorderPoint;
    this.router.navigate(['/portal/pages/demand/purchase-orders/create'], {
      queryParams: {
        productId:    alert.productUuid,
        qty:          qty > 0 ? qty : 1,
        productName:  alert.productName,
        warehouseId:  alert.warehouseUuid,
        warehouseName: alert.warehouseName
      }
    });
  }

  // ── Accessor ───────────────────────────────────────────────────────────────
  get f() { return this.adjustmentForm.controls; }
}
