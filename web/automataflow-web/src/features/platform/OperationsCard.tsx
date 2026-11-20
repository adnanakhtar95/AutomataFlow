import { useQuery } from '@tanstack/react-query';
import { unwrap } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Card, ErrorState, Spinner, Stat } from '../../components/ui';
import { formatBytes, formatDateTime } from '../../lib/format';

/** Heartbeats older than this are shown as stale: the supervisor fences a Worker whose process died. */
export const StaleHeartbeatSeconds = 60;

const seconds = (value: number | null | undefined) => (value == null ? '—' : value < 120 ? `${Math.round(value)} s` : `${Math.round(value / 60)} min`);

/** Platform operations (Phase 10): queue depth, Worker liveness, 24-hour execution metrics and artifact storage. */
export function OperationsCard() {
  const { api } = useApp();
  const operations = useQuery({ queryKey: ['platform-operations'], queryFn: () => unwrap(api.GET('/api/v1/platform/operations')), refetchInterval: 10_000 });

  if (operations.isPending) {
    return <Spinner />;
  }

  if (operations.isError) {
    return <ErrorState error={operations.error} />;
  }

  const o = operations.data;
  const live = o.workers.filter((w) => !w.fenced && (w.heartbeatAgeSeconds ?? Infinity) < StaleHeartbeatSeconds);
  const attempts = Object.values(o.metrics.attemptsByStatus).reduce((a, b) => a + b, 0);
  const usedPercent = o.storage.totalBytes && o.storage.freeBytes != null ? Math.round(100 - (o.storage.freeBytes / o.storage.totalBytes) * 100) : null;

  return (
    <Card title="Operations" actions={<span className="text-xs text-slate-400">updated {formatDateTime(o.at)}</span>}>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4" data-testid="operations">
        <Stat label="Ready records" value={o.queue.readyRecords} hint={`oldest waiting ${seconds(o.queue.oldestReadyAgeSeconds)}`} />
        <Stat label="Running" value={o.queue.runningRecords} hint={`${o.queue.activeJobs} active jobs · ${o.queue.scheduledRetries} retries scheduled`} />
        <Stat label="Needs attention" value={o.queue.needsReview + o.queue.awaitingFence} hint={`${o.queue.needsReview} needs review · ${o.queue.awaitingFence} awaiting fence`} />
        <Stat label="Live workers" value={live.length} hint={live.length === 0 ? 'No Worker is processing records' : undefined} />
        <Stat label="Attempts (24 h)" value={attempts} hint={`${o.metrics.recordsCompleted} records completed`} />
        <Stat label="Attempt time" value={seconds(o.metrics.averageAttemptSeconds)} hint={`p95 ${seconds(o.metrics.p95AttemptSeconds)}`} />
        <Stat label="Artifacts" value={formatBytes(o.storage.artifactBytes)} hint={`${o.storage.artifactFiles} files`} />
        <Stat label="Disk used" value={usedPercent == null ? '—' : `${usedPercent} %`} hint={o.storage.freeBytes == null ? undefined : `${formatBytes(o.storage.freeBytes)} free`} />
      </div>
      {o.workers.length > 0 && (
        <ul className="mt-4 space-y-1 text-xs text-slate-600">
          {o.workers.slice(0, 8).map((w) => (
            <li key={w.incarnationId}>
              <code>{w.incarnationId.slice(0, 8)}</code> on {w.hostId} · heartbeat {seconds(w.heartbeatAgeSeconds)} ago
              {w.fenced ? ' · fenced (stopped)' : (w.heartbeatAgeSeconds ?? Infinity) >= StaleHeartbeatSeconds ? ' · stale' : ' · live'}
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
