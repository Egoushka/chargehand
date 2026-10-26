import { expect, type Page, test } from '@playwright/test';
import { REPO } from '../src/testing/fixtures.ts';
import { HEAD } from '../src/testing/start.ts';
import { SIZES, settle, shot, sidewaysScroll, violations } from './looks.ts';
import { listenForViolations, mockApi, signIn, watch } from './mock.ts';

// Start a batch, at a desktop and a phone width, in the dark and the light theme: the form empty, with its gaps named, and filled;
// tasks, dependencies and the estimate; the dialogs and the JSON view; and the start itself. Each look passes axe with no serious or
// critical violation, and none may log an error, break the page's policy or call another origin.

const NEW_RUN = 'run-20261013-150000-abc123';

const open = async (page: Page) => {
  await signIn(page, '/console/new');
  await expect(page.getByRole('heading', { level: 1, name: 'Start a batch' })).toBeVisible();
  await expect(page.getByLabel('Repository')).toBeVisible();
};

/** The basics a start needs: the repository (which brings its commit), a label, the first goal and a dollar cap. */
const fillBasics = async (page: Page) => {
  await page.getByLabel('Repository').selectOption(REPO);
  await expect(page.getByLabel('Commit')).toHaveValue(HEAD);
  await page.getByLabel('Label').fill('Intake aliases and peak memory');
  await page.getByLabel('Goal').first().fill('Let intake watch a repository by an alias.');
  await page.getByLabel('Dollar cap').fill('12');
};

