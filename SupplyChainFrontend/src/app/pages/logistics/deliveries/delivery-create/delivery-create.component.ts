import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { ToastModule } from 'primeng/toast';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CreateDeliveryRequest,
  CreateDeliveryFromSourceRequest,
  CreateDeliveryLineRequest,
  AddressRequest
} from '../../../../services/logistics.service';
import { DemandService } from '../../../../services/demand.service';
import { WarehouseService } from '../../../../services/warehouse.service';
import { MaterialService } from '../../../../services/material.service';
import { InventoryService } from '../../../../services/inventory.service';

/** What raises the delivery. TRANSFER has no document to read lines from, same as MANUAL. */
type Mode = 'MANUAL' | 'PO' | 'SRO' | 'MIV' | 'TRANSFER';

/** One row the person is typing in — becomes a `CreateDeliveryLineRequest` on save. */
interface LineDraft {
  itemDescription: string;
  unitOfMeasure: string;
  qtyOrdered: number | null;
  unitValue: number | null;
}

/** A source document's own line, shown read-only so the preview matches what "advise everything" sends. */
interface PreviewLine {
  description: string;
  qty: number;
  uom?: string;
}

/**
 * Raises a delivery — the one document nothing in this application could create until now.
 *
 * **Why this screen exists.** `LogisticsService.createDelivery` and `createDeliveryFromSource`
 * were wired up in T-18 and have sat unused since: no button anywhere called either one. T-19's
 * own notes said so explicitly — "No 'New delivery' button — there is no create screen yet" — and
 * left it that way on purpose, because a button routing nowhere is worse than none. This is that
 * screen, so the button now has somewhere to go.
 *
 * **Two shapes, one screen.** A delivery either copies its lines from a source document (PO, SRO,
 * MIV) or states them by hand (MANUAL, TRANSFER). Rather than two components, the mode switch
 * changes which half of the form is visible and which of the two create endpoints gets called —
 * the two request shapes are different enough that building one super-request and hoping the
 * server sorts it out would be the wrong kind of clever.
 *
 * **From a source document, the lines are not editable here.** Omitting `lines` on the request
 * advises every outstanding line in full, which is what the preview table shows before you commit
 * to it. Selecting a subset of lines, or a partial quantity, is `SourceLineSelection` on the same
 * endpoint and is real future work — deferred rather than half-built, the same call T-20 made
 * about packages and linked shipments.
 */
@Component({
  selector: 'app-delivery-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, DropdownModule,
    InputNumberModule, CalendarModule, ToastModule,
    TextareaModule, TooltipModule
  ],
  templateUrl: './delivery-create.component.html',
  styleUrls: ['./delivery-create.component.scss'],
  providers: [MessageService]
})
export class DeliveryCreateComponent implements OnInit {
  mode: Mode = 'MANUAL';

  readonly modeOptions: { label: string; value: Mode }[] = [
    { label: 'Manual — no source document',        value: 'MANUAL' },
    { label: 'From a Purchase Order (inbound)',     value: 'PO' },
    { label: 'From a Supplier Return (outbound)',   value: 'SRO' },
    { label: 'From a Material Issue (to site)',     value: 'MIV' },
    { label: 'Warehouse Transfer',                  value: 'TRANSFER' }
  ];

  readonly directionOptions = [
    { label: 'Outbound', value: 'OUTBOUND' },
    { label: 'Inbound',  value: 'INBOUND' }
  ];

  readonly priorityOptions = [
    { label: 'Low',    value: 'LOW' },
    { label: 'Normal', value: 'NORMAL' },
    { label: 'High',   value: 'HIGH' },
    { label: 'Urgent', value: 'URGENT' }
  ];

  // ── Shared header fields ─────────────────────────────────────────────────────
  direction = 'OUTBOUND';
  priority  = 'NORMAL';
  incoterm  = '';
  notes     = '';
  requestedDateVal: Date | null = null;
  promisedDateVal:  Date | null = null;

  // ── Addresses (MANUAL / PO / SRO / MIV — not TRANSFER, which moves between warehouses) ───────
  shipToAddress: AddressRequest   = { line1: '', cityName: '', countryName: '' };
  shipFromAddress: AddressRequest = { line1: '', cityName: '', countryName: '' };
  useShipFromAddress = false;

  // ── Manual / Transfer lines ──────────────────────────────────────────────────
  lines: LineDraft[] = [this.blankLine()];

  // ── Transfer ──────────────────────────────────────────────────────────────────
  warehouseOptions: { label: string; value: string }[] = [];
  shipFromWarehouseUuid: string | null = null;
  shipToWarehouseUuid: string | null = null;

  // ── From-source ───────────────────────────────────────────────────────────────
  poOptions:  { label: string; value: string }[] = [];
  sroOptions: { label: string; value: string }[] = [];
  mivOptions: { label: string; value: string }[] = [];
  sourceUuid: string | null = null;

