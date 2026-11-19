import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, PageHeader, Pager, Spinner, StatusBadge, Table, inputClass } from '../../components/ui';
import { downloadAuthorized } from '../../lib/download';
import { formatDateTime, humanize, shortId } from '../../lib/format';
import { JobProgress } from './JobProgress';
import { RecordPanel } from './RecordPanel';

const PageSize = 50;
const RecordStatuses: Schemas['JobRecordStatus'][] = ['Pending', 'Running', 'Succeeded', 'Failed', 'Invalid', 'NeedsReview', 'AwaitingFence', 'Reconciled', 'Cancelled'];

const isActive = (status: string | undefined) => status === 'Pending' || status === 'Running';

export function JobDetailPage() {
  const { jobId = '' } = useParams();
  const { api, authFetch, can } = useApp();
  const queries = useQueryClient();
  const [params, setParams] = useSearchParams();
  const status = (params.get('status') ?? '') as Schemas['JobRecordStatus'] | '';
  const skip = Number(params.get('skip') ?? 0) || 0;
  const selectedRecord = params.get('record');
  const [exportError, setExportError] = useState<unknown>(null);

  const job = useQuery({
    queryKey: ['job', jobId],
    queryFn: () => unwrap(api.GET('/api/v1/jobs/{jobId}', { params: { path: { jobId } } })),
    refetchInterval: (q) => (isActive(q.state.data?.status) ? 3_000 : false),
  });

  const records = useQuery({
    queryKey: ['job-records', jobId, status, skip],
    queryFn: () => unwrap(api.GET('/api/v1/jobs/{jobId}/records', { params: { path: { jobId }, query: { status: status || undefined, skip, take: PageSize } } })),
    placeholderData: keepPreviousData,
    refetchInterval: isActive(job.data?.status) ? 3_000 : false,
  });

  const cancel = useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/jobs/{jobId}/cancel', { params: { path: { jobId } } })),
    onSettled: () => void queries.invalidateQueries({ queryKey: ['job', jobId] }),
  });

  const update = (next: Record<string, string | null>) => {
    const merged = { ...Object.fromEntries(params), ...next };
    setParams(Object.fromEntries(Object.entries(merged).filter(([, v]) => v !== null && v !== '')) as Record<string, string>);
  };

  if (job.isPending) {
    return <Spinner />;
  }

  if (job.isError) {
    return <ErrorState error={job.error} onRetry={() => void job.refetch()} />;
  }

  const j = job.data;
  const exportResults = async () => {
    setExportError(null);
    try {
      await downloadAuthorized(authFetch, `/api/v1/jobs/${j.id}/results.csv`, `results-${shortId(j.id)}.csv`);
    } catch (e) {
      setExportError(e);
    }
  };

  return (
    <>
      <PageHeader
        title={j.sourceName ?? `Job ${shortId(j.id)}`}
        description={
          <>
            <Link to={`/workflows/${j.workflowId}`} className="text-indigo-700 hover:underline">
              {j.workflowName}
            </Link>{' '}
            v{j.workflowVersionNumber} · created {formatDateTime(j.createdAt)}
          </>
        }
        actions={
          <>
            <Button variant="secondary" onClick={() => void exportResults()}>
              Export results
            </Button>
            {can('Operator') && isActive(j.status) && !j.cancelRequested && (
              <Button variant="danger" disabled={cancel.isPending} onClick={() => window.confirm('Cancel this job? Records already running finish safely first.') && cancel.mutate()}>
                Cancel job
              </Button>
            )}
          </>
        }
      />
      {exportError !== null && <ErrorState error={exportError} />}
      {cancel.isError && <ErrorState error={cancel.error} />}

      <div className="grid gap-4 md:grid-cols-4">
        <Card className="md:col-span-3">
          <div className="flex items-center gap-3">
            <StatusBadge status={j.status} />
            {j.cancelRequested && isActive(j.status) && <span className="text-sm text-slate-500">Cancellation requested</span>}
          </div>
          <div className="mt-3">
            <JobProgress job={j} />
          </div>
        </Card>
        <Card>
          <dl className="space-y-1 text-sm">
            <div className="flex justify-between">
              <dt className="text-slate-500">Started</dt>
              <dd>{formatDateTime(j.startedAt)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-slate-500">Finished</dt>
              <dd>{formatDateTime(j.finishedAt)}</dd>
            </div>
            <div className="flex justify-between">
              <dt className="text-slate-500">Parallel / attempts</dt>
              <dd>
                {j.options.maxConcurrency} / {j.options.maxAttempts}
              </dd>
            </div>
          </dl>
        </Card>
      </div>

      <div className="mt-4 flex flex-wrap gap-2 text-xs" aria-label="Records by status">
        {Object.entries(j.recordsByStatus).map(([s, n]) => (
          <button key={s} type="button" onClick={() => update({ status: s, skip: '0' })} className="rounded-full bg-white px-2 py-1 ring-1 ring-slate-200 hover:bg-slate-50">
            {humanize(s)}: <strong>{n}</strong>
          </button>
        ))}
      </div>

      <div className="mt-4 grid gap-6 xl:grid-cols-5">
        <Card title="Records" className="xl:col-span-3">
          <div className="mb-3 flex items-center gap-2">
            <label htmlFor="record-status" className="text-sm text-slate-600">
              Status
            </label>
            <select id="record-status" className={`${inputClass} w-48`} value={status} onChange={(e) => update({ status: e.target.value, skip: '0' })}>
              <option value="">All</option>
              {RecordStatuses.map((s) => (
                <option key={s} value={s}>
                  {humanize(s)}
                </option>
              ))}
            </select>
          </div>
          {records.isPending ? (
            <Spinner />
          ) : records.isError ? (
            <ErrorState error={records.error} />
          ) : (
            <>
              <Table head={['Row', 'Status', 'Attempts', 'Error', 'Result']}>
                {records.data.map((r) => (
                  <tr
                    key={r.id}
                    data-testid="record-row"
                    className={`cursor-pointer hover:bg-slate-50 ${selectedRecord === r.id ? 'bg-indigo-50' : ''}`}
                    onClick={() => update({ record: r.id })}
                  >
                    <td className="px-3 py-2 font-mono text-xs">{r.rowNumber}</td>
                    <td className="px-3 py-2">
                      <StatusBadge status={r.status} />
                    </td>
                    <td className="px-3 py-2 text-slate-600">{r.attemptCount}</td>
                    <td className="px-3 py-2 text-xs text-rose-700">{r.errorCode ?? (r.validationErrors ? 'Validation failed' : '')}</td>
                    <td className="max-w-56 truncate px-3 py-2 font-mono text-xs text-slate-600">{r.result ? JSON.stringify(r.result) : ''}</td>
                  </tr>
                ))}
              </Table>
              <Pager skip={skip} take={PageSize} count={records.data.length} onChange={(s) => update({ skip: String(s) })} />
            </>
          )}
        </Card>
        <div className="xl:col-span-2">
          {selectedRecord && records.data ? (
            <RecordPanel jobId={j.id} record={records.data.find((r) => r.id === selectedRecord)} recordId={selectedRecord} onClose={() => update({ record: null })} />
          ) : (
            <Card title="Record details">
              <p className="text-sm text-slate-500">Select a record to see its attempts, logs, screenshots and artifacts.</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}
