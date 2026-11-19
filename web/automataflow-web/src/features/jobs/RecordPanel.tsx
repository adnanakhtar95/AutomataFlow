import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { unwrap, type Schemas } from '../../api/client';
import { useApp } from '../../app/AppContext';
import { Button, Card, ErrorState, Spinner, StatusBadge } from '../../components/ui';
import { authorizedObjectUrl, DisplayableImageTypes, downloadAuthorized } from '../../lib/download';
import { formatBytes, formatDateTime, formatDuration, humanize } from '../../lib/format';

interface StepRecord {
  stepId: string;
  type: string;
  status: string;
  durationMs: number;
  errorCode: string | null;
}

function stepsOf(attempt: Schemas['AttemptView']): StepRecord[] {
  return Array.isArray(attempt.steps) ? (attempt.steps as StepRecord[]) : [];
}

/** What an operator should know about a record state, in plain words. */
const explanations: Partial<Record<string, string>> = {
  NeedsReview:
    'The platform could not prove whether the last attempt changed the target system, so it will not repeat it automatically. Check the target system; an Admin can requeue the record, which leads to reconciliation, never a blind repeat.',
  AwaitingFence: 'A Worker stopped during this record. The platform is waiting for proof that it is gone before deciding; this resolves automatically or becomes Needs review.',
  Reconciled: 'An earlier attempt’s outcome was found in the target system by reconciliation; nothing was repeated.',
  Invalid: 'The record failed validation at import and was never executed.',
};

function Screenshot({ artifact }: { artifact: Schemas['ArtifactView'] }) {
  const { authFetch } = useApp();
  const [url, setUrl] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let objectUrl: string | null = null;
    let live = true;
    authorizedObjectUrl(authFetch, `/api/v1/artifacts/${artifact.id}`, artifact.contentType)
      .then((u) => {
        objectUrl = u;
        if (live) {
          setUrl(u);
        } else {
          URL.revokeObjectURL(u);
        }
      })
      .catch(() => live && setFailed(true));
    return () => {
      live = false;
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl);
      }
    };
  }, [authFetch, artifact.id, artifact.contentType]);

  if (failed) {
    return <p className="text-xs text-rose-700">The screenshot could not be loaded.</p>;
  }

  return url ? <img src={url} alt={`Screenshot ${artifact.fileName}`} className="mt-2 max-h-80 rounded ring-1 ring-slate-200" /> : <Spinner label="Loading screenshot…" />;
}

function Logs({ attemptId }: { attemptId: string }) {
  const { api } = useApp();
  const logs = useQuery({ queryKey: ['logs', attemptId], queryFn: () => unwrap(api.GET('/api/v1/attempts/{attemptId}/logs', { params: { path: { attemptId } } })) });
  if (logs.isPending) {
    return <Spinner label="Loading logs…" />;
  }

  if (logs.isError) {
    return <ErrorState error={logs.error} />;
  }

  return logs.data.length === 0 ? (
    <p className="text-xs text-slate-500">No log entries.</p>
  ) : (
    <ol className="max-h-72 space-y-0.5 overflow-auto rounded bg-slate-900 p-2 font-mono text-[11px] leading-relaxed text-slate-100" data-testid="attempt-logs">
      {logs.data.map((l) => (
        <li key={l.sequence} className={l.level === 'Error' ? 'text-rose-300' : l.level === 'Warning' ? 'text-amber-200' : ''}>
          <span className="text-slate-400">{new Date(l.timestamp).toLocaleTimeString()}</span> {l.stepId ? `[${l.stepId}] ` : ''}
          {l.eventType}: {l.message}
        </li>
      ))}
    </ol>
  );
}

