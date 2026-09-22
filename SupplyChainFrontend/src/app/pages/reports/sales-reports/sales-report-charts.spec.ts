import { Chart } from 'chart.js/auto';

import {
  AgingReceivablesReport, MarginAnalysisReport, ProfitabilityReport, SalesVsPurchaseReport
} from '../../../services/sales-reports.service';
import {
  AGING_LABELS, CHART_TOP_N, agingCharts, marginCharts, profitabilityCharts, salesVsPurchaseCharts
} from './sales-report-charts';

const page = { generatedAt: '2026-09-21T00:00:00Z', totalRecords: 0, page: 1, pageSize: 20, totalPages: 1 };

function bucket(currencyCode: string, a: number, b: number, c: number, d: number) {
  return { currencyCode, invoiceCount: 1, days0To30: a, days31To60: b, days61To90: c, over90: d, total: a + b + c + d };
}

function customer(name: string | null, currencyCode: string, a: number, b = 0, c = 0, d = 0) {
  return { partnerId: `id-${name}`, customerName: name, ...bucket(currencyCode, a, b, c, d) };
}

describe('sales report charts', () => {
  describe('aging', () => {
    const report = (over: Partial<AgingReceivablesReport>): AgingReceivablesReport =>
      ({ ...page, totals: [], customers: [], invoices: [], ...over });

    it('draws what is owed by age, one bar for each bucket, for each currency', () => {
      const charts = agingCharts(report({ totals: [bucket('PKR', 100, 50, 25, 10), bucket('USD', 5, 0, 0, 1)] }));

      const byAge = charts.filter(c => c.id.startsWith('aging-buckets'));
      expect(byAge.map(c => c.currency)).toEqual(['PKR', 'USD']);
      expect(byAge[0].data.labels).toEqual(AGING_LABELS);
      expect(byAge[0].data.datasets[0].data).toEqual([100, 50, 25, 10]);
      expect(byAge[1].data.datasets[0].data).toEqual([5, 0, 0, 1]);
      expect(byAge[0].title).toContain('PKR');
    });

    it('colours the buckets from green to red, so the older the debt the redder', () => {
      const [chart] = agingCharts(report({ totals: [bucket('PKR', 1, 1, 1, 1)] }));

      expect(chart.data.datasets[0].backgroundColor).toEqual(['#10b981', '#f59e0b', '#f97316', '#ef4444']);
    });

    it('stacks the customers who owe most, biggest first, each bar split by age', () => {
      const charts = agingCharts(report({
        totals: [bucket('PKR', 0, 0, 0, 0)],
        customers: [customer('Small', 'PKR', 10), customer('Big', 'PKR', 100, 50), customer('Medium', 'PKR', 20, 20)]
      }));

      const who = charts.find(c => c.id === 'aging-customers-PKR')!;
      expect(who.data.labels).toEqual(['Big', 'Medium', 'Small']);
      expect(who.data.datasets.map((d: any) => d.label)).toEqual(['0-30 days', '31-60 days', '61-90 days', '90+ days']);
      expect(who.data.datasets[0].data).toEqual([100, 20, 10]);
      expect(who.data.datasets[1].data).toEqual([50, 20, 0]);
      expect(who.data.datasets.every((d: any) => d.stack === 'age')).toBeTrue();
      expect(who.options.scales.x.stacked).toBeTrue();
      expect(who.options.indexAxis).toBe('y');
    });

    it('keeps each currency to its own customers', () => {
      const charts = agingCharts(report({
        totals: [bucket('PKR', 0, 0, 0, 0), bucket('USD', 0, 0, 0, 0)],
        customers: [customer('A', 'PKR', 1), customer('B', 'PKR', 2), customer('C', 'USD', 3), customer('D', 'USD', 4)]
      }));

      expect(charts.find(c => c.id === 'aging-customers-PKR')!.data.labels).toEqual(['B', 'A']);
      expect(charts.find(c => c.id === 'aging-customers-USD')!.data.labels).toEqual(['D', 'C']);
    });

    it('shows only the customers who owe most when there are more than fit', () => {
      const many = Array.from({ length: CHART_TOP_N + 5 }, (_, i) => customer(`C${i}`, 'PKR', i + 1));
      const who = agingCharts(report({ totals: [bucket('PKR', 0, 0, 0, 0)], customers: many }))
        .find(c => c.id === 'aging-customers-PKR')!;

      expect(who.data.labels.length).toBe(CHART_TOP_N);
      expect(who.data.labels[0]).toBe(`C${CHART_TOP_N + 4}`);
      expect(who.subtitle).toContain(String(CHART_TOP_N));
    });

    it('names a customer the books cannot name', () => {
      const who = agingCharts(report({ totals: [bucket('PKR', 0, 0, 0, 0)], customers: [customer(null, 'PKR', 2), customer('B', 'PKR', 1)] }))
        .find(c => c.id === 'aging-customers-PKR')!;

      expect(who.data.labels).toEqual(['Unknown customer', 'B']);
    });

    it('leaves out the customers chart when one customer owes it all, since it would say nothing new', () => {
      const charts = agingCharts(report({ totals: [bucket('PKR', 10, 0, 0, 0)], customers: [customer('Only', 'PKR', 10)] }));

      expect(charts.map(c => c.id)).toEqual(['aging-buckets-PKR']);
    });

    it('draws nothing when nothing is owed', () => {
      expect(agingCharts(report({}))).toEqual([]);
    });
  });

  describe('margin analysis', () => {
    const item = (name: string | null, currencyCode: string, sellingValue: number, cost: number, marginPercent: number | null) =>
      ({ groupId: name ?? 'x', name, currencyCode, lineCount: 1, sellingValue, cost, margin: sellingValue - cost, marginPercent });

    const report = (groupBy: string, items: any[]): MarginAnalysisReport =>
      ({ ...page, criteria: { groupBy }, totals: [], items });

    it('sets selling value against cost, with the margin as a line on its own axis', () => {
      const [chart] = marginCharts(report('PRODUCT', [item('Cable', 'PKR', 1000, 700, 30), item('Wire', 'PKR', 500, 400, 20)]));

      expect(chart.data.labels).toEqual(['Cable', 'Wire']);
      const [selling, cost, margin] = chart.data.datasets;
      expect([selling.type, selling.label, selling.data]).toEqual(['bar', 'Selling value', [1000, 500]]);
      expect([cost.type, cost.label, cost.data]).toEqual(['bar', 'Cost', [700, 400]]);
      expect([margin.type, margin.label, margin.data, margin.yAxisID]).toEqual(['line', 'Margin %', [30, 20], 'percent']);
      expect(chart.options.scales.percent).toBeDefined();
    });

    it('draws a chart for each currency, from that currencys rows only', () => {
      const charts = marginCharts(report('PRODUCT', [item('A', 'PKR', 1, 1, 0), item('B', 'USD', 2, 1, 50), item('C', 'PKR', 3, 1, 66)]));

      expect(charts.map(c => c.currency)).toEqual(['PKR', 'USD']);
      expect(charts[0].data.labels).toEqual(['A', 'C']);
      expect(charts[1].data.labels).toEqual(['B']);
    });

    it('says what the rows are, by what the report is grouped by', () => {
      expect(marginCharts(report('CUSTOMER', [item('A', 'PKR', 1, 1, 0)]))[0].subtitle).toContain('customers');
      expect(marginCharts(report('ORDER', [item('A', 'PKR', 1, 1, 0)]))[0].subtitle).toContain('orders');
      expect(marginCharts(report('PRODUCT', [item('A', 'PKR', 1, 1, 0)]))[0].subtitle).toContain('products');
    });

    it('shows a gap, not a zero, where there is no margin percent, and names an unnamed row', () => {
      const [chart] = marginCharts(report('PRODUCT', [item(null, 'PKR', 0, 0, null)]));

      expect(chart.data.datasets[2].data).toEqual([null]);
      expect(chart.data.labels).toEqual(['Unnamed']);
    });

    it('shows only the first rows when there are more than fit', () => {
      const rows = Array.from({ length: CHART_TOP_N + 4 }, (_, i) => item(`P${i}`, 'PKR', 10, 5, 50));

      expect(marginCharts(report('PRODUCT', rows))[0].data.labels.length).toBe(CHART_TOP_N);
    });

    it('draws nothing when there are no rows', () => {
      expect(marginCharts(report('PRODUCT', []))).toEqual([]);
    });
  });

  describe('sales vs purchase', () => {
    const period = (label: string, currencyCode: string, revenue: number, costOfGoodsSold: number, grossMarginPercent: number | null) =>
      ({ periodStart: '2026-09-01', periodLabel: label, currencyCode, invoiceCount: 1, revenue, costOfGoodsSold, grossMargin: revenue - costOfGoodsSold, grossMarginPercent, uncostedRevenue: 0 });

    const report = (items: any[]): SalesVsPurchaseReport => ({ ...page, criteria: { period: 'MONTH' }, totals: [], items });

    it('sets revenue against cost of goods sold period by period, with the gross margin as a line', () => {
      const [chart] = salesVsPurchaseCharts(report([period('2026-08', 'PKR', 1000, 600, 40), period('2026-09', 'PKR', 1500, 900, 40)]));

      expect(chart.data.labels).toEqual(['2026-08', '2026-09']);
      const [revenue, cogs, margin] = chart.data.datasets;
      expect([revenue.label, revenue.data]).toEqual(['Revenue', [1000, 1500]]);
      expect([cogs.label, cogs.data]).toEqual(['Cost of goods sold', [600, 900]]);
      expect([margin.type, margin.label, margin.data, margin.yAxisID]).toEqual(['line', 'Gross margin %', [40, 40], 'percent']);
    });

    it('draws each currency apart', () => {
      const charts = salesVsPurchaseCharts(report([period('2026-09', 'PKR', 1, 1, 0), period('2026-09', 'USD', 2, 1, 50)]));

      expect(charts.map(c => c.currency)).toEqual(['PKR', 'USD']);
      expect(charts[1].data.datasets[0].data).toEqual([2]);
    });

    it('shows a gap where nothing was billed', () => {
      const [chart] = salesVsPurchaseCharts(report([period('2026-09', 'PKR', 0, 0, null)]));

      expect(chart.data.datasets[2].data).toEqual([null]);
    });

    it('draws nothing for a range with no sales', () => {
      expect(salesVsPurchaseCharts(report([]))).toEqual([]);
    });
  });

  describe('product profitability', () => {
    const product = (rank: number, name: string | null, currencyCode: string, grossProfit: number, marginPercent: number | null) =>
      ({ rank, productUuid: `p${rank}`, productName: name, currencyCode, quantitySold: 1, revenue: 100, costOfGoodsSold: 100 - grossProfit, grossProfit, marginPercent });

    const report = (items: any[]): ProfitabilityReport => ({ ...page, totals: [], items });

    it('ranks the products by their gross profit, most profitable at the top, horizontally', () => {
      const [chart] = profitabilityCharts(report([product(1, 'Cable', 'PKR', 500, 50), product(2, 'Wire', 'PKR', 200, 20)]));

      expect(chart.data.labels).toEqual(['1. Cable', '2. Wire']);
      expect(chart.data.datasets[0].data).toEqual([500, 200]);
      expect(chart.options.indexAxis).toBe('y');
    });

    it('colours a loss red and a profit green', () => {
      const [chart] = profitabilityCharts(report([product(1, 'A', 'PKR', 100, 10), product(2, 'B', 'PKR', -40, -4)]));

      expect(chart.data.datasets[0].backgroundColor).toEqual(['#10b981', '#ef4444']);
    });

    it('says the margin in the tooltip, and nothing when there is none', () => {
      const [chart] = profitabilityCharts(report([product(1, 'A', 'PKR', 100, 12.34), product(2, 'B', 'PKR', 0, null)]));
      const afterLabel = chart.options.plugins.tooltip.callbacks.afterLabel;

      expect(afterLabel({ dataIndex: 0 })).toBe('Margin: 12.3%');
      expect(afterLabel({ dataIndex: 1 })).toBe('');
    });

    it('ranks each currency apart and shows a chart for each', () => {
      const charts = profitabilityCharts(report([product(1, 'A', 'PKR', 5, 5), product(1, 'B', 'USD', 6, 6), product(2, 'C', 'PKR', 4, 4)]));

      expect(charts.map(c => c.currency)).toEqual(['PKR', 'USD']);
      expect(charts[0].data.labels).toEqual(['1. A', '2. C']);
    });

    it('names an unnamed product, and shows only the first that fit', () => {
      const rows = Array.from({ length: CHART_TOP_N + 3 }, (_, i) => product(i + 1, i === 0 ? null : `P${i}`, 'PKR', 10, 10));
      const [chart] = profitabilityCharts(report(rows));

      expect(chart.data.labels.length).toBe(CHART_TOP_N);
      expect(chart.data.labels[0]).toBe('1. Unnamed');
    });
  });

  describe('drawn by chart.js', () => {
    // A configuration chart.js does not accept shows up as a console error and a blank canvas, not as a failed
    // test, so each kind of chart is actually drawn here: the mixed bar and line, the stacked and the horizontal.
    const specs = () => [
      ...agingCharts({
        ...page, invoices: [], totals: [bucket('PKR', 100, 50, 25, 10)],
        customers: [customer('A', 'PKR', 60, 10), customer('B', 'PKR', 40, 40, 25, 10)]
      }),
      ...marginCharts({
        ...page, criteria: { groupBy: 'PRODUCT' }, totals: [],
        items: [{ groupId: 'g', name: 'Cable', currencyCode: 'PKR', lineCount: 1, sellingValue: 1000, cost: 700, margin: 300, marginPercent: 30 }]
      }),
      ...salesVsPurchaseCharts({
        ...page, criteria: { period: 'MONTH' }, totals: [],
        items: [{ periodStart: '2026-09-01', periodLabel: '2026-09', currencyCode: 'PKR', invoiceCount: 1, revenue: 1000, costOfGoodsSold: 600, grossMargin: 400, grossMarginPercent: 40, uncostedRevenue: 0 }]
      }),
      ...profitabilityCharts({
        ...page, totals: [],
        items: [{ rank: 1, productUuid: 'p', productName: 'Cable', currencyCode: 'PKR', quantitySold: 1, revenue: 100, costOfGoodsSold: 40, grossProfit: 60, marginPercent: 60 }]
      })
    ];

    it('accepts every chart the reports draw', () => {
      const all = specs();
      expect(all.length).toBe(5);

      for (const spec of all) {
        const canvas = document.createElement('canvas');
        canvas.width = 300;
        canvas.height = 200;
        const chart = new Chart(canvas, { type: spec.type, data: spec.data, options: { ...spec.options, animation: false, responsive: false } });

        expect(chart.data.datasets.length).withContext(spec.id).toBeGreaterThan(0);
        chart.destroy();
      }
    });
  });

  describe('every chart', () => {
    it('formats a money tooltip with the currency, and a percentage on the percentage axis', () => {
      const [chart] = salesVsPurchaseCharts({
        ...page, criteria: { period: 'MONTH' }, totals: [],
        items: [{ periodStart: '2026-09-01', periodLabel: '2026-09', currencyCode: 'PKR', invoiceCount: 1, revenue: 1234.5, costOfGoodsSold: 1, grossMargin: 1, grossMarginPercent: 25, uncostedRevenue: 0 }]
      });
      const label = chart.options.plugins.tooltip.callbacks.label;

      expect(label({ dataset: { label: 'Revenue' }, parsed: { y: 1234.5 } })).toContain('Revenue: 1,234.50 PKR');
      expect(label({ dataset: { label: 'Gross margin %', yAxisID: 'percent' }, parsed: { y: 25 } })).toBe(' Gross margin %: 25.0%');
    });

    it('is a plain object a chart can be drawn from, with no function that could not be serialised in its data', () => {
      const [chart] = agingCharts({ ...page, totals: [bucket('PKR', 1, 2, 3, 4)], customers: [], invoices: [] });

      expect(() => JSON.stringify(chart.data)).not.toThrow();
      expect(chart.type).toBe('bar');
    });
  });
});
