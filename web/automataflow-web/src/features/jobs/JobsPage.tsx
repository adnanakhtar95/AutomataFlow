import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link, useSearchParams } from 'react-router';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Card, EmptyState, ErrorState, PageHeader, Pager, Spinner, StatusBadge, Table, inputClass } from '../../components/ui';
import { formatDateTime, humanize } from '../../lib/format';
import { JobProgress } from './JobProgress';

const PageSize = 25;
const Statuses: Schemas['JobStatus'][] = ['Pending', 'Running', 'Completed', 'CompletedWithErrors', 'Failed', 'Cancelled'];

export function JobsPage() {
  const { api, can } = useApp();
  const [params, setParams] = useSearchParams();
  const status = (params.get('status') ?? '') as Schemas['JobStatus'] | '';
  const skip = Number(params.get('skip') ?? 0) || 0;

  const jobs = useQuery({
    queryKey: ['jobs', status, skip],
    queryFn: () => unwrap(api.GET('/api/v1/jobs', { params: { query: { status: status || undefined, skip, take: PageSize } } })),
    placeholderData: keepPreviousData,
    refetchInterval: 5_000,
  });

  const update = (next: Record<string, string>) => setParams({ ...Object.fromEntries(params), ...next });

  return (
    <>
      <PageHeader
        title="Jobs"
        description="Every job of this workspace, newest first. Running jobs refresh automatically."
        actions={can('Operator') && <Link to="/jobs/new" className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-indigo-500">New job</Link>}
      />
      <Card>
        <div className="mb-3 flex items-center gap-2">
          <label htmlFor="job-status" className="text-sm text-slate-600">
            Status
          </label>
          <select id="job-status" className={`${inputClass} w-56`} value={status} onChange={(e) => update({ status: e.target.value, skip: '0' })}>
            <option value="">All</option>
            {Statuses.map((s) => (
              <option key={s} value={s}>
                {humanize(s)}
              </option>
            ))}
          </select>
        </div>
        {jobs.isPending ? (
          <Spinner />
        ) : jobs.isError ? (
          <ErrorState error={jobs.error} onRetry={() => void jobs.refetch()} />
        ) : jobs.data.length === 0 && skip === 0 ? (
          <EmptyState title="No jobs">{status ? 'No job has this status.' : 'Create a job by uploading a record file.'}</EmptyState>
        ) : (
          <>
            <Table head={['Job', 'Workflow', 'Status', 'Records', 'Progress', 'Created', 'Finished']}>
              {jobs.data.map((job) => (
                <tr key={job.id} data-testid="job-row">
                  <td className="px-3 py-2">
                    <Link to={`/jobs/${job.id}`} className="font-medium text-indigo-700 hover:underline">
                      {job.sourceName ?? job.id.slice(0, 8)}
                    </Link>
                  </td>
                  <td className="px-3 py-2 text-slate-600">
                    {job.workflowName} v{job.workflowVersionNumber}
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={job.status} />
                    {job.cancelRequested && job.status === 'Running' && <span className="ml-2 text-xs text-slate-500">cancelling</span>}
                  </td>
                  <td className="px-3 py-2 text-slate-600">{job.totalRecords}</td>
                  <td className="w-48 px-3 py-2">
                    <JobProgress job={job} compact />
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(job.createdAt)}</td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(job.finishedAt)}</td>
                </tr>
              ))}
            </Table>
            <Pager skip={skip} take={PageSize} count={jobs.data.length} onChange={(s) => update({ skip: String(s) })} />
          </>
        )}
      </Card>
    </>
  );
}
