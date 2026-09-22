import {
  AgingReceivablesCustomer, AgingReceivablesReport, MarginAnalysisReport, ProfitabilityReport, SalesVsPurchaseReport
} from '../../../services/sales-reports.service';

// The charts of the aging and margin reports, worked out from the report as it came back.
//
// Every figure in these reports is kept per currency, because there is no exchange rate to add one currency to
// another with — so a chart is never drawn across currencies: each currency has its own, and its title says which.

export interface ChartSpec {
  id: string;
  title: string;
  /** What the chart is of, when the title does not say enough: which rows it covers. */
  subtitle?: string;
  currency: string;
  type: 'bar' | 'line';
  data: any;
  options: any;
}

/** Bars that are more than this many rows are unreadable, so a chart shows the first of them. */
export const CHART_TOP_N = 10;

export const AGING_LABELS = ['0-30', '31-60', '61-90', '90+'];
const AGING_COLORS = ['#10b981', '#f59e0b', '#f97316', '#ef4444'];

const REVENUE_COLOR = '#3b82f6';
const COST_COLOR    = '#94a3b8';
const MARGIN_COLOR  = '#10b981';
const LOSS_COLOR    = '#ef4444';
const TEXT_COLOR    = '#64748b';
const GRID_COLOR    = 'rgba(148, 163, 184, 0.25)';

const money = (v: number) => Number(v ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const percent = (v: number) => `${Number(v ?? 0).toFixed(1)}%`;
const compact = (v: number | string) => Number(v).toLocaleString(undefined, { notation: 'compact', maximumFractionDigits: 1 });

const tooltip = {
  backgroundColor: '#1e293b', titleColor: '#f8fafc', bodyColor: '#cbd5e1',
  padding: 12, cornerRadius: 10, boxPadding: 4
};

/** A tooltip line: money for a money series, a percentage for the series drawn against the percentage axis. */
function label(currency: string) {
  return (ctx: any) => {
    const isPercent = ctx.dataset.yAxisID === 'percent';
    const value = ctx.parsed.y ?? ctx.parsed.x;
    return ` ${ctx.dataset.label}: ${isPercent ? percent(value) : `${money(value)} ${currency}`}`;
  };
}

/** Vertical bars, with the percentage axis on the right when a series asks for it. */
function verticalOptions(currency: string, withPercent: boolean, legend = true): any {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { display: legend, position: 'bottom', labels: { color: TEXT_COLOR, usePointStyle: true } },
      tooltip: { ...tooltip, callbacks: { label: label(currency) } }
    },
    scales: {
      x: { ticks: { color: TEXT_COLOR }, grid: { display: false } },
      y: { beginAtZero: true, ticks: { color: TEXT_COLOR, callback: compact }, grid: { color: GRID_COLOR } },
      ...(withPercent ? {
        percent: {
          position: 'right', ticks: { color: TEXT_COLOR, callback: (v: number | string) => `${v}%` },
          grid: { drawOnChartArea: false }
        }
      } : {})
    }
  };
}

/** Horizontal bars, one per row, which is what a ranking or a customer's name wants: room to be read. */
function horizontalOptions(currency: string, stacked: boolean, legend: boolean, extra?: (ctx: any) => string): any {
  return {
    indexAxis: 'y',
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { display: legend, position: 'bottom', labels: { color: TEXT_COLOR, usePointStyle: true } },
      tooltip: { ...tooltip, callbacks: { label: label(currency), ...(extra ? { afterLabel: extra } : {}) } }
    },
    scales: {
      x: { stacked, beginAtZero: true, ticks: { color: TEXT_COLOR, callback: compact }, grid: { color: GRID_COLOR } },
      y: { stacked, ticks: { color: TEXT_COLOR }, grid: { display: false } }
    }
  };
}

const currenciesOf = (rows: { currencyCode: string }[]) => [...new Set(rows.map(r => r.currencyCode))];

// ── R3 Aging receivables ─────────────────────────────────────────────────────

const bucketsOf = (row: { days0To30: number; days31To60: number; days61To90: number; over90: number }) =>
  [row.days0To30, row.days31To60, row.days61To90, row.over90];

/**
 * For each currency, what is owed by age, and which customers owe it. The customers chart is left out when one
 * customer owes it all: a single stacked bar says nothing the first chart does not.
 */
