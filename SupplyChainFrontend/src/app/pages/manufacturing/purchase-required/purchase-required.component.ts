import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CalendarModule } from 'primeng/calendar';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';

import {
  PurchaseRequiredService, PurchaseRequiredLine, PurchaseRequiredAffectedOrder, CreatePurchaseRequiredPoRequest
} from '../../../services/purchase-required.service';
import { SupplierService, SupplierListItemModel } from '../../../services/supplier.service';
import { AuthService } from '../../service/auth.service';

/** A31 C9 §11 — shortages aggregated per material variant across every open production order, so
 * procurement raises one purchase order for a product short across several orders instead of one
 * per order (the duplicate-PO problem the FSD's own §11.1 describes). */
@Component({
  selector: 'app-purchase-required',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TableModule, TagModule, TooltipModule, ToastModule, DialogModule,
    DropdownModule, InputNumberModule, InputTextModule, TextareaModule, CalendarModule, AutoCompleteModule
  ],
  templateUrl: './purchase-required.component.html',
  styleUrls: ['./purchase-required.component.scss'],
  providers: [MessageService]
})
export class PurchaseRequiredComponent implements OnInit {
  lines: PurchaseRequiredLine[] = [];
  isLoading = true;
  loadFailed = false;

  // ── Filters ──────────────────────────────────────────────────────────────────
  supplierFilter: SupplierListItemModel | null = null;
  supplierSuggestions: SupplierListItemModel[] = [];
  minShortageFilter: number | null = null;
  sortBy: 'shortage' | 'urgency' | 'product' = 'shortage';

  sortOptions = [
    { label: 'Total shortage', value: 'shortage' },
    { label: 'Urgency (earliest required)', value: 'urgency' },
    { label: 'Product name', value: 'product' }
  ];

  // ── Affected POs drawer ─────────────────────────────────────────────────────
  drawerVisible = false;
  drawerLine: PurchaseRequiredLine | null = null;
  affectedOrders: PurchaseRequiredAffectedOrder[] = [];
  isLoadingAffected = false;

  // ── Create Purchase Order dialog (FSD §11.4) ────────────────────────────────
  createVisible = false;
  createLine: PurchaseRequiredLine | null = null;
  createSupplier: SupplierListItemModel | null = null;
  createQty: number | null = null;
  createUnitPrice: number | null = null;
  createRequiredBy: Date | null = null;
  createNotes = '';
  isCreating = false;

  // ── "Handled manually" dialog (the user's own explicit ask, not in the FSD text) ───
  ackVisible = false;
  ackLine: PurchaseRequiredLine | null = null;
  ackNotes = '';
  isAcknowledging = false;
  isClearingAck: string | null = null; // variantUuid currently being cleared

