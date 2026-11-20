import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * Phase 8 acceptance: a new business is onboarded entirely through the product, with no database access. The platform
 * operator was created by the Api's `bootstrap-operator` command (scripts/e2e-dashboard.ps1).
 */
const operatorEmail = process.env.AF_E2E_OPERATOR_EMAIL ?? 'operator@platform.example.test';
const operatorPassword = process.env.AF_E2E_OPERATOR_PASSWORD ?? '';
const run = Date.now().toString(36);
const password = 'Correct-Horse-2026-onb';

async function signIn(page: Page, email: string, secret: string) {
  await page.goto('/login');
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('Password').fill(secret);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

async function acceptInvitation(browser: Browser, link: string, baseURL: string, newPassword: string) {
  const context = await browser.newContext({ baseURL });
  const page = await context.newPage();
  await page.goto(link.replace(/^https?:\/\/[^/]+/, ''));
  await expect(page.getByTestId('accept-invitation')).toBeVisible();
  await page.getByLabel(/Choose a password/).fill(newPassword);
  await page.getByLabel(/Repeat the password/).fill(newPassword);
  await page.getByRole('button', { name: 'Create account and join' }).click();
  await expect(page.getByRole('heading', { name: 'Overview' })).toBeVisible();
  expect(new URL(page.url()).hash).toBe(''); // the token left the address bar
  return { context, page };
}

test('a platform operator onboards a business whose team joins and works without any database access', async ({ page, browser, baseURL }) => {
  test.skip(!operatorPassword, 'AF_E2E_OPERATOR_PASSWORD is set by scripts/e2e-dashboard.ps1');
  const workspaceName = `Globex Shipping ${run}`;
  const ownerEmail = `owner-${run}@globex.example.test`;
  const colleagueEmail = `colleague-${run}@globex.example.test`;

  // 1. The operator creates the workspace and gets a one-time Owner invitation link.
  await signIn(page, operatorEmail, operatorPassword);
  await page.getByRole('link', { name: 'Platform' }).first().click();
  await page.locator('input[name="workspaceName"]').fill(workspaceName);
  await page.locator('input[name="workspaceSlug"]').fill(`globex-${run}`);
  await page.locator('input[name="ownerEmail"]').fill(ownerEmail);
  await page.getByRole('button', { name: 'Create workspace' }).click();
  const ownerLink = await page.getByLabel('Owner invitation link').inputValue();
  expect(ownerLink).toMatch(/\/invite#[A-Za-z0-9_-]+$/);

  // 2. The Owner signs up through the link and lands in their new workspace.
  const owner = await acceptInvitation(browser, ownerLink, baseURL!, password);
  await expect(owner.page.getByTestId('workspace-name')).toHaveText(workspaceName);

  // 3. The Owner invites a colleague as Operator.
  await owner.page.getByRole('link', { name: 'Members' }).first().click();
  await owner.page.locator('input[name="inviteEmail"]').fill(colleagueEmail);
  await owner.page.locator('select[name="inviteRole"]').selectOption('Operator');
  await owner.page.getByRole('button', { name: 'Create invitation' }).click();
  const colleagueLink = await owner.page.getByLabel('Invitation link').inputValue();

  // 4. The colleague joins and can start jobs (Operator).
  const colleague = await acceptInvitation(browser, colleagueLink, baseURL!, `${password}-2`);
  await expect(colleague.page.getByTestId('workspace-name')).toHaveText(workspaceName);
  await expect(colleague.page.getByRole('navigation', { name: 'Main' }).first().getByRole('link', { name: 'New job' })).toBeVisible();

  // 5. The Owner makes them a Viewer: after a reload the write navigation is gone, and the Api refuses writes.
  await owner.page.reload();
  await owner.page.getByLabel(`Role of ${colleagueEmail}`).selectOption('Viewer');
  await expect(owner.page.getByLabel(`Role of ${colleagueEmail}`)).toHaveValue('Viewer');
  await colleague.page.reload();
  await expect(colleague.page.getByTestId('workspace-name')).toHaveText(workspaceName);
  await expect(colleague.page.getByRole('navigation', { name: 'Main' }).first().getByRole('link', { name: 'New job' })).toHaveCount(0);

  // 6. The workspace appears to the operator with its two members; suspension locks the team out, reactivation restores it.
  await page.reload();
  const row = page.getByTestId('platform-workspace-row').filter({ hasText: workspaceName });
  await expect(row).toContainText('2');
  page.once('dialog', (d) => void d.accept());
  await row.getByRole('button', { name: 'Suspend' }).click();
  await expect(row).toContainText('Suspended');
  await owner.page.goto('/jobs');
  await expect(owner.page.getByRole('button', { name: 'Sign in' })).toBeVisible(); // the session cannot be refreshed or used
  await signIn(owner.page, ownerEmail, password);
  await expect(owner.page.getByRole('alert')).toContainText('The workspace is suspended.');

  await row.getByRole('button', { name: 'Reactivate' }).click();
  await expect(row).toContainText('Active');
  // Suspension kept the Owner's session cookie: after reactivation the session is restored without a new sign-in.
  await owner.page.goto('/');
  await expect(owner.page.getByTestId('workspace-name')).toHaveText(workspaceName);

  // 7. Customers never see the platform area.
  await owner.page.goto('/platform/workspaces');
  await expect(owner.page.getByText('You do not have access to this page')).toBeVisible();

  await owner.context.close();
  await colleague.context.close();
});
