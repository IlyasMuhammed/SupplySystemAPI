import { Component, ElementRef, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { TooltipModule } from 'primeng/tooltip';
import { Subscription } from 'rxjs';

import { AuthService } from '../../service/auth.service';
import { ReportDef, SALES_REPORTS, findReport } from './sales-report-definitions';
import { SalesReportPanelComponent } from './sales-report-panel/sales-report-panel.component';

const BASE = '/portal/pages/reports/sales-reports';

/** The sales reports (A29 §15) in one place: pick a report, filter it, read its tables and charts, download it. */
@Component({
  selector: 'app-sales-reports',
  standalone: true,
  imports: [CommonModule, RouterModule, TooltipModule, SalesReportPanelComponent],
  templateUrl: './sales-reports.component.html',
  styleUrls: ['./sales-reports.component.scss']
})
export class SalesReportsComponent implements OnInit, OnDestroy {
  @ViewChild('panelAnchor') panelAnchor?: ElementRef<HTMLElement>;

  reports = SALES_REPORTS;

  /** The report showing: the one in the address, if this user may see it. */
  selected: ReportDef | null = null;
  /** The report in the address, when this user may not see it. */
  denied: ReportDef | null = null;
  /** What the address named, when there is no such report. */
  unknownKey: string | null = null;

  private params?: Subscription;

  constructor(private route: ActivatedRoute, private router: Router, public authService: AuthService) {}

  ngOnInit() {
    // The route is reused when one report gives way to another, so follow the address, not a snapshot of it.
    this.params = this.route.paramMap.subscribe(params => this.show(params.get('report')));
  }

  ngOnDestroy() {
    this.params?.unsubscribe();
  }

  private show(key: string | null) {
    this.selected = null;
    this.denied = null;
    this.unknownKey = null;
    if (!key) return;

    const report = findReport(key);
    if (!report) this.unknownKey = key;
    else if (this.canView(report)) this.selected = report;
    else this.denied = report;
  }

  /** The reports themselves, and the books this one is made of: a report must not hand out what the books withhold. */
  canView(report: ReportDef): boolean {
    return report.viewPermissions.every(p => this.authService.hasPermission(p));
  }

  /** What this user is missing to see the report. */
  missing(report: ReportDef): string[] {
    return report.viewPermissions.filter(p => !this.authService.hasPermission(p));
  }

  get availableCount(): number {
    return this.reports.filter(r => this.canView(r)).length;
  }

  open(report: ReportDef) {
    if (!this.canView(report)) return;
    this.router.navigate([BASE, report.key]).then(() => {
      // With the cards above it, the report can be off the bottom of the screen.
      this.panelAnchor?.nativeElement.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
    });
  }

  lockedHint(report: ReportDef): string {
    return `Needs ${this.missing(report).join(', ')}`;
  }
}
