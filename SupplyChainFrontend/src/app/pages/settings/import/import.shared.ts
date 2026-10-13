import { FlowTone } from '../../../shared/flow';
import { ColumnMapping, ImportField, ImportProfile, ImportSessionStatus } from '../../../services/import.service';

// A40 — what the import pages share: status pills, match marks, the mapping rules (IMP-MAP-01..03 with the server's exact
// sentences, checked here first so Next can be disabled), and small formatters.

export interface StatusPill { label: string; tone: FlowTone; }

/** Dashboard / list pills: Completed · Errors · In progress · Failed · Cancelled. */
export function sessionStatusPill(status: ImportSessionStatus | string | null | undefined): StatusPill {
  switch (status) {
    case 'COMPLETED': return { label: 'Completed', tone: 'ok' };
    case 'COMPLETED_WITH_ERRORS': return { label: 'Errors', tone: 'wn' };
    case 'FAILED': return { label: 'Failed', tone: 'er' };
    case 'CANCELLED': return { label: 'Cancelled', tone: '' };
    default: return { label: 'In progress', tone: 'in' };
  }
}

/** The step a session in this state resumes on in the wizard (0 Upload … 3 Results). */
export function stepForStatus(status: ImportSessionStatus | string): number {
  switch (status) {
    case 'UPLOADED':
    case 'MAPPING': return 1;
    case 'VALIDATING':
    case 'VALIDATED': return 2;
    default: return 3;
  }
}

export interface MatchMark { mark: '✓' | '~' | '—' | '✗'; label: string; tone: FlowTone; }

/** ✓ exact (1.00) · ~ normalized/alias/fuzzy with % · — manual · ✗ unmapped. */
export function matchMark(target: string | null | undefined, confidence: number | null | undefined, isAuto = true): MatchMark {
  if (!target) return { mark: '✗', label: 'Not mapped', tone: '' };
  if (!isAuto || confidence === null || confidence === undefined) return { mark: '—', label: 'Manual', tone: 'in' };
  if (confidence >= 0.995) return { mark: '✓', label: 'Exact', tone: 'ok' };
  return { mark: '~', label: `${Math.round(confidence * 100)}%`, tone: 'wn' };
}

export interface MappingRow {
  sourceColumnIndex: number;
  sourceColumnName: string;
  /** Bound to the dropdown; null = "— Ignore —". */
  target: string | null;
  isAutoMapped: boolean;
  confidence: number | null;
  mark: MatchMark;
  duplicate: boolean;
}

export function mappingRows(mappings: ColumnMapping[]): MappingRow[] {
  const rows = [...(mappings ?? [])].sort((a, b) => a.sourceColumnIndex - b.sourceColumnIndex).map(m => ({
    sourceColumnIndex: m.sourceColumnIndex,
    sourceColumnName: m.sourceColumnName,
    target: m.targetFieldName ?? null,
    isAutoMapped: !!m.isAutoMapped,
    confidence: m.confidenceScore ?? null,
    mark: matchMark(m.targetFieldName, m.confidenceScore, !!m.isAutoMapped),
    duplicate: false
  }));
  markDuplicates(rows);
  return rows;
}

/** Re-flags the rows whose target is chosen by another row too. */
export function markDuplicates(rows: MappingRow[]): void {
  const counts = new Map<string, number>();
  for (const r of rows) if (r.target) counts.set(r.target, (counts.get(r.target) ?? 0) + 1);
  for (const r of rows) r.duplicate = !!r.target && (counts.get(r.target) ?? 0) > 1;
}

/**
 * Required fields the server does not ask to be mapped: unmapped in the session it returned yet absent from its
 * `unmappedRequiredFields` (a required field with a default value need not be mapped).
 */
export function defaultedRequired(fields: ImportField[], mappings: ColumnMapping[], unmappedRequiredFields: string[]): Set<string> {
  const mapped = new Set((mappings ?? []).map(m => m.targetFieldName).filter(Boolean));
  const asked = new Set(unmappedRequiredFields ?? []);
  return new Set(fields.filter(f => f.isRequired && !mapped.has(f.fieldName) && !asked.has(f.fieldName)).map(f => f.fieldName));
}

