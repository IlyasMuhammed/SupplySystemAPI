import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators, AbstractControl, ValidationErrors } from '@angular/forms';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { CardModule } from 'primeng/card';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import { forkJoin } from 'rxjs';
import {
  InventoryService,
  CreateAdjustmentRequest,
  StockAdjustmentModel,
  RejectAdjustmentRequest,
  ProductListItemModel,
  WarehouseModel
} from '../../../services/inventory.service';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../shared/product-variant-picker/product-variant-picker.component';

@Component({
  selector: 'app-stock-adjustments',
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
    ConfirmDialogModule,
    DialogModule,
    CardModule,
    DividerModule,
    TooltipModule,
    ProductVariantPickerComponent
  ],
  templateUrl: './stock-adjustments.component.html',
  styleUrls: ['./stock-adjustments.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class StockAdjustmentsComponent implements OnInit {

  // ── Form ──────────────────────────────────────────────────────────────────
  adjustmentForm!: FormGroup;
  isSubmitting = false;

  // ── Lookups ───────────────────────────────────────────────────────────────
  products: ProductListItemModel[] = [];
  warehouseOptions: { label: string; value: number }[] = [];
  isLoadingLookups = true;

  // ── Current variant selection (from the two-level picker) ──────────────────
  selectedProductName = '';
  selectedVariantSku = '';
  selectedVariantName = '';

  // ── Session history ────────────────────────────────────────────────────────
  recentAdjustments: StockAdjustmentModel[] = [];

  // ── Pending approvals ──────────────────────────────────────────────────────
  pendingApprovals: StockAdjustmentModel[] = [];
  isLoadingPending = false;

  // ── Adj type & reason lookups ──────────────────────────────────────────────
  adjTypeOptions = [
    { label: 'Write-off',  value: 'Write-off' },
    { label: 'Damage',     value: 'Damage' },
    { label: 'Count',      value: 'Count' },
    { label: 'Transfer',   value: 'Transfer' }
  ];

  reasonOptions = [
    { label: 'Damage',          value: 'Damage' },
    { label: 'Expiry',          value: 'Expiry' },
    { label: 'Theft',           value: 'Theft' },
    { label: 'Count Variance',  value: 'Count Variance' },
    { label: 'Other',           value: 'Other' }
  ];

  // ── Approve / Reject dialogs ───────────────────────────────────────────────
  showRejectDialog = false;
  rejectReason = '';
  selectedAdjustmentUuid = '';
  isActioning = false;

  // ── Private helpers ────────────────────────────────────────────────────────
  private _warehouseMap = new Map<number, string>();

  constructor(
    private fb: FormBuilder,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.initForm();
    this.loadLookups();
    this.loadPendingApprovals();
  }

  // ── Initialise reactive form ───────────────────────────────────────────────
  private initForm(): void {
    this.adjustmentForm = this.fb.group({
      variantId:    [null, Validators.required],
      warehouseId:  [null, Validators.required],
      adjType:      [null, Validators.required],
      reason:       [null, Validators.required],
      referenceDoc: [''],
      qtyAdjusted:  [null, [Validators.required, Validators.min(-999999), this.nonZeroValidator]],
      unitCost:     [null],
      notes:        ['']
    });
  }

  // ── Two-level product/variant picker selection ──────────────────────────────
  onVariantSelected(sel: VariantPickerSelection): void {
    this.selectedProductName = sel.productName ?? '';
    this.selectedVariantSku  = sel.variantSku ?? '';
    this.selectedVariantName = sel.variantName ?? '';
    this.adjustmentForm.patchValue({ variantId: sel.variantId });
    this.adjustmentForm.get('variantId')!.markAsTouched();
    if (sel.purchasePrice != null) {
      this.adjustmentForm.patchValue({ unitCost: sel.purchasePrice }, { emitEvent: false });
    }
  }

  private nonZeroValidator(control: AbstractControl): ValidationErrors | null {
    if (control.value === 0) {
      return { nonZero: true };
    }
    return null;
  }

  // ── Load pending approvals from API ───────────────────────────────────────
  loadPendingApprovals(): void {
    this.isLoadingPending = true;
    this.inventoryService.getAdjustments({ status: 'PENDING_APPROVAL', pageSize: 100 }).subscribe({
      next: (res) => {
        this.isLoadingPending = false;
        if (res.success && res.result) {
          this.pendingApprovals = res.result.data;
        }
      },
      error: () => { this.isLoadingPending = false; }
    });
  }

  // ── Load products + warehouses in parallel ─────────────────────────────────
  loadLookups(): void {
    this.isLoadingLookups = true;
    forkJoin({
      products:   this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }),
      warehouses: this.inventoryService.getWarehouses()
    }).subscribe({
      next: ({ products, warehouses }) => {
        if (products.success && products.result) {
          this.products = products.result.data;
        }

        if (warehouses.success && warehouses.result) {
          this.warehouseOptions = warehouses.result
            .filter(w => w.isActive)
            .map(w => ({ label: `${w.code} – ${w.name}`, value: w.id }));
          warehouses.result.forEach(w => this._warehouseMap.set(w.id, w.name));
        }

        this.isLoadingLookups = false;
      },
      error: () => {
        this.isLoadingLookups = false;
        this.messageService.add({
          severity: 'warn',
          summary: 'Warning',
          detail: 'Some lookup options failed to load'
        });
      }
    });
  }

  // ── Submit new adjustment ──────────────────────────────────────────────────
  onSubmit(): void {
    if (this.adjustmentForm.invalid) {
      this.adjustmentForm.markAllAsTouched();
      return;
    }

    const raw = this.adjustmentForm.value;
    const payload: CreateAdjustmentRequest = {
      variantId:    raw.variantId,
      warehouseId:  raw.warehouseId,
      adjType:      raw.adjType     || undefined,
      reason:       raw.reason      || undefined,
      referenceDoc: raw.referenceDoc || undefined,
      qtyAdjusted:  raw.qtyAdjusted,
      unitCost:     raw.unitCost    || undefined,
      notes:        raw.notes       || undefined
    };

    this.isSubmitting = true;
    this.inventoryService.createAdjustment(payload).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (!res.success) {
          this.messageService.add({
            severity: 'error',
            summary: 'Error',
            detail: res.message || 'Failed to create adjustment'
          });
          return;
        }

        const result = res.result;
        // Build a StockAdjustmentModel-compatible object for the session history
        const newEntry: StockAdjustmentModel = {
          id: result.id,
          uuid: result.uuid,
          adjNumber: result.adjNumber,
          inventoryItemId: 0,
          variantId: raw.variantId,
          variantSku: this.selectedVariantSku,
          variantName: this.selectedVariantName,
          productName: this.selectedProductName,
          warehouseId: raw.warehouseId,
          warehouseName: this._warehouseMap.get(raw.warehouseId) ?? '',
          adjType: raw.adjType,
          reason: raw.reason,
          referenceDoc: raw.referenceDoc,
          qtyBefore: 0,
          qtyAdjusted: raw.qtyAdjusted,
          qtyAfter: 0,
          unitCost: raw.unitCost,
          notes: raw.notes,
          status: result.status,
          createdDate: new Date().toISOString(),
          createdBy: 0
        };

        this.recentAdjustments.unshift(newEntry);

        if (result.status === 'PENDING_APPROVAL') {
          this.pendingApprovals.unshift(newEntry);
          this.messageService.add({
            severity: 'warn',
            summary: 'Pending Approval',
            detail: `${result.adjNumber ?? 'Adjustment'} submitted — awaiting Inventory Manager approval`
          });
        } else {
          this.messageService.add({
            severity: 'success',
            summary: 'Stock Updated',
            detail: `${result.adjNumber ?? 'Adjustment'} auto-approved and stock updated`
          });
        }

        // Keep product & warehouse selected; reset the rest
        this.adjustmentForm.patchValue({
          adjType:      null,
          reason:       null,
          referenceDoc: '',
          qtyAdjusted:  null,
          unitCost:     null,
          notes:        ''
        });
        ['adjType','reason','qtyAdjusted'].forEach(f => this.adjustmentForm.get(f)?.markAsUntouched());
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error',
          summary: 'Error',
          detail: err.error?.message || 'Failed to create adjustment'
        });
      }
    });
  }

  // ── Approve ────────────────────────────────────────────────────────────────
  approveAdjustment(uuid: string): void {
    this.confirmationService.confirm({
      message: 'Are you sure you want to approve this stock adjustment? The stock levels will be updated immediately.',
      header: 'Confirm Approval',
      icon: 'pi pi-check-circle',
      acceptButtonStyleClass: 'p-button-success',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.isActioning = true;
        this.inventoryService.approveAdjustment(uuid).subscribe({
          next: (res) => {
            this.isActioning = false;
            if (res.success) {
              this._removePending(uuid);
              this._updateRecentStatus(uuid, 'APPROVED');
              this.messageService.add({
                severity: 'success',
                summary: 'Approved',
                detail: 'Adjustment approved and stock updated'
              });
            } else {
              this.messageService.add({
                severity: 'error',
                summary: 'Error',
                detail: res.message || 'Approval failed'
              });
            }
          },
          error: (err) => {
            this.isActioning = false;
            this.messageService.add({
              severity: 'error',
              summary: 'Error',
              detail: err.error?.message || 'Approval failed'
            });
          }
        });
      }
    });
  }

  // ── Open reject dialog ─────────────────────────────────────────────────────
  openRejectDialog(uuid: string): void {
    this.selectedAdjustmentUuid = uuid;
    this.rejectReason = '';
    this.showRejectDialog = true;
  }

  // ── Submit reject ──────────────────────────────────────────────────────────
  submitReject(): void {
    if (!this.rejectReason.trim()) { return; }

    const payload: RejectAdjustmentRequest = { reason: this.rejectReason.trim() };
    this.isActioning = true;
    this.inventoryService.rejectAdjustment(this.selectedAdjustmentUuid, payload).subscribe({
      next: (res) => {
        this.isActioning = false;
        if (res.success) {
          this._removePending(this.selectedAdjustmentUuid);
          this._updateRecentStatus(this.selectedAdjustmentUuid, 'REJECTED');
          this.showRejectDialog = false;
          this.messageService.add({
            severity: 'info',
            summary: 'Rejected',
            detail: 'Adjustment has been rejected'
          });
        } else {
          this.messageService.add({
            severity: 'error',
            summary: 'Error',
            detail: res.message || 'Rejection failed'
          });
        }
      },
      error: (err) => {
        this.isActioning = false;
        this.messageService.add({
          severity: 'error',
          summary: 'Error',
          detail: err.error?.message || 'Rejection failed'
        });
      }
    });
  }

  // ── Status severity helper ─────────────────────────────────────────────────
  getStatusSeverity(status: string): 'success' | 'warn' | 'danger' | 'info' | 'secondary' {
    switch (status) {
      case 'PENDING_APPROVAL': return 'warn';
      case 'AUTO_APPROVED':    return 'success';
      case 'APPROVED':         return 'success';
      case 'REJECTED':         return 'danger';
      default:                 return 'secondary';
    }
  }

  // ── Status label helper ────────────────────────────────────────────────────
  getStatusLabel(status: string): string {
    switch (status) {
      case 'PENDING_APPROVAL': return 'Pending';
      case 'AUTO_APPROVED':    return 'Auto-Approved';
      case 'APPROVED':         return 'Approved';
      case 'REJECTED':         return 'Rejected';
      default:                 return status;
    }
  }

  // ── Convenience accessor ───────────────────────────────────────────────────
  get f() { return this.adjustmentForm.controls; }

  // ── Private helpers ────────────────────────────────────────────────────────
  private _removePending(uuid: string): void {
    this.pendingApprovals = this.pendingApprovals.filter(a => a.uuid !== uuid);
  }

  private _updateRecentStatus(uuid: string, status: string): void {
    const item = this.recentAdjustments.find(a => a.uuid === uuid);
    if (item) { item.status = status; }
  }
}