  constructor(
    private service: PurchaseRequiredService,
    private supplierService: SupplierService,
    private authService: AuthService,
    private messageService: MessageService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  get canCreate(): boolean { return this.authService.hasPermission('PO_CREATE'); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getList({
      supplierId: this.supplierFilter?.uuid,
      minShortageQty: this.minShortageFilter ?? undefined,
      sortBy: this.sortBy
    }).subscribe({
      next: (res) => { this.isLoading = false; this.lines = res.result ?? []; },
      error: () => { this.isLoading = false; this.loadFailed = true; }
    });
  }

  searchSuppliers(event: any): void {
    this.supplierService.getSuppliers({ search: event.query, status: 'ACTIVE', page: 1, pageSize: 50 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.result?.data ?? []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onSupplierFilterChange(val: SupplierListItemModel | string | null): void {
    this.supplierFilter = val && typeof val === 'object' ? val : null;
    this.load();
  }

  clearSupplierFilter(): void {
    this.supplierFilter = null;
    this.load();
  }

  // ── Affected orders drawer ───────────────────────────────────────────────────

  openDrawer(line: PurchaseRequiredLine): void {
    this.drawerLine = line;
    this.drawerVisible = true;
    this.affectedOrders = [];
    this.isLoadingAffected = true;
    this.service.getAffectedOrders(line.variantUuid).subscribe({
      next: (res) => { this.isLoadingAffected = false; this.affectedOrders = res.result ?? []; },
      error: () => { this.isLoadingAffected = false; }
    });
  }

  // ── Create Purchase Order (FSD §11.4) ────────────────────────────────────────

  openCreateDialog(line: PurchaseRequiredLine): void {
    this.createLine = line;
    this.createSupplier = line.defaultSupplierId
      ? { uuid: line.defaultSupplierId, supplierName: line.defaultSupplierName || '' } as SupplierListItemModel
      : null;
    this.createQty = line.totalShortageQty;
    this.createUnitPrice = null;
    this.createRequiredBy = new Date(line.earliestRequiredDate);
    this.createNotes = `Purchase for production shortage — ${line.affectedPoCount} production order${line.affectedPoCount === 1 ? '' : 's'} affected.`;
    this.createVisible = true;
  }

  onCreateSupplierChange(val: SupplierListItemModel | string | null): void {
    this.createSupplier = val && typeof val === 'object' ? val : null;
  }

  get canSubmitCreate(): boolean {
    return !!this.createSupplier?.uuid && !!this.createQty && this.createQty > 0
        && !!this.createUnitPrice && this.createUnitPrice > 0 && !this.isCreating;
  }

  submitCreate(): void {
    if (!this.canSubmitCreate || !this.createLine || !this.createSupplier) return;
    this.isCreating = true;

    const req: CreatePurchaseRequiredPoRequest = {
      supplierId: this.createSupplier.uuid,
      supplierName: this.createSupplier.supplierName,
      quantity: this.createQty!,
      unitPrice: this.createUnitPrice!,
      requiredDate: (this.createRequiredBy ?? new Date()).toISOString(),
      notes: this.createNotes || undefined
    };

    this.service.createPurchaseOrder(this.createLine.variantUuid, req).subscribe({
      next: () => {
        this.isCreating = false;
        this.createVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Purchase order created', detail: 'Draft purchase order raised.' });
        this.load();
      },
      error: (err) => {
        this.isCreating = false;
        this.messageService.add({ severity: 'error', summary: 'Not created', detail: err?.error?.message || 'The purchase order could not be created.' });
      }
    });
  }

  // ── "Handled manually" marker ────────────────────────────────────────────────

  openAckDialog(line: PurchaseRequiredLine): void {
    this.ackLine = line;
    this.ackNotes = '';
    this.ackVisible = true;
  }

  confirmAck(): void {
    if (!this.ackLine || this.isAcknowledging) return;
    this.isAcknowledging = true;
    this.service.acknowledge(this.ackLine.variantUuid, this.ackNotes.trim() || undefined).subscribe({
      next: () => {
        this.isAcknowledging = false;
        this.ackVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Marked', detail: 'Marked as purchased manually — no purchase order needed here.' });
        this.load();
      },
      error: (err) => {
        this.isAcknowledging = false;
        this.messageService.add({ severity: 'error', summary: 'Not marked', detail: err?.error?.message || 'Could not mark this as handled.' });
      }
    });
  }

  clearAck(line: PurchaseRequiredLine): void {
    if (this.isClearingAck) return;
    this.isClearingAck = line.variantUuid;
    this.service.clearAcknowledgement(line.variantUuid).subscribe({
      next: () => {
        this.isClearingAck = null;
        this.messageService.add({ severity: 'info', summary: 'Marker cleared', detail: 'This shortage needs a purchase order again.' });
        this.load();
      },
      error: (err) => {
        this.isClearingAck = null;
        this.messageService.add({ severity: 'error', summary: 'Not cleared', detail: err?.error?.message || 'Could not clear the marker.' });
      }
    });
  }
}
