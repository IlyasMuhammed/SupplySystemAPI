import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { DemandService, PatchQuotationRequest } from '../../../../services/demand.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';

@Component({
  selector: 'app-quotation-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule, InputNumberModule,
    DropdownModule, CalendarModule, DividerModule, ToastModule, TooltipModule,
    ProductVariantPickerComponent
  ],
  templateUrl: './quotation-edit.component.html',
  styleUrls: ['./quotation-edit.component.scss'],
  providers: [MessageService]
})
export class QuotationEditComponent implements OnInit {
  uuid = '';
  form!: FormGroup;
  isLoading = true;
  isSubmitting = false;
  minDate = new Date();

  products: ProductListItemModel[] = [];
  loadingProducts = false;

  // UOM options from FSD Section 6.5 — kept identical across every line-item form (PR/Quotation/PO)
  // so a value copied from one document's lines always matches an option in the next.
  uomOptions = [
    { label: 'Each (EA)',    value: 'EA'   },
    { label: 'Kilogram (KG)', value: 'KG'  },
    { label: 'Litre (LTR)',  value: 'LTR'  },
    { label: 'Box (BOX)',    value: 'BOX'  },
    { label: 'Set (SET)',    value: 'SET'  },
    { label: 'Meter (MTR)',  value: 'MTR'  },
    { label: 'Packet (PKT)', value: 'PKT'  },
    { label: 'Pair (PAIR)',  value: 'PAIR' }
  ];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private demandService: DemandService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.buildForm();
    this.loadProducts();
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  private loadProducts() {
    this.loadingProducts = true;
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: res => {
        this.loadingProducts = false;
        this.products = res?.result?.data ?? [];
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  // PV-004 — mirrors po-create.component.ts's onLineVariantSelected (no price field on
  // Quotation lines).
  onLineVariantSelected(i: number, sel: VariantPickerSelection) {
    const line = this.lines.at(i);
    line.patchValue({
      productUuid: sel.productUuid,
      variantUuid: sel.variantUuid
    });
    if (sel.variantUuid) {
      const label = sel.variantName && sel.variantName !== 'Default'
        ? `${sel.productName} — ${sel.variantName}`
        : (sel.productName ?? '');
      line.patchValue({
        itemDescription: label,
        unitOfMeasure:   sel.uomCode ?? line.get('unitOfMeasure')?.value ?? null
      });
    }
  }

  private buildForm() {
    this.form = this.fb.group({
      title:   ['', [Validators.required, Validators.minLength(3)]],
      dueDate: [null],
      notes:   [''],
      lines:   this.fb.array([this.newLine()])
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    return this.fb.group({
      productUuid:     [null],
      variantUuid:     [null],
      itemDescription: ['', Validators.required],
      specification:   [''],
      unitOfMeasure:   [null],
      quantity:        [1, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      requiredDate:    [null],
      lineNotes:       [''],
      budgetCode:      ['']
    });
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  load() {
    this.isLoading = true;
    this.demandService.getQuotationById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Quotation not found.' });
          return;
        }
        const q = res.result;
        if (q.status !== 'DRAFT') {
          this.messageService.add({ severity: 'warn', summary: 'Read-only', detail: `Only DRAFT quotations can be edited. Status: ${q.status}` });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/quotations', this.uuid]), 1500);
          return;
        }

        while (this.lines.length) this.lines.removeAt(0);
        (q.lines ?? []).forEach(l => this.lines.push(this.fb.group({
          productUuid:     [(l as any).productUuid ?? null],
          variantUuid:     [(l as any).productId ?? null],
          itemDescription: [l.itemDescription, Validators.required],
          specification:   [l.specification ?? ''],
          unitOfMeasure:   [l.unitOfMeasure ?? null],
          quantity:        [l.quantity, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
          requiredDate:    [l.requiredDate ? new Date(l.requiredDate) : null],
          lineNotes:       [(l as any).lineNotes ?? ''],
          budgetCode:      [(l as any).budgetCode ?? '']
        })));
        if (!this.lines.length) this.lines.push(this.newLine());

        this.form.patchValue({
          title:   q.title,
          dueDate: q.dueDate ? new Date(q.dueDate) : null,
          notes:   q.notes ?? ''
        });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load quotation.' });
      }
    });
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSubmitting = true;
    const v = this.form.getRawValue();
    const req: PatchQuotationRequest = {
      title:   v.title,
      dueDate: v.dueDate instanceof Date ? v.dueDate.toISOString() : v.dueDate || undefined,
      notes:   v.notes || undefined,
      lines: v.lines.map((l: any) => ({
        productId:       l.variantUuid     || undefined,
        itemDescription: l.itemDescription,
        specification:   l.specification   || undefined,
        unitOfMeasure:   l.unitOfMeasure   || undefined,
        quantity:        l.quantity,
        requiredDate:    l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:       l.lineNotes        || undefined,
        budgetCode:      l.budgetCode       || undefined
      }))
    };

    this.demandService.patchQuotation(this.uuid, req).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Quotation updated.' });
        setTimeout(() => this.router.navigate(['/portal/pages/demand/quotations', this.uuid]), 1200);
      },
      error: (err) => {
        this.isSubmitting = false;
        const detail = err?.error?.message ?? `Failed to update quotation. (HTTP ${err?.status ?? 0})`;
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name); return !!(c?.invalid && c.touched);
  }
  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lines.at(i).get(name); return !!(c?.invalid && c.touched);
  }
}
