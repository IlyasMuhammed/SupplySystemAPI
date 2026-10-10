import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  DeliveryPackingModel,
  PackageModel,
  PackContentRequest,
  PatchPackageRequest,
  PACKAGE_TYPES
} from '../../../../services/logistics.service';
import { DELIVERY_STATUS_SEVERITY } from '../delivery-list/delivery-list.component';
import { packagesAmendableWhileStaged } from '../delivery-detail/delivery-detail.component';
import { FLOW } from '../../../../shared/flow';

/** The fields of a carton the packer can correct after packing (PATCH: a field left empty stays as it is). */
interface PackageEdit {
  lengthCm: number | null;
  widthCm: number | null;
  heightCm: number | null;
  grossWeightKg: number | null;
  netWeightKg: number | null;
  sealNumber: string;
}

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** One row of the "what goes in this carton" form. */
interface PackLineDraft {
  deliveryLineUuid: string;
  lineNo: number;
  itemDescription: string;
  unitOfMeasure?: string;
  /** What is picked and not yet boxed — the ceiling the server will enforce anyway. */
  available: number;
  qty: number | null;
  batchNumber: string;
}

/**
 * The dock screen: put picked goods into cartons, print the paperwork, stage, and issue.
 *
 * Every action here is gated on what the server says the delivery may do, not on a local guess —
 * `status` and `isFullyPacked` come back with each reload, and the screen re-reads rather than
 * patching its own state, so the buttons can never offer something the API refuses.
 */
@Component({
  selector: 'app-pack-station',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, TextareaModule, SelectModule, InputNumberModule, InputTextModule,
    ...FLOW
  ],
  templateUrl: './pack-station.component.html',
  styleUrls: ['./pack-station.component.scss'],
  providers: [MessageService]
})
export class PackStationComponent implements OnInit {
  uuid = '';
  packing: DeliveryPackingModel | null = null;
  isLoading = true;
  notFound = false;
  isSubmitting = false;

  // ── The carton being built ──────────────────────────────────────────────────

  packDialogVisible = false;
  packageTypes = PACKAGE_TYPES.map(code => ({ label: this.titleCase(code), value: code }));

  draft = this.emptyDraft();
  draftLines: PackLineDraft[] = [];

  // ── Voiding ─────────────────────────────────────────────────────────────────

  voidDialogVisible = false;
  voidTarget: PackageModel | null = null;
  voidReason = '';

  // ── Issuing ─────────────────────────────────────────────────────────────────

  issueDialogVisible = false;

  // ── A33 REV-04b — correcting a carton (weight, size, seal) ─────────────────