export function agingCharts(report: AgingReceivablesReport): ChartSpec[] {
  const specs: ChartSpec[] = [];

  for (const total of report.totals ?? []) {
    const currency = total.currencyCode;

    specs.push({
      id: `aging-buckets-${currency}`,
      title: `Owed by age, ${currency}`,
      subtitle: 'Days past the invoice due date, over every outstanding invoice',
      currency,
      type: 'bar',
      data: {
        labels: AGING_LABELS,
        datasets: [{ label: `Outstanding (${currency})`, data: bucketsOf(total), backgroundColor: AGING_COLORS, borderRadius: 4 }]
      },
      options: verticalOptions(currency, false, false)
    });

    const owing: AgingReceivablesCustomer[] = (report.customers ?? [])
      .filter(c => c.currencyCode === currency)
      .sort((a, b) => b.total - a.total)
      .slice(0, CHART_TOP_N);

    if (owing.length > 1) {
      specs.push({
        id: `aging-customers-${currency}`,
        title: `Who owes it, ${currency}`,
        subtitle: owing.length === CHART_TOP_N ? `The ${CHART_TOP_N} customers who owe most` : undefined,
        currency,
        type: 'bar',
        data: {
          labels: owing.map(c => c.customerName || 'Unknown customer'),
          datasets: AGING_LABELS.map((bucket, i) => ({
            label: `${bucket} days`,
            data: owing.map(c => bucketsOf(c)[i]),
            backgroundColor: AGING_COLORS[i],
            stack: 'age'
          }))
        },
        options: horizontalOptions(currency, true, true)
      });
    }
  }

  return specs;
}

// ── R7 Margin analysis ───────────────────────────────────────────────────────

const GROUP_NOUN: Record<string, string> = { PRODUCT: 'products', CUSTOMER: 'customers', ORDER: 'orders' };

/** What each product, customer or order sells for against what it cost, with the margin it makes. */
export function marginCharts(report: MarginAnalysisReport): ChartSpec[] {
  const noun = GROUP_NOUN[report.criteria?.groupBy] ?? 'rows';

  return currenciesOf(report.items ?? []).map(currency => {
    const rows = report.items.filter(i => i.currencyCode === currency).slice(0, CHART_TOP_N);

    return {
      id: `margin-${currency}`,
      title: `Selling value, cost and margin, ${currency}`,
      subtitle: `The ${rows.length} ${noun} with the highest margin on this page`,
      currency,
      type: 'bar' as const,
      data: {
        labels: rows.map(r => r.name || 'Unnamed'),
        datasets: [
          { type: 'bar', label: 'Selling value', data: rows.map(r => r.sellingValue), backgroundColor: REVENUE_COLOR, borderRadius: 4 },
          { type: 'bar', label: 'Cost', data: rows.map(r => r.cost), backgroundColor: COST_COLOR, borderRadius: 4 },
          {
            type: 'line', label: 'Margin %', data: rows.map(r => r.marginPercent ?? null), yAxisID: 'percent',
            borderColor: MARGIN_COLOR, backgroundColor: MARGIN_COLOR, tension: 0.3, pointRadius: 4
          }
        ]
      },
      options: verticalOptions(currency, true)
    };
  });
}

// ── R8 Sales vs purchase ─────────────────────────────────────────────────────

/** Revenue against the cost of the goods sold, period by period, with the gross margin they leave. */
export function salesVsPurchaseCharts(report: SalesVsPurchaseReport): ChartSpec[] {
  return currenciesOf(report.items ?? []).map(currency => {
    const rows = report.items.filter(i => i.currencyCode === currency);

    return {
      id: `sales-vs-purchase-${currency}`,
      title: `Revenue and cost of goods sold, ${currency}`,
      subtitle: 'The periods on this page',
      currency,
      type: 'bar' as const,
      data: {
        labels: rows.map(r => r.periodLabel),
        datasets: [
          { type: 'bar', label: 'Revenue', data: rows.map(r => r.revenue), backgroundColor: REVENUE_COLOR, borderRadius: 4 },
          { type: 'bar', label: 'Cost of goods sold', data: rows.map(r => r.costOfGoodsSold), backgroundColor: COST_COLOR, borderRadius: 4 },
          {
            type: 'line', label: 'Gross margin %', data: rows.map(r => r.grossMarginPercent ?? null), yAxisID: 'percent',
            borderColor: MARGIN_COLOR, backgroundColor: MARGIN_COLOR, tension: 0.3, pointRadius: 4
          }
        ]
      },
      options: verticalOptions(currency, true)
    };
  });
}

// ── R10 Product profitability ────────────────────────────────────────────────

/** Gross profit by product, most profitable first: a loss is red. */
export function profitabilityCharts(report: ProfitabilityReport): ChartSpec[] {
  return currenciesOf(report.items ?? []).map(currency => {
    const rows = report.items.filter(i => i.currencyCode === currency).slice(0, CHART_TOP_N);

    return {
      id: `profitability-${currency}`,
      title: `Gross profit by product, ${currency}`,
      subtitle: `The ${rows.length} most profitable products on this page`,
      currency,
      type: 'bar' as const,
      data: {
        labels: rows.map(r => `${r.rank}. ${r.productName || 'Unnamed'}`),
        datasets: [{
          label: 'Gross profit',
          data: rows.map(r => r.grossProfit),
          backgroundColor: rows.map(r => r.grossProfit < 0 ? LOSS_COLOR : MARGIN_COLOR),
          borderRadius: 4
        }]
      },
      options: horizontalOptions(currency, false, false, (ctx: any) => {
        const row = rows[ctx.dataIndex];
        return row?.marginPercent == null ? '' : `Margin: ${percent(row.marginPercent)}`;
      })
    };
  });
}
