import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CarrierInvoiceModel,
  InvoiceMatchResultModel,
  InvoiceLineMatchModel,
  MatchCandidateModel,
  ThreeWayMatchModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * One carrier's bill: its lines, what each is tied to, and how the whole thing compares with what
 * was expected.
 *
 * **Two comparisons, in order.** Matching ties lines to movements; the three-way match then
 * compares money. The screen keeps them apart because the questions are different — *which
 * movement is this?* and *is the amount right?* — and the first has to be answered before the
 * second means anything.
 */
@Component({
  selector: 'app-carrier-invoice-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule, TextareaModule
  ],
  templateUrl: './carrier-invoice-detail.component.html',
  styleUrls: ['./carrier-invoice-detail.component.scss'],
  providers: [MessageService]
})
export class CarrierInvoiceDetailComponent implements OnInit {
  uuid = '';
  invoice: CarrierInvoiceModel | null = null;
  matches: InvoiceMatchResultModel | null = null;
  threeWay: ThreeWayMatchModel | null = null;

  isLoading = true;
  notFound = false;
  isSubmitting = false;
  isMatching = false;

  // ── Deciding a line by hand ─────────────────────────────────────────────────

  lineDialogVisible = false;
  workingLine: InvoiceLineMatchModel | null = null;
  candidates: MatchCandidateModel[] = [];
  chosenConsignment: string | null = null;
  lineNote = '';
  lineAction: 'match' | 'exclude' | 'unmatch' = 'match';

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.logisticsService.getCarrierInvoiceById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.invoice = res.result ?? null;
        this.notFound = !this.invoice;

