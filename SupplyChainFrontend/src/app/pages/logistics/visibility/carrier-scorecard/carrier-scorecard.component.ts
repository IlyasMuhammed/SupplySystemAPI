import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CarrierScorecardModel,
  CarrierScoreModel,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * How each carrier has actually performed.
 *
 * **There is no overall score, and this screen does not invent one.** Rolling on-time, exceptions
 * and billing accuracy into a single number needs weights nobody has agreed, and a carrier that is
 * cheap and late would come out wherever those weights put it. The sort chooses which question is
 * being asked; the measures stay beside each other.
 *
 * **Every percentage is shown with the count it came from.** "100% on time" from two consignments
 * and from two hundred are different claims, and a bare percentage makes them look identical.
 */
@Component({
  selector: 'app-carrier-scorecard',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule,
    SelectModule, DatePickerModule
  ],
  templateUrl: './carrier-scorecard.component.html',
  styleUrls: ['./carrier-scorecard.component.scss'],
  providers: [MessageService]
})
export class CarrierScorecardComponent implements OnInit {
  /** Matches the server's own bar. Below it, a percentage is noise. */
  static readonly MinimumForRate = 10;

  scorecard: CarrierScorecardModel | null = null;
  carriers: CarrierListItemModel[] = [];

  isLoading = true;

  filter = {
    from: null as Date | null,
    to: null as Date | null,
    carrierUuid: null as string | null,
    sortBy: 'VOLUME' as string
  };

  readonly sortOptions = [
    { label: 'Most consignments',  value: 'VOLUME' },
    { label: 'Best on time',       value: 'ON_TIME' },
    { label: 'Most exceptions',    value: 'EXCEPTIONS' },
    { label: 'Worst billing',      value: 'BILLING' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    // Ninety days back, which is the server's default said out loud rather than left implicit.
    this.filter.to = new Date();
    this.filter.from = new Date(Date.now() - 90 * 86_400_000);

    this.load();
    this.loadCarriers();
  }

  load() {
    this.isLoading = true;

    this.logisticsService.getCarrierScorecard({
      from:        this.filter.from?.toISOString(),
      to:          this.filter.to?.toISOString(),
      carrierUuid: this.filter.carrierUuid ?? undefined,
      sortBy:      this.filter.sortBy
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.scorecard = res.result ?? null;
      },
      error: (err) => {
        this.isLoading = false;
        this.scorecard = null;
        this.fail(err, 'The scorecard could not be loaded.');
      }
    });
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

  get carriersOnCard(): CarrierScoreModel[] {
    return this.scorecard?.carriers ?? [];
  }

  /** True when the caller may not see what the company pays. Said, never shown as zeroes. */
  get billingWithheld(): boolean {
    return this.carriersOnCard.length > 0 && !this.carriersOnCard[0].billing;
  }

  // ── Saying a figure honestly ────────────────────────────────────────────────

  /**
   * A percentage, or a dash. **Never zero for "we could not tell"** — that is the difference
   * between a carrier that was late and one nothing could be judged about.
   */
  percent(value: number | undefined | null): string {
    return value === null || value === undefined ? '—' : `${value}%`;
  }

  /** The count a percentage was worked out from. A rate without its denominator is a rumour. */
  outOf(part: number, whole: number): string {
    return `${part} of ${whole}`;
  }

  /** Below the server's bar, the number is real and the comparison is not. */
  isThinSample(carrier: CarrierScoreModel): boolean {
    return carrier.consignments < CarrierScorecardComponent.MinimumForRate;
  }

  onTimeTag(carrier: CarrierScoreModel): Severity {
    if (carrier.onTimePercent === null || carrier.onTimePercent === undefined) return 'secondary';
    if (carrier.onTimePercent >= 95) return 'success';
    if (carrier.onTimePercent >= 85) return 'warn';
    return 'danger';
  }

  exceptionTag(carrier: CarrierScoreModel): Severity {
    if (carrier.criticalExceptions > 0) return 'danger';
    if ((carrier.exceptionsPer100 ?? 0) > 10) return 'warn';
    return 'secondary';
  }

  billingTag(carrier: CarrierScoreModel): Severity {
    const accuracy = carrier.billing?.accuracyPercent;

    if (accuracy === null || accuracy === undefined) return 'secondary';
    if (accuracy >= 95) return 'success';
    if (accuracy >= 80) return 'warn';
    return 'danger';
  }

  proofTag(carrier: CarrierScoreModel): Severity {
    const coverage = carrier.proofCoveragePercent;

    if (coverage === null || coverage === undefined) return 'secondary';
    return coverage >= 95 ? 'success' : coverage >= 80 ? 'warn' : 'danger';
  }

  /** Signed, because the direction is the point: positive means overcharged. */
  varianceLabel(variance: number, currency: string): string {
    const sign = variance > 0 ? '+' : '';
    return `${sign}${variance.toFixed(2)} ${currency}`;
  }

  hasVariance(carrier: CarrierScoreModel): boolean {
    return (carrier.billing?.variance?.length ?? 0) > 0;
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
