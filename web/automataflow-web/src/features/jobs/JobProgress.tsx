import type { Schemas } from '../../api/client';

type Job = Pick<Schemas['JobView'], 'totalRecords' | 'recordsByStatus'>;

/** Record states that will not change without new action (a requeue is an explicit operator action). */
export const FinishedRecordStates = ['Succeeded', 'Reconciled', 'Failed', 'Invalid', 'Cancelled', 'NeedsReview'];

export function progressOf(job: Job) {
  const by = job.recordsByStatus ?? {};
  const value = (k: string) => by[k] ?? 0;
  const done = FinishedRecordStates.reduce((sum, k) => sum + value(k), 0);
  return {
    total: job.totalRecords,
    done,
    succeeded: value('Succeeded') + value('Reconciled'),
    failed: value('Failed') + value('Invalid'),
    review: value('NeedsReview') + value('AwaitingFence'),
    percent: job.totalRecords === 0 ? 100 : Math.round((done / job.totalRecords) * 100),
  };
}

export function JobProgress({ job, compact = false }: { job: Job; compact?: boolean }) {
  const p = progressOf(job);
  const width = (n: number) => `${p.total === 0 ? 0 : (n / p.total) * 100}%`;
  return (
    <div>
      <div
        className="flex h-2 overflow-hidden rounded-full bg-slate-100"
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={p.total}
        aria-valuenow={p.done}
        aria-label={`${p.done} of ${p.total} records finished`}
      >
        <div className="bg-emerald-500" style={{ width: width(p.succeeded) }} />
        <div className="bg-amber-400" style={{ width: width(p.review) }} />
        <div className="bg-rose-500" style={{ width: width(p.failed) }} />
      </div>
      {!compact && (
        <p className="mt-1 text-xs text-slate-500">
          {p.done} of {p.total} records finished · {p.succeeded} succeeded · {p.failed} failed or invalid · {p.review} need review
        </p>
      )}
    </div>
  );
}
