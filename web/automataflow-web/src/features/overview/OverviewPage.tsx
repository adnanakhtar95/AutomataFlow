import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Card, EmptyState, ErrorState, PageHeader, Spinner, Stat, StatusBadge, Table } from '../../components/ui';
import { formatDateTime } from '../../lib/format';
import { JobProgress } from '../jobs/JobProgress';

const count = (map: Record<string, number> | undefined, ...keys: string[]) => keys.reduce((sum, k) => sum + (map?.[k] ?? 0), 0);

export function OverviewPage() {
  const { api, can } = useApp();
  const overview = useQuery({
    queryKey: ['overview'],
    queryFn: () => unwrap(api.GET('/api/v1/overview')),
    refetchInterval: 10_000,
  });

  if (overview.isPending) {
    return <Spinner />;
  }

  if (overview.isError) {
    return <ErrorState error={overview.error} onRetry={() => void overview.refetch()} />;
  }

  const o = overview.data;
  const needsReview = count(o.recordsByStatus, 'NeedsReview');

  return (
    <>
      <PageHeader
        title="Overview"
        description="Jobs, records and workflows in this workspace."
        actions={can('Operator') && <Link to="/jobs/new" className="rounded-md bg-indigo-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-indigo-500">New job</Link>}
      />
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4" data-testid="overview-stats">
        <Stat label="Total jobs" value={o.totalJobs} />
        <Stat label="Running" value={count(o.jobsByStatus, 'Running', 'Pending')} hint="Pending or running" />
        <Stat label="Completed" value={count(o.jobsByStatus, 'Completed')} hint={`${count(o.jobsByStatus, 'CompletedWithErrors')} with errors`} />
        <Stat label="Failed" value={count(o.jobsByStatus, 'Failed')} hint={`${count(o.jobsByStatus, 'Cancelled')} cancelled`} />
      </div>
      <div className="mt-4 grid grid-cols-2 gap-4 md:grid-cols-4">
        <Stat label="Records" value={o.totalRecords} />
        <Stat label="Succeeded" value={count(o.recordsByStatus, 'Succeeded', 'Reconciled')} />
        <Stat label="Failed or invalid" value={count(o.recordsByStatus, 'Failed', 'Invalid')} />
        <Stat label="Needs review" value={needsReview} hint={needsReview > 0 ? 'Open the job to investigate' : undefined} />
      </div>

      <div className="mt-6 grid gap-6 lg:grid-cols-3">
        <Card title="Recent jobs" className="lg:col-span-2" actions={<Link to="/jobs" className="text-sm text-indigo-700 hover:underline">All jobs</Link>}>
          {o.recentJobs.length === 0 ? (
            <EmptyState title="No jobs yet">Upload records to a published workflow to start one.</EmptyState>
          ) : (
            <Table head={['Job', 'Workflow', 'Status', 'Progress', 'Created']}>
              {o.recentJobs.map((job) => (
                <tr key={job.id}>
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
                  </td>
                  <td className="w-48 px-3 py-2">
                    <JobProgress job={job} compact />
                  </td>
                  <td className="whitespace-nowrap px-3 py-2 text-slate-500">{formatDateTime(job.createdAt)}</td>
                </tr>
              ))}
            </Table>
          )}
        </Card>
        <Card title="Recent workflows" actions={<Link to="/workflows" className="text-sm text-indigo-700 hover:underline">All workflows</Link>}>
          <p className="mb-3 text-sm text-slate-500">
            {o.publishedWorkflows} of {o.totalWorkflows} workflows have a published version.
          </p>
          {o.recentWorkflows.length === 0 ? (
            <EmptyState title="No workflows yet" />
          ) : (
            <ul className="divide-y divide-slate-100">
              {o.recentWorkflows.map((w) => (
                <li key={w.id} className="py-2">
                  <Link to={`/workflows/${w.id}`} className="text-sm font-medium text-indigo-700 hover:underline">
                    {w.name}
                  </Link>
                  <p className="text-xs text-slate-500">
                    {w.latestVersion} version{w.latestVersion === 1 ? '' : 's'} · created {formatDateTime(w.createdAt)}
                  </p>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </>
  );
}
