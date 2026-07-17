import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { SidebarModule } from 'primeng/sidebar';
import { DropdownModule } from 'primeng/dropdown';
import { FormsModule } from '@angular/forms';
import { TimelineService, TimelineDetail, TimelineEvent } from '../../services/timeline.service';

interface FilterOption {
  label: string;
  value: string;
}

// Interface-code prefixes this panel understands; anything else falls back to a neutral colour/label.
const INTERFACE_META: Record<string, { label: string; color: string; route: string }> = {
  PR:           { label: 'Purchase Requisition',            color: '#3b82f6', route: '/portal/pages/demand/requisitions' },
  QUOTATION:    { label: 'Quotation',                        color: '#14b8a6', route: '/portal/pages/demand/quotations' },
  PO:           { label: 'Purchase Order',                   color: '#1e3a8a', route: '/portal/pages/demand/purchase-orders' },
  GRN:          { label: 'GRN',                               color: '#22c55e', route: '/portal/pages/warehouse/grn' },
  GRN_QC:       { label: 'GRN (QC)',                          color: '#22c55e', route: '/portal/pages/warehouse/grn' },
  INVOICE:      { label: 'Invoice',                           color: '#f59e0b', route: '/portal/pages/finance/invoices' },
  MIR_GENERAL:  { label: 'Material Issue Request',            color: '#8b5cf6', route: '/portal/pages/material/mir' },
  MIR_PROJECT:  { label: 'Material Issue Request (Project)',  color: '#8b5cf6', route: '/portal/pages/material/mir' }
};

@Component({
  selector: 'app-timeline-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, SidebarModule, DropdownModule],
  templateUrl: './timeline-panel.component.html',
  styleUrls: ['./timeline-panel.component.scss']
})
export class TimelinePanelComponent implements OnChanges {
  // Primary usage: a document's own id + interface code (each of the 6 detail screens calls by-document).
  @Input() documentId?: string;
  @Input() interfaceCode?: string;
  // Alternate usage: when the trace_id is already known, skip resolution and fetch directly.
  @Input() traceId?: string;

  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();

  isLoading  = false;
  loadFailed = false;
  detail: TimelineDetail | null = null;

  selectedFilter = 'ALL';
  filterOptions: FilterOption[] = [{ label: 'All Types', value: 'ALL' }];

  constructor(private timelineService: TimelineService, private router: Router) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['visible'] && this.visible) {
      this.load();
    }
  }

  onVisibleChange(v: boolean) {
    this.visible = v;
    this.visibleChange.emit(v);
  }

  close() {
    this.visible = false;
    this.visibleChange.emit(false);
  }

  load() {
    this.isLoading  = true;
    this.loadFailed = false;
    this.detail     = null;
    this.selectedFilter = 'ALL';
    this.filterOptions  = [{ label: 'All Types', value: 'ALL' }];

    const request$ = this.traceId
      ? this.timelineService.getByTraceId(this.traceId)
      : (this.documentId && this.interfaceCode)
        ? this.timelineService.getByDocument(this.interfaceCode, this.documentId)
        : null;

    if (!request$) {
      this.isLoading  = false;
      this.loadFailed = true;
      return;
    }

    request$.subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.detail = res.result;
          this.buildFilterOptions();
        } else {
          this.loadFailed = true;
        }
      },
      error: () => {
        this.isLoading  = false;
        this.loadFailed = true;
      }
    });
  }

  private buildFilterOptions() {
    const codes = Array.from(new Set((this.detail?.events ?? []).map(e => e.interfaceCode)));
    this.filterOptions = [
      { label: 'All Types', value: 'ALL' },
      ...codes.map(c => ({ label: this.interfaceLabel(c), value: c }))
    ];
  }

  get filteredEvents(): TimelineEvent[] {
    const events = this.detail?.events ?? [];
    return this.selectedFilter === 'ALL' ? events : events.filter(e => e.interfaceCode === this.selectedFilter);
  }

  colorFor(code: string): string {
    return INTERFACE_META[(code || '').toUpperCase()]?.color ?? '#94a3b8';
  }

  interfaceLabel(code: string): string {
    return INTERFACE_META[(code || '').toUpperCase()]?.label ?? code;
  }

  describeEvent(e: TimelineEvent): string {
    const humanized = (e.eventType || '')
      .toLowerCase()
      .split('_')
      .map(w => w.charAt(0).toUpperCase() + w.slice(1))
      .join(' ');
    return e.notes ? `${humanized} — ${e.notes}` : humanized;
  }

  goToDocument(e: TimelineEvent) {
    const meta = INTERFACE_META[(e.interfaceCode || '').toUpperCase()];
    if (!meta) return;
    this.close();
    this.router.navigate([meta.route, e.documentId]);
  }
}
