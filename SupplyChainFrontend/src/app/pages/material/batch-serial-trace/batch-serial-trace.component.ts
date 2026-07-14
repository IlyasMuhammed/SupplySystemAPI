import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TimelineModule } from 'primeng/timeline';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import {
  MaterialService,
  ChainOfCustodyResponse,
  IssueEventInfo
} from '../../../services/material.service';

@Component({
  selector: 'app-batch-serial-trace',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, InputTextModule, TagModule,
    ToastModule, TimelineModule, CardModule
  ],
  templateUrl: './batch-serial-trace.component.html',
  styleUrls: ['./batch-serial-trace.component.scss'],
  providers: [MessageService]
})
export class BatchSerialTraceComponent {
  reference   = '';
  isSearching = false;
  result: ChainOfCustodyResponse | null = null;
  searched    = false;

  constructor(
    private materialService: MaterialService,
    private messageService:  MessageService
  ) {}

  search() {
    if (!this.reference.trim()) return;
    this.isSearching = true;
    this.result      = null;
    this.searched    = false;

    this.materialService.traceBatchSerial(this.reference.trim()).subscribe({
      next: (res) => {
        this.isSearching = false;
        this.searched    = true;
        if (res.success && res.result) {
          this.result = res.result;
        }
      },
      error: (err) => {
        this.isSearching = false;
        this.searched    = true;
        const msg = err?.error?.message ?? 'No records found for this reference.';
        this.messageService.add({ severity: 'warn', summary: 'Not Found', detail: msg });
      }
    });
  }

  getMivStatusSeverity(s: string): 'success' | 'warn' | 'secondary' | 'info' | 'danger' | 'contrast' {
    switch (s) {
      case 'POSTED':    return 'success';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }

  get timelineEvents(): { label: string; icon: string; color: string; data: any }[] {
    if (!this.result) return [];
    const events: { label: string; icon: string; color: string; data: any }[] = [];

    if (this.result.grnReceipt) {
      events.push({
        label: 'GRN Receipt',
        icon:  'pi pi-truck',
        color: '#10b981',
        data:  this.result.grnReceipt
      });
    }

    if (this.result.currentLocation) {
      events.push({
        label: 'Inventory Location',
        icon:  'pi pi-warehouse',
        color: '#7c3aed',
        data:  this.result.currentLocation
      });
    }

    (this.result.issueEvents ?? []).forEach(ev => {
      events.push({
        label: `Issued — ${ev.issueNo}`,
        icon:  'pi pi-send',
        color: ev.mivStatus === 'POSTED' ? '#f59e0b' : '#94a3b8',
        data:  ev
      });
    });

    return events;
  }

  isGrnEvent(data: any): boolean { return 'grnNumber' in data; }
  isLocationEvent(data: any): boolean { return 'warehouseName' in data && !('issueNo' in data); }
  isIssueEvent(data: any): data is IssueEventInfo { return 'issueNo' in data; }
}
