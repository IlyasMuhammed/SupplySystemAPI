import { Observable, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import {
  SaleInquiryLine, SaleInquiryLineRequest, SaleInquiryLineStatus, SaleInquiryStatus, UpdateSaleInquiryLineRequest
} from '../../../services/sales-preorder.service';
import { UserService } from '../../../services/user.service';
import { AuthService } from '../../service/auth.service';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { formatCode } from '../../../shared/format-code';

// A32 C1 — what the inquiry list, form, detail and lines tab share: the spec's status colours (§10.1/§10.2), the
// line-evaluation draft with its client validation (BR-C1-04/05, mirroring the server), and the date-only reads and
// writes (yyyy-MM-dd as the day picked; never toISOString()).

export type StatusColor = 'blue' | 'orange' | 'green' | 'purple' | 'red' | 'yellow' | 'grey';

/** §10.1 legend: RECEIVED blue, UNDER_REVIEW orange, REVIEW_COMPLETE green, QUOTED purple, DECLINED red. */
export const INQUIRY_STATUS_COLOR: Record<SaleInquiryStatus, StatusColor> = {
  RECEIVED:        'blue',
  UNDER_REVIEW:    'orange',
  REVIEW_COMPLETE: 'green',
  QUOTED:          'purple',
  DECLINED:        'red'
};

/** §10.2 legend: CAN_SUPPLY green, PARTIAL yellow, CANNOT_SUPPLY red, UNDER_REVIEW orange, PENDING grey. */
export const INQUIRY_LINE_STATUS_COLOR: Record<SaleInquiryLineStatus, StatusColor> = {
  PENDING:       'grey',
  CAN_SUPPLY:    'green',
  PARTIAL:       'yellow',
  CANNOT_SUPPLY: 'red',
  UNDER_REVIEW:  'orange'
};

/** "Under review" and "pending" both stand between UNDER_REVIEW and REVIEW_COMPLETE (INQ's rule). */
export const UNDECIDED_LINE_STATUSES: SaleInquiryLineStatus[] = ['PENDING', 'UNDER_REVIEW'];

/** Column lengths (SalesPreOrderMaps). */
export const MAX = {
  customerReference: 50,
  headerNotes: 2000,
  declineReason: 500,
  productDescription: 500,
  uomCode: 20,
  rejectionNotes: 500,
  alternativeNotes: 500,
  lineNotes: 1000
} as const;

export const statusLabel = (code?: string | null) => formatCode(code);

export function undecidedLineCount(lines: SaleInquiryLine[]): number {
  return lines.filter(l => UNDECIDED_LINE_STATUSES.includes(l.lineStatus)).length;
}

// ── Dates ─────────────────────────────────────────────────────────────────────

/** A server date-only value as the calendar day it names (for pickers and the date pipe); null stays null. */
export function readDate(iso?: string | null): Date | null {
  return iso ? fromDateOnly(iso) : null;
}

/** A picked day as yyyy-MM-dd; null stays null. */
export function writeDate(date?: Date | null): string | null {
  return date ? toDateOnly(date) : null;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "25 Oct 2026" for a server date-only value; a dash when there is none. */
export function displayDate(iso?: string | null): string {
  const d = readDate(iso);
  if (!d) return '—';
  return `${String(d.getDate()).padStart(2, '0')} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
}

function trimOrNull(text?: string | null): string | null {
  const t = (text ?? '').trim();
  return t ? t : null;
}

function formatQty(n: number): string {
  return Number.isInteger(n) ? String(n) : String(Math.round(n * 10000) / 10000);
}

// ── Line draft ────────────────────────────────────────────────────────────────

/** What the add-line dialog and the evaluation drawer edit. Dates are Dates (picker values). */
export interface LineDraft {
  productUuid: string | null;
  variantUuid: string | null;
  /** The picked catalogue item, for display. */
  variantLabel: string | null;
  productDescription: string;
  requestedQuantity: number | null;
  requestedUomCode: string;
  requestedDeliveryDate: Date | null;
  lineStatus: SaleInquiryLineStatus;
  canSupplyQuantity: number | null;
  estimatedDeliveryDate: Date | null;
  rejectionReasonUuid: string | null;
  rejectionNotes: string;
  alternativeProductUuid: string | null;
  alternativeVariantUuid: string | null;
  alternativeLabel: string | null;
  alternativeNotes: string;
  requiresProcurement: boolean;
  procurementLeadDays: number | null;
  notes: string;
}

export type LineField = keyof LineDraft;
export type LineProblems = Partial<Record<LineField, string>>;

export function emptyLineDraft(): LineDraft {
  return {
    productUuid: null, variantUuid: null, variantLabel: null, productDescription: '', requestedQuantity: null,
    requestedUomCode: '', requestedDeliveryDate: null, lineStatus: 'PENDING', canSupplyQuantity: null,
    estimatedDeliveryDate: null, rejectionReasonUuid: null, rejectionNotes: '', alternativeProductUuid: null,
    alternativeVariantUuid: null, alternativeLabel: null, alternativeNotes: '', requiresProcurement: false,
    procurementLeadDays: null, notes: ''
  };
}

function variantLabel(name?: string | null, sku?: string | null): string | null {
  if (!name && !sku) return null;
  return name && sku ? `${name} (${sku})` : (name ?? sku)!;
}

export function draftFromLine(line: SaleInquiryLine): LineDraft {
  return {
    productUuid: line.productUuid ?? null,
    variantUuid: line.variantUuid ?? null,
    variantLabel: variantLabel(line.variantName, line.variantSku),
    productDescription: line.productDescription,
    requestedQuantity: line.requestedQuantity,
    requestedUomCode: line.requestedUomCode ?? '',
    requestedDeliveryDate: readDate(line.requestedDeliveryDate),
    lineStatus: line.lineStatus,
    canSupplyQuantity: line.canSupplyQuantity ?? null,
    estimatedDeliveryDate: readDate(line.estimatedDeliveryDate),
    rejectionReasonUuid: line.rejectionReasonUuid ?? null,
    rejectionNotes: line.rejectionNotes ?? '',
    alternativeProductUuid: line.alternativeProductUuid ?? null,
    alternativeVariantUuid: line.alternativeVariantUuid ?? null,
    alternativeLabel: variantLabel(line.alternativeVariantName, line.alternativeVariantSku),
    alternativeNotes: line.alternativeNotes ?? '',
    requiresProcurement: line.requiresProcurement,
    procurementLeadDays: line.procurementLeadDays ?? null,
    notes: line.notes ?? ''
  };
}

/** Whether the line names an alternative (then CANNOT_SUPPLY may carry the alternative's delivery date). */
export function hasAlternative(d: LineDraft): boolean {
  return !!(d.alternativeVariantUuid || d.alternativeProductUuid);
}

/**
 * Which evaluation fields apply to the draft's status — what the drawer shows and what is sent. Mirrors the
 * server's clearing (INQ): CAN_SUPPLY/PARTIAL need a date; UNDER_REVIEW may carry one; CANNOT_SUPPLY carries the
 * reason, notes and alternative, and a date only for a named alternative; PENDING carries none.
 */
export function applies(d: LineDraft, field: 'canSupplyQuantity' | 'estimatedDeliveryDate' | 'rejection' | 'alternative'): boolean {
  switch (field) {
    case 'canSupplyQuantity':     return d.lineStatus === 'PARTIAL';
    case 'rejection':
    case 'alternative':           return d.lineStatus === 'CANNOT_SUPPLY';
    case 'estimatedDeliveryDate':
      return d.lineStatus === 'CAN_SUPPLY' || d.lineStatus === 'PARTIAL' || d.lineStatus === 'UNDER_REVIEW'
          || (d.lineStatus === 'CANNOT_SUPPLY' && hasAlternative(d));
  }
}

/** Client validation — the server's rules (contract §4, BR-C1-04/05) plus the column lengths. Empty = valid. */
export function lineProblems(d: LineDraft): LineProblems {
  const p: LineProblems = {};
  const description = d.productDescription.trim();
  if (!description) p.productDescription = 'Describe what the customer asked for.';
  else if (description.length > MAX.productDescription) p.productDescription = `At most ${MAX.productDescription} characters.`;

  if (d.requestedQuantity == null || !(d.requestedQuantity > 0)) p.requestedQuantity = 'The quantity must be more than zero.';
  if (d.requestedUomCode.trim().length > MAX.uomCode) p.requestedUomCode = `At most ${MAX.uomCode} characters.`;
  if (d.notes.trim().length > MAX.lineNotes) p.notes = `At most ${MAX.lineNotes} characters.`;

  switch (d.lineStatus) {
    case 'CAN_SUPPLY':
      if (!d.estimatedDeliveryDate) p.estimatedDeliveryDate = 'Say when it can be delivered.';
      break;
    case 'PARTIAL': {
      const qty = d.canSupplyQuantity;
      const requested = d.requestedQuantity ?? 0;
      if (qty == null || !(qty > 0) || !(qty < requested)) {
        p.canSupplyQuantity = `A partial supply is more than 0 and less than the ${formatQty(requested)} requested.`;
      }
      if (!d.estimatedDeliveryDate) p.estimatedDeliveryDate = 'Say when the partial quantity can be delivered.';
      break;
    }
    case 'CANNOT_SUPPLY':
      if (!d.rejectionReasonUuid) p.rejectionReasonUuid = 'Choose why the line cannot be supplied.';
      if (d.rejectionNotes.trim().length > MAX.rejectionNotes) p.rejectionNotes = `At most ${MAX.rejectionNotes} characters.`;
      if (d.alternativeNotes.trim().length > MAX.alternativeNotes) p.alternativeNotes = `At most ${MAX.alternativeNotes} characters.`;
      break;
  }

  if (d.requiresProcurement && d.procurementLeadDays != null
      && (d.procurementLeadDays < 0 || !Number.isInteger(d.procurementLeadDays))) {
    p.procurementLeadDays = 'Lead time is a whole number of days.';
  }
  return p;
}

/** POST …/lines — what the customer asked for, nothing evaluated yet. */
export function toNewLineRequest(d: LineDraft): SaleInquiryLineRequest {
  return {
    productUuid: d.productUuid,
    variantUuid: d.variantUuid,
    productDescription: d.productDescription.trim(),
    requestedQuantity: d.requestedQuantity ?? 0,
    requestedUomCode: trimOrNull(d.requestedUomCode),
    requestedDeliveryDate: writeDate(d.requestedDeliveryDate),
    notes: trimOrNull(d.notes)
  };
}

/** PUT …/lines/{lineUuid} — the request fields plus only the evaluation fields the status keeps. */
export function toLineRequest(d: LineDraft): UpdateSaleInquiryLineRequest {
  const rejected = applies(d, 'rejection');
  return {
    ...toNewLineRequest(d),
    lineStatus: d.lineStatus,
    canSupplyQuantity: applies(d, 'canSupplyQuantity') ? d.canSupplyQuantity : null,
    estimatedDeliveryDate: applies(d, 'estimatedDeliveryDate') ? writeDate(d.estimatedDeliveryDate) : null,
    rejectionReasonUuid: rejected ? d.rejectionReasonUuid : null,
    rejectionNotes: rejected ? trimOrNull(d.rejectionNotes) : null,
    alternativeProductUuid: rejected ? d.alternativeProductUuid : null,
    alternativeVariantUuid: rejected ? d.alternativeVariantUuid : null,
    alternativeNotes: rejected ? trimOrNull(d.alternativeNotes) : null,
    requiresProcurement: d.requiresProcurement,
    procurementLeadDays: d.requiresProcurement ? d.procurementLeadDays : null
  };
}

/** The sub-row under a line (§10.2): what was decided about it, a sentence each. Empty for an untouched line. */
export function lineSummary(line: SaleInquiryLine): string[] {
  const out: string[] = [];
  const uom = line.requestedUomCode ? ` ${line.requestedUomCode}` : '';
  if (line.lineStatus === 'PARTIAL' && line.canSupplyQuantity != null) {
    out.push(`Can supply: ${formatQty(line.canSupplyQuantity)}${uom} by ${displayDate(line.estimatedDeliveryDate)}`);
  } else if (line.lineStatus === 'CAN_SUPPLY') {
    out.push(`Can supply in full by ${displayDate(line.estimatedDeliveryDate)}`);
  } else if (line.lineStatus === 'UNDER_REVIEW' && line.estimatedDeliveryDate) {
    out.push(`Estimated delivery: ${displayDate(line.estimatedDeliveryDate)}`);
  }
  if (line.lineStatus === 'CANNOT_SUPPLY') {
    const reason = [line.rejectionReasonCode, line.rejectionReasonDescription].filter(Boolean).join(' — ');
    if (reason) out.push(`Reason: ${reason}`);
    if (line.rejectionNotes) out.push(line.rejectionNotes);
    const alt = variantLabel(line.alternativeVariantName, line.alternativeVariantSku);
    if (alt || line.alternativeVariantUuid || line.alternativeProductUuid) {
      out.push(`Alternative: ${alt ?? 'catalogue item'}${line.estimatedDeliveryDate ? ` by ${displayDate(line.estimatedDeliveryDate)}` : ''}`);
    }
    if (line.alternativeNotes) out.push(line.alternativeNotes);
  }
  if (line.requiresProcurement) {
    out.push(`Needs procurement${line.procurementLeadDays != null ? ` (${line.procurementLeadDays} days lead time)` : ''}`);
  }
  if (line.notes) out.push(line.notes);
  return out;
}

// ── Assignee choices ──────────────────────────────────────────────────────────

export interface AssigneeOption { label: string; value: number; }

/**
 * Who an inquiry can be assigned to. Listing users needs USER_MANAGE (api/users), which most sales staff lack, so
 * everyone else gets themself plus whoever already holds the inquiry; a user manager gets every active user.
 */
export function assigneeOptions$(
  users: UserService, auth: AuthService, current?: { id?: number | null; name?: string | null }
): Observable<AssigneeOption[]> {
  const me = auth.getUserData();
  const base: AssigneeOption[] = [];
  if (me?.userId) base.push({ value: me.userId, label: [me.firstName, me.lastName].filter(Boolean).join(' ') || `User ${me.userId}` });
  if (current?.id && !base.some(o => o.value === current.id)) {
    base.push({ value: current.id, label: current.name || `User ${current.id}` });
  }
  if (!auth.hasPermission('USER_MANAGE')) return of(base);

  return users.getUsers({ status: 'Active', pageSize: 100 }).pipe(
    map(res => {
      const items: any[] = res?.result?.items ?? res?.result?.data ?? [];
      const all = [...base];
      for (const u of items.filter(u => u.isActive !== false)) {
        if (!all.some(o => o.value === u.userID)) {
          all.push({ value: u.userID, label: [u.firstName, u.lastName].filter(Boolean).join(' ') || u.email || `User ${u.userID}` });
        }
      }
      return all;
    }),
    catchError(() => of(base))
  );
}

/** The server's own explanation of a refusal, else the fallback. */
export function serverMessage(err: any, fallback: string): string {
  return err?.error?.message || fallback;
}
