import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';
import {
  MaterialService,
  MirIssuableResponse,
  MirLineIssuable,
  CreateMivRequest,
  AvailableBatchesResponse,
  AvailableBatchRow,
  AvailableSerialRow
} from '../../../../services/material.service';

interface IssueLine extends MirLineIssuable {
  issuedQtyInput: number;
  notes: string;
  error?: string;
  // Batch/serial selection state
  batchData?: AvailableBatchesResponse;
  batchLoading: boolean;
  batchQtys: Record<number, number>;      // inventoryItemId -> qty to draw from that batch
  selectedSerialIds: Set<number>;          // inventoryItemId of chosen serials
}

@Component({
  selector: 'app-miv-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, InputNumberModule,
    CalendarModule, TagModule, ToastModule,
    ProgressSpinnerModule, CheckboxModule
  ],
  templateUrl: './miv-create.component.html',
  styleUrls: ['./miv-create.component.scss'],
  providers: [MessageService]
})
export class MivCreateComponent implements OnInit {
  mirUuid   = '';
  isLoading = true;
  isSaving  = false;

  mirData: MirIssuableResponse | null = null;
  lines: IssueLine[] = [];

  issuedTo  = '';
  issueDate: Date = new Date();
  notes     = '';

  constructor(
    private route:           ActivatedRoute,
    private router:          Router,
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() {
    this.mirUuid = this.route.snapshot.queryParamMap.get('mirUuid') ?? '';
    if (!this.mirUuid) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Missing MIR UUID.' });
      this.isLoading = false;
      return;
    }
    this.loadIssuable();
  }

  loadIssuable() {
    this.isLoading = true;
    this.materialService.getMivIssuable(this.mirUuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.mirData = res.result;
          this.lines   = res.result.lines.map(l => ({
            ...l,
            issuedQtyInput:   l.availableToIssue,
            notes:            '',
            batchLoading:     false,
            batchQtys:        {},
            selectedSerialIds: new Set<number>()
          }));
          // Auto-load batch/serial data for tracked lines
          this.lines.forEach(line => {
            if (line.isBatchTracked || line.isSerialTracked) {
              this.loadBatchData(line);
            }
          });
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to load issuable data.'
        });
      }
    });
  }

  loadBatchData(line: IssueLine) {
    line.batchLoading = true;
    this.materialService.getAvailableBatches(line.productUuid).subscribe({
      next: (res) => {
        line.batchLoading = false;
        if (res.success && res.result) {
          line.batchData = res.result;
          // Pre-fill batch qtys using FEFO suggestion (fill from oldest expiry first)
          if (line.isBatchTracked && res.result.batches.length > 0) {
            this.autoFillBatchQtys(line);
          }
        }
      },
      error: () => {
        line.batchLoading = false;
      }
    });
  }

  autoFillBatchQtys(line: IssueLine) {
    if (!line.batchData) return;
    let remaining = line.issuedQtyInput;
    line.batchQtys = {};
    for (const batch of line.batchData.batches) {
      if (remaining <= 0) break;
      const take = Math.min(remaining, batch.qtyAvailable);
      if (take > 0) {
        line.batchQtys[batch.inventoryItemId] = take;
        remaining -= take;
      }
    }
  }

  onQtyChange(line: IssueLine) {
    if (line.issuedQtyInput <= 0) {
      line.error = 'Qty must be greater than 0.';
    } else if (line.issuedQtyInput > line.availableToIssue) {
      line.error = `Exceeds available qty (${line.availableToIssue}).`;
    } else {
      line.error = undefined;
    }
    // Re-fill batch suggestion when qty changes
    if (line.isBatchTracked && line.batchData) {
      this.autoFillBatchQtys(line);
    }
    // Reset serial selection if qty changes
    if (line.isSerialTracked) {
      line.selectedSerialIds = new Set<number>();
    }
  }

  getBatchTotal(line: IssueLine): number {
    return Object.values(line.batchQtys).reduce((s, v) => s + (v || 0), 0);
  }

  isBatchSelectionValid(line: IssueLine): boolean {
    if (!line.isBatchTracked) return true;
    if (!line.batchData || line.batchData.batches.length === 0) return true; // non-strict if no data yet
    return Math.abs(this.getBatchTotal(line) - line.issuedQtyInput) < 0.0001;
  }

  isSerialSelectionValid(line: IssueLine): boolean {
    if (!line.isSerialTracked) return true;
    if (!line.batchData) return true;
    return line.selectedSerialIds.size === Math.round(line.issuedQtyInput);
  }

  toggleSerial(line: IssueLine, serial: AvailableSerialRow) {
    if (line.selectedSerialIds.has(serial.inventoryItemId)) {
      line.selectedSerialIds.delete(serial.inventoryItemId);
    } else {
      // Enforce cap = issuedQty
      if (line.selectedSerialIds.size < Math.round(line.issuedQtyInput)) {
        line.selectedSerialIds.add(serial.inventoryItemId);
      }
    }
    // Force Angular change detection on Set mutation
    line.selectedSerialIds = new Set(line.selectedSerialIds);
  }

  isSerialSelected(line: IssueLine, serial: AvailableSerialRow): boolean {
    return line.selectedSerialIds.has(serial.inventoryItemId);
  }

  get hasErrors(): boolean {
    return this.lines.some(l =>
      !!l.error ||
      l.issuedQtyInput <= 0 ||
      !this.isBatchSelectionValid(l) ||
      !this.isSerialSelectionValid(l)
    );
  }

  get totalValue(): number {
    return this.lines.reduce((sum, l) => sum + (l.issuedQtyInput * l.unitCost), 0);
  }

  save() {
    if (this.hasErrors) return;

    const req: CreateMivRequest = {
      mirUuid:   this.mirUuid,
      issuedTo:  this.issuedTo?.trim() || undefined,
      issueDate: this.issueDate?.toISOString(),
      notes:     this.notes?.trim() || undefined,
      lines: this.lines
        .filter(l => l.issuedQtyInput > 0)
        .map(l => ({
          mirLineUuid: l.lineUuid,
          issuedQty:   l.issuedQtyInput,
          notes:       l.notes?.trim() || undefined,
          batchSelections: l.isBatchTracked
            ? Object.entries(l.batchQtys)
                .filter(([, qty]) => qty > 0)
                .map(([id, qty]) => ({ inventoryItemId: +id, issuedQty: qty }))
            : [],
          serialSelections: l.isSerialTracked
            ? [...l.selectedSerialIds].map(id => ({ inventoryItemId: id }))
            : []
        }))
    };

    this.isSaving = true;
    this.materialService.createMiv(req).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success && res.result) {
          this.messageService.add({ severity: 'success', summary: 'Success', detail: 'Issue voucher created.' });
          setTimeout(() => this.router.navigate(['/portal/pages/material/miv', res.result]), 500);
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to create issue voucher.'
        });
      }
    });
  }

  cancel() {
    this.router.navigate(['/portal/pages/material/mir', this.mirUuid]);
  }

  getLineStatusSeverity(s: string): 'success' | 'warn' | 'secondary' | 'info' | 'danger' | 'contrast' {
    switch (s) {
      case 'FULLY_ISSUED':     return 'success';
      case 'PARTIALLY_ISSUED': return 'warn';
      default:                 return 'secondary';
    }
  }

  isExpiringSoon(expiryDate?: string): boolean {
    if (!expiryDate) return false;
    const diff = new Date(expiryDate).getTime() - Date.now();
    return diff > 0 && diff < 30 * 24 * 60 * 60 * 1000;
  }

  objectEntries(obj: Record<number, number>): [string, number][] {
    return Object.entries(obj);
  }
}
