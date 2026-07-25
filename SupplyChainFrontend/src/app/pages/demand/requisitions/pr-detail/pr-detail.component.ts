import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { TableModule } from 'primeng/table';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { SidebarModule } from 'primeng/sidebar';
import { MessageService, ConfirmationService } from 'primeng/api';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import {
  DemandService,
  PrDetailModel,
  PrLineModel,
  ConvertPrToPoRequest,
  ConvertPrSplitRequest,
  PrLineVendorAssignment
} from '../../../../services/demand.service';
import { SupplierService, SupplierListItemModel } from '../../../../services/supplier.service';
import { InventoryService } from '../../../../services/inventory.service';
import { MaterialService, PrLineDisbursement } from '../../../../services/material.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-pr-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CardModule, TagModule, ToastModule, DialogModule,
    InputTextModule, TextareaModule, InputNumberModule, DropdownModule,
    CalendarModule, DividerModule, TooltipModule, TableModule, ConfirmDialogModule,
    AutoCompleteModule, SidebarModule, TimelinePanelComponent, AttachmentListComponent
  ],
  templateUrl: './pr-detail.component.html',
  styleUrls: ['./pr-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class PrDetailComponent implements OnInit {
  uuid = '';
  pr: PrDetailModel | null = null;
  isLoading = true;
  isActioning = false;

  // ── Reject dialog ─────────────────────────────────────────────────────────
  showRejectDialog = false;
  rejectionReason  = '';

  // ── Convert single-vendor dialog ──────────────────────────────────────────
  showConvertDialog   = false;
  convertSupplierId   = '';
  convertSupplierName = '';
  convertDeliveryDate: Date | null = null;
  convertNotes        = '';
  convertSupplierAuto: any = null;

  // ── Convert split dialog ──────────────────────────────────────────────────
  showSplitDialog = false;
  splitAssignments: { prLineUuid: string; itemDescription: string; supplierId: string; supplierName: string }[] = [];
  splitSupplierAuto: any[] = [];

  // ── Supplier autocomplete ─────────────────────────────────────────────────
  supplierSuggestions: SupplierListItemModel[] = [];

  // ── Product name lookup ───────────────────────────────────────────────────
  productsMap: Map<string, string> = new Map();

  // ── Disbursement drill-down panel ─────────────────────────────────────────
  showDisbursementPanel   = false;
  disbursementLine: PrLineModel | null = null;
  disbursements: PrLineDisbursement[] = [];
  isLoadingDisbursements  = false;

  // ── Timeline panel (TL-010) ───────────────────────────────────────────────
  showTimeline = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private inventoryService: InventoryService,
    private materialService: MaterialService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.inventoryService.getProducts({ pageSize: 1000 }).subscribe({
      next: res => {
        if (res.success) {
          res.result.data.forEach(p => this.productsMap.set(p.uuid, p.name));
        }
      }
    });
    this.route.params.subscribe(p => {
      this.uuid = p['uuid'];
      this.load();
    });
  }

  getProductName(productId?: string | null): string | null {
    if (!productId) return null;
    return this.productsMap.get(productId) ?? null;
  }

  load() {
    this.isLoading = true;
    this.demandService.getPrById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.pr = res.success ? res.result : null;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load requisition.' });
      }
    });
  }

  // ── Status helpers ────────────────────────────────────────────────────────

  getStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (status) {
      case 'APPROVED': case 'FULLY_CONVERTED':    return 'success';
      case 'SUBMITTED':                            return 'info';
      case 'PARTIALLY_CONVERTED':                  return 'warn';
      case 'REJECTED': case 'CANCELLED':           return 'danger';
      default:                                     return 'secondary';
    }
  }

  getLineStatusSeverity(s?: string): 'success' | 'warn' | 'secondary' | 'info' | 'danger' | 'contrast' {
    switch (s) {
      case 'FULLY_CONVERTED': return 'success';
      case 'OPEN':            return 'secondary';
      default:                return 'secondary';
    }
  }

  // ── Disbursement badge (purely computed — no stored status is read) ────────
  // TL-004: never derived from any database status column, only disbursedQty vs quantity.

  getDisbursementBadge(line: PrLineModel): { label: string; severity: 'warn' | 'success' } | null {
    if (!line.disbursedQty || line.disbursedQty <= 0) return null;
    if (line.disbursedQty >= line.quantity) {
      return { label: 'Fully Disbursed', severity: 'success' };
    }
    return { label: `${line.disbursedQty} / ${line.quantity} disbursed`, severity: 'warn' };
  }

  openDisbursementPanel(line: PrLineModel) {
    this.disbursementLine       = line;
    this.disbursements          = [];
    this.showDisbursementPanel  = true;
    this.isLoadingDisbursements = true;

    this.materialService.getPrLineDisbursements(this.uuid, line.uuid).subscribe({
      next: (res) => {
        this.isLoadingDisbursements = false;
        this.disbursements = res.success && res.result ? res.result : [];
      },
      error: () => {
        this.isLoadingDisbursements = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load linked MIRs.' });
      }
    });
  }

  goToMir(mirUuid: string) {
    this.router.navigate(['/portal/pages/material/mir', mirUuid]);
  }

  get canEdit():    boolean { return this.pr?.status === 'DRAFT'; }
  get canSubmit():  boolean { return this.pr?.status === 'DRAFT'; }
  get canApprove(): boolean { return this.pr?.status === 'SUBMITTED'; }
  get canReject():  boolean { return this.pr?.status === 'SUBMITTED'; }
  get canConvert(): boolean { return this.pr?.status === 'APPROVED' || this.pr?.status === 'PARTIALLY_CONVERTED'; }

  // ── Submit ────────────────────────────────────────────────────────────────

  onSubmit() {
    this.confirmationService.confirm({
      message: 'Submit this requisition for approval?',
      header: 'Submit Requisition',
      icon: 'pi pi-send',
      accept: () => {
        this.isActioning = true;
        this.demandService.submitPr(this.uuid).subscribe({
          next: () => { this.isActioning = false; this.messageService.add({ severity: 'success', summary: 'Submitted', detail: 'Requisition submitted for approval.' }); this.load(); },
          error: (err) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to submit.' }); }
        });
      }
    });
  }

  // ── Approve ───────────────────────────────────────────────────────────────

  onApprove() {
    this.confirmationService.confirm({
      message: 'Approve this requisition?',
      header: 'Approve Requisition',
      icon: 'pi pi-check-circle',
      accept: () => {
        this.isActioning = true;
        this.demandService.approvePr(this.uuid).subscribe({
          next: () => { this.isActioning = false; this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Requisition approved.' }); this.load(); },
          error: (err) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to approve.' }); }
        });
      }
    });
  }

  // ── Reject ────────────────────────────────────────────────────────────────

  openRejectDialog() { this.rejectionReason = ''; this.showRejectDialog = true; }

  confirmReject() {
    if (!this.rejectionReason.trim()) { return; }
    this.isActioning = true;
    this.demandService.rejectPr(this.uuid, this.rejectionReason).subscribe({
      next: () => { this.isActioning = false; this.showRejectDialog = false; this.messageService.add({ severity: 'warn', summary: 'Rejected', detail: 'Requisition rejected.' }); this.load(); },
      error: (err) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to reject.' }); }
    });
  }

  // ── Convert — single vendor ───────────────────────────────────────────────

  openConvertDialog() {
    this.convertSupplierId = ''; this.convertSupplierName = '';
    this.convertDeliveryDate = null; this.convertNotes = '';
    this.convertSupplierAuto = null;
    this.showConvertDialog = true;
  }

  searchSuppliers(event: any) {
    this.supplierService.getSuppliers({ search: event.query, page: 1, pageSize: 10 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.result?.data ?? []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onConvertSupplierChange(val: any) {
    if (val && typeof val === 'object') {
      this.convertSupplierId = val.uuid;
      this.convertSupplierName = val.supplierName;
    } else {
      this.convertSupplierName = val ?? '';
      this.convertSupplierId = '';
    }
  }

  onSplitSupplierChange(val: any, index: number) {
    if (val && typeof val === 'object') {
      this.splitAssignments[index].supplierId = val.uuid;
      this.splitAssignments[index].supplierName = val.supplierName;
    } else {
      this.splitAssignments[index].supplierName = val ?? '';
      this.splitAssignments[index].supplierId = '';
    }
  }

  confirmConvert() {
    if (!this.convertSupplierName.trim()) { return; }
    this.isActioning = true;
    const req: ConvertPrToPoRequest = {
      supplierId:   this.convertSupplierId || '00000000-0000-0000-0000-000000000000',
      supplierName: this.convertSupplierName,
      deliveryDate: this.convertDeliveryDate?.toISOString(),
      notes:        this.convertNotes || undefined
    };
    this.demandService.convertPrToSingleVendorPo(this.uuid, req).subscribe({
      next: (res) => {
        this.isActioning = false; this.showConvertDialog = false;
        this.messageService.add({ severity: 'success', summary: 'PO Created', detail: 'Purchase order created successfully.' });
        this.load();
        setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', res.result]), 1500);
      },
      error: (err) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Conversion failed.' }); }
    });
  }

  // ── Convert — split by vendor ─────────────────────────────────────────────

  openSplitDialog() {
    this.splitAssignments = (this.pr?.lines ?? []).map(l => ({
      prLineUuid:    l.uuid,
      itemDescription: l.itemDescription,
      supplierId:    '',
      supplierName:  ''
    }));
    this.splitSupplierAuto = new Array(this.splitAssignments.length).fill(null);
    this.showSplitDialog = true;
  }

  confirmSplit() {
    const assigned = this.splitAssignments.filter(a => a.supplierName.trim());
    if (!assigned.length) { return; }
    this.isActioning = true;
    const req: ConvertPrSplitRequest = {
      lines: assigned.map(a => ({
        prLineUuid:    a.prLineUuid,
        supplierId:    a.supplierId || '00000000-0000-0000-0000-000000000000',
        supplierName:  a.supplierName
      }))
    };
    this.demandService.convertPrToSplitPos(this.uuid, req).subscribe({
      next: (res) => {
        this.isActioning = false; this.showSplitDialog = false;
        this.messageService.add({ severity: 'success', summary: 'POs Created', detail: `${res.result?.length ?? 0} purchase order(s) created.` });
        this.load();
      },
      error: (err) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Split conversion failed.' }); }
    });
  }

  get totalEstimated(): number {
    return (this.pr?.lines ?? []).reduce((s, l) => s + l.lineTotal, 0);
  }
}
