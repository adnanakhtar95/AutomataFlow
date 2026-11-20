import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { json, on, renderApp, tokenBody } from '../test/harness';

const overview = {
  totalJobs: 3,
  jobsByStatus: { Completed: 1, Running: 1, Failed: 1 },
  totalRecords: 12,
  recordsByStatus: { Succeeded: 9, Failed: 2, NeedsReview: 1 },
  totalWorkflows: 2,
  publishedWorkflows: 1,
  recentJobs: [],
  recentWorkflows: [{ id: 'wf-1', name: 'Customer registration', latestVersion: 2, createdAt: '2026-11-01T10:00:00Z' }],
};

const catalog = {
  schemaVersion: 1,
  actions: [
    { type: 'navigate', sections: ['setup', 'reconcile', 'steps'], outputs: ['status', 'url', 'failed'], sideEffectAllowed: false, continueAllowed: false, defaultTimeoutMs: 30000 },
    { type: 'click', sections: ['setup', 'reconcile', 'steps'], outputs: ['failed'], sideEffectAllowed: true, continueAllowed: false, defaultTimeoutMs: 10000 },
    { type: 'upload', sections: ['steps'], outputs: ['fileName', 'failed'], sideEffectAllowed: true, continueAllowed: false, defaultTimeoutMs: 30000 },
    { type: 'reconcileMatch', sections: ['reconcile'], outputs: ['failed'], sideEffectAllowed: false, continueAllowed: false, defaultTimeoutMs: 10000 },
  ],
  inputTypes: ['string', 'email'],
  parameterTypes: ['url', 'string'],
  evidenceLevels: ['advisory', 'authoritative'],
  matchKeys: ['operationId', 'businessKey'],
  errorModes: ['fail', 'continue'],
  idempotencyGuarantees: ['targetEnforced'],
};

const templates = [
  { id: 'invoice-approval', title: 'Invoice approval', category: 'Finance', summary: 'Approves invoices after checking the amount.', setup: 'Parameters: backOfficeUrl.' },
];

