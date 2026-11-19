import { describe, expect, it } from 'vitest';
import { checkRecordFile, MaxUploadBytes } from '../features/jobs/NewJobPage';
import { progressOf } from '../features/jobs/JobProgress';
import { readSelector, writeSelector } from '../features/workflows/editor/actionForms';
import { formatBytes, formatDuration, humanize } from './format';
import { atLeast } from './roles';

describe('roles', () => {
  it('orders Viewer < Operator < Admin < Owner and refuses unknown roles', () => {
    expect(atLeast('Owner', 'Admin')).toBe(true);
    expect(atLeast('Operator', 'Operator')).toBe(true);
    expect(atLeast('Operator', 'Admin')).toBe(false);
    expect(atLeast('PlatformOperator', 'Viewer')).toBe(false);
    expect(atLeast(undefined, 'Viewer')).toBe(false);
  });
});

describe('record file pre-check', () => {
  const file = (name: string, size: number) => new File([new Uint8Array(size)], name);

  it('accepts non-empty .csv and .json files within the limit', () => {
    expect(checkRecordFile(file('customers.CSV', 10))).toBeNull();
    expect(checkRecordFile(file('orders.json', 10))).toBeNull();
  });

  it('refuses other extensions, empty and oversized files', () => {
    expect(checkRecordFile(null)).toMatch(/choose/i);
    expect(checkRecordFile(file('macro.xlsm', 10))).toMatch(/only/i);
    expect(checkRecordFile(file('empty.csv', 0))).toMatch(/empty/i);
    expect(checkRecordFile(file('big.csv', MaxUploadBytes + 1))).toMatch(/larger/i);
  });
});

describe('progress', () => {
  it('counts finished states, including records waiting for review, and never divides by zero', () => {
    const p = progressOf({ totalRecords: 10, recordsByStatus: { Succeeded: 4, Reconciled: 1, Failed: 1, Invalid: 1, NeedsReview: 1, Running: 1, Pending: 1 } });
    expect(p).toMatchObject({ done: 8, succeeded: 5, failed: 2, review: 1, percent: 80 });
    expect(progressOf({ totalRecords: 0, recordsByStatus: {} }).percent).toBe(100);
  });
});

describe('selectors', () => {
  it('round-trips each selector kind and keeps a role name', () => {
    expect(readSelector({ testId: 'submit' })).toEqual({ kind: 'testId', value: 'submit', name: '' });
    expect(readSelector({ role: 'button', name: 'Save' })).toEqual({ kind: 'role', value: 'button', name: 'Save' });
    expect(writeSelector('role', 'button', 'Save')).toEqual({ role: 'button', name: 'Save' });
    expect(writeSelector('css', '#x', 'ignored')).toEqual({ css: '#x' });
    expect(readSelector(undefined).kind).toBe('testId');
  });
});

describe('format', () => {
  it('formats sizes, durations and statuses', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatDuration('2026-01-01T00:00:00Z', '2026-01-01T00:00:02.500Z')).toBe('3 s');
    expect(formatDuration('2026-01-01T00:00:00Z', null)).toBe('—');
    expect(humanize('CompletedWithErrors')).toBe('Completed with errors');
  });
});
