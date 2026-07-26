import { Component, OnInit, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageModule } from 'primeng/message';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { MessageService } from 'primeng/api';
import { Subscription } from 'rxjs';
import { DemandService, CreateQuotationRequest, PrListItemModel, PoListItemModel } from '../../../../services/demand.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-quotation-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, CardModule, InputTextModule, TextareaModule,
    InputNumberModule, DropdownModule, CalendarModule,
    DividerModule, ToastModule, TooltipModule, MessageModule, ProgressSpinnerModule, AttachmentListComponent
  ],
  templateUrl: './quotation-create.component.html',
  styleUrls: ['./quotation-create.component.scss'],
  providers: [MessageService]
})
export class QuotationCreateComponent implements OnInit, OnDestroy {
  form!: FormGroup;
  isSubmitting  = false;
  loadingPrLines = false;

  // Generated once per page load so attachments uploaded before save can be linked to the
  // eventual Quotation (which is created with this same UUID on submit).
  readonly quotationUuid = crypto.randomUUID();

  // ── Product catalogue ─────────────────────────────────────────────────────
  productOptions: { label: string; value: string }[] = [];
  private productsMap = new Map<string, ProductListItemModel>();
  loadingProducts = false;

  // ── PR dropdown ───────────────────────────────────────────────────────────
  prOptions: { label: string; value: string }[] = [];
  loadingPrs = false;
  selectedPrLabel: string | null = null;

  // ── PO dropdown ───────────────────────────────────────────────────────────
  poOptions: { label: string; value: string }[] = [];
  loadingPos = false;
  selectedPoLabel: string | null = null;

  sourceTypeOptions = [
    { label: 'Standalone (no linked PR/PO)', value: 'STANDALONE' },
    { label: 'Linked to Purchase Requisition', value: 'PR' },
    { label: 'Linked to Purchase Order',       value: 'PO' }
  ];

  uomOptions = [
    { label: 'PC',   value: 'PC' },
    { label: 'BOX',  value: 'BOX' },
    { label: 'KG',   value: 'KG' },
    { label: 'L',    value: 'L' },
    { label: 'M',    value: 'M' },
    { label: 'SET',  value: 'SET' },
    { label: 'UNIT', value: 'UNIT' }
  ];

  private subs = new Subscription();

  constructor(
    private fb: FormBuilder,
    private demandService: DemandService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router
  ) {
    this.buildForm();
  }

  ngOnInit() {
    this.loadProducts();

    // Watch sourceType changes to load PR list or clear selection
    this.subs.add(
      this.form.get('sourceType')!.valueChanges.subscribe(type => {
        this.form.get('sourceId')!.setValue(null, { emitEvent: false });
        this.selectedPrLabel = null;
        this.selectedPoLabel = null;
        this.clearAndAddOneLine();

        if (type === 'PR') {
          this.loadPrOptions();
        } else if (type === 'PO') {
          this.loadPoOptions();
        }
      })
    );
  }

  ngOnDestroy() { this.subs.unsubscribe(); }

  // ── Product loading ───────────────────────────────────────────────────────

