import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { SelectModule } from 'primeng/select';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  InvoiceLineMatchModel,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * Every charge nobody could tie to a movement, across every bill.
 *
 * **Largest first.** The biggest unexplained charge is the one worth an hour of somebody's time,
 * and a queue ordered by date buries it — which is how a wrong charge goes unnoticed.
 */
@Component({
  selector: 'app-match-queue',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, SelectModule
  ],
  templateUrl: './match-queue.component.html',
  styleUrls: ['./match-queue.component.scss'],
  providers: [MessageService]
})
export class MatchQueueComponent implements OnInit {
  lines: InvoiceLineMatchModel[] = [];
  carriers: CarrierListItemModel[] = [];

  total = 0;
  page = 1;
  pageSize = 20;

  isLoading = true;

  filter = { carrierUuid: null as string | null, matchStatus: null as string | null };

  readonly statusOptions = [
    { label: 'Everything needing a person', value: null },
    { label: 'Unmatched',                   value: 'UNMATCHED' },
    { label: 'More than one movement fits', value: 'AMBIGUOUS' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
    this.loadCarriers();
  }

  load(event?: TableLazyLoadEvent) {
    if (event) {
      this.pageSize = event.rows ?? this.pageSize;
      this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    }

    this.isLoading = true;

    this.logisticsService.getMatchQueue({
      carrierUuid: this.filter.carrierUuid ?? undefined,
      matchStatus: this.filter.matchStatus ?? undefined,
      page:        this.page,
      pageSize:    this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.lines = res.result?.data ?? [];
        this.total = res.result?.totalRecords ?? 0;
      },
      error: (err) => {
        this.isLoading = false;
        this.lines = [];
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The queue could not be loaded.'
        });
      }
    });
  }

  search() {
    this.page = 1;
    this.load();
  }

  private loadCarriers() {
    this.logisticsService.getActiveCarriers().subscribe({
      next: (res) => this.carriers = res.result ?? [],
      error: () => this.carriers = []
    });
  }

  get carrierOptions() {
    return [
      { label: 'Every carrier', value: null },
      ...this.carriers.map(c => ({ label: c.name, value: c.uuid }))
    ];
  }

  /** What the queue is worth in total, on the page shown. */
  get pageValue(): number {
    return this.lines.reduce((sum, l) => sum + Math.abs(l.amount), 0);
  }

  matchSeverity(status: string): Severity {
    return status === 'AMBIGUOUS' ? 'warn' : 'danger';
  }

  matchLabel(status: string): string {
    return status === 'AMBIGUOUS' ? 'Needs a person' : 'Unmatched';
  }
}
