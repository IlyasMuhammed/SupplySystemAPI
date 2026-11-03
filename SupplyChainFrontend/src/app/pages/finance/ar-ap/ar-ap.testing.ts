import { Type } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of } from 'rxjs';
import { BusinessPartnerService } from '../../../services/business-partner.service';
import { AuthService } from '../../service/auth.service';
import { fakeAuth as glFakeAuth } from '../gl/gl.testing';
import {
  AgingReport, ApSummary, ArApService, ArSummary, CashFlowForecast, CreditExposure, DunningHistory, DunningProcedure, DunningRunDetail, DunningRunItem,
  MatchDetail, MatchSummary, PartnerFinancialProfile, PartnerOpenItem, PaymentRunDetail, PaymentRunDetailWire, PaymentRunItemWire, PaymentSchedule,
  StatementResult, toRun
} from '../../../services/ar-ap.service';

// A48 — fixtures shared by the AR / AP specs (not imported by app code), in the shapes the API answers with.

export { fakeAuth } from '../gl/gl.testing';

export const page = <T>(data: T[], totalRecords = data.length) => of({ data, totalRecords, page: 1, pageSize: 25 });

export function aging(over: Partial<AgingReport> = {}): AgingReport {
  return {
    asOfDate: '2026-10-25', bucketDays: [30, 60, 90, 120], bucketLabels: ['Current', '1-30', '31-60', '61-90', '91-120', '120+'], baseCurrencyCode: 'PKR',
    lines: [
      { partner: { uuid: 'c1', name: 'Acme Corp', code: 'C-1' }, bucketAmounts: [12000, 8500, 5200, 3100, 1800, 0], totalOutstanding: 30600 },
      { partner: { uuid: 'c2', name: 'Beta Ltd', code: 'C-2' }, bucketAmounts: [25000, 15000, 7500, 0, 0, 0], totalOutstanding: 47500, onHold: true }
    ],
    totals: { bucketTotals: [37000, 23500, 12700, 3100, 1800, 0], grandTotal: 78100, totalPartners: 2, totalOpenItems: 9 },
    totalOutstanding: 78100, ...over
  };
}

/** The aging report as the API sends it (flat partnerId / partnerName / partnerCode). */
export function agingWire(lines: { id: string; name: string; buckets: number[] }[]): any {
  return {
    reportType: 'Receivable', asOfDate: '2026-10-25', basis: 'DueDate', bucketDays: [30, 60, 90, 120], bucketLabels: ['Current', '1-30', '31-60', '61-90', '91-120', '120+'],
    baseCurrencyCode: 'PKR', byCurrency: [], dsoOrDpo: 38, page: 1, pageSize: 200, totalRecords: lines.length, totalPages: 1,
    lines: lines.map(l => ({ partnerId: l.id, partnerName: l.name, partnerCode: null, bucketAmounts: l.buckets, totalOutstanding: l.buckets.reduce((s, x) => s + x, 0),
      totalOverdue: 0, unappliedCredits: 0, netBalance: 0, openItemCount: 1, oldestBucket: 0 })),
    totals: { bucketTotals: [], grandTotal: lines.reduce((s, l) => s + l.buckets.reduce((a, b) => a + b, 0), 0), totalOverdue: 0, unappliedCredits: 0, netBalance: 0,
      totalPartners: lines.length, totalOpenItems: lines.length },
    totalOutstanding: 0
  };
}

export function arSummary(over: Partial<ArSummary> = {}): ArSummary {
  return {
    asOfDate: '2026-10-25', baseCurrencyCode: 'PKR', periodDays: 365, totalOutstanding: 0, totalOverdue: 0, totalCurrent: 0, unappliedCredits: 0, netReceivable: 0,
    totalCustomersWithBalance: 0, totalCustomersOverdue: 0, dso: 0, daysBeyondTerms: 0, collectionRate: 0, invoicedInPeriod: 0, collectedInPeriod: 0,
    agingBuckets: [], agingLabels: [], ...over
  };
}

export function apSummary(over: Partial<ApSummary> = {}): ApSummary {
  return {
    asOfDate: '2026-10-25', baseCurrencyCode: 'PKR', periodDays: 365, totalOutstanding: 0, totalOverdue: 0, totalCurrent: 0, totalDueThisWeek: 0, totalDueThisMonth: 0,
    unappliedCredits: 0, netPayable: 0, totalVendorsWithBalance: 0, totalVendorsOverdue: 0, dpo: 0, discountOpportunityAmount: 0, agingBuckets: [], agingLabels: [], ...over
  };
}

