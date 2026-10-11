import { Pipe, PipeTransform } from '@angular/core';

const fmt = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const qtyFmt = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3 });

/** "PKR 1,234.50" (POS is single-currency: the org sale-base currency, §0). */
export function formatPosMoney(value: number | null | undefined, currency?: string | null): string {
    const v = fmt.format(value ?? 0);
    return currency ? `${currency} ${v}` : v;
}

export function formatQty(value: number | null | undefined): string {
    return qtyFmt.format(value ?? 0);
}

@Pipe({ name: 'posMoney', standalone: true })
export class PosMoneyPipe implements PipeTransform {
    transform(value: number | null | undefined, currency?: string | null): string {
        return formatPosMoney(value, currency);
    }
}

@Pipe({ name: 'posQty', standalone: true })
export class PosQtyPipe implements PipeTransform {
    transform(value: number | null | undefined): string {
        return formatQty(value);
    }
}

/** "SAFE_DROP" → "Safe drop". */
export function codeLabel(code: string | null | undefined): string {
    if (!code) return '';
    const s = code.replace(/_/g, ' ').toLowerCase();
    return s.charAt(0).toUpperCase() + s.slice(1);
}

@Pipe({ name: 'posLabel', standalone: true })
export class PosLabelPipe implements PipeTransform {
    transform(code: string | null | undefined): string {
        return codeLabel(code);
    }
}