/** The first problems the server would refuse (IMP-MAP-01 required, -02 key, -03 duplicates), in its exact words. */
export function mappingProblems(rows: MappingRow[], fields: ImportField[], exempt: Set<string> = new Set()): string[] {
  const problems: string[] = [];
  const mapped = new Set(rows.map(r => r.target).filter((t): t is string => !!t));
  for (const f of fields) {
    if (f.isRequired && !exempt.has(f.fieldName) && !mapped.has(f.fieldName)) problems.push(`Map required field: ${f.displayName}.`);
  }
  const keys = fields.filter(f => f.isKey);
  if (keys.length && !keys.some(k => mapped.has(k.fieldName))) {
    problems.push(`Map at least one key field: ${keys.map(k => k.displayName).join(', ')}.`);
  }
  const seen = new Set<string>();
  for (const r of rows) {
    if (!r.target || seen.has(r.target) || !r.duplicate) continue;
    seen.add(r.target);
    const display = fields.find(f => f.fieldName === r.target)?.displayName ?? r.target;
    problems.push(`Field '${display}' is mapped twice.`);
  }
  return problems;
}

/** Required fields no column is mapped to (highlighted on the Map step). */
export function unmappedRequired(rows: MappingRow[], fields: ImportField[], exempt: Set<string> = new Set()): ImportField[] {
  const mapped = new Set(rows.map(r => r.target).filter(Boolean));
  return fields.filter(f => f.isRequired && !exempt.has(f.fieldName) && !mapped.has(f.fieldName));
}

const ICONS: Record<string, string> = {
  UNIT: 'pi-sliders-h', PRODUCT_CATEGORY: 'pi-tags', PRODUCT: 'pi-box', WAREHOUSE: 'pi-warehouse', WAREHOUSE_LOCATION: 'pi-map-marker',
  TAX_CODE: 'pi-percentage', CUSTOMER: 'pi-users', SUPPLIER: 'pi-building', SUPPLIER_CONTACT: 'pi-id-card', PRICING_RULE: 'pi-dollar',
  OPENING_STOCK: 'pi-database', CUSTOMER_OPENING_BALANCE: 'pi-wallet', SUPPLIER_OPENING_BALANCE: 'pi-credit-card'
};

export function entityIcon(entityType: string): string {
  return 'pi ' + (ICONS[entityType] ?? 'pi-file');
}

/** Profiles in wizard order (importOrder, then name). */
export function byImportOrder(a: ImportProfile, b: ImportProfile): number {
  return (a.importOrder - b.importOrder) || a.name.localeCompare(b.name);
}

export function formatBytes(bytes: number | null | undefined): string {
  const b = Number(bytes ?? 0);
  if (b < 1024) return `${b} B`;
  if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`;
  return `${(b / (1024 * 1024)).toFixed(1)} MB`;
}

/** "1 min 05 s" / "12 s" between two instants; '' when either is missing. */
export function formatDuration(from: string | null | undefined, to: string | null | undefined): string {
  if (!from || !to) return '';
  const ms = new Date(to).getTime() - new Date(from).getTime();
  if (isNaN(ms) || ms < 0) return '';
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min ${String(s % 60).padStart(2, '0')} s`;
  return `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;
}

/** Client-side copy of the server's first file checks (IMP-FILE-01/02), same sentences. */
export function fileProblem(file: File | null, maxFileSizeMb: number | null | undefined): string | null {
  if (!file) return null;
  if (!/\.(xlsx|csv)$/i.test(file.name)) return 'Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).';
  if (maxFileSizeMb && file.size > maxFileSizeMb * 1024 * 1024) return `File exceeds the maximum size of ${maxFileSizeMb} MB.`;
  return null;
}

export const OPENING_DATE_MESSAGE = 'Set the opening balance date in Finance settings first.';