export function exposure(over: Partial<CreditExposure> = {}): CreditExposure {
  return {
    customerUuid: 'c1', customerName: 'Acme Corp', customerCode: 'C-1', baseCurrencyCode: 'PKR', creditLimit: 100000, temporaryCreditLimit: null,
    temporaryCreditExpiry: null, effectiveCreditLimit: 100000, openOrdersAmount: 35000, openInvoicesAmount: 52500, totalExposure: 87500,
    availableCredit: 12500, isOverLimit: false, isOnHold: false, riskTier: 'Medium', creditControlMode: 'Block', ...over
  };
}

export function profile(over: Partial<PartnerFinancialProfile> = {}): PartnerFinancialProfile {
  return {
    uuid: 'pf1', partnerUuid: 'c1', role: 'Customer', partnerName: 'Acme Corp', partnerCode: 'C-1', creditLimit: 100000, effectiveCreditLimit: 100000,
    creditHold: false, riskTier: 'Medium', currentDunningLevel: 0, paymentPriority: 'Normal', paymentTermsDays: 30, rowVersion: 'rv1', ...over
  };
}

export function dunningHistory(events: DunningHistory['events'] = []): DunningHistory {
  return { customerUuid: 'c1', customerName: 'Acme Corp', currentDunningLevel: 0, excludeFromDunning: false, events };
}

export function procedure(over: Partial<DunningProcedure> = {}): DunningProcedure {
  const lvl = (n: number, days: number) => ({
    uuid: 'l' + n, levelNumber: n, daysAfterPrevious: days, subjectTemplate: 'Reminder ' + n, bodyTemplate: 'Dear {CustomerName}',
    includeInvoiceList: true, includeStatement: false, chargeAmount: null, actionType: 'Email'
  });
  return {
    uuid: 'p1', code: 'STANDARD', name: 'Standard dunning', isDefault: true, isActive: true, graceDays: 5, minimumAmount: 50, blockSalesAtLevel: 4,
    levels: [lvl(1, 0), lvl(2, 14), lvl(3, 14), lvl(4, 14)], ...over
  };
}

export function dunningItem(id: string, over: Partial<DunningRunItem> = {}): DunningRunItem {
  return {
    uuid: id, customerUuid: 'c' + id, customerName: 'Customer ' + id, dunningLevel: 1, previousLevel: 0, overdueAmount: 1000, openItemCount: 1,
    oldestDueDate: '2026-08-01', daysOverdue: 40, actionType: 'Email', invoiceUuids: [], status: 'Pending', ...over
  };
}

export function dunningRun(over: Partial<DunningRunDetail> = {}): DunningRunDetail {
  return {
    uuid: 'r1', runNumber: 'DUN-0007', runDate: '2026-10-25', asOfDate: '2026-10-25', status: 'Draft', totalCustomers: 0, totalAmount: 0,
    itemsProcessed: 0, itemsSent: 0, itemsSkipped: 0, itemsFailed: 0, autoResetCount: 0, items: [], ...over
  };
}

export function match(over: Partial<MatchDetail> = {}): MatchDetail {
  return {
    invoiceUuid: 'i1', invoiceNumber: 'INV-0089', supplierInvoiceNo: 'V-89', vendorUuid: 'v1', vendorName: 'Supplier A', poUuid: 'po1', poNumber: 'PO-0234',
    grnUuid: 'g1', grnNumber: 'GRN-0567', matchStatus: 'Variance', matchType: 'ThreeWay', lineMatchResult: 'PartialMatch', withinTolerance: false,
    variancePercent: 1.33, varianceAmount: 100, toleranceRule: { uuid: 't1', name: 'DEFAULT' }, autoApproved: false, currencyCode: 'PKR', subtotal: 7600, totalAmount: 7980,
    lines: [
      { lineNo: 1, itemDescription: 'Widget A', poQuantity: 100, poUnitPrice: 25, poLineTotal: 2500, grnQuantity: 100, invoiceQuantity: 100, invoiceUnitPrice: 25, invoiceLineTotal: 2500, lineStatus: 'Matched' },
      { lineNo: 2, itemDescription: 'Widget B', poQuantity: 200, poUnitPrice: 15, poLineTotal: 3000, grnQuantity: 195, invoiceQuantity: 200, invoiceUnitPrice: 15.5,
        invoiceLineTotal: 3100, priceVariance: 0.5, priceVariancePercent: 3.33, totalVariance: 100, lineStatus: 'PriceMismatch' },
      { lineNo: 3, itemDescription: 'Widget C', poQuantity: 50, poUnitPrice: 40, poLineTotal: 2000, grnQuantity: 50, invoiceQuantity: 50, invoiceUnitPrice: 40, invoiceLineTotal: 2000, lineStatus: 'Matched' }
    ], ...over
  };
}

