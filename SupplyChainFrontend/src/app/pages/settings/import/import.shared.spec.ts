import { ImportField } from '../../../services/import.service';
import {
  defaultedRequired, fileProblem, formatDuration, mappingProblems, mappingRows, markDuplicates, matchMark, sessionStatusPill, stepForStatus, unmappedRequired
} from './import.shared';

function field(o: Partial<ImportField>): ImportField {
  return { fieldName: 'x', displayName: 'X', fieldType: 'TEXT', isRequired: false, isKey: false, sortOrder: 0, ...o };
}

const FIELDS: ImportField[] = [
  field({ fieldName: 'code', displayName: 'Customer code', isRequired: true, isKey: true, sortOrder: 1 }),
  field({ fieldName: 'name', displayName: 'Name', isRequired: true, sortOrder: 2 }),
  field({ fieldName: 'external_id', displayName: 'External ID', isKey: true, sortOrder: 3 }),
  field({ fieldName: 'email', displayName: 'Email', fieldType: 'EMAIL', sortOrder: 4 })
];

describe('import.shared — A40 mapping rules and display helpers', () => {
  it('status pills: Completed / Errors / In progress / Failed / Cancelled', () => {
    expect(sessionStatusPill('COMPLETED')).toEqual({ label: 'Completed', tone: 'ok' });
    expect(sessionStatusPill('COMPLETED_WITH_ERRORS')).toEqual({ label: 'Errors', tone: 'wn' });
    for (const s of ['UPLOADED', 'MAPPING', 'VALIDATING', 'VALIDATED', 'IMPORTING']) expect(sessionStatusPill(s).label).withContext(s).toBe('In progress');
    expect(sessionStatusPill('FAILED')).toEqual({ label: 'Failed', tone: 'er' });
    expect(sessionStatusPill('CANCELLED').label).toBe('Cancelled');
  });

  it('a session resumes on the step its status belongs to', () => {
    expect(stepForStatus('UPLOADED')).toBe(1);
    expect(stepForStatus('MAPPING')).toBe(1);
    expect(stepForStatus('VALIDATED')).toBe(2);
    expect(stepForStatus('IMPORTING')).toBe(3);
    expect(stepForStatus('COMPLETED')).toBe(3);
  });

  it('match marks: ✓ exact 1.00, ~ with % for normalized / alias / fuzzy, — manual, ✗ unmapped', () => {
    expect(matchMark('code', 1)).toEqual({ mark: '✓', label: 'Exact', tone: 'ok' });
    expect(matchMark('code', 0.95)).toEqual({ mark: '~', label: '95%', tone: 'wn' });
    expect(matchMark('code', 0.9).label).toBe('90%');
    expect(matchMark('code', 0.734).label).toBe('73%');
    expect(matchMark('code', null).mark).toBe('—');
    expect(matchMark('code', 1, false).mark).toBe('—');
    expect(matchMark(null, null).mark).toBe('✗');
  });

  it('rows come sorted by column and flag a field chosen twice', () => {
    const rows = mappingRows([
      { sourceColumnIndex: 1, sourceColumnName: 'Cust Name', targetFieldName: 'name', isAutoMapped: true, confidenceScore: 0.8 },
      { sourceColumnIndex: 0, sourceColumnName: 'Code', targetFieldName: 'code', isAutoMapped: true, confidenceScore: 1 },
      { sourceColumnIndex: 2, sourceColumnName: 'Title', targetFieldName: 'name', isAutoMapped: false, confidenceScore: null }
    ]);
    expect(rows.map(r => r.sourceColumnName)).toEqual(['Code', 'Cust Name', 'Title']);
    expect(rows.map(r => r.duplicate)).toEqual([false, true, true]);
    rows[2].target = null;
    markDuplicates(rows);
    expect(rows.every(r => !r.duplicate)).toBeTrue();
  });

  it('IMP-MAP-01/02/03 problems in the server\'s exact words', () => {
    const rows = mappingRows([
      { sourceColumnIndex: 0, sourceColumnName: 'Mail', targetFieldName: 'email', isAutoMapped: true, confidenceScore: 1 },
      { sourceColumnIndex: 1, sourceColumnName: 'E-mail', targetFieldName: 'email', isAutoMapped: true, confidenceScore: 0.9 }
    ]);
    expect(mappingProblems(rows, FIELDS)).toEqual([
      'Map required field: Customer code.',
      'Map required field: Name.',
      'Map at least one key field: Customer code, External ID.',
      "Field 'Email' is mapped twice."
    ]);
    expect(unmappedRequired(rows, FIELDS).map(f => f.fieldName)).toEqual(['code', 'name']);
  });

  it('no problems once required + a key are mapped and nothing is doubled', () => {
    const rows = mappingRows([
      { sourceColumnIndex: 0, sourceColumnName: 'Code', targetFieldName: 'code', isAutoMapped: true, confidenceScore: 1 },
      { sourceColumnIndex: 1, sourceColumnName: 'Name', targetFieldName: 'name', isAutoMapped: true, confidenceScore: 1 },
      { sourceColumnIndex: 2, sourceColumnName: 'Junk', targetFieldName: null, isAutoMapped: false, confidenceScore: null }
    ]);
    expect(mappingProblems(rows, FIELDS)).toEqual([]);
  });

  it('required fields with a server default (unmapped, not in unmappedRequiredFields) are exempt', () => {
    const mappings = [{ sourceColumnIndex: 0, sourceColumnName: 'Code', targetFieldName: 'code', isAutoMapped: true, confidenceScore: 1 }];
    const exempt = defaultedRequired(FIELDS, mappings, []);
    expect([...exempt]).toEqual(['name']);
    expect(mappingProblems(mappingRows(mappings), FIELDS, exempt)).toEqual([]);
    expect(defaultedRequired(FIELDS, mappings, ['name']).size).toBe(0);
  });

  it('a key requirement is met by any key field (external id)', () => {
    const fields = [field({ fieldName: 'code', displayName: 'Code', isKey: true }), field({ fieldName: 'ext', displayName: 'Ext', isKey: true })];
    const rows = mappingRows([{ sourceColumnIndex: 0, sourceColumnName: 'E', targetFieldName: 'ext', isAutoMapped: true, confidenceScore: 1 }]);
    expect(mappingProblems(rows, fields)).toEqual([]);
  });

  it('fileProblem copies IMP-FILE-01/02', () => {
    expect(fileProblem(new File(['x'], 'a.xls'), 10)).toBe('Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).');
    const big = new File([new Uint8Array(2 * 1024 * 1024 + 1)], 'big.xlsx');
    expect(fileProblem(big, 2)).toBe('File exceeds the maximum size of 2 MB.');
    expect(fileProblem(new File(['x'], 'ok.CSV'), 10)).toBeNull();
    expect(fileProblem(null, 10)).toBeNull();
  });

  it('formatDuration', () => {
    expect(formatDuration('2026-10-11T10:00:00Z', '2026-10-11T10:00:12Z')).toBe('12 s');
    expect(formatDuration('2026-10-11T10:00:00Z', '2026-10-11T10:01:05Z')).toBe('1 min 05 s');
    expect(formatDuration(null, '2026-10-11T10:01:05Z')).toBe('');
  });
});
