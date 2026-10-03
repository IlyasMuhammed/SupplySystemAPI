import { Component, EventEmitter, Input, OnChanges, OnInit, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DrawerModule } from 'primeng/drawer';
import { SelectModule } from 'primeng/select';
import { SelectButtonModule } from 'primeng/selectbutton';
import { DatePickerModule } from 'primeng/datepicker';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  SalesPreorderService, SaleInquiry, SaleInquiryLine, SaleInquiryLineStatus, RejectionReason, SALE_INQUIRY_LINE_STATUSES
} from '../../../../services/sales-preorder.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../../shared/product-variant-picker/product-variant-picker.component';
import {
  INQUIRY_LINE_STATUS_COLOR, LineDraft, LineProblems, MAX, StatusColor, applies, displayDate, draftFromLine, emptyLineDraft,
  lineProblems, lineSummary, serverMessage, statusLabel, toLineRequest, toNewLineRequest
} from '../sale-inquiry.shared';

export type AddMode = 'CATALOGUE' | 'TEXT';
export type DrawerField = 'canSupplyQuantity' | 'estimatedDeliveryDate' | 'rejectionReasonUuid' | 'alternative';

/**
 * A32-PB-10 — the inquiry's Lines tab (spec §10.2). Each requested line with its evaluation status as a coloured dot,
 * an expandable sub-row saying what was decided (can-supply, reason, alternative), an evaluation drawer whose fields
 * follow the chosen status (client validation = BR-C1-04/05), and "Add line" for a catalogue item or free text.
 * Every write asks the page to reload: the server may move the inquiry (e.g. back to UNDER_REVIEW).
 */