export function matchSummary(over: Partial<MatchSummary> = {}): MatchSummary {
  return {
    invoiceUuid: 'i1', invoiceNumber: 'INV-0089', supplierInvoiceNo: 'V-89', vendorUuid: 'v1', vendorName: 'Supplier A', poUuid: 'po1', poNumber: 'PO-0234',
    matchStatus: 'Variance', matchType: 'ThreeWay', lineMatchResult: 'PartialMatch', withinTolerance: false, variancePercent: 1.33, varianceAmount: 100,
    overridden: false, currencyCode: 'PKR', totalAmount: 7980, invoiceDate: '2026-10-20', ...over
  };
}

export function schedule(id: string, over: Partial<PaymentSchedule> = {}): PaymentSchedule {
  return {
    uuid: id, vendorUuid: 'v1', vendorName: 'Supplier A', invoiceUuid: 'inv-' + id, invoiceNumber: 'INV-' + id, invoiceDate: '2026-10-01', dueDate: '2026-10-30',
    daysUntilDue: 5, currencyCode: 'PKR', originalAmount: 1000, amountRemaining: 1000, paymentPriority: 'Normal', paymentBlock: false, status: 'Open', ...over
  };
}

export function cashFlow(over: Partial<CashFlowForecast> = {}): CashFlowForecast {
  return {
    from: '2026-10-25', weeks: 8, baseCurrencyCode: 'PKR', overdueAmount: 0, overdueCount: 0, overdueBlockedAmount: 0, weekRows: [], laterAmount: 0,
    totalDue: 0, totalBlocked: 0, totalDiscountAvailable: 0, ...over
  };
}

export function openItem(id: string, over: Partial<PartnerOpenItem> = {}): PartnerOpenItem {
  return {
    documentType: 'SALES_INVOICE', documentUuid: id, documentNumber: 'SI-' + id, documentDate: '2026-09-01', dueDate: '2026-10-01', currencyCode: 'PKR',
    originalAmount: 1000, openAmount: 1000, openAmountBase: 1000, daysOutstanding: 50, daysOverdue: 20, isOverdue: true, isCredit: false, ...over
  };
}

export function statement(over: Partial<StatementResult> = {}): StatementResult {
  return {
    partnerType: 'Customer', partnerId: 'c1', partnerName: 'Acme Corp', fromDate: '2026-07-01', toDate: '2026-09-30', type: 'Activity', baseCurrencyCode: 'PKR',
    sections: [], openingBalanceBase: 0, closingBalanceBase: 0, lineCount: 0, agingLabels: [], generatedAt: '2026-10-25T10:00:00Z', ...over
  };
}

/** A payment run as the API sends it (§8 PaymentRunDetailModel). */
export function runWire(over: Partial<PaymentRunDetailWire> = {}, status = 'Proposed'): PaymentRunDetailWire {
  const item = (id: string, net: number, extra: Partial<PaymentRunItemWire> = {}): PaymentRunItemWire => ({
    itemUuid: id, scheduleUuid: 's' + id, invoiceUuid: 'inv' + id, invoiceNumber: 'INV-' + id, currencyCode: 'PKR', dueDate: '2026-10-28', originalAmount: net,
    discountAmount: 0, netPaymentAmount: net, daysUntilDue: 3, hasDiscount: false, status: 'Proposed', ...extra
  });
  const batch = (vendor: string, name: string, items: PaymentRunItemWire[]) => ({
    batchUuid: 'b' + vendor, vendorUuid: vendor, vendorName: name, currencyCode: 'PKR', paymentMethod: 'BANK_TRANSFER', itemCount: items.length,
    totalAmount: items.reduce((s, i) => s + i.originalAmount, 0), totalDiscount: items.reduce((s, i) => s + i.discountAmount, 0),
    netAmount: items.reduce((s, i) => s + i.netPaymentAmount, 0), status: 'Pending', items
  });
  return {
    run: { uuid: 'r1', runNumber: 'PR-0042', runDate: '2026-10-25', paymentDate: '2026-10-28', dueDateCutoff: '2026-10-31', status, baseCurrencyCode: 'PKR',
      totalVendors: 2, totalItems: 3, totalAmount: 18000, totalDiscount: 102, netPaymentAmount: 17898, failedBatchCount: 0 },
    bankAccount: 'Operating', includeDiscount: true, rowVersion: 'rv',
    vendorGroups: [
      batch('v1', 'Supplier A', [item('a1', 8200), item('a2', 4998, { originalAmount: 5100, discountAmount: 102, hasDiscount: true })]),
      batch('v2', 'Supplier B', [item('b1', 4700, { daysUntilDue: -3 })])
    ],
    removedItems: [],
    blockedItems: [{ scheduleUuid: 'sc1', invoiceUuid: 'invc1', invoiceNumber: 'INV-c1', vendorUuid: 'v3', vendorName: 'Supplier C', currencyCode: 'PKR', dueDate: '2026-10-15',
      amountRemaining: 5400, blockReason: 'Disputed' }],
    warnings: [],
    ...over
  };
}