  private loadProducts() {
    this.loadingProducts = true;
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: res => {
        this.loadingProducts = false;
        const data = res?.result?.data ?? [];
        this.productsMap.clear();
        this.productOptions = data.map(p => {
          this.productsMap.set(p.uuid, p);
          return { label: p.name, value: p.uuid };
        });
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  onProductChange(i: number) {
    const uuid = this.lines.at(i).get('productId')?.value;
    if (!uuid) return;
    const p = this.productsMap.get(uuid);
    if (!p) return;
    this.lines.at(i).patchValue({
      itemDescription: p.name,
      specification:   (p as any).description ?? '',
      unitOfMeasure:   p.uomCode ?? 'PC'
    });
  }

  // ── PR dropdown ───────────────────────────────────────────────────────────

  private loadPrOptions() {
    this.loadingPrs = true;
    this.prOptions  = [];
    // Load APPROVED and PARTIALLY_CONVERTED PRs — these are the ones that can be quoted
    this.demandService.getPrs({ status: 'APPROVED', pageSize: 200, page: 1 }).subscribe({
      next: res => {
        this.loadingPrs = false;
        const data = (res?.result?.data ?? []) as PrListItemModel[];
        this.prOptions = data.map(pr => ({
          label: `${pr.prNumber} — ${pr.prTitle}`,
          value: pr.uuid
        }));
        if (this.prOptions.length === 0) {
          this.messageService.add({
            severity: 'info', summary: 'No Approved PRs',
            detail: 'There are no approved requisitions available to link.'
          });
        }
      },
      error: () => {
        this.loadingPrs = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load requisitions.' });
      }
    });
  }

  onPrSelected(prUuid: string) {
    if (!prUuid) {
      this.selectedPrLabel = null;
      this.clearAndAddOneLine();
      return;
    }

    this.loadingPrLines = true;
    this.demandService.getPrById(prUuid).subscribe({
      next: res => {
        this.loadingPrLines = false;
        const pr = res?.result;
        if (!pr) return;

        this.selectedPrLabel = `${pr.prNumber} — ${pr.prTitle}`;

        // Replace line items with lines from the selected PR
        while (this.lines.length > 0) this.lines.removeAt(0);

        for (const line of pr.lines) {
          this.lines.push(this.fb.group({
            sourcePrLineUuid: [line.uuid],
            productId:        [line.productId   ?? null],
            itemDescription:  [line.itemDescription, Validators.required],
            specification:    [line.specification   ?? ''],
            unitOfMeasure:    [line.unitOfMeasure   ?? 'PC'],
            quantity:         [line.quantity, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
            requiredDate:     [line.requiredDate ? new Date(line.requiredDate) : null],
            lineNotes:        [line.lineNotes    ?? '']
          }));
        }

        if (pr.lines.length === 0) {
          this.lines.push(this.newLine());
        }
      },
      error: () => {
        this.loadingPrLines = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load requisition lines.' });
      }
    });
  }

  // ── PO dropdown ───────────────────────────────────────────────────────────

  private loadPoOptions() {
    this.loadingPos = true;
    this.poOptions  = [];
    this.demandService.getPos({ page: 1, pageSize: 200 }).subscribe({
      next: res => {
        this.loadingPos = false;
        const data = (res?.result?.data ?? []) as PoListItemModel[];
        this.poOptions = data.map(po => ({
          label: `${po.poNumber}${po.title ? ' — ' + po.title : ''}`,
          value: po.uuid
        }));
        if (this.poOptions.length === 0) {
          this.messageService.add({
            severity: 'info', summary: 'No Purchase Orders',
            detail: 'There are no purchase orders available to link.'
          });
        }
      },
      error: () => {
        this.loadingPos = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load purchase orders.' });
      }
    });
  }

  onPoSelected(poUuid: string) {
    if (!poUuid) {
      this.selectedPoLabel = null;
      return;
    }
    const match = this.poOptions.find(o => o.value === poUuid);
    this.selectedPoLabel = match?.label ?? null;
  }

  // ── Form helpers ──────────────────────────────────────────────────────────

  private buildForm() {
    this.form = this.fb.group({
      title:      ['', [Validators.required, Validators.minLength(3)]],
      sourceType: ['STANDALONE', Validators.required],
      sourceId:   [null],
      dueDate:    [null],
      notes:      [''],
      lines:      this.fb.array([this.newLine()])
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  get sourceType(): string { return this.form.get('sourceType')?.value ?? 'STANDALONE'; }

  newLine(): FormGroup {
    return this.fb.group({
      sourcePrLineUuid: [null],
      productId:        [null],
      itemDescription:  ['', Validators.required],
      specification:    [''],
      unitOfMeasure:    ['PC'],
      quantity:         [1, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      requiredDate:     [null],
      lineNotes:        ['']
    });
  }

  private clearAndAddOneLine() {
    while (this.lines.length > 0) this.lines.removeAt(0);
    this.lines.push(this.newLine());
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  // ── Submit ────────────────────────────────────────────────────────────────

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSubmitting = true;
    const v = this.form.value;

    const req: CreateQuotationRequest = {
      title:      v.title,
      sourceType: v.sourceType,
      sourceId:   v.sourceId || undefined,
      dueDate:    v.dueDate instanceof Date ? v.dueDate.toISOString() : v.dueDate || undefined,
      notes:      v.notes || undefined,
      lines: v.lines.map((l: any) => ({
        sourcePrLineUuid: l.sourcePrLineUuid || undefined,
        productId:        l.productId        || undefined,
        itemDescription:  l.itemDescription,
        specification:    l.specification    || undefined,
        unitOfMeasure:    l.unitOfMeasure    || undefined,
        quantity:         l.quantity,
        requiredDate:     l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:        l.lineNotes        || undefined
      })),
      quotationUuid: this.quotationUuid
    };

    this.demandService.createQuotation(req).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Quotation created successfully.' });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/quotations', res.result]), 1200);
        }
      },
      error: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to create quotation.' });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name);
    return !!(c?.invalid && c.touched);
  }

  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lines.at(i).get(name);
    return !!(c?.invalid && c.touched);
  }
}
