import { Component, EventEmitter, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';

import {
  CoverageIssueModel, CoverageKindModel, CoverageModel, QuickBooksIntegrationService, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { QboTabKey } from '../quickbooks.shared';
import { TAB_LABEL, coverageLabel, coveragePercent, coverageRows, issueTab, tabForArea } from '../a44.shared';

/**
 * A44 D-27 — is everything QuickBooks needs in place? READY / NOT READY with the blocking issues (each linked to the tab
 * that fixes it), and how much of each kind is mapped. Read-only; Refresh asks again.
 */
@Component({
  selector: 'app-qbo-coverage-tab',
  standalone: true,
  imports: [CommonModule, ButtonModule, TagModule],
  templateUrl: './coverage-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './coverage-tab.component.scss']
})
export class CoverageTabComponent implements OnInit {
  @Output() navigate = new EventEmitter<QboTabKey>();

  coverage: CoverageModel | null = null;
  /** The bars: entities, tax codes, provider defaults, GL accounts (information). */
  rows: CoverageKindModel[] = [];
  isLoading = true;
  loadFailed = false;
  loadError = '';

  readonly percent = coveragePercent;
  readonly kindLabel = coverageLabel;

  constructor(private service: QuickBooksIntegrationService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getCoverage().subscribe({
      next: (c) => { this.isLoading = false; this.coverage = c; this.rows = coverageRows(c); },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'Coverage could not be checked.');
      }
    });
  }

  get ready(): boolean { return !!this.coverage?.ready; }

  /** Tax codes / rates without a mapping, listed under the tax bar. */
  get unmappedTax(): string[] { return this.coverage?.taxCodes?.unmapped ?? []; }

  issueTab(i: CoverageIssueModel): QboTabKey { return issueTab(i); }

  kindTab(k: CoverageKindModel): QboTabKey { return tabForArea(k.kind); }

  tabLabel(tab: QboTabKey): string { return TAB_LABEL[tab] ?? tab; }

  tone(k: CoverageKindModel): 'ok' | 'wn' | 'er' | 'info' {
    if (k.informational) return 'info';
    const p = coveragePercent(k);
    return p >= 100 ? 'ok' : p >= 50 ? 'wn' : 'er';
  }

  go(tab: QboTabKey): void { this.navigate.emit(tab); }
}