  /** STAGED by its route's auto-stage, not approved, on no live consignment: the server still lets cartons change. */
  amendableWhileStaged = false;
  editDialogVisible = false;
  editTarget: PackageModel | null = null;
  edit: PackageEdit = this.emptyEdit();
  private editOriginal: PackageEdit = this.emptyEdit();

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.logisticsService.getDeliveryPackages(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.packing = res.result;
          this.notFound = false;
          this.checkAmendableWhileStaged();
        } else {
          this.packing = null;
          this.notFound = true;
          this.amendableWhileStaged = false;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.packing = null;
        this.amendableWhileStaged = false;
        // A 404 means this delivery does not exist; anything else is a failure to ask, and
        // showing "not found" for a dropped connection sends someone hunting for a live record.
        this.notFound = err?.status === 404;
        if (!this.notFound) this.fail(err, 'Failed to load the delivery.');
      }
    });
  }

  /**
   * REV-04b. Only a STAGED delivery needs the question, and the packing model doesn't carry the route, the approval or
   * the consignments, so the delivery is read for them. If it can't be read the cartons stay frozen (today's rule):
   * the server is the judge either way.
   */
  private checkAmendableWhileStaged() {
    this.amendableWhileStaged = false;
    if (this.packing?.status !== 'STAGED') return;

    this.logisticsService.getDeliveryById(this.uuid).subscribe({
      next: (res) => this.amendableWhileStaged = !!res.success && packagesAmendableWhileStaged(res.result),
      error: () => this.amendableWhileStaged = false
    });
  }

  // ── What the dock may do ────────────────────────────────────────────────────

  get livePackages(): PackageModel[] {
    return this.packing?.packages.filter(p => !p.isVoided) ?? [];
  }

  get voidedPackages(): PackageModel[] {
    return this.packing?.packages.filter(p => p.isVoided) ?? [];
  }

  /** Packing is open while goods are off the shelf and before they leave the building. */
  get canPack(): boolean {
    return this.packing?.status === 'PICKED' || this.packing?.status === 'PACKED';
  }

  /**
   * Existing cartons can be corrected or voided: always while PICKED / PACKED, and (REV-04) while an auto-staged
   * delivery is still amendable. New cartons still wait for PICKED — voiding one steps the delivery back there.
   */
  get canAmend(): boolean {
    return this.canPack || (this.packing?.status === 'STAGED' && this.amendableWhileStaged);
  }

  get canStage(): boolean {
    return this.packing?.status === 'PACKED';
  }

  get canIssue(): boolean {
    return this.packing?.status === 'STAGED' || this.packing?.status === 'PENDING_APPROVAL';
  }

  /** The packing list needs a carton to describe; the gate pass needs the goods at the dock. */
  get canPrintPackingList(): boolean {
    return this.livePackages.length > 0;
  }

  get canPrintGatePass(): boolean {
    const status = this.packing?.status ?? '';
    return ['STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED', 'IN_TRANSIT',
            'DELIVERED', 'PARTIALLY_DELIVERED'].includes(status);
  }

  get nothingLeftToPack(): boolean {
    return (this.packing?.qtyUnpacked ?? 0) === 0;
  }

  // ── Packing ─────────────────────────────────────────────────────────────────

  openPackDialog() {
    // Only lines with something still on the floor: offering a row for a line that is already
    // fully boxed is a field whose only valid value is zero.
    this.draftLines = (this.packing?.lines ?? [])
      .filter(l => l.qtyToPack > 0)
      .map(l => ({
        deliveryLineUuid: l.deliveryLineUuid,
        lineNo: l.lineNo,
        itemDescription: l.itemDescription,
        unitOfMeasure: l.unitOfMeasure,
        available: l.qtyToPack,
        // Pre-filled with everything outstanding, which is what a packer usually does; changing
        // one number is quicker than typing every number.
        qty: l.qtyToPack,
        batchNumber: ''
      }));

    this.draft = this.emptyDraft();
    this.packDialogVisible = true;
  }

  get draftContents(): PackContentRequest[] {
    return this.draftLines
      .filter(l => (l.qty ?? 0) > 0)
      .map(l => ({
        deliveryLineUuid: l.deliveryLineUuid,
        qty: l.qty as number,
        batchNumber: l.batchNumber.trim() || undefined
      }));
  }

  /** A carton needs something in it, and nothing in it may exceed what is on the floor. */
  get canConfirmPack(): boolean {
    if (this.isSubmitting) return false;
    if (this.draftContents.length === 0) return false;
    return !this.draftLines.some(l => (l.qty ?? 0) > l.available);
  }

  get overPackedLine(): PackLineDraft | undefined {
    return this.draftLines.find(l => (l.qty ?? 0) > l.available);
  }

  confirmPack() {
    if (!this.canConfirmPack) return;

    this.isSubmitting = true;

    this.logisticsService.packDelivery(this.uuid, {
      packageBarcode: this.draft.packageBarcode.trim() || undefined,
      packageType:    this.draft.packageType,
      lengthCm:       this.draft.lengthCm       ?? undefined,
      widthCm:        this.draft.widthCm        ?? undefined,
      heightCm:       this.draft.heightCm       ?? undefined,
      grossWeightKg:  this.draft.grossWeightKg  ?? undefined,
      netWeightKg:    this.draft.netWeightKg    ?? undefined,
      sealNumber:     this.draft.sealNumber.trim() || undefined,
      parentPackageUuid: this.draft.parentPackageUuid || undefined,
      contents:       this.draftContents
    }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.packDialogVisible = false;
        this.ok('Package created.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The package could not be created.');
      }
    });
  }

  /** Pallets already on this delivery, as options to load a carton onto. */
  get palletOptions(): { label: string; value: string | null }[] {
    return [
      { label: 'Not on a pallet', value: null },
      ...this.livePackages
        // One level of nesting only, which is what the server enforces too.
        .filter(p => !p.parentPackageUuid)
        .map(p => ({ label: `${p.packageBarcode} (${this.titleCase(p.packageType)})`, value: p.uuid }))
    ];
  }

  // ── Voiding ─────────────────────────────────────────────────────────────────

  openVoidDialog(pkg: PackageModel) {
    this.voidTarget = pkg;
    this.voidReason = '';
    this.voidDialogVisible = true;
  }

  confirmVoid() {
    if (!this.voidTarget || !this.voidReason.trim() || this.isSubmitting) return;

    this.isSubmitting = true;

    this.logisticsService.voidPackage(this.voidTarget.uuid, { reason: this.voidReason.trim() })
      .subscribe({
        next: () => {
          this.isSubmitting = false;
          this.voidDialogVisible = false;
          this.ok('Package voided. Its contents are unpacked again.');
          this.load();
        },
        error: (err) => {
          this.isSubmitting = false;
          this.fail(err, 'The package could not be voided.');
        }
      });
  }

  // ── Staging and issuing ─────────────────────────────────────────────────────

  stage() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.stageDelivery(this.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Delivery staged at the dock.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The delivery could not be staged.');
      }
    });
  }

  confirmIssue() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.goodsIssueDelivery(this.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.issueDialogVisible = false;

        const result = res.result;
        this.messageService.add({
          severity: 'success',
          summary: 'Goods issued',
          // The server distinguishes posting the movement from recording one another document
          // already posted, and that distinction matters to whoever reconciles the ledger.
          detail: result?.postedStock
            ? `Stock posted: ${result.qtyOut} issued.`
            : result?.note ?? 'The source document had already posted the stock movement.',
          life: 6000
        });

        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The goods could not be issued.');
      }
    });
  }

  // ── Correcting a carton (REV-04b) ───────────────────────────────────────────

  private emptyEdit(): PackageEdit {
    return { lengthCm: null, widthCm: null, heightCm: null, grossWeightKg: null, netWeightKg: null, sealNumber: '' };
  }

  openEditDialog(pkg: PackageModel) {
    this.editTarget = pkg;
    this.editOriginal = {
      lengthCm: pkg.lengthCm ?? null, widthCm: pkg.widthCm ?? null, heightCm: pkg.heightCm ?? null,
      grossWeightKg: pkg.grossWeightKg ?? null, netWeightKg: pkg.netWeightKg ?? null, sealNumber: pkg.sealNumber ?? ''
    };
    this.edit = { ...this.editOriginal };
    this.editDialogVisible = true;
  }

  /**
   * Only the fields that changed and have a value. PATCH leaves an absent field as it is, so a cleared field can't
   * erase a weight — the server has no "clear" for these, and a courier needs them anyway.
   */
  get editChanges(): PatchPackageRequest {
    const changes: PatchPackageRequest = {};
    const numbers: (keyof Omit<PackageEdit, 'sealNumber'>)[] = ['lengthCm', 'widthCm', 'heightCm', 'grossWeightKg', 'netWeightKg'];
    for (const key of numbers) {
      const value = this.edit[key];
      if (value != null && value !== this.editOriginal[key]) changes[key] = value;
    }
    const seal = this.edit.sealNumber.trim();
    if (seal && seal !== this.editOriginal.sealNumber) changes.sealNumber = seal;
    return changes;
  }

  get canSaveEdit(): boolean {
    return !this.isSubmitting && !!this.editTarget && Object.keys(this.editChanges).length > 0;
  }

  saveEdit() {
    if (!this.canAmend || !this.canSaveEdit || !this.editTarget) return;
    this.isSubmitting = true;

    this.logisticsService.patchPackage(this.editTarget.uuid, this.editChanges).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.editDialogVisible = false;
        this.ok('Package updated.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The package could not be updated.');
      }
    });
  }

  // ── Documents ───────────────────────────────────────────────────────────────

  downloadPackingList() {
    this.download(
      this.logisticsService.downloadPackingList(this.uuid),
      `PackingList-${this.packing?.deliveryNumber ?? this.uuid}.pdf`);
  }

  downloadGatePass() {
    this.download(
      this.logisticsService.downloadGatePass(this.uuid),
      `GatePass-${this.packing?.deliveryNumber ?? this.uuid}.pdf`);
  }

  private download(request: { subscribe: Function }, fileName: string) {
    (request as any).subscribe({
      next: (blob: Blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        link.click();
        // Revoked immediately: the click has already handed the blob to the browser, and leaving
        // object URLs behind holds the whole PDF in memory for the life of the tab.
        URL.revokeObjectURL(url);
      },
      error: (err: any) => this.fail(err, 'The document could not be generated.')
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary';
  }

  titleCase(code?: string): string {
    if (!code) return '';
    return code.split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }

  dimensions(pkg: PackageModel): string {
    if (pkg.lengthCm == null || pkg.widthCm == null || pkg.heightCm == null) return '—';
    return `${pkg.lengthCm} × ${pkg.widthCm} × ${pkg.heightCm} cm`;
  }

  private emptyDraft() {
    return {
      packageBarcode: '',
      packageType: 'BOX',
      lengthCm: null as number | null,
      widthCm: null as number | null,
      heightCm: null as number | null,
      grossWeightKg: null as number | null,
      netWeightKg: null as number | null,
      sealNumber: '',
      parentPackageUuid: null as string | null
    };
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error',
      summary: 'Not allowed',
      // The server explains its refusals — "only 40 is picked and unpacked" — and that is far
      // more use to a packer than a generic failure.
      detail: err?.error?.message ?? fallback
    });
  }
}