function Attempt({ attempt }: { attempt: Schemas['AttemptView'] }) {
  const { authFetch } = useApp();
  const [showLogs, setShowLogs] = useState(false);
  const [downloadError, setDownloadError] = useState<unknown>(null);
  const steps = stepsOf(attempt);

  return (
    <li className="rounded-md p-3 ring-1 ring-slate-200" data-testid="attempt">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium">Attempt {attempt.attemptNumber}</span>
        <StatusBadge status={attempt.status} />
        {attempt.intent !== 'Normal' && <span className="rounded bg-sky-50 px-1.5 text-xs text-sky-800">{humanize(attempt.intent)}</span>}
        <span className="text-xs text-slate-500">
          {formatDateTime(attempt.startedAt)} · {formatDuration(attempt.startedAt, attempt.finishedAt)}
        </span>
      </div>
      {attempt.errorCode && (
        <p className="mt-1 text-xs text-rose-700">
          {attempt.errorCode}
          {attempt.errorCategory ? ` (${humanize(attempt.errorCategory)})` : ''}
        </p>
      )}
      <p className="mt-1 text-xs text-slate-500">
        Side effect started: {attempt.sideEffectStarted ? 'yes' : 'no'} · Execution fenced: {attempt.executionFenced ? 'yes' : 'no'}
      </p>
      {steps.length > 0 && (
        <ol className="mt-2 space-y-0.5 text-xs">
          {steps.map((s, i) => (
            <li key={`${s.stepId}-${i}`} className="flex items-center justify-between gap-2">
              <span>
                <code>{s.stepId}</code> <span className="text-slate-400">{s.type}</span>
              </span>
              <span className={s.status === 'Failed' ? 'text-rose-700' : 'text-slate-500'}>
                {humanize(String(s.status))}
                {s.errorCode ? ` · ${s.errorCode}` : ''} · {s.durationMs} ms
              </span>
            </li>
          ))}
        </ol>
      )}
      {attempt.artifacts.length > 0 && (
        <div className="mt-3 space-y-2">
          {attempt.artifacts.map((a) => (
            <div key={a.id}>
              <div className="flex items-center justify-between gap-2 text-xs">
                <span>
                  {a.kind}: {a.fileName} <span className="text-slate-400">({formatBytes(a.sizeBytes)})</span>
                </span>
                <Button
                  variant="ghost"
                  className="text-xs"
                  onClick={() => {
                    setDownloadError(null);
                    downloadAuthorized(authFetch, `/api/v1/artifacts/${a.id}`, a.fileName).catch(setDownloadError);
                  }}
                >
                  Download
                </Button>
              </div>
              {a.kind === 'Screenshot' && DisplayableImageTypes.has(a.contentType) && <Screenshot artifact={a} />}
            </div>
          ))}
          {downloadError !== null && <ErrorState error={downloadError} />}
        </div>
      )}
      <Button variant="ghost" className="mt-2 text-xs" onClick={() => setShowLogs((s) => !s)}>
        {showLogs ? 'Hide logs' : 'Show logs'}
      </Button>
      {showLogs && <Logs attemptId={attempt.id} />}
    </li>
  );
}

export function RecordPanel({ jobId, recordId, record, onClose }: { jobId: string; recordId: string; record: Schemas['RecordView'] | undefined; onClose: () => void }) {
  const { api, can } = useApp();
  const queries = useQueryClient();
  const attempts = useQuery({
    queryKey: ['attempts', jobId, recordId, record?.attemptCount, record?.status],
    queryFn: () => unwrap(api.GET('/api/v1/jobs/{jobId}/records/{recordId}/attempts', { params: { path: { jobId, recordId } } })),
  });
  const requeue = useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/jobs/{jobId}/records/{recordId}/requeue', { params: { path: { jobId, recordId } } })),
    onSettled: () => {
      void queries.invalidateQueries({ queryKey: ['job', jobId] });
      void queries.invalidateQueries({ queryKey: ['job-records', jobId] });
    },
  });

  return (
    <Card title={record ? `Record row ${record.rowNumber}` : 'Record'} actions={<Button variant="ghost" onClick={onClose} aria-label="Close record">✕</Button>}>
      {record && (
        <div className="space-y-2 text-sm" data-testid="record-panel">
          <div className="flex items-center gap-2">
            <StatusBadge status={record.status} />
            {record.errorCode && <span className="text-xs text-rose-700">{record.errorCode}</span>}
          </div>
          {explanations[record.status] && <p className="rounded bg-amber-50 p-2 text-xs text-amber-900">{explanations[record.status]}</p>}
          {record.errorMessage && <p className="text-xs text-slate-600">{record.errorMessage}</p>}
          <p className="text-xs text-slate-500">
            Operation id <code>{record.operationId}</code>
          </p>
          {record.validationErrors != null && (
            <div>
              <p className="text-xs font-medium text-slate-700">Validation errors</p>
              <pre className="mt-1 overflow-auto rounded bg-slate-50 p-2 text-xs">{JSON.stringify(record.validationErrors, null, 2)}</pre>
            </div>
          )}
          {record.result != null && (
            <div>
              <p className="text-xs font-medium text-slate-700">Result</p>
              <pre className="mt-1 overflow-auto rounded bg-slate-50 p-2 text-xs">{JSON.stringify(record.result, null, 2)}</pre>
            </div>
          )}
          {record.status === 'NeedsReview' && can('Admin') && (
            <Button
              variant="secondary"
              disabled={requeue.isPending}
              onClick={() => window.confirm('Requeue this record? The platform will reconcile with the target system first and never repeat blindly.') && requeue.mutate()}
            >
              Requeue for reconciliation
            </Button>
          )}
          {requeue.isError && <ErrorState error={requeue.error} />}
        </div>
      )}
      <h3 className="mb-2 mt-4 text-xs font-semibold uppercase tracking-wide text-slate-500">Attempts</h3>
      {attempts.isPending ? (
        <Spinner />
      ) : attempts.isError ? (
        <ErrorState error={attempts.error} />
      ) : attempts.data.length === 0 ? (
        <p className="text-sm text-slate-500">No attempt yet.</p>
      ) : (
        <ol className="space-y-3">
          {attempts.data.map((a) => (
            <Attempt key={a.id} attempt={a} />
          ))}
        </ol>
      )}
    </Card>
  );
}