        if (this.invoice) {
          this.loadMatches();
          this.loadThreeWay();
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.invoice = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.fail(err, 'The bill could not be loaded.');
      }
    });
  }

  private loadMatches() {
    this.logisticsService.getInvoiceMatches(this.uuid).subscribe({
      next: (res) => this.matches = res.result ?? null,
      error: () => this.matches = null
    });
  }

  /** The preview, which records nothing — safe to load with the page. */
  private loadThreeWay() {
    this.logisticsService.previewThreeWayMatch(this.uuid).subscribe({
      next: (res) => this.threeWay = res.result ?? null,
      error: () => this.threeWay = null
    });
  }

  // ── Reading ─────────────────────────────────────────────────────────────────

  get isWithdrawn(): boolean {
    return this.invoice?.status === 'CANCELLED';
  }

  get canWork(): boolean {
    return !!this.invoice && !this.isWithdrawn && !this.isSubmitting;
  }

  statusSeverity(status?: string): Severity {
    switch (status) {
      case 'MATCHED':  return 'success';
      case 'DISPUTED': return 'warn';
      case 'RECEIVED': return 'info';
      default:         return 'secondary';
    }
  }

  matchSeverity(status: string): Severity {
    switch (status) {
      case 'MATCHED':   return 'success';
      case 'AMBIGUOUS': return 'warn';
      case 'EXCLUDED':  return 'secondary';
      default:          return 'danger';
    }
  }

  matchLabel(status: string): string {
    switch (status) {
      case 'MATCHED':   return 'Matched';
      case 'AMBIGUOUS': return 'Needs a person';
      case 'EXCLUDED':  return 'Set aside';
      default:          return 'Unmatched';
    }
  }

  /** How strong the claim is. Matched by hand is weaker than matched on an airway bill. */
  methodLabel(method?: string): string {
    switch (method) {
      case 'AWB':       return 'on the airway bill';
      case 'REFERENCE': return 'on our reference';
      case 'MANUAL':    return 'by hand';
      default:          return '';
    }
  }

  outcomeSeverity(outcome: string): Severity {
    switch (outcome) {
      case 'WITHIN_TOLERANCE': return 'success';
      case 'OVERCHARGED':      return 'danger';
      case 'UNDERCHARGED':     return 'warn';
      default:                 return 'secondary';
    }
  }

  outcomeLabel(outcome: string): string {
    switch (outcome) {
      case 'WITHIN_TOLERANCE': return 'As expected';
      case 'OVERCHARGED':      return 'Overcharged';
      case 'UNDERCHARGED':     return 'Undercharged';
      default:                 return 'Nothing to compare';
    }
  }

  reasonLabel(reason?: string): string {
    switch (reason) {
      case 'WEIGHT':    return 'Weight';
      case 'SURCHARGE': return 'Surcharge';
      case 'SERVICE':   return 'Service';
      case 'UNEXPLAINED': return 'Unexplained';
      default:          return '';
    }
  }

  // ── Matching ────────────────────────────────────────────────────────────────

  runMatching() {
    if (!this.canWork || this.isMatching) return;
    this.isMatching = true;

    this.logisticsService.matchCarrierInvoice(this.uuid).subscribe({
      next: (res) => {
        this.isMatching = false;
        this.matches = res.result ?? this.matches;

        const left = (this.matches?.unmatched ?? 0) + (this.matches?.ambiguous ?? 0);

        if (left > 0) {
          // The lines nothing matched are where a wrong charge goes unnoticed, so the toast says
          // how many rather than simply reporting success.
          this.messageService.add({
            severity: 'warn', summary: 'Some lines need a person',
            detail: `${left} line(s) could not be tied to a movement.`, life: 8000
          });
        } else {
          this.ok('Every line is tied to a movement.');
        }

        this.loadThreeWay();
      },
      error: (err) => {
        this.isMatching = false;
        this.fail(err, 'The bill could not be matched.');
      }
    });
  }

  runThreeWay() {
    if (!this.canWork || this.isMatching) return;
    this.isMatching = true;

    this.logisticsService.runThreeWayMatch(this.uuid).subscribe({
      next: (res) => {
        this.isMatching = false;
        this.threeWay = res.result ?? this.threeWay;

        this.messageService.add({
          severity: this.threeWay?.isClean ? 'success' : 'warn',
          summary: this.threeWay?.isClean ? 'Bill agrees' : 'Bill disputed',
          detail: this.threeWay?.isClean
            ? 'Every charge is within tolerance.'
            : 'Something does not agree — see the comparison below.',
          life: 8000
        });

        this.load();
      },
      error: (err) => {
        this.isMatching = false;
        this.fail(err, 'The comparison could not be run.');
      }
    });
  }

  // ── Deciding a line ─────────────────────────────────────────────────────────

  openLine(line: InvoiceLineMatchModel, action: 'match' | 'exclude' | 'unmatch') {
    this.workingLine = line;
    this.lineAction = action;
    this.lineNote = '';
    this.chosenConsignment = null;
    this.candidates = [];
    this.lineDialogVisible = true;

    if (action === 'match') {
      this.logisticsService.getMatchCandidates(line.lineUuid).subscribe({
        next: (res) => this.candidates = res.result ?? [],
        error: () => this.candidates = []
      });
    }
  }

  get canDecide(): boolean {
    if (this.isSubmitting || !this.lineNote.trim()) return false;
    // A match needs something to match to; the other two only need the reason.
    return this.lineAction !== 'match' || !!this.chosenConsignment;
  }

  confirmLine() {
    if (!this.canDecide || !this.workingLine) return;
    this.isSubmitting = true;

    const done = (message: string) => {
      this.isSubmitting = false;
      this.lineDialogVisible = false;
      this.ok(message);
      this.loadMatches();
      this.loadThreeWay();
    };

    const failed = (err: any) => {
      this.isSubmitting = false;
      this.fail(err, 'That could not be recorded.');
    };

    const line = this.workingLine.lineUuid;
    const note = this.lineNote.trim();

    if (this.lineAction === 'match') {
      this.logisticsService.matchInvoiceLine(line, this.chosenConsignment!, note)
        .subscribe({ next: () => done('Line matched.'), error: failed });
    } else if (this.lineAction === 'exclude') {
      this.logisticsService.excludeInvoiceLine(line, note)
        .subscribe({ next: () => done('Line set aside.'), error: failed });
    } else {
      this.logisticsService.unmatchInvoiceLine(line, note)
        .subscribe({ next: () => done('Line returned to the queue.'), error: failed });
    }
  }

  get dialogHeader(): string {
    switch (this.lineAction) {
      case 'exclude': return 'Not a movement charge';
      case 'unmatch': return 'Undo this match';
      default:        return 'Match this line by hand';
    }
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
