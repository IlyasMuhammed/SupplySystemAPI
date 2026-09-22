import { SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import {
  allocationProblem, allocationTotal, oldestFirst, planOldestFirst, roundMoney, toAllocations
} from './payment-allocation';

function invoice(uuid: string, balanceDue: number, invoiceDate = '2026-09-01T00:00:00Z', number = `INV-${uuid}`): SalesInvoiceListItemModel {
  return {
    uuid, invoiceNumber: number, saleOrderUuid: 's', saleOrderNumber: 'SO-1', partnerId: 'p', partnerName: 'Acme',
    invoiceDate, dueDate: '2026-10-01T00:00:00Z', grandTotal: balanceDue, amountPaid: 0, balanceDue,
    status: 'ISSUED', currencyCode: 'PKR'
  };
}

describe('payment allocation', () => {
  describe('roundMoney', () => {
    it('keeps two decimal places, without losing a cent to floating point', () => {
      expect(roundMoney(0.1 + 0.2)).toBe(0.3);
      expect(roundMoney(1.006)).toBe(1.01);
      expect(roundMoney(1.004)).toBe(1);
      expect(roundMoney(118.80000000000001)).toBe(118.8);
      expect(roundMoney(100)).toBe(100);
    });
  });

  describe('oldestFirst', () => {
    it('orders by invoice date, then by number, and leaves its input alone', () => {
      const input = [invoice('c', 1, '2026-09-20T00:00:00Z'), invoice('b', 1, '2026-08-01T00:00:00Z', 'INV-2'), invoice('a', 1, '2026-08-01T00:00:00Z', 'INV-1')];

      expect(oldestFirst(input).map(i => i.uuid)).toEqual(['a', 'b', 'c']);
      expect(input.map(i => i.uuid)).toEqual(['c', 'b', 'a']);
    });
  });

  describe('planOldestFirst', () => {
    const open = [invoice('old', 100, '2026-08-01T00:00:00Z'), invoice('mid', 50, '2026-08-15T00:00:00Z'), invoice('new', 200, '2026-09-01T00:00:00Z')];

    it('pays the oldest invoice in full before touching the next', () => {
      expect(planOldestFirst(open, 120)).toEqual({ old: 100, mid: 20 });
    });

    it('pays everything and leaves the rest when the money is more than is owed', () => {
      expect(planOldestFirst(open, 1000)).toEqual({ old: 100, mid: 50, new: 200 });
    });

    it('pays part of the oldest when the money does not cover it', () => {
      expect(planOldestFirst(open, 30)).toEqual({ old: 30 });
    });

    it('plans nothing for no money, nothing owed, or negative money', () => {
      expect(planOldestFirst(open, 0)).toEqual({});
      expect(planOldestFirst([], 100)).toEqual({});
      expect(planOldestFirst(open, -5)).toEqual({});
    });

    it('skips an invoice with nothing owing', () => {
      expect(planOldestFirst([invoice('paid', 0, '2026-08-01T00:00:00Z'), invoice('owing', 10, '2026-08-02T00:00:00Z')], 10)).toEqual({ owing: 10 });
    });

    it('does not lose a cent adding amounts that floating point cannot hold', () => {
      const plan = planOldestFirst([invoice('a', 0.1, '2026-08-01T00:00:00Z'), invoice('b', 0.2, '2026-08-02T00:00:00Z')], 0.3);

      expect(plan).toEqual({ a: 0.1, b: 0.2 });
      expect(allocationTotal(plan)).toBe(0.3);
    });
  });

  describe('allocationTotal', () => {
    it('adds what is given and counts nothing or blank as nothing', () => {
      expect(allocationTotal({ a: 10.1, b: 20.2, c: null })).toBe(30.3);
      expect(allocationTotal({})).toBe(0);
    });

    it('counts only the invoices on offer when it is given them', () => {
      const offered = [invoice('a', 100), invoice('b', 100)];

      expect(allocationTotal({ a: 10, b: 20, elsewhere: 500 }, offered)).toBe(30);
      expect(allocationTotal({ elsewhere: 500 }, offered)).toBe(0);
    });
  });

  describe('toAllocations', () => {
    const open = [invoice('a', 100), invoice('b', 100), invoice('c', 100)];

    it('sends only the invoices given something, in the order they are listed', () => {
      expect(toAllocations(open, { c: 5, a: 10, b: 0 })).toEqual([
        { invoiceUuid: 'a', amount: 10 },
        { invoiceUuid: 'c', amount: 5 }
      ]);
    });

    it('sends nothing for blank amounts, and ignores amounts for invoices that are not listed', () => {
      expect(toAllocations(open, { a: null, elsewhere: 50 })).toEqual([]);
    });

    it('rounds to the cent', () => {
      expect(toAllocations(open, { a: 10.006 })).toEqual([{ invoiceUuid: 'a', amount: 10.01 }]);
    });
  });

  describe('allocationProblem', () => {
    const open = [invoice('a', 100), invoice('b', 50)];

    it('has nothing to say about amounts the server would take', () => {
      expect(allocationProblem(open, { a: 100, b: 20 }, 120)).toBeNull();
      expect(allocationProblem(open, {}, 0)).toBeNull();
    });

    it('accepts an allocation of exactly the money there is, and of exactly what is owed', () => {
      expect(allocationProblem(open, { a: 100 }, 100)).toBeNull();
      expect(allocationProblem(open, { b: 50 }, 500)).toBeNull();
    });

    it('names the invoice when more is given than is owing', () => {
      expect(allocationProblem(open, { b: 50.01 }, 500)).toBe('INV-b: 50.01 is more than the 50.00 still owing.');
    });

    it('refuses a negative amount', () => {
      expect(allocationProblem(open, { a: -1 }, 100)).toBe('INV-a: an amount cannot be negative.');
    });

    it('refuses allocations that come to more than the money there is', () => {
      expect(allocationProblem(open, { a: 100, b: 50 }, 120)).toBe('The allocations come to 150.00, more than the 120.00 available.');
    });

    it('does not count an amount for an invoice that is not on offer', () => {
      expect(allocationProblem(open, { a: 100, elsewhere: 500 }, 100)).toBeNull();
    });

    it('is not fooled by cents that floating point cannot hold', () => {
      expect(allocationProblem([invoice('a', 0.3), invoice('b', 0.3)], { a: 0.1, b: 0.2 }, 0.3)).toBeNull();
    });
  });
});