describe('authentication and routing', () => {
  it('sends a signed-out visitor to sign-in, then back to the page they asked for', async () => {
    const user = userEvent.setup();
    const app = renderApp('/jobs', {
      signedIn: false,
      handlers: [
        on('POST', '/api/v1/auth/login', () => json(tokenBody('Operator'))),
        on('GET', '/api/v1/jobs', () => json([])),
      ],
    });

    await screen.findByRole('button', { name: 'Sign in' });
    await user.type(screen.getByLabelText('E-mail'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'Correct-Horse-2026');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByRole('heading', { name: 'Jobs' });
    expect(app.router.state.location.pathname).toBe('/jobs');
    const login = app.requests.find((r) => new URL(r.url).pathname === '/api/v1/auth/login')!;
    expect(login.headers.get('X-AutomataFlow-Session')).toBe('browser');
  });

  it('shows the Api message when sign-in fails and clears the password', async () => {
    const user = userEvent.setup();
    renderApp('/login', { signedIn: false, handlers: [on('POST', '/api/v1/auth/login', () => json({ title: 'Authentication failed.' }, 401))] });

    await user.type(await screen.findByLabelText('E-mail'), 'ada@example.test');
    await user.type(screen.getByLabelText('Password'), 'wrong-password');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Authentication failed.');
    expect(screen.getByLabelText('Password')).toHaveValue('');
  });

  it('restores a session from the cookie and shows real statistics and the workspace name', async () => {
    renderApp('/', { signedIn: 'Owner', handlers: [on('GET', '/api/v1/overview', () => json(overview))] });

    const stats = await screen.findByTestId('overview-stats');
    expect(within(stats).getByText('Total jobs').nextSibling).toHaveTextContent('3');
    expect(await screen.findByTestId('workspace-name')).toHaveTextContent('Acme Logistics');
    expect(screen.getByText('Customer registration')).toBeInTheDocument();
  });
});

describe('role-aware navigation', () => {
  it('hides write navigation from a Viewer and refuses the secrets page', async () => {
    renderApp('/settings/secrets', { signedIn: 'Viewer', handlers: [] });

    expect(await screen.findByText('You do not have access to this page')).toBeInTheDocument();
    const nav = screen.getAllByRole('navigation', { name: 'Main' })[0]!;
    expect(within(nav).getByText('Jobs')).toBeInTheDocument();
    expect(within(nav).queryByText('New job')).toBeNull();
    expect(within(nav).queryByText('Secrets')).toBeNull();
  });

  it('shows secrets to an Admin and never displays a stored value', async () => {
    renderApp('/settings/secrets', { signedIn: 'Admin', handlers: [on('GET', '/api/v1/secrets', () => json([{ name: 'crmPassword', updatedAt: '2026-11-19T10:00:00Z' }]))] });
    expect(await screen.findByText('crmPassword')).toBeInTheDocument();
    expect(screen.getByLabelText('Value')).toHaveAttribute('type', 'password');
  });
});

describe('workflow editor', () => {
  it('offers in each section only the actions the Api catalog allows there', async () => {
    renderApp('/workflows/new', { signedIn: 'Operator', handlers: [on('GET', '/api/v1/workflow-catalog', () => json(catalog)),
        on('GET', '/api/v1/workflow-templates', () => json(templates))] });

    const steps = await screen.findByLabelText('Action to add to steps');
    const options = (select: HTMLElement) => within(select).getAllByRole('option').map((o) => o.textContent);
    expect(options(steps)).toEqual(['navigate', 'click', 'upload']);
    expect(options(screen.getByLabelText('Action to add to reconcile'))).toEqual(['navigate', 'click', 'reconcileMatch']);
    expect(options(screen.getByLabelText('Action to add to setup'))).toEqual(['navigate', 'click']);
  });

  it('sends the edited definition to the Api for validation and shows its diagnostics with paths', async () => {
    const user = userEvent.setup();
    let sent: unknown = null;
    renderApp('/workflows/new', {
      signedIn: 'Operator',
      handlers: [
        on('GET', '/api/v1/workflow-catalog', () => json(catalog)),
        on('GET', '/api/v1/workflow-templates', () => json(templates)),
        on('POST', '/api/v1/workflows/validate', async (r) => {
          sent = await r.json();
          return json({ valid: false, definitionHash: null, errors: [{ path: 'steps[1].config.selector', code: 'selector-empty', message: 'A selector value is required.' }], warnings: [], attestations: [] });
        }),
      ],
    });

    await user.selectOptions(await screen.findByLabelText('Action to add to steps'), 'click');
    await user.click(within(screen.getByLabelText('Action to add to steps').closest('section')!).getByRole('button', { name: 'Add step' }));
    await user.click(screen.getByRole('button', { name: 'Validate' }));

    const result = await screen.findByTestId('validation-result');
    expect(result).toHaveTextContent('steps[1].config.selector');
    expect(result).toHaveTextContent('A selector value is required.');
    const definition = (sent as { definition: { steps: { id: string; type: string }[] } }).definition;
    expect(definition.steps.map((s) => s.type)).toEqual(['navigate', 'click']);
    expect(definition.steps[1]!.id).toBe('click-1');
  });
});

describe('workflow templates', () => {
  it('creates a draft from a template under the chosen name and opens it', async () => {
    const user = userEvent.setup();
    let body: unknown = null;
    const app = renderApp('/workflows/new', {
      signedIn: 'Operator',
      handlers: [
        on('GET', '/api/v1/workflow-catalog', () => json(catalog)),
        on('GET', '/api/v1/workflow-templates', () => json(templates)),
        on('POST', '/api/v1/workflow-templates/invoice-approval/instantiate', async (r) => {
          body = await r.json();
          return json({ workflowId: 'wf-9', version: { id: 'v-9', versionNumber: 1, status: 'Draft', definitionHash: 'h', createdAt: '2026-11-20T00:00:00Z', publishedAt: null, acknowledgedAttestations: null }, definitionJson: '{}', warnings: [] }, 201);
        }),
        on('GET', '/api/v1/workflows/wf-9', () => json({ workflow: { id: 'wf-9', name: 'Supplier invoices', description: null, latestVersion: 1, createdAt: '2026-11-20T00:00:00Z', publishedVersionId: null, publishedVersionNumber: null }, versions: [] })),
      ],
    });

    const use = await screen.findByRole('button', { name: 'Use template Invoice approval' });
    expect(use).toBeDisabled(); // a name is required first
    await user.type(screen.getByLabelText(/Workflow name/), 'Supplier invoices');
    await user.click(use);
    await waitFor(() => expect(app.router.state.location.pathname).toBe('/workflows/wf-9'));
    expect(body).toEqual({ name: 'Supplier invoices' });
  });
});

describe('job investigation', () => {
  const job = {
    id: 'job-1',
    workflowId: 'wf-1',
    workflowVersionId: 'v-1',
    status: 'CompletedWithErrors',
    options: { maxConcurrency: 2, maxAttempts: 3, retryBackoffSeconds: 5 },
    cancelRequested: false,
    sourceName: 'customers.csv',
    totalRecords: 2,
    createdAt: '2026-11-19T10:00:00Z',
    startedAt: '2026-11-19T10:00:01Z',
    finishedAt: '2026-11-19T10:01:00Z',
    recordsByStatus: { Succeeded: 1, NeedsReview: 1 },
    workflowName: 'Customer registration',
    workflowVersionNumber: 2,
    invalidRecords: 0,
  };
  const records = [
    { id: 'rec-1', rowNumber: 1, operationId: 'op-1', status: 'Succeeded', attemptCount: 1, errorCode: null, errorMessage: null, result: { customerId: 'C-1' }, validationErrors: null, completedAt: '2026-11-19T10:00:30Z' },
    { id: 'rec-2', rowNumber: 2, operationId: 'op-2', status: 'NeedsReview', attemptCount: 3, errorCode: 'IndeterminateOutcome', errorMessage: null, result: null, validationErrors: null, completedAt: null },
  ];
  const handlers = [
    on('GET', '/api/v1/jobs/job-1', () => json(job)),
    on('GET', '/api/v1/jobs/job-1/records', () => json(records)),
    on('GET', /\/api\/v1\/jobs\/job-1\/records\/rec-2\/attempts$/, () =>
      json([{ id: 'att-1', attemptNumber: 1, intent: 'Normal', status: 'Indeterminate', errorCode: 'NavigationTimeout', errorCategory: 'Transient', sideEffectStarted: true, executionFenced: false, startedAt: '2026-11-19T10:00:01Z', finishedAt: '2026-11-19T10:00:31Z', steps: [{ stepId: 'submit', type: 'Click', status: 'Failed', durationMs: 30000, errorCode: 'NavigationTimeout' }], artifacts: [] }]),
    ),
  ];

  it('explains NeedsReview and lets only an Admin requeue it', async () => {
    const user = userEvent.setup();
    renderApp('/jobs/job-1?record=rec-2', { signedIn: 'Admin', handlers });

    const panel = await screen.findByTestId('record-panel');
    expect(panel).toHaveTextContent('could not prove whether the last attempt changed the target system');
    expect(within(panel).getByRole('button', { name: 'Requeue for reconciliation' })).toBeInTheDocument();
    const attempt = await screen.findByTestId('attempt');
    expect(attempt).toHaveTextContent('Side effect started: yes');
    expect(attempt).toHaveTextContent('NavigationTimeout');
    await user.click(within(attempt).getByRole('button', { name: 'Show logs' }));
  });

  it('does not offer requeue to an Operator', async () => {
    renderApp('/jobs/job-1?record=rec-2', { signedIn: 'Operator', handlers });
    const panel = await screen.findByTestId('record-panel');
    expect(within(panel).queryByRole('button', { name: 'Requeue for reconciliation' })).toBeNull();
  });

  it('shows "not found" for a job of another workspace (the Api answers 404)', async () => {
    renderApp('/jobs/foreign', { signedIn: 'Owner', handlers: [on('GET', '/api/v1/jobs/foreign', () => json({ title: 'Not Found' }, 404))] });
    expect(await screen.findByRole('alert')).toHaveTextContent('Not found, or not in this workspace.');
  });

  it('exports results with the bearer header, never with a token in the URL', async () => {
    const user = userEvent.setup();
    URL.createObjectURL = () => 'blob:results';
    URL.revokeObjectURL = () => {};
    const app = renderApp('/jobs/job-1', { signedIn: 'Viewer', handlers: [...handlers, on('GET', '/api/v1/jobs/job-1/results.csv', () => new Response('rowNumber\n1\n', { status: 200 }))] });

    await user.click(await screen.findByRole('button', { name: 'Export results' }));
    await waitFor(() => expect(app.requests.some((r) => r.url.endsWith('/results.csv'))).toBe(true));
    const exportRequest = app.requests.find((r) => r.url.endsWith('/results.csv'))!;
    expect(exportRequest.headers.get('Authorization')).toBe('Bearer access-token');
    expect(exportRequest.url).not.toContain('access-token');
  });
});