for (const [device, viewport] of Object.entries(SIZES)) {
  for (const scheme of ['dark', 'light'] as const) {
    test.describe(`${device}, ${scheme}`, () => {
      test.use({ viewport, colorScheme: scheme });

      test.beforeEach(async ({ page }) => {
        await listenForViolations(page);
      });

      test('an empty form is quiet, a start without the basics names each gap at its field, a filled one passes Check', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await open(page);

        // Nothing is red before a start is tried; the history behind the first task's budget is named.
        await expect(page.getByRole('alert')).toHaveCount(0);
        await expect(page.getByLabel('Task id')).toHaveValue('task-1');
        await expect(
          page.getByText('about p80 of 26 past M tasks in 2 repositories').first(),
        ).toBeVisible();
        await settle(page);
        expect(await page.evaluate(() => document.documentElement.dataset.theme)).toBe(scheme);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        if (device === 'phone') {
          // Check and Start sit in a bar above the tab bar, as 44 px targets.
          const heights = await page
            .locator('.start-bar .ch-btn')
            .evaluateAll((els) => els.map((el) => el.getBoundingClientRect().height));
          expect(heights).toHaveLength(2);
          expect(Math.min(...heights)).toBeGreaterThanOrEqual(44);
        }
        await shot(page, `start-empty-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await page.getByRole('button', { name: 'Start batch' }).click();
        const todo = page.getByRole('alert').filter({ hasText: /things to fix/ });
        await expect(todo).toContainText('Label');
        await expect(todo).toContainText('Repository');
        await expect(todo).toContainText('Caps');
        await expect(page.getByLabel('Label')).toBeFocused();
        await expect(
          page.getByText('The request needs text: a label that names the batch in lists.').first(),
        ).toBeVisible();
        await expect(
          page.getByText('Describe what the change should do.', { exact: true }),
        ).toBeVisible();
        await settle(page);
        await shot(page, `start-gaps-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await fillBasics(page);
        await page.getByRole('button', { name: 'Check' }).click();
        await expect(
          page.getByRole('status').filter({ hasText: 'No problems found' }),
        ).toBeVisible();
        await expect(page.getByRole('alert').filter({ hasText: /things to fix/ })).toHaveCount(0);
        await settle(page);
        await shot(page, `start-checked-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        expect(await seen.finish()).toEqual([]);
      });

      test('tasks come from pasted text, depend on each other with no loop offered, and show in the graph and the estimate', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await open(page);

        await page.getByRole('button', { name: 'Import tasks' }).click();
        const dialog = page.getByRole('dialog', { name: 'Import tasks' });
        await expect(dialog).toBeVisible();
        await dialog
          .getByLabel('Tasks')
          .fill('[S] Report peak memory per container\n- (L) Rework the intake watcher');
        await expect(dialog.getByText('2 tasks found.')).toBeVisible();
        await expect(dialog.getByText(/the server has no route that lists them/)).toBeVisible();
        await shot(page, `start-import-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        await dialog.getByRole('button', { name: 'Add 2 tasks' }).click();
        await expect(dialog).toBeHidden();
        await expect(page.getByLabel('Task id')).toHaveCount(3);

        await page
          .getByLabel('Add a dependency to report-peak-memory-per-container')
          .selectOption('task-1');
        await page
          .getByLabel('Add a dependency to rework-the-intake-watcher')
          .selectOption('report-peak-memory-per-container');
        // task-1 may not wait for what already waits for it, directly or down the chain.
        await expect(
          page
            .getByLabel('Add a dependency to task-1')
            .locator('option', { hasText: 'rework-the-intake-watcher (would make a cycle)' }),
        ).toBeDisabled();
        await expect(
          page.getByRole('group', {
            name: 'Dependency graph: 3 tasks, 2 dependencies, 3 steps',
          }),
        ).toBeVisible();
        await expect(page.getByText('3 tasks in a row')).toBeVisible();
        // The L history is too thin anywhere, so its budget is the flat one, and the page says so.
        await expect(
          page.getByText(/the flat L budget; only 6 past L tasks/).first(),
        ).toBeVisible();
        await settle(page);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `start-tasks-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        await page.getByRole('button', { name: 'Remove report-peak-memory-per-container' }).click();
        await expect(page.getByLabel('Task id')).toHaveCount(2);
        await expect(
          page.getByRole('group', { name: 'Dependency graph: 2 tasks, 0 dependencies, 1 step' }),
        ).toBeVisible();
        expect(await seen.finish()).toEqual([]);
      });

      test('the JSON view is the form’s twin, and the templates dialog keeps a form in this browser', async ({
        page,
      }) => {
        await mockApi(page);
        const seen = watch(page);
        await open(page);
        await fillBasics(page);

        await page.getByRole('button', { name: 'JSON' }).click();
        const json = page.getByLabel('Request JSON');
        await expect(json).toHaveValue(/"contract_version": "request\/v1"/);
        await expect(json).toHaveValue(/"max_usd_total": 12/);
        await expect(
          page.getByRole('heading', { name: 'The same request from the shell' }),
        ).toBeVisible();
        await settle(page);
        expect(await sidewaysScroll(page)).toBeLessThanOrEqual(0);
        await shot(page, `start-json-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);

        // JSON that parses fills the form; JSON that does not is named and keeps the last good form.
        const request = JSON.parse(await json.inputValue());
        request.text = 'Edited in JSON';
        request.driven.tasks[0].goal = 'A goal from the JSON view.';
        await json.fill(JSON.stringify(request));
        await json.fill(`${JSON.stringify(request)} oops`);
        await expect(
          page.getByRole('alert').filter({ hasText: /last JSON that parsed/ }),
        ).toBeVisible();
        await shot(page, `start-json-broken-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        await json.fill(JSON.stringify(request));
        await page.getByRole('button', { name: 'Form' }).click();
        await expect(page.getByLabel('Label')).toHaveValue('Edited in JSON');
        await expect(page.getByLabel('Goal').first()).toHaveValue('A goal from the JSON view.');

        await page.getByRole('button', { name: 'Templates' }).click();
        const templates = page.getByRole('dialog', { name: 'Templates' });
        await expect(templates.getByText('No templates yet')).toBeVisible();
        await templates.getByLabel('Save the form as a template').fill('Nightly backlog');
        await templates.getByRole('button', { name: 'Save template' }).click();
        await expect(
          templates.getByRole('list', { name: 'Saved templates' }).getByText('Nightly backlog'),
        ).toBeVisible();
        await shot(page, `start-templates-${device}-${scheme}`);
        expect(await violations(page)).toEqual([]);
        const saved = await page.evaluate(() =>
          JSON.parse(localStorage.getItem('chargehand.console.start-templates') ?? '[]'),
        );
        expect(saved).toHaveLength(1);
        // A template is not a secret and carries no commit: a commit goes stale.
        expect(saved[0].request.context.repository).toEqual({ path: REPO });
        await page.keyboard.press('Escape');
        await expect(templates).toBeHidden();
        expect(await seen.finish()).toEqual([]);
      });

      test('Start batch sends the request with Prefer wait=0 and opens the new run', async ({
        page,
      }) => {
        const mock = await mockApi(page);
        const seen = watch(page);
        await open(page);
        await fillBasics(page);
        await page.getByRole('button', { name: 'Plan first' }).click();
        await page.getByLabel('max_parallel').fill('3');
        await page.getByRole('button', { name: 'Start batch' }).click();

        await expect(page).toHaveURL(new RegExp(`/console/runs/${NEW_RUN}$`));
        // The editor also POSTs /v1/forecast as the form fills (#583); that call may land first.
        const post = mock.calls.find((c) => c.method === 'POST' && c.path === '/v1/runs');
        expect(post?.path).toBe('/v1/runs');
        expect(post?.headers.prefer).toBe('wait=0');
        expect(post?.body).toEqual({
          contract_version: 'request/v1',
          text: 'Intake aliases and peak memory',
          context: {
            interactive: false,
            preset: 'driven',
            repository: { path: REPO, commit: HEAD },
          },
          driven: {
            plan: true,
            max_parallel: 3,
            max_usd_total: 12,
            draft_pr: true,
            tasks: [
              { id: 'task-1', goal: 'Let intake watch a repository by an alias.', size: 'M' },
            ],
          },
        });
        expect(mock.calls.filter((c) => c.method === 'POST' && c.path === '/v1/runs')).toHaveLength(
          1,
        );
        expect(await seen.finish()).toEqual([]);
      });
    });
  }
}

test.describe('desktop only', () => {
  test.use({ viewport: SIZES.desktop });

  test.beforeEach(async ({ page }) => {
    await listenForViolations(page);
  });

  test('a server that refuses the start is quoted with its status and code, and the form stays', async ({
    page,
  }) => {
    await mockApi(page, {
      replies: {
        'POST /v1/runs': {
          status: 403,
          body: {
            errors: [`account ci-bot may not use the repository ${REPO}`],
            error: { code: 'repository_not_allowed' },
          },
        },
      },
    });
    const seen = watch(page);
    await open(page);
    await fillBasics(page);
    await page.getByRole('button', { name: 'Start batch' }).click();

    const refusal = page.getByRole('alert').filter({ hasText: 'The server refused the request' });
    await expect(refusal).toContainText('403 repository_not_allowed');
    await expect(refusal).toContainText('may not use the repository');
    await expect(page).toHaveURL(/\/console\/new$/);
    await expect(page.getByLabel('Label')).toHaveValue('Intake aliases and peak memory');
    await settle(page);
    await shot(page, 'start-refused');
    expect(await violations(page)).toEqual([]);
    // The browser logs the 403 itself; that is the one console line this test expects.
    expect((await seen.finish()).filter((p) => !p.includes('403'))).toEqual([]);
  });

  test('without the list of checkouts the repository is a typed path, and the page says which call failed', async ({
    page,
  }) => {
    await mockApi(page, {
      replies: {
        '/v1/repositories': { status: 503, body: { errors: ['timed out after 10 s'] } },
      },
    });
    await signIn(page, '/console/new');
    const box = page.getByRole('alert').filter({ hasText: 'Cannot list the repositories' });
    await expect(box).toContainText('GET /v1/repositories');
    await expect(box).toContainText('timed out after 10 s');
    await expect(box.getByRole('button', { name: 'Retry' })).toBeVisible();
    await page.getByLabel('Repository').fill(REPO);
    await settle(page);
    await shot(page, 'start-repositories-error');
    expect(await violations(page)).toEqual([]);
  });

  test('an account sees its own limits and no call reads the profile it may not read', async ({
    page,
  }) => {
    const mock = await mockApi(page, { account: true });
    const seen = watch(page);
    await open(page);
    await expect(page.getByRole('heading', { name: 'Your limits' })).toBeVisible();
    await expect(page.getByText('ci-bot', { exact: true }).last()).toBeVisible();
    await expect(page.getByText(/left of \$10\.00/)).toBeVisible();
    await settle(page);
    expect(mock.calls.filter((c) => c.path === '/v1/system')).toEqual([]);
    await shot(page, 'start-account');
    expect(await violations(page)).toEqual([]);
    expect(await seen.finish()).toEqual([]);
  });
});
