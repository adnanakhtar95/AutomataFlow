import type { RouteObject } from 'react-router';
import { Layout, type RouteHandle } from '../components/Layout';
import { LoginPage } from '../features/auth/LoginPage';
import { NotFound, RequireAuth, RequireRole, RouteError } from '../features/auth/guards';
import { JobDetailPage } from '../features/jobs/JobDetailPage';
import { JobsPage } from '../features/jobs/JobsPage';
import { NewJobPage } from '../features/jobs/NewJobPage';
import { OverviewPage } from '../features/overview/OverviewPage';
import { SecretsPage } from '../features/settings/SecretsPage';
import { WorkflowDetailPage } from '../features/workflows/WorkflowDetailPage';
import { WorkflowEditorPage } from '../features/workflows/WorkflowEditorPage';
import { WorkflowsPage } from '../features/workflows/WorkflowsPage';

const crumb = (value: RouteHandle['crumb']): RouteHandle => ({ crumb: value });

export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    path: '/',
    element: (
      <RequireAuth>
        <Layout />
      </RequireAuth>
    ),
    errorElement: <RouteError />,
    handle: crumb('Overview'),
    children: [
      { index: true, element: <OverviewPage /> },
      {
        path: 'workflows',
        handle: crumb('Workflows'),
        children: [
          { index: true, element: <WorkflowsPage /> },
          { path: 'new', handle: crumb('New'), element: <RequireRole minimum="Operator"><WorkflowEditorPage /></RequireRole> },
          {
            path: ':workflowId',
            handle: crumb('Workflow'),
            children: [
              { index: true, element: <WorkflowDetailPage /> },
              { path: 'versions/:versionId/edit', handle: crumb('Edit'), element: <RequireRole minimum="Operator"><WorkflowEditorPage /></RequireRole> },
            ],
          },
        ],
      },
      {
        path: 'jobs',
        handle: crumb('Jobs'),
        children: [
          { index: true, element: <JobsPage /> },
          { path: 'new', handle: crumb('New job'), element: <RequireRole minimum="Operator"><NewJobPage /></RequireRole> },
          { path: ':jobId', handle: crumb((p) => `Job ${(p.jobId ?? '').slice(0, 8)}`), element: <JobDetailPage /> },
        ],
      },
      { path: 'settings/secrets', handle: crumb('Secrets'), element: <RequireRole minimum="Admin"><SecretsPage /></RequireRole> },
      { path: '*', element: <NotFound /> },
    ],
  },
];