@Component({
  selector: 'app-sale-inquiry-lines',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, TableModule, TooltipModule, ToastModule, DrawerModule, SelectModule,
    SelectButtonModule, DatePickerModule, InputNumberModule, InputTextModule, TextareaModule, CheckboxModule,
    ConfirmDialogModule, ProductVariantPickerComponent
  ],
  templateUrl: './sale-inquiry-lines.component.html',
  styleUrls: ['../sale-inquiry.shared.scss', './sale-inquiry-lines.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SaleInquiryLinesComponent implements OnInit, OnChanges {
  @Input({ required: true }) inquiry!: SaleInquiry;
  /** The inquiry is editable and the user holds SALE_INQUIRY_EDIT. */
  @Input() canEdit = false;
  /** A line was added, changed or removed: reload the inquiry. */
  @Output() changed = new EventEmitter<void>();

  readonly max = MAX;
  readonly displayDate = displayDate;
  readonly statusLabel = statusLabel;
  readonly statusOptions = SALE_INQUIRY_LINE_STATUSES.map(s => ({ label: statusLabel(s), value: s }));
  readonly addModeOptions: { label: string; value: AddMode }[] = [
    { label: 'Catalogue item', value: 'CATALOGUE' },
    { label: 'Free text', value: 'TEXT' }
  ];

  expanded: Record<string, boolean> = {};

  reasons: RejectionReason[] = [];
  private reasonsRequested = false;

  products: ProductListItemModel[] = [];
  productsFailed = false;
  private productsRequested = false;

  // ── Drawer (add or evaluate) ────────────────────────────────────────────────
  drawerVisible = false;
  /** The line being evaluated; null while adding one. */
  editing: SaleInquiryLine | null = null;
  addMode: AddMode = 'CATALOGUE';
  draft: LineDraft = emptyLineDraft();
  pickingItem = false;
  pickingAlternative = false;
  submitted = false;
  isSaving = false;
  saveError = '';
  /** The description last filled in from a catalogue pick, so a later pick may replace it but a typed one is kept. */
  private autoDescription: string | null = null;

  busy: Record<string, boolean> = {};

  constructor(
    private service: SalesPreorderService,
    private inventoryService: InventoryService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit() {
    this.resetExpansion();
    if (this.canEdit) this.ensureReasons();
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['inquiry'] && !changes['inquiry'].firstChange) this.resetExpansion();
    if (changes['canEdit'] && this.canEdit && !changes['canEdit'].firstChange) this.ensureReasons();
  }

  /** Lines with something decided about them open with their sub-row showing (as §10.2 draws it). */
  private resetExpansion() {
    const open: Record<string, boolean> = {};
    for (const l of this.inquiry?.lines ?? []) if (lineSummary(l).length) open[l.uuid] = true;
    this.expanded = open;
  }

  get isAdd(): boolean { return this.editing === null; }

  summary(line: SaleInquiryLine): string[] { return lineSummary(line); }

  lineColor(status: SaleInquiryLineStatus): StatusColor { return INQUIRY_LINE_STATUS_COLOR[status] ?? 'grey'; }

  // ── Lookups ─────────────────────────────────────────────────────────────────

  private ensureReasons() {
    if (this.reasonsRequested) return;
    this.reasonsRequested = true;
    this.service.getRejectionReasons(false).subscribe({
      next: (res) => { this.reasons = (res.result ?? []).filter(r => r.isActive); },
      error: () => { this.reasonsRequested = false; this.reasons = []; }
    });
  }

  private ensureProducts() {
    if (this.productsRequested) return;
    this.productsRequested = true;
    this.productsFailed = false;
    // Only what can be sold: an identified item has to survive conversion to a sale order (retail channel).
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500, availableFor: 'RETAIL' }).subscribe({
      next: (res) => { this.products = res.result?.data ?? []; },
      error: () => { this.productsRequested = false; this.productsFailed = true; this.products = []; }
    });
  }

  /** Active reasons from the API; a deactivated one the line already carries stays visible, marked as such. */
  get reasonOptions(): { label: string; value: string }[] {
    const options = this.reasons.map(r => ({ label: `${r.code} — ${r.description}`, value: r.uuid }));
    const current = this.editing;
    if (current?.rejectionReasonUuid && !options.some(o => o.value === current.rejectionReasonUuid)) {
      const name = [current.rejectionReasonCode, current.rejectionReasonDescription].filter(Boolean).join(' — ') || 'Reason';
      options.push({ label: `${name} (deactivated)`, value: current.rejectionReasonUuid });
    }
    return options;
  }

  get reasonIsDeactivated(): boolean {
    const uuid = this.draft.rejectionReasonUuid;
    return !!uuid && this.reasons.length > 0 && !this.reasons.some(r => r.uuid === uuid);
  }

  // ── Drawer ──────────────────────────────────────────────────────────────────

  openAdd() {
    if (!this.canEdit) return;
    this.editing = null;
    this.addMode = 'CATALOGUE';
    this.draft = emptyLineDraft();
    this.autoDescription = null;
    this.pickingItem = true;
    this.pickingAlternative = false;
    this.resetDrawerState();
    this.ensureProducts();
    this.drawerVisible = true;
  }

  openEdit(line: SaleInquiryLine) {
    if (!this.canEdit) return;
    this.editing = line;
    this.draft = draftFromLine(line);
    this.autoDescription = null;
    this.pickingItem = false;
    this.pickingAlternative = false;
    this.resetDrawerState();
    this.ensureReasons();
    this.ensureProducts();
    this.drawerVisible = true;
  }

  private resetDrawerState() {
    this.submitted = false;
    this.isSaving = false;
    this.saveError = '';
  }

  /** Which conditional fields the chosen status shows (and sends). */
  shows(field: DrawerField): boolean {
    switch (field) {
      case 'canSupplyQuantity':     return applies(this.draft, 'canSupplyQuantity');
      case 'estimatedDeliveryDate': return applies(this.draft, 'estimatedDeliveryDate');
      case 'rejectionReasonUuid':   return applies(this.draft, 'rejection');
      case 'alternative':           return applies(this.draft, 'alternative');
    }
  }

  get estimatedDateRequired(): boolean {
    return this.draft.lineStatus === 'CAN_SUPPLY' || this.draft.lineStatus === 'PARTIAL';
  }

  /** Client validation: the server's line rules, plus "pick the item" when adding from the catalogue. */
  get problems(): LineProblems {
    const p = lineProblems(this.draft);
    if (this.isAdd) {
      // A new line is only what the customer asked for; its evaluation comes after.
      delete p.estimatedDeliveryDate; delete p.canSupplyQuantity; delete p.rejectionReasonUuid;
      if (this.addMode === 'CATALOGUE' && !this.draft.variantUuid) p.variantUuid = 'Pick the catalogue item, or switch to free text.';
    }
    return p;
  }

  get hasProblems(): boolean { return Object.keys(this.problems).length > 0; }

  showError(field: keyof LineProblems): string | null {
    return this.submitted ? (this.problems[field] ?? null) : null;
  }

  onAddModeChange(mode: AddMode) {
    this.addMode = mode;
    if (mode === 'TEXT') {
      this.draft.productUuid = null;
      this.draft.variantUuid = null;
      this.draft.variantLabel = null;
    }
    this.pickingItem = mode === 'CATALOGUE';
  }

  onVariantPicked(sel: VariantPickerSelection) {
    this.draft.productUuid = sel.productUuid;
    this.draft.variantUuid = sel.variantUuid;
    this.draft.variantLabel = this.label(sel);
    const name = sel.productName
      ? (sel.variantName && sel.variantName !== 'Default' ? `${sel.productName} — ${sel.variantName}` : sel.productName)
      : '';
    // The customer's own words win; a description that only echoed an earlier pick follows the new one.
    if (name && (!this.draft.productDescription.trim() || this.draft.productDescription === this.autoDescription)) {
      this.draft.productDescription = name;
      this.autoDescription = name;
    }
    if (sel.uomCode && !this.draft.requestedUomCode.trim()) this.draft.requestedUomCode = sel.uomCode;
    if (sel.variantUuid && !this.isAdd) this.pickingItem = false;
  }

  clearItem() {
    this.draft.productUuid = null;
    this.draft.variantUuid = null;
    this.draft.variantLabel = null;
  }

  onAlternativePicked(sel: VariantPickerSelection) {
    this.draft.alternativeProductUuid = sel.productUuid;
    this.draft.alternativeVariantUuid = sel.variantUuid;
    this.draft.alternativeLabel = this.label(sel);
    if (sel.variantUuid) this.pickingAlternative = false;
  }

  clearAlternative() {
    this.draft.alternativeProductUuid = null;
    this.draft.alternativeVariantUuid = null;
    this.draft.alternativeLabel = null;
  }

  private label(sel: VariantPickerSelection): string | null {
    if (!sel.productName && !sel.variantSku) return null;
    const name = sel.variantName && sel.variantName !== 'Default' ? `${sel.productName} — ${sel.variantName}` : (sel.productName ?? '');
    return sel.variantSku ? `${name} (${sel.variantSku})` : name;
  }

  saveLine() {
    this.submitted = true;
    if (this.hasProblems || this.isSaving) return;
    this.isSaving = true;
    this.saveError = '';

    const call: Observable<unknown> = this.editing
      ? this.service.updateInquiryLine(this.inquiry.uuid, this.editing.uuid, toLineRequest(this.draft))
      : this.service.addInquiryLine(this.inquiry.uuid, toNewLineRequest(this.draft));

    call.subscribe({
      next: () => {
        this.isSaving = false;
        this.drawerVisible = false;
        this.messages.add({ severity: 'success', summary: this.editing ? 'Line saved' : 'Line added' });
        this.changed.emit();
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = serverMessage(err, 'The line could not be saved.');
      }
    });
  }

  // ── Delete ──────────────────────────────────────────────────────────────────

  confirmDelete(line: SaleInquiryLine) {
    if (!this.canEdit) return;
    this.confirmation.confirm({
      key: 'inquiry-lines',
      header: `Delete line ${line.lineNumber}?`,
      message: `"${line.productDescription}" will be removed from the inquiry.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete', rejectLabel: 'Keep',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.deleteLine(line)
    });
  }

  deleteLine(line: SaleInquiryLine) {
    if (this.busy[line.uuid]) return;
    this.busy[line.uuid] = true;
    this.service.deleteInquiryLine(this.inquiry.uuid, line.uuid).subscribe({
      next: () => {
        this.busy[line.uuid] = false;
        this.messages.add({ severity: 'success', summary: 'Line deleted' });
        this.changed.emit();
      },
      error: (err) => {
        this.busy[line.uuid] = false;
        this.messages.add({ severity: 'error', summary: 'Not deleted', detail: serverMessage(err, 'The line could not be deleted.') });
      }
    });
  }
}
