import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TableModule } from 'primeng/table';
import { CalendarModule } from 'primeng/calendar';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  MaterialService, ProjectDetail, CostLedgerEntry, CostLedgerFilter
} from '../../../../services/material.service';

@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule, ConfirmDialogModule,
    TableModule, CalendarModule
  ],
  templateUrl: './project-detail.component.html',
  styleUrls: ['./project-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ProjectDetailComponent implements OnInit {
  uuid!: string;
  project: ProjectDetail | null = null;
  isLoading = true;

  // Cost ledger
  activeTab: 'details' | 'cost-ledger' = 'details';
  ledgerLoading = false;
  ledgerEntries: CostLedgerEntry[] = [];
  ledgerTotal   = 0;
  ledgerPage    = 1;
  ledgerPageSize = 20;
  ledgerFilter: CostLedgerFilter = {};
  dateFrom: Date | null = null;
  dateTo:   Date | null = null;

  get netCost(): number {
    return this.ledgerEntries.reduce((s, e) => s + e.amount, 0);
  }
  get totalIssued(): number {
    return this.ledgerEntries.filter(e => e.amount > 0).reduce((s, e) => s + e.amount, 0);
  }
  get totalReturned(): number {
    return this.ledgerEntries.filter(e => e.amount < 0).reduce((s, e) => s + Math.abs(e.amount), 0);
  }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private materialService: MaterialService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid')!;
    this.load();
  }

  load() {
    this.isLoading = true;
    this.materialService.getProject(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.project   = res.success ? res.result ?? null : null;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load project.' });
      }
    });
  }

  switchTab(tab: 'details' | 'cost-ledger') {
    this.activeTab = tab;
    if (tab === 'cost-ledger' && this.ledgerEntries.length === 0) {
      this.loadLedger();
    }
  }

  loadLedger() {
    this.ledgerLoading = true;
    const filter: CostLedgerFilter = {
      ...this.ledgerFilter,
      dateFrom: this.dateFrom?.toISOString(),
      dateTo:   this.dateTo?.toISOString(),
      page:     this.ledgerPage,
      pageSize: this.ledgerPageSize
    };
    this.materialService.getProjectCostLedger(this.uuid, filter).subscribe({
      next: (res) => {
        this.ledgerLoading = false;
        if (res.success && res.result) {
          this.ledgerEntries = res.result.data;
          this.ledgerTotal   = res.result.totalRecords;
        }
      },
      error: () => { this.ledgerLoading = false; }
    });
  }

  onLedgerPage(event: any) {
    this.ledgerPage     = Math.floor(event.first / event.rows) + 1;
    this.ledgerPageSize = event.rows;
    this.loadLedger();
  }

  applyDateFilter() {
    this.ledgerPage = 1;
    this.loadLedger();
  }

  confirmDelete() {
    this.confirmationService.confirm({
      message: 'Are you sure you want to delete this project?',
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      accept: () => {
        this.materialService.deleteProject(this.uuid).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Project deleted.' });
            setTimeout(() => this.router.navigate(['/portal/pages/material/projects']), 1200);
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to delete.' })
        });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'ACTIVE':    return 'success';
      case 'ON_HOLD':   return 'warn';
      case 'COMPLETED': return 'info';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      ACTIVE: 'Active', ON_HOLD: 'On Hold', COMPLETED: 'Completed', CANCELLED: 'Cancelled'
    };
    return map[s] ?? s;
  }
}