/** The run as the wizard reads it. */
export function run(status = 'Proposed', over: Partial<PaymentRunDetailWire> = {}): PaymentRunDetail { return toRun(runWire(over, status)); }

export interface Mounted<T> {
  fixture: ComponentFixture<T>;
  c: T;
  api: jasmine.SpyObj<ArApService>;
  partners: jasmine.SpyObj<BusinessPartnerService>;
  toast: jasmine.Spy;
  /** null when the page has no ConfirmationService. */
  confirm: jasmine.Spy | null;
  q: (testId: string) => HTMLElement | null;
  text: (testId: string) => string;
}

/**
 * Creates a standalone A48 page with a fake API (adjust it in `prepare`), the given permissions, route params and query
 * params. Confirm dialogs accept at once; toasts are spied.
 */
export async function mount<T>(type: Type<T>, opts: {
  perms?: string[]; params?: Record<string, string>; query?: Record<string, string>; prepare?: (api: jasmine.SpyObj<ArApService>) => void;
} = {}): Promise<Mounted<T>> {
  const api = fakeArAp();
  opts.prepare?.(api);
  const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
  partners.getPartners.and.returnValue(of({ success: true, message: '', result: { data: [{ uuid: 'c9', companyName: 'Gamma' }] } }) as any);
  partners.getPartnerById.and.returnValue(of({ success: true, message: '', result: { uuid: 'c1', companyName: 'Acme Corp' } }) as any);
  const params = convertToParamMap(opts.params ?? {});
  const query = convertToParamMap(opts.query ?? {});
  await TestBed.resetTestingModule().configureTestingModule({
    imports: [type],
    providers: [provideNoopAnimations(), provideRouter([]), { provide: ArApService, useValue: api }, { provide: BusinessPartnerService, useValue: partners },
      { provide: AuthService, useValue: glFakeAuth(opts.perms ?? []) },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: params, queryParamMap: query }, paramMap: of(params), queryParamMap: of(query) } }]
  }).compileComponents();
  const fixture = TestBed.createComponent(type);
  const inj = fixture.debugElement.injector;
  const messages = inj.get(MessageService, null);
  const toast = messages ? spyOn(messages, 'add') : jasmine.createSpy('toast');
  let confirm: jasmine.Spy | null = null;
  const confirmations = inj.get(ConfirmationService, null);
  if (confirmations) confirm = spyOn(confirmations, 'confirm').and.callFake((o: any) => { o.accept(); return null as any; });
  fixture.detectChanges();
  fixture.detectChanges();
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  return { fixture, c: fixture.componentInstance, api, partners, toast, confirm, q, text: (id: string) => (q(id)?.textContent ?? '').replace(/\s+/g, ' ').trim() };
}

/** An ArApService double whose every read answers with an empty, successful result (override per test). */
export function fakeArAp(): jasmine.SpyObj<ArApService> {
  const methods = Object.getOwnPropertyNames(ArApService.prototype).filter(m => m !== 'constructor') as (keyof ArApService)[];
  const api = jasmine.createSpyObj<ArApService>('ArApService', methods);
  const empty = aging({ lines: [], totals: { bucketTotals: [], grandTotal: 0, totalPartners: 0, totalOpenItems: 0 }, totalOutstanding: 0 });
  api.getArSummary.and.returnValue(of(arSummary()));
  api.getApSummary.and.returnValue(of(apSummary()));
  api.getArAging.and.returnValue(of(empty));
  api.getApAging.and.returnValue(of(empty));
  api.getProcedures.and.returnValue(of([]));
  api.getDunningRuns.and.returnValue(page([]));
  api.getCreditHistory.and.returnValue(page([]));
  api.getCustomerDunningHistory.and.returnValue(of(dunningHistory()));
  api.getExposures.and.returnValue(page([]));
  api.getSettings.and.returnValue(of({ creditControlMode: 'Off' }));
  api.getStatementLog.and.returnValue(page([]));
  api.getPendingMatches.and.returnValue(page([]));
  api.getToleranceRules.and.returnValue(of([]));
  api.getEarlyPaymentTerms.and.returnValue(of([]));
  api.getUpcomingPayments.and.returnValue(of([]));
  api.getDiscountOpportunities.and.returnValue(of([]));
  api.getCashFlow.and.returnValue(of(cashFlow()));
  api.getPaymentRuns.and.returnValue(page([]));
  api.getOpenCredits.and.returnValue(of([]));
  api.getCustomerOpenItems.and.returnValue(page([]));
  api.getVendorOpenItems.and.returnValue(page([]));
  api.getCreditApplications.and.returnValue(of([]));
  api.getDebitApplications.and.returnValue(of([]));
  api.getVendorDebitMemos.and.returnValue(page([]));
  return api;
}
