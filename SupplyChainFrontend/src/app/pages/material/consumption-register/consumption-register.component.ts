import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DividerModule } from 'primeng/divider';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  MaterialService,
  ConsumptionRegisterResponse,
  MirListItem
} from '../../../services/material.service';

@Component({
  selector: 'app-consumption-register',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, TableModule, TagModule, ToastModule, DividerModule, DropdownModule
  ],
  templateUrl: './consumption-register.component.html',
  styleUrls: ['./consumption-register.component.scss'],
  providers: [MessageService]
})
export class ConsumptionRegisterComponent implements OnInit {
  mirOptions: { label: string; value: string }[] = [];
  isLoadingMirs = false;
  selectedMirUuid = '';

  isLoading = false;
  register: ConsumptionRegisterResponse | null = null;

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  ngOnInit() {
    this.loadMirOptions();
  }

  loadMirOptions() {
    this.isLoadingMirs = true;
    this.materialService.getMirs({ pageSize: 500 }).subscribe({
      next: (res) => {
        this.isLoadingMirs = false;
        if (res.success && res.result?.data) {
          this.mirOptions = res.result.data.map(m => ({
            label: this.mirLabel(m),
            value: m.uuid
          }));
        }
      },
      error: () => { this.isLoadingMirs = false; }
    });
  }

  private mirLabel(m: MirListItem): string {
    let label = `${m.requestNo}  —  ${m.status}`;
    if (m.projectName)     label += `  ·  ${m.projectName}`;
    else if (m.department) label += `  ·  ${m.department}`;
    return label;
  }

  onMirSelect(uuid: string) {
    if (!uuid) return;
    this.isLoading = true;
    this.register  = null;
    this.materialService.getConsumptionRegister(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) this.register = res.result;
        else this.messageService.add({ severity: 'warn', summary: 'Not found', detail: 'No consumption data for this MIR.' });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load register.' });
      }
    });
  }

  clearSelection() {
    this.selectedMirUuid = '';
    this.register = null;
  }

  get totalIssued()   { return this.register?.lines.reduce((s, l) => s + l.issuedQty,   0) ?? 0; }
  get totalConsumed() { return this.register?.lines.reduce((s, l) => s + l.consumedQty, 0) ?? 0; }
  get totalReturned() { return this.register?.lines.reduce((s, l) => s + l.returnedQty, 0) ?? 0; }
  get totalWasted()   { return this.register?.lines.reduce((s, l) => s + l.wastedQty,   0) ?? 0; }
  get totalBalance()  { return this.register?.lines.reduce((s, l) => s + l.balanceQty,  0) ?? 0; }
  get totalValue()    { return this.register?.lines.reduce((s, l) => s + l.balanceValue, 0) ?? 0; }

  getBalanceSeverity(balanceQty: number): 'success' | 'warn' | 'danger' {
    if (balanceQty === 0) return 'success';
    if (balanceQty < 0)  return 'danger';
    return 'warn';
  }
}
