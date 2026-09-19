import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { MessageService, ConfirmationService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { LogisticsService, CarrierListItemModel, CarrierDetailModel, PatchCarrierRequest, CreateCarrierRequest } from '../../../../services/logistics.service';
import { SupplierService } from '../../../../services/supplier.service';

@Component({
  selector: 'app-carrier-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule,
    InputIconModule, IconFieldModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    ConfirmDialogModule, DialogModule, InputNumberModule
  ],
  templateUrl: './carrier-list.component.html',
  styleUrls: ['./carrier-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CarrierListComponent implements OnInit {
  carriers: CarrierListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';

  showCreateDialog = false;
  createForm: CreateCarrierRequest = { name: '', code: '' };
  isCreating = false;

  showEditDialog = false;
  editCarrier: Partial<CarrierDetailModel & { uuid: string; supplierId: string | null }> = {};
  isSaving = false;

  supplierOptions: { label: string; value: string | null }[] = [];
  private activeSupplierOptions: { label: string; value: string }[] = [];
  private linkedSupplierOption: { label: string; value: string } | null = null;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Active',       value: 'Active' },
    { label: 'Inactive',     value: 'Inactive' }
  ];

  serviceTypeOptions = [
    { label: 'Courier', value: 'Courier' },
    { label: 'LTL',     value: 'LTL' },
    { label: 'FTL',     value: 'FTL' },
    { label: 'Air',     value: 'Air' },
    { label: 'Sea',     value: 'Sea' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private supplierService: SupplierService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() { this.load(); this.loadSuppliers(); }

  // The supplier list endpoint caps pageSize at 100.
  loadSuppliers() {
    this.supplierService.getSuppliers({ pageSize: 100 }).subscribe({
      next: (res) => {
        this.activeSupplierOptions = (res.result?.data ?? [])
          .filter(s => s.isActive)
          .map(s => ({ label: this.supplierLabel(s), value: s.uuid }));
        this.rebuildSupplierOptions();
      },
      error: () => this.messageService.add({ severity: 'warn', summary: 'Suppliers', detail: 'Could not load suppliers for linking.' })
    });
  }

  // A linked supplier outside the loaded page (or since deactivated) must still show as linked,
  // otherwise the dialog would claim "Not linked" for a carrier whose bills are payable.
  private ensureLinkedSupplierOption(uuid: string | null | undefined) {
    this.linkedSupplierOption = null;
    this.rebuildSupplierOptions();
    if (!uuid || this.activeSupplierOptions.some(o => o.value === uuid)) return;
    this.supplierService.getSupplierById(uuid).subscribe({
      next: (res) => {
        const s = res.result;
        if (!s || this.editCarrier.supplierId !== uuid) return;
        this.linkedSupplierOption = {
          label: this.supplierLabel(s) + (s.isActive ? '' : ' — inactive'),
          value: s.uuid
        };
        this.rebuildSupplierOptions();
      }
    });
  }

  private rebuildSupplierOptions() {
    const extra = this.linkedSupplierOption && !this.activeSupplierOptions.some(o => o.value === this.linkedSupplierOption!.value)
      ? [this.linkedSupplierOption] : [];
    this.supplierOptions = [{ label: '— Not linked —', value: null }, ...extra, ...this.activeSupplierOptions];
  }

  private supplierLabel(s: { supplierName: string; supplierCode?: string }) {
    return s.supplierCode ? `${s.supplierName} (${s.supplierCode})` : s.supplierName;
  }

  load() {
    this.isLoading = true;
    this.logisticsService.getCarriers({
      page: this.currentPage, pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.carriers     = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else { this.carriers = []; this.totalRecords = 0; }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load carriers.' });
      }
    });
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() { this.searchText = ''; this.selectedStatus = ''; this.currentPage = 1; this.load(); }

  openCreate() {
    this.createForm = { name: '', code: '', serviceType: undefined, trackingUrlTemplate: undefined,
                        contactName: undefined, contactPhone: undefined, contactEmail: undefined };
    this.showCreateDialog = true;
  }

  saveCreate() {
    if (!this.createForm.name?.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Carrier name is required.' });
      return;
    }
    if (!this.createForm.code?.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Carrier code is required.' });
      return;
    }
    this.isCreating = true;
    this.logisticsService.createCarrier(this.createForm).subscribe({
      next: () => {
        this.isCreating = false;
        this.showCreateDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Carrier created successfully.' });
        this.load();
      },
      error: (err) => {
        this.isCreating = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to create carrier.' });
      }
    });
  }

  openEdit(carrier: CarrierListItemModel) {
    this.logisticsService.getCarrierById(carrier.uuid).subscribe({
      next: (res) => {
        if (res.success && res.result) {
          this.editCarrier   = { ...res.result };
          this.ensureLinkedSupplierOption(res.result.supplierId);
          this.showEditDialog = true;
        }
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load carrier details.' })
    });
  }

  saveEdit() {
    if (!this.editCarrier.uuid) return;
    this.isSaving = true;
    const req: PatchCarrierRequest = {
      name:                this.editCarrier.name,
      serviceType:         this.editCarrier.serviceType,
      trackingUrlTemplate: this.editCarrier.trackingUrlTemplate,
      contactName:         this.editCarrier.contactName,
      contactPhone:        this.editCarrier.contactPhone,
      contactEmail:        this.editCarrier.contactEmail,
      status:              this.editCarrier.status,
      supplierId:          this.editCarrier.supplierId ?? undefined,
      clearSupplierLink:   !this.editCarrier.supplierId
    };
    this.logisticsService.patchCarrier(this.editCarrier.uuid, req).subscribe({
      next: () => {
        this.isSaving       = false;
        this.showEditDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Carrier updated.' });
        this.load();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Save failed.' });
      }
    });
  }

  confirmDelete(c: CarrierListItemModel) {
    this.confirmationService.confirm({
      message: `Delete carrier <strong>${c.name}</strong>?`,
      header: 'Delete Carrier',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.logisticsService.deleteCarrier(c.uuid).subscribe({
        next: () => { this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Carrier deleted.' }); this.load(); },
        error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Delete failed.' })
      })
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'secondary' {
    return s === 'Active' ? 'success' : s === 'Inactive' ? 'danger' : 'secondary';
  }
}
