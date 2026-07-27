import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { TableModule } from 'primeng/table';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { DropdownModule } from 'primeng/dropdown';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  DemandService,
  QuotationDetailModel,
  VendorResponseModel,
  VendorResponseLineModel,
  SendWithLinkRequest,
  RecordVendorResponseRequest,
  VendorResponseLineRequest,
  AwardQuotationRequest,
  CreatePoRequest,
  RfqAccessLinkModel
} from '../../../../services/demand.service';
import {
  SupplierService,
  SupplierListItemModel,
  EligibleContactModel
} from '../../../../services/supplier.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

interface SendSupplierRow {
  supplierId: string;
  supplierName: string;
  isLoadingContacts: boolean;
  hasUsableContact: boolean;
  hasEmailContact: boolean;
  contacts: EligibleContactModel[];
  selectedContact: EligibleContactModel | null;
}

function emptyRow(): SendSupplierRow {
  return { supplierId: '', supplierName: '', isLoadingContacts: false, hasUsableContact: true, hasEmailContact: true, contacts: [], selectedContact: null };
}

@Component({
  selector: 'app-quotation-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CardModule, TagModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, TableModule,
    CalendarModule, DividerModule, TooltipModule, ConfirmDialogModule,
    AutoCompleteModule, DropdownModule, TimelinePanelComponent, AttachmentListComponent
  ],
  templateUrl: './quotation-detail.component.html',
  styleUrls: ['./quotation-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class QuotationDetailComponent implements OnInit, OnDestroy {
  uuid         = '';
  showTimeline = false;
  quotation: QuotationDetailModel | null = null;
  responses:  VendorResponseModel[] = [];
  isLoading   = true;
  isActioning = false;

  private pollInterval: ReturnType<typeof setInterval> | null = null;

  // ── Send dialog ───────────────────────────────────────────────────────────
  showSendDialog        = false;
  isLoadingAllSuppliers = false;
  allSuppliersSelected  = false;
  sendRows: SendSupplierRow[] = [emptyRow()];
  sendSupplierAuto: any[] = [null];
  sendSuggestions: SupplierListItemModel[][] = [[]];

  // ── Edit Contact sub-dialog ───────────────────────────────────────────────
  showEditContactDialog = false;
  editRowIndex          = -1;
  editContactId         = 0;
  editContactName       = '';
  editPhone             = '';
  editEmail             = '';
  isSavingContact       = false;

  // ── Supplier autocomplete ─────────────────────────────────────────────────
  supplierSuggestions: SupplierListItemModel[] = [];
  responseSupplierAuto: any = null;

  // ── Record response dialog ────────────────────────────────────────────────
  showResponseDialog   = false;
  responseSupplierId   = '';
  responseSupplierName = '';
  responseDate: Date | null = null;
  responseNotes        = '';
  responseLines: { quotationLineUuid: string; lineNo: number; itemDescription: string; specification: string; unitOfMeasure: string; requiredDate: string; netUnitPrice: number; quantity: number; leadTimeDays: number | null; notes: string }[] = [];

  // ── Cancel dialog ─────────────────────────────────────────────────────────
  showCancelDialog = false;
  cancelReason     = '';

  // ── Open Bids (sealed-bid reveal) ─────────────────────────────────────────
  showOpenBidsDialog = false;
  isOpeningBids      = false;

  // ── Comparison ────────────────────────────────────────────────────────────
  showComparisonDialog = false;
  comparison: VendorResponseModel[] = [];
  isLoadingComparison  = false;

  // Side-by-side matrix filters — supplier search + sort, applied client-side over `comparison`.
  comparisonSearch = '';
  comparisonSortBy: 'total_asc' | 'total_desc' | 'leadtime_asc' | 'leadtime_desc' = 'total_asc';
  comparisonSortOptions = [
    { label: 'Total: Low to High',       value: 'total_asc' },
    { label: 'Total: High to Low',       value: 'total_desc' },
    { label: 'Delivery: Fastest First',  value: 'leadtime_asc' },
    { label: 'Delivery: Slowest First',  value: 'leadtime_desc' }
  ];

  // ── Convert to PO ─────────────────────────────────────────────────────────
  showConvertToPoDialog = false;
  isConvertingToPo      = false;
  awardedResponse: VendorResponseModel | null = null;

  // ── Access Links ──────────────────────────────────────────────────────────
  accessLinks: RfqAccessLinkModel[] = [];
  isLoadingAccessLinks = false;
  resendingLinkId: number | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  ngOnDestroy() {
    this.stopPolling();
  }

  private startPolling() {
    this.stopPolling();
    this.pollInterval = setInterval(() => this.silentRefresh(), 30_000);
  }

  private stopPolling() {
    if (this.pollInterval !== null) {
      clearInterval(this.pollInterval);
      this.pollInterval = null;
    }
  }

  private silentRefresh() {
    this.demandService.getQuotationById(this.uuid).subscribe({
      next: (res) => {
        if (res.success && res.result) {
          this.quotation = res.result;
          if (res.result.status !== 'SENT') this.stopPolling();
          this.loadAccessLinks();
        }
      },
      error: () => {}
    });
  }

  load() {
    this.isLoading = true;
    this.demandService.getQuotationById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.quotation = res.success ? res.result : null;
        if (this.quotation?.status === 'SENT') {
          this.startPolling();
        } else {
          this.stopPolling();
        }
        // Auto-load so who-submitted / when is visible immediately — pricing itself stays
        // sealed regardless (access links never carry line prices), so this is safe to show
        // without any "open bids" action.
        if (this.quotation?.status === 'SENT' || this.quotation?.status === 'AWARDED') {
          this.loadAccessLinks();
        }
      },
      error: () => { this.isLoading = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load quotation.' }); }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'AWARDED':   return 'success';
      case 'SENT':      return 'info';
      case 'DRAFT':     return 'secondary';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  get canEdit():         boolean { return this.quotation?.status === 'DRAFT'; }
  get canSend():         boolean { return this.quotation?.status === 'DRAFT'; }
  get canRespond():      boolean { return this.quotation?.status === 'SENT'; }
  get hasResponses():    boolean { return (this.quotation?.submittedResponseCount ?? 0) > 0; }
  get isSealed():        boolean { return this.quotation?.status === 'SENT' && !this.quotation?.bidsOpenedAt; }
  get canOpenBids():     boolean { return this.isSealed && this.hasResponses; }
  get canAward():        boolean { return this.quotation?.status === 'SENT' && !!this.quotation?.bidsOpenedAt; }
  get openBidsEarly():   boolean {
    return !!this.quotation?.dueDate && new Date(this.quotation.dueDate) > new Date();
  }
  get canCancel():       boolean { return ['DRAFT','SENT'].includes(this.quotation?.status ?? ''); }
  get canConvertToPo():  boolean { return this.quotation?.status === 'AWARDED'; }
  get filledSupplierCount(): number { return this.sendRows.filter(r => r.supplierName.trim()).length; }

  get sendContactsValid(): boolean {
    const filled = this.sendRows.filter(r => r.supplierName.trim());
    return filled.length > 0 && filled.every(r =>
      !r.isLoadingContacts &&
      r.selectedContact !== null &&
      !!r.selectedContact?.email
    );
  }

  // ── Supplier autocomplete ─────────────────────────────────────────────────
  searchSuppliers(event: any) {
    this.supplierService.getSuppliers({ search: event.query, page: 1, pageSize: 10 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.result?.data ?? []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onSendSupplierChange(val: any, index: number) {
    if (val && typeof val === 'object') {
      const duplicate = this.sendRows.some(
        (r, i) => i !== index && r.supplierId && r.supplierId === val.uuid
      );
      if (duplicate) {
        this.sendSupplierAuto[index] = null;
        this.sendRows[index] = emptyRow();
        this.messageService.add({ severity: 'warn', summary: 'Duplicate', detail: `${val.supplierName} is already in the list.` });
        return;
      }
      this.sendRows[index].supplierId   = val.uuid;
      this.sendRows[index].supplierName = val.supplierName;
      this.sendRows[index].contacts      = [];
      this.sendRows[index].selectedContact = null;
      this.loadEligibleContacts(index);
    } else {
      this.sendRows[index] = { ...emptyRow(), supplierName: val ?? '' };
    }
  }

  trackByIndex(index: number) { return index; }

  searchSuppliersSend(event: any, index: number) {
    this.supplierService.getSuppliers({ search: event.query, page: 1, pageSize: 10 }).subscribe({
      next: (res) => {
        const all = res.result?.data ?? [];
        this.sendSuggestions[index] = all.filter(s =>
          !this.sendRows.some((row: any, i: number) =>
            i !== index && row.supplierId && row.supplierId === s.uuid
          )
        );
      },
      error: () => { this.sendSuggestions[index] = []; }
    });
  }

  onResponseSupplierChange(val: any) {
    if (val && typeof val === 'object') {
      this.responseSupplierId = val.uuid;
      this.responseSupplierName = val.supplierName;
    } else {
      this.responseSupplierName = val ?? '';
      this.responseSupplierId = '';
    }
  }

  // ── Send ──────────────────────────────────────────────────────────────────
  openSendDialog() {
    this.sendRows              = [emptyRow()];
    this.sendSupplierAuto      = [null];
    this.sendSuggestions       = [[]];
    this.allSuppliersSelected  = false;
    this.showSendDialog        = true;
  }

  loadAllSuppliers() {
    if (this.allSuppliersSelected) {
      this.sendRows             = [emptyRow()];
      this.sendSupplierAuto     = [null];
      this.sendSuggestions      = [[]];
      this.allSuppliersSelected = false;
      return;
    }

    this.isLoadingAllSuppliers = true;
    this.supplierService.getSuppliers({ search: '', page: 1, pageSize: 1000 }).subscribe({
      next: (res) => {
        const all = res.result?.data ?? [];
        if (all.length === 0) {
          this.messageService.add({ severity: 'info', summary: 'No Suppliers', detail: 'No suppliers found in the system.' });
          this.isLoadingAllSuppliers = false;
          return;
        }
        this.sendRows             = all.map(s => ({ ...emptyRow(), supplierId: s.uuid, supplierName: s.supplierName }));
        this.sendSupplierAuto     = all.map(s => s);
        this.sendSuggestions      = all.map(() => []);
        this.allSuppliersSelected = true;
        this.isLoadingAllSuppliers = false;
        this.sendRows.forEach((_, i) => this.loadEligibleContacts(i));
      },
      error: () => {
        this.isLoadingAllSuppliers = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load suppliers.' });
      }
    });
  }

  addSendSupplier() {
    this.sendRows.push(emptyRow());
    this.sendSupplierAuto.push(null);
    this.sendSuggestions.push([]);
  }

  removeSendSupplier(i: number) {
    if (this.sendRows.length > 1) {
      this.sendRows.splice(i, 1);
      this.sendSupplierAuto.splice(i, 1);
      this.sendSuggestions.splice(i, 1);
    }
  }

  loadEligibleContacts(index: number) {
    const uuid = this.sendRows[index]?.supplierId;
    if (!uuid) return;

    this.sendRows[index].isLoadingContacts = true;
    this.sendRows[index].contacts          = [];
    this.sendRows[index].selectedContact   = null;

    this.supplierService.getEligibleContacts(uuid).subscribe({
      next: (res) => {
        const data = res.result;
        const contacts = data?.contacts ?? [];
        this.sendRows[index].isLoadingContacts = false;
        this.sendRows[index].hasUsableContact  = data?.hasUsableContact ?? false;
        this.sendRows[index].contacts          = contacts;
        this.sendRows[index].hasEmailContact   = contacts.some(c => !!c.email);
        // prefer: primary with email → any with email → primary with mobile → any with mobile
        const primaryEmail = contacts.find(c => c.isPrimary && !!c.email);
        const anyEmail     = contacts.find(c => !!c.email);
        const primaryMob   = contacts.find(c => c.isPrimary && c.isMobileValid);
        const anyMob       = contacts.find(c => c.isMobileValid);
        this.sendRows[index].selectedContact = primaryEmail ?? anyEmail ?? primaryMob ?? anyMob ?? null;
      },
      error: () => {
        this.sendRows[index].isLoadingContacts = false;
        this.sendRows[index].hasUsableContact  = false;
        this.sendRows[index].hasEmailContact   = false;
      }
    });
  }

  openEditContactDialog(index: number) {
    const contact = this.sendRows[index]?.selectedContact;
    if (!contact) return;
    this.editRowIndex   = index;
    this.editContactId  = contact.id;
    this.editContactName = contact.contactName;
    this.editPhone      = contact.phone ?? '';
    this.editEmail      = contact.email ?? '';
    this.showEditContactDialog = true;
  }

  saveContactEdit() {
    if (this.isSavingContact) return;
    const supplierUuid = this.sendRows[this.editRowIndex].supplierId;
    this.isSavingContact = true;
    this.supplierService.patchContact(supplierUuid, this.editContactId, {
      phone: this.editPhone || undefined,
      email: this.editEmail || undefined
    }).subscribe({
      next: () => {
        this.isSavingContact       = false;
        this.showEditContactDialog = false;
        this.loadEligibleContacts(this.editRowIndex);
      },
      error: (e) => {
        this.isSavingContact = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Failed to update contact.' });
      }
    });
  }

  confirmSend() {
    const valid = this.sendRows.filter(r => r.supplierName.trim() && r.selectedContact?.email);
    if (!valid.length) return;
    this.isActioning = true;
    const req: SendWithLinkRequest = {
      suppliers: valid.map(r => ({
        supplierId:           r.supplierId || '00000000-0000-0000-0000-000000000000',
        supplierName:         r.supplierName,
        contactId:            r.selectedContact!.id,
        supplierEmail:        r.selectedContact!.email ?? undefined,
        contactMobileNumber:  r.selectedContact!.normalisedMobile ?? r.selectedContact!.phone ?? undefined
      }))
    };
    this.demandService.sendWithLink(this.uuid, req).subscribe({
      next: (res) => {
        this.isActioning = false;
        this.showSendDialog = false;
        const r = res.result;
        this.messageService.add({ severity: 'success', summary: 'Sent Successfully', detail: 'Quotation sent to suppliers.' });
        if (r?.emailWarning) {
          this.messageService.add({ severity: 'info', summary: 'Email Note', detail: r.emailWarning, life: 15000 });
        }
        if (r?.whatsAppWarning) {
          this.messageService.add({ severity: 'info', summary: 'WhatsApp Note', detail: r.whatsAppWarning, life: 15000 });
        }
        this.load();
        this.loadAccessLinks();
      },
      error: (e) => {
        this.isActioning = false;
        const detail = e?.error?.result?.exceptionMessage ?? e?.error?.message ?? 'Failed to send.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail, life: 10000 });
      }
    });
  }

  // ── Record response ───────────────────────────────────────────────────────
  openResponseDialog() {
    this.responseSupplierId = ''; this.responseSupplierName = '';
    this.responseSupplierAuto = null;
    this.responseDate = null; this.responseNotes = '';
    this.responseLines = (this.quotation?.lines ?? []).map(l => ({
      quotationLineUuid: l.uuid,
      lineNo:            l.lineNo,
      itemDescription:   l.itemDescription,
      specification:     l.specification ?? '',
      unitOfMeasure:     l.unitOfMeasure ?? '',
      requiredDate:      l.requiredDate  ?? '',
      netUnitPrice:      0,
      quantity:          l.quantity,
      leadTimeDays:      null,
      notes:             ''
    }));
    this.showResponseDialog = true;
    setTimeout(() => { this.responseDate = new Date(); }, 50);
  }

  confirmResponse() {
    if (!this.responseSupplierName.trim()) return;
    this.isActioning = true;
    const req: RecordVendorResponseRequest = {
      supplierId:   this.responseSupplierId || '00000000-0000-0000-0000-000000000000',
      supplierName: this.responseSupplierName,
      responseDate: this.responseDate?.toISOString(),
      notes:        this.responseNotes || undefined,
      lines:        this.responseLines.map(l => ({
        quotationLineUuid: l.quotationLineUuid,
        netUnitPrice:      l.netUnitPrice,
        quantity:          l.quantity,
        leadTimeDays:      l.leadTimeDays ?? undefined,
        notes:             l.notes || undefined
      }) as VendorResponseLineRequest)
    };
    this.demandService.recordVendorResponse(this.uuid, req).subscribe({
      next: () => { this.isActioning = false; this.showResponseDialog = false; this.messageService.add({ severity: 'success', summary: 'Recorded', detail: 'Vendor response recorded.' }); this.load(); },
      error: (e) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Failed to record response.' }); }
    });
  }

  // ── Open Bids (sealed-bid reveal) ─────────────────────────────────────────
  openBidsDialog() { this.showOpenBidsDialog = true; }

  confirmOpenBids() {
    this.isOpeningBids = true;
    this.demandService.openBids(this.uuid).subscribe({
      next: () => {
        this.isOpeningBids = false;
        this.showOpenBidsDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Bids Opened', detail: 'Vendor pricing is now visible.' });
        this.load();
      },
      error: (e) => {
        this.isOpeningBids = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Failed to open bids.' });
      }
    });
  }

  // ── Comparison & Award ────────────────────────────────────────────────────
  openComparison() {
    this.isLoadingComparison = true;
    this.showComparisonDialog = true;
    this.demandService.getQuotationComparison(this.uuid).subscribe({
      next: (res) => { this.isLoadingComparison = false; this.comparison = res.result ?? []; },
      error: () => { this.isLoadingComparison = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load comparison.' }); }
    });
  }

  // Row list for the matrix — driven by the quotation's own lines (always complete and correctly
  // ordered) rather than any single response's lines, since a response may be missing a line.
  get comparisonLines(): { uuid: string; itemDescription: string }[] {
    return (this.quotation?.lines ?? []).map(l => ({ uuid: l.uuid, itemDescription: l.itemDescription }));
  }

  private avgLeadDays(r: VendorResponseModel): number {
    const days = r.lines.map(l => l.leadTimeDays).filter((d): d is number => d != null && d > 0);
    return days.length ? days.reduce((s, d) => s + d, 0) / days.length : Number.MAX_SAFE_INTEGER;
  }

  get filteredComparison(): VendorResponseModel[] {
    const q = this.comparisonSearch.trim().toLowerCase();
    let rows = q ? this.comparison.filter(r => r.supplierName.toLowerCase().includes(q)) : [...this.comparison];

    switch (this.comparisonSortBy) {
      case 'total_desc':    rows.sort((a, b) => b.totalAmount - a.totalAmount); break;
      case 'leadtime_asc':  rows.sort((a, b) => this.avgLeadDays(a) - this.avgLeadDays(b)); break;
      case 'leadtime_desc': rows.sort((a, b) => this.avgLeadDays(b) - this.avgLeadDays(a)); break;
      default:              rows.sort((a, b) => a.totalAmount - b.totalAmount); // total_asc
    }
    return rows;
  }

  get lowestTotalAmount(): number | null {
    return this.comparison.length ? Math.min(...this.comparison.map(r => r.totalAmount)) : null;
  }

  cellFor(response: VendorResponseModel, lineUuid: string): VendorResponseLineModel | undefined {
    return response.lines.find(l => l.quotationLineUuid === lineUuid);
  }

  awardResponse(responseUuid: string) {
    this.isActioning = true;
    const req: AwardQuotationRequest = { vendorResponseUuid: responseUuid };
    this.demandService.awardQuotation(this.uuid, req).subscribe({
      next: () => { this.isActioning = false; this.showComparisonDialog = false; this.messageService.add({ severity: 'success', summary: 'Awarded', detail: 'Quotation awarded.' }); this.load(); },
      error: (e) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Award failed.' }); }
    });
  }

  // ── Cancel ────────────────────────────────────────────────────────────────
  openCancelDialog() { this.cancelReason = ''; this.showCancelDialog = true; }

  confirmCancel() {
    if (!this.cancelReason.trim()) return;
    this.isActioning = true;
    this.demandService.cancelQuotation(this.uuid, this.cancelReason).subscribe({
      next: () => { this.isActioning = false; this.showCancelDialog = false; this.messageService.add({ severity: 'warn', summary: 'Cancelled', detail: 'Quotation cancelled.' }); this.load(); },
      error: (e) => { this.isActioning = false; this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Cancel failed.' }); }
    });
  }

  // ── Convert to PO ─────────────────────────────────────────────────────────
  openConvertToPoDialog() {
    if (this.comparison.length > 0) {
      this.awardedResponse = this.comparison.find(r => r.status === 'AWARDED') ?? null;
      this.showConvertToPoDialog = true;
      return;
    }
    this.isConvertingToPo = true;
    this.demandService.getQuotationComparison(this.uuid).subscribe({
      next: (res) => {
        this.isConvertingToPo = false;
        this.comparison = res.result ?? [];
        this.awardedResponse = this.comparison.find(r => r.status === 'AWARDED') ?? null;
        this.showConvertToPoDialog = true;
      },
      error: () => {
        this.isConvertingToPo = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load quotation data.' });
      }
    });
  }

  confirmConvertToPo() {
    if (!this.awardedResponse || !this.quotation) return;
    this.isConvertingToPo = true;
    const qLines = this.quotation.lines;
    const req: CreatePoRequest = {
      supplierId:   this.awardedResponse.supplierId,
      supplierName: this.awardedResponse.supplierName,
      title:        `PO from ${this.quotation.quotationNumber}`,
      lines: this.awardedResponse.lines.map(rl => {
        const ql = qLines.find(l => l.uuid === rl.quotationLineUuid);
        return {
          itemDescription: rl.itemDescription,
          specification:   ql?.specification,
          unitOfMeasure:   ql?.unitOfMeasure,
          quantity:        rl.quantity,
          unitPrice:       rl.netUnitPrice,
          requiredDate:    ql?.requiredDate,
          lineNotes:       rl.notes
        };
      })
    };
    this.demandService.createPo(req).subscribe({
      next: (res) => {
        this.isConvertingToPo = false;
        this.showConvertToPoDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Purchase order created successfully.' });
        setTimeout(() => this.router.navigate(['/portal/pages/demand/purchase-orders', res.result]), 800);
      },
      error: (e) => {
        this.isConvertingToPo = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Failed to create purchase order.' });
      }
    });
  }

  getResponseSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'AWARDED':  return 'success';
      case 'REJECTED': return 'danger';
      case 'PENDING':  return 'warn';
      default:         return 'secondary';
    }
  }

  // ── Access Links ──────────────────────────────────────────────────────────
  loadAccessLinks() {
    this.isLoadingAccessLinks = true;
    this.demandService.getAccessLinks(this.uuid).subscribe({
      next: (res) => {
        this.isLoadingAccessLinks = false;
        this.accessLinks = res.result ?? [];
      },
      error: () => {
        this.isLoadingAccessLinks = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load access links.' });
      }
    });
  }

  resendLink(linkId: number) {
    this.resendingLinkId = linkId;
    this.demandService.resendAccessLink(this.uuid, linkId).subscribe({
      next: () => {
        this.resendingLinkId = null;
        this.messageService.add({ severity: 'success', summary: 'Queued', detail: 'Email and WhatsApp notifications re-queued.' });
      },
      error: (e) => {
        this.resendingLinkId = null;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: e?.error?.message ?? 'Failed to resend.' });
      }
    });
  }

  getLinkStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (status) {
      case 'PENDING':  return 'info';
      case 'ACCESSED': return 'warn';
      case 'CONSUMED': return 'success';
      case 'EXPIRED':  return 'secondary';
      case 'REVOKED':  return 'danger';
      default:         return 'secondary';
    }
  }

  // WhatsAppSentAt only means "the provider accepted the send request" — whatsAppStatus carries
  // the real outcome once Twilio's async status callback updates it (see WhatsAppWebhookController
  // in SMS.Modules.Notifications). Undefined/SENT/QUEUED means still in flight, not confirmed yet.
  waStatusClass(status?: string): string {
    switch (status) {
      case 'DELIVERED':
      case 'READ':        return 'al-sent-wa';
      case 'FAILED':
      case 'UNDELIVERED': return 'al-sent-wa-failed';
      default:            return 'al-sent-wa-pending';
    }
  }

  waStatusIcon(status?: string): string {
    switch (status) {
      case 'DELIVERED':
      case 'READ':        return 'pi pi-check-circle';
      case 'FAILED':
      case 'UNDELIVERED': return 'pi pi-times-circle';
      default:            return 'pi pi-clock';
    }
  }

  waStatusLabel(status?: string): string {
    switch (status) {
      case 'DELIVERED':   return 'Delivered';
      case 'READ':        return 'Read';
      case 'FAILED':      return 'Failed';
      case 'UNDELIVERED': return 'Undelivered';
      default:            return 'Sending…';
    }
  }

  waStatusTooltip(status?: string): string {
    switch (status) {
      case 'DELIVERED':
      case 'READ':        return 'Confirmed delivered by WhatsApp.';
      case 'FAILED':
      case 'UNDELIVERED': return 'WhatsApp reported this message could not be delivered — the recipient may not have opted in yet, or the number may be invalid.';
      default:            return 'Accepted by the provider, but delivery is not confirmed yet.';
    }
  }

  canResendLink(link: RfqAccessLinkModel): boolean {
    return link.status !== 'CONSUMED' && link.status !== 'EXPIRED';
  }
}
