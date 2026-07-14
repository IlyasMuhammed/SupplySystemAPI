import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { SelectButtonModule } from 'primeng/selectbutton';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  MaterialService,
  MivReturnableResponse,
  ReturnableLine,
  CreateReturnLineRequest,
  CreateReturnRequest
} from '../../../../services/material.service';

interface ReturnLine extends ReturnableLine {
  returnQty: number;
  condition: 'GOOD' | 'DAMAGED';
  reason: string;
  included: boolean;
}

@Component({
  selector: 'app-return-create',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, InputTextModule, InputNumberModule, TextareaModule,
    TagModule, ToastModule, SelectButtonModule, DropdownModule
  ],
  templateUrl: './return-create.component.html',
  styleUrls: ['./return-create.component.scss'],
  providers: [MessageService]
})
export class ReturnCreateComponent implements OnInit {
  step: 'select' | 'form' = 'select';

  mivOptions: { label: string; value: string }[] = [];
  isLoadingMivs = false;
  selectedMivUuid = '';

  isSearching = false;
  isSaving    = false;

  returnable: MivReturnableResponse | null = null;
  lines: ReturnLine[] = [];
  headerNotes = '';

  conditionOptions = [
    { label: 'Good',    value: 'GOOD' },
    { label: 'Damaged', value: 'DAMAGED' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService,
    private router:          Router
  ) {}

  ngOnInit() {
    this.loadMivOptions();
  }

  loadMivOptions() {
    this.isLoadingMivs = true;
    this.materialService.getMivs({ status: 'POSTED', pageSize: 500 }).subscribe({
      next: (res) => {
        this.isLoadingMivs = false;
        if (res.success && res.result?.data) {
          this.mivOptions = res.result.data.map(m => ({
            label: `${m.issueNo}  —  MIR: ${m.mirRequestNo}${m.issuedTo ? '  ·  ' + m.issuedTo : ''}`,
            value: m.uuid
          }));
        }
      },
      error: () => { this.isLoadingMivs = false; }
    });
  }

  onMivSelect(uuid: string) {
    if (!uuid) return;
    this.isSearching = true;
    this.returnable = null;
    this.lines = [];

    this.materialService.getMivReturnable(uuid).subscribe({
      next: (res) => {
        this.isSearching = false;
        if (res.success && res.result) {
          this.returnable = res.result;
          this.lines = res.result.lines.map(l => ({
            ...l,
            returnQty: 0,
            condition: 'GOOD',
            reason:    '',
            included:  false
          }));
          this.step = 'form';
        } else {
          this.messageService.add({ severity: 'error', summary: 'Not Found', detail: res.message || 'MIV not found or has no returnable lines.' });
          this.selectedMivUuid = '';
        }
      },
      error: (err) => {
        this.isSearching = false;
        this.selectedMivUuid = '';
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to load voucher.'
        });
      }
    });
  }

  changeMiv() {
    this.step = 'select';
    this.selectedMivUuid = '';
    this.returnable = null;
    this.lines = [];
    this.headerNotes = '';
  }

  get selectedLines(): ReturnLine[] {
    return this.lines.filter(l => l.included && l.returnQty > 0);
  }

  get goodCount(): number    { return this.selectedLines.filter(l => l.condition === 'GOOD').length; }
  get damagedCount(): number { return this.selectedLines.filter(l => l.condition === 'DAMAGED').length; }

  get hasErrors(): boolean {
    for (const l of this.lines.filter(l => l.included)) {
      if (l.returnQty <= 0) return true;
      if (l.returnQty > l.returnableQty) return true;
      if (l.condition === 'DAMAGED' && !l.reason.trim()) return true;
    }
    return this.selectedLines.length === 0;
  }

  save() {
    if (this.hasErrors) return;
    this.isSaving = true;

    const req: CreateReturnRequest = {
      mivUuid: this.returnable!.mivUuid,
      notes:   this.headerNotes || undefined,
      lines:   this.selectedLines.map(l => ({
        mivLineUuid:     l.lineUuid,
        returnedQty:     l.returnQty,
        condition:       l.condition,
        reason:          l.reason || undefined,
        inventoryItemId: l.inventoryItemId > 0 ? l.inventoryItemId : undefined
      }) as CreateReturnLineRequest)
    };

    this.materialService.createReturn(req).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success && res.result?.uuid) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Return voucher created as DRAFT.' });
          setTimeout(() => this.router.navigate(['/portal/pages/material/returns', res.result!.uuid]), 1200);
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'error', summary: 'Error',
          detail: err?.error?.message ?? 'Failed to create return.'
        });
      }
    });
  }
}
