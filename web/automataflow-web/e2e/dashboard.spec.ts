import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

/**
 * Phase 6 acceptance journey through the dashboard UI, against the real Api, Worker (real Chromium, real supervision
 * posture), PostgreSQL and the DemoCrm fixture. Every number asserted below comes from what the platform did.
 */
const repo = process.env.AF_E2E_REPO ?? path.resolve(import.meta.dirname, '../../..');
const password = 'Correct-Horse-2026-e2e';
const crmBaseUrl = process.env.AF_E2E_CRM_URL ?? 'http://democrm:8080';
const run = Date.now().toString(36);

async function register(request: APIRequestContext, prefix: string) {
  const email = `${prefix}-${run}@example.test`;
  const response = await request.post('/api/v1/auth/register', {
    data: { email, password, organizationName: `${prefix} ${run} Ltd`, organizationSlug: `${prefix}-${run}` },
  });
  expect(response.status()).toBe(201);
  return { email, ...(await response.json()) } as { email: string; organizationId: string };
}

async function signIn(page: Page, email: string) {
  await page.goto('/login');
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
}

test('an operator configures, runs, monitors and investigates a real job from the dashboard', async ({ page, request, context }) => {
  // The gateway's strict Content-Security-Policy must not break anything the dashboard does.
  const cspViolations: string[] = [];
  page.on('console', (m) => {
    if (m.type() === 'error' && /Content Security Policy/i.test(m.text())) {
      cspViolations.push(m.text());
    }
  });
  const owner = await register(request, 'acme');
  const workflowName = `Customer registration ${run}`;

  // 1. Sign in; the session survives a reload (HttpOnly refresh cookie, ADR-077) and nothing lands in web storage.
  await signIn(page, owner.email);
  await expect(page.getByTestId('workspace-name')).toHaveText(`acme ${run} Ltd`);
  await page.reload();
  await expect(page.getByTestId('workspace-name')).toHaveText(`acme ${run} Ltd`);
  expect(await page.evaluate(() => window.localStorage.length + window.sessionStorage.length)).toBe(0);
  const cookies = await context.cookies();
  const refresh = cookies.find((c) => c.name === '__Secure-af_refresh');
  expect(refresh?.httpOnly).toBe(true);
  expect(refresh?.sameSite).toBe('Strict');
  expect(refresh?.path).toBe('/api/v1/auth');

  // 2. Create a workflow from its JSON definition, then change it with the guided form and save the draft.
  await page.getByRole('link', { name: 'Workflows' }).first().click();
  await page.getByRole('link', { name: 'New workflow' }).click();
  await page.locator('input[name="workflowName"]').fill(workflowName);
  await page.getByRole('tab', { name: 'JSON' }).click();
  await page.getByLabel('Definition JSON').fill(await readFile(path.join(repo, 'docs/workflows/democrm-register-customer.json'), 'utf8'));
  await page.getByRole('button', { name: 'Validate' }).click();
  await expect(page.getByTestId('validation-result')).toContainText('The definition is valid.');
  await page.getByRole('button', { name: 'Create draft' }).click();
  await expect(page.getByRole('heading', { name: workflowName })).toBeVisible();

  await page.getByRole('link', { name: 'Edit' }).click();
  const form = page.getByTestId('definition-form');
  await form.getByLabel('Description').fill('Registers DemoCrm customers from a CSV (edited in the guided form).');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await expect(page.getByRole('heading', { name: workflowName })).toBeVisible();

  // 3. Publish: the definition attests target-enforced idempotency, which must be acknowledged explicitly.
  await page.getByRole('button', { name: 'Publish' }).first().click();
  const dialog = page.getByTestId('publish-dialog');
  await expect(dialog).toContainText('Target enforced idempotency');
  await expect(dialog.getByRole('button', { name: 'Publish' })).toBeDisabled();
  await dialog.getByRole('checkbox').check();
  await dialog.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByTestId('version-row').first()).toContainText('Published');

  // 4. The workflow's credential is a workspace secret (write-only).
  await page.getByRole('link', { name: 'Secrets' }).first().click();
  await page.locator('input[name="secretName"]').fill('crmPassword');
  await page.locator('input[name="secretValue"]').fill('DemoPass-2026!');
  await page.getByRole('button', { name: 'Save secret' }).click();
  await expect(page.getByRole('cell', { name: 'crmPassword' })).toBeVisible();
  await expect(page.locator('input[name="secretValue"]')).toHaveValue('');

  // 5. Upload the demo CSV and create the job.
  await page.getByRole('link', { name: 'New job' }).first().click();
  await page.locator('select[name="workflow"]').selectOption({ label: `${workflowName} (v1)` });
  await page.locator('input[name="param-crmBaseUrl"]').fill(crmBaseUrl);
  await page.locator('input[name="param-crmUser"]').fill('agent@democrm.test');
  await page.locator('input[name="file"]').setInputFiles(path.join(repo, 'samples/demo-customers.csv'));
  await page.locator('input[name="ignoreUnknownColumns"]').check();
  await page.getByRole('button', { name: 'Create job' }).click();
  const created = page.getByTestId('job-created');
  await expect(created).toContainText('14 records imported');
  await expect(created).toContainText('1 invalid'); // "not-an-email" fails validation at import
  await created.getByRole('link', { name: 'Open the job' }).click();
  const jobUrl = page.url();

  // 6. Monitor until the job finishes (the page polls the Api).
  await expect(page.getByText('Completed with errors').first()).toBeVisible({ timeout: 12 * 60_000 });
  const counts = Object.fromEntries(
    (await page.getByLabel('Records by status').getByRole('button').allTextContents()).map((t) => {
      const [label, n] = t.split(':');
      return [label!.trim(), Number(n)];
    }),
  );
  // The 7 rows the fixture expects to be created (incl. fail-twice, commit-then-fail and slow) end successfully;
  // a commit-then-fail record may end as Reconciled (found, never repeated).
  expect((counts['Succeeded'] ?? 0) + (counts['Reconciled'] ?? 0)).toBe(7);
  expect(counts['Invalid']).toBe(1);
  expect(counts['Needs review'] ?? 0).toBe(0);

  // 7. Investigate: a succeeded record has its confirmation screenshot (served only through the authorized endpoint).
  await page.getByLabel('Status', { exact: true }).selectOption('Succeeded');
  await page.getByTestId('record-row').first().click();
  const attempt = page.getByTestId('attempt').last();
  await expect(attempt.getByRole('img', { name: /Screenshot/ })).toBeVisible();
  expect(await attempt.getByRole('img', { name: /Screenshot/ }).getAttribute('src')).toMatch(/^blob:/);
  await attempt.getByRole('button', { name: 'Show logs' }).click();
  await expect(page.getByTestId('attempt-logs')).toBeVisible();
  await expect(page.getByTestId('attempt-logs')).not.toContainText('DemoPass-2026!');

  // ... and a failed record shows why (a duplicate customer, detected by the workflow, not repeated).
  await page.getByLabel('Status', { exact: true }).selectOption('Failed');
  await page.getByTestId('record-row').first().click();
  await expect(page.getByTestId('record-panel')).toContainText(/DuplicateCustomer|RegistrationRejected/);
  await expect(page.getByTestId('attempt').first()).toBeVisible();

  // 8. Export results: a CSV without record inputs.
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export results' }).click()]);
  const csv = await readFile((await download.path())!, 'utf8');
  expect(csv.split(/\r?\n/)[0]).toContain('rowNumber,operationId,status,errorCode');
  expect(csv.trim().split(/\r?\n/)).toHaveLength(15);
  expect(csv).not.toContain('priya.raman@example.test');

  // 9. The overview reflects the job.
  await page.getByRole('link', { name: 'Overview' }).first().click();
  await expect(page.getByTestId('overview-stats')).toContainText('1');

  // 10. Another workspace sees "not found" for this job, through the same UI.
  const other = await register(request, 'globex');
  const otherContext = await page.context().browser()!.newContext({ baseURL: page.url() });
  const otherPage = await otherContext.newPage();
  await signIn(otherPage, other.email);
  await otherPage.goto(jobUrl);
  await expect(otherPage.getByRole('alert')).toContainText('Not found, or not in this workspace.');
  await otherContext.close();

  // 11. Sign out revokes the session: a reload stays signed out.
  await page.getByRole('button', { name: /example\.test/ }).click();
  await page.getByRole('menuitem', { name: 'Sign out' }).click();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  await page.goto('/jobs');
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  expect(cspViolations).toEqual([]);
});