  previewLines: PreviewLine[] = [];
  isLoadingPreview = false;
  isLoadingSources = false;

  isSaving = false;

  constructor(
    private logisticsService: LogisticsService,
    private demandService: DemandService,
    private warehouseService: WarehouseService,
    private materialService: MaterialService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit() {
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        if (res.success && res.result)
          this.warehouseOptions = res.result.map(w => ({ label: `${w.name} (${w.code})`, value: w.uuid }));
      },
      error: () => this.warehouseOptions = []
    });

    this.loadSourceOptions();
  }

  // ── Switching mode ───────────────────────────────────────────────────────────

  onModeChange() {
    this.sourceUuid = null;
    this.previewLines = [];
    this.loadSourceOptions();
  }

  /**
   * Filtered to the statuses the backend will actually accept — a PO that is still DRAFT, or
   * already CLOSED, would only be offered here to be refused a moment later.
   */
  private loadSourceOptions() {
    if (this.mode === 'PO' && this.poOptions.length === 0) {
      this.isLoadingSources = true;
      this.demandService.getPos({ page: 1, pageSize: 200 }).subscribe({
        next: (res) => {
          this.isLoadingSources = false;
          const advisable = ['APPROVED', 'SENT', 'PARTIALLY_RECEIVED'];
          this.poOptions = (res.result?.data ?? [])
            .filter(p => advisable.includes(p.status))
            .map(p => ({ label: `${p.poNumber} — ${p.supplierName}`, value: p.uuid }));
        },
        error: () => { this.isLoadingSources = false; this.poOptions = []; }
      });
    } else if (this.mode === 'SRO' && this.sroOptions.length === 0) {
      this.isLoadingSources = true;
      this.warehouseService.getSros({ status: 'APPROVED', page: 1, pageSize: 200 }).subscribe({
        next: (res) => {
          this.isLoadingSources = false;
          this.sroOptions = (res.result?.data ?? [])
            .map(s => ({ label: `${s.sroNumber} — ${s.supplierName}`, value: s.uuid }));
        },
        error: () => { this.isLoadingSources = false; this.sroOptions = []; }
      });
    } else if (this.mode === 'MIV' && this.mivOptions.length === 0) {
      this.isLoadingSources = true;
      this.materialService.getMivs({ status: 'POSTED', page: 1, pageSize: 200 }).subscribe({
        next: (res) => {
          this.isLoadingSources = false;
          this.mivOptions = (res.result?.data ?? [])
            .map(m => ({ label: `${m.issueNo} — ${m.mirRequestNo}`, value: m.uuid }));
        },
        error: () => { this.isLoadingSources = false; this.mivOptions = []; }
      });
    }
  }

  /** Loads the source's own lines, purely so the preview shows what "advise everything" will send. */
  onSourceSelected() {
    this.previewLines = [];
    if (!this.sourceUuid) return;

    this.isLoadingPreview = true;
    const finish = () => this.isLoadingPreview = false;

    if (this.mode === 'PO') {
      this.demandService.getPoById(this.sourceUuid).subscribe({
        next: (res) => {
          this.previewLines = (res.result?.lines ?? [])
            .map(l => ({ description: l.itemDescription, qty: l.quantity, uom: l.unitOfMeasure }));
          finish();
        },
        error: finish
      });
    } else if (this.mode === 'SRO') {
      this.warehouseService.getSroById(this.sourceUuid).subscribe({
        next: (res) => {
          this.previewLines = (res.result?.lines ?? [])
            .filter(l => l.qtyToReturn > 0)
            .map(l => ({ description: l.itemDescription, qty: l.qtyToReturn, uom: l.unitOfMeasure }));
          finish();
        },
        error: finish
      });
    } else if (this.mode === 'MIV') {
      this.materialService.getMiv(this.sourceUuid).subscribe({
        next: (res) => {
          this.previewLines = (res.result?.lines ?? [])
            .filter(l => l.issuedQty > 0)
            .map(l => ({ description: l.itemDescription, qty: l.issuedQty, uom: l.unitOfMeasure }));
          finish();
        },
        error: finish
      });
    }
  }

  get sourceOptions(): { label: string; value: string }[] {
    switch (this.mode) {
      case 'PO':  return this.poOptions;
      case 'SRO': return this.sroOptions;
      case 'MIV': return this.mivOptions;
      default:    return [];
    }
  }

  get sourceLabel(): string {
    switch (this.mode) {
      case 'PO':  return 'purchase order';
      case 'SRO': return 'supplier return';
      case 'MIV': return 'material issue voucher';
      default:    return 'source document';
    }
  }

  get previewTotalQty(): number {
    return this.previewLines.reduce((sum, l) => sum + l.qty, 0);
  }

  // ── Manual / transfer lines ───────────────────────────────────────────────────

  private blankLine(): LineDraft {
    return { itemDescription: '', unitOfMeasure: '', qtyOrdered: null, unitValue: null };
  }

  addLine() { this.lines.push(this.blankLine()); }

  removeLine(i: number) {
    this.lines.splice(i, 1);
    if (this.lines.length === 0) this.lines.push(this.blankLine());
  }

  // ── Validation ────────────────────────────────────────────────────────────────

  /** What the server will refuse, said before the round trip — mirrors DeliveryRepository.CreateAsync. */
  get validationError(): string | null {
    if (this.mode === 'PO' || this.mode === 'SRO' || this.mode === 'MIV')
      return this.sourceUuid ? null : `Select a ${this.sourceLabel} first.`;

    if (this.mode === 'TRANSFER') {
      if (!this.shipFromWarehouseUuid || !this.shipToWarehouseUuid)
        return 'A transfer needs both a source and a destination warehouse — it posts a stock movement out of one and into the other.';
      if (this.shipFromWarehouseUuid === this.shipToWarehouseUuid)
        return 'Source and destination warehouses must differ.';
    }

    // MANUAL and TRANSFER both state their own lines.
    const real = this.lines.filter(l => l.itemDescription.trim());
    if (real.length === 0)
      return 'A delivery must have at least one line. Without lines it cannot be picked, packed, rated or received.';

    const badLine = real.find(l => !l.qtyOrdered || l.qtyOrdered <= 0);
    if (badLine)
      return `"${badLine.itemDescription}": quantity must be greater than zero.`;

    return null;
  }

  get canSave(): boolean {
    return !this.isSaving && !this.validationError;
  }

  // ── Save ──────────────────────────────────────────────────────────────────────

  save() {
    const error = this.validationError;
    if (error) {
      this.messageService.add({ severity: 'warn', summary: 'Check the form', detail: error });
      return;
    }

    this.isSaving = true;

    if (this.mode === 'PO' || this.mode === 'SRO' || this.mode === 'MIV') {
      const req: CreateDeliveryFromSourceRequest = {
        sourceType: this.mode,
        sourceUuid: this.sourceUuid!,
        shipToAddress:   this.hasAddress(this.shipToAddress) ? this.shipToAddress : undefined,
        shipFromAddress: this.useShipFromAddress && this.hasAddress(this.shipFromAddress)
          ? this.shipFromAddress : undefined,
        requestedDate: this.requestedDateVal?.toISOString(),
        promisedDate:  this.promisedDateVal?.toISOString(),
        priority:      this.priority,
        incoterm:      this.incoterm.trim() || undefined,
        notes:         this.notes.trim() || undefined
        // lines: omitted deliberately — advises every outstanding line, which is what the
        // preview table above already showed before this was pressed.
      };
      this.logisticsService.createDeliveryFromSource(req).subscribe(this.saveHandlers());
      return;
    }

    const req: CreateDeliveryRequest = {
      sourceType: this.mode,
      direction:  this.mode === 'MANUAL' ? this.direction : undefined,
      shipFromWarehouseUuid: this.mode === 'TRANSFER' ? this.shipFromWarehouseUuid! : undefined,
      shipToWarehouseUuid:   this.mode === 'TRANSFER' ? this.shipToWarehouseUuid!   : undefined,
      shipToAddress:   this.mode !== 'TRANSFER' && this.hasAddress(this.shipToAddress) ? this.shipToAddress : undefined,
      shipFromAddress: this.mode !== 'TRANSFER' && this.useShipFromAddress && this.hasAddress(this.shipFromAddress)
        ? this.shipFromAddress : undefined,
      requestedDate: this.requestedDateVal?.toISOString(),
      promisedDate:  this.promisedDateVal?.toISOString(),
      priority:      this.priority,
      incoterm:      this.incoterm.trim() || undefined,
      notes:         this.notes.trim() || undefined,
      lines: this.lines
        .filter(l => l.itemDescription.trim())
        .map((l): CreateDeliveryLineRequest => ({
          itemDescription: l.itemDescription.trim(),
          unitOfMeasure:   l.unitOfMeasure.trim() || undefined,
          qtyOrdered:      l.qtyOrdered!,
          unitValue:       l.unitValue ?? undefined
        }))
    };

    this.logisticsService.createDelivery(req).subscribe(this.saveHandlers());
  }

  /**
   * An address with no line 1, city or country is not an address the server can use — it throws
   * on a structurally meaningless one rather than silently dropping it, so this either sends all
   * three or none, matching what T-07 documented as the actual rule.
   */
  private hasAddress(a: AddressRequest): boolean {
    return !!(a.line1?.trim() && a.cityName?.trim() && a.countryName?.trim());
  }

  private saveHandlers() {
    return {
      next: (res: any) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Delivery raised.' });
        this.router.navigate(['/portal/pages/logistics/deliveries', res.result]);
      },
      error: (err: any) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'error', summary: 'Could not create the delivery',
          detail: err?.error?.message || 'Something went wrong.', life: 8000
        });
      }
    };
  }
}
