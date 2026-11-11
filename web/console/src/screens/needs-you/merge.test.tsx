import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { decisionInbox } from '../../testing/fixtures';
import { renderConsole } from '../../testing/render';
import { serveApi } from '../../testing/server';

const MERGE = '/v1/pulls/example/payments-api/412/merge';
const PR_ID = 'pull_request:https://github.com/example/payments-api/pull/412';

/** The decision inbox with the server's `merge` option on the pull request, as it offers one for a pull request waiting only on policy. */
function offered(withMerge = true) {
  const body = decisionInbox();
  const item = body.items.find((i) => i.id === PR_ID);
  if (withMerge && item?.options) {
    item.options.splice(1, 0, {
      id: 'merge',
      label: 'Merge pull request',
      effect: 'Squash-merges it once the required checks have passed.',
      call: { method: 'POST', path: MERGE },
    });
  }
  return body;
}

const card = async () => within(await screen.findByRole('article', { name: /#412/ }));
const asked = (calls: { path: string; method: string }[]) =>
  calls.filter((c) => c.path === MERGE && c.method === 'POST');
const confirm = async () =>
  fireEvent.click(
    within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Merge PR 412' }),
  );

describe('Merge on the pull request card', () => {
  it('asks before it merges, then merges once', async () => {
    const { calls } = serveApi({
      inbox: offered(),
      replies: { [MERGE]: [202, { action: 'merge', changed: true }] },
    });
    renderConsole('/console/needs-you');
    fireEvent.click((await card()).getByRole('button', { name: 'Merge pull request' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(asked(calls)).toHaveLength(0);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep it open' }));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).toBeNull());
    expect(asked(calls)).toHaveLength(0);
    fireEvent.click((await card()).getByRole('button', { name: 'Merge pull request' }));
    await confirm();
    expect(await (await card()).findByText('Merged.')).toBeTruthy();
    expect(asked(calls)).toHaveLength(1);
  });

  it("shows the server's reason on the card when it refuses", async () => {
    serveApi({
      inbox: offered(),
      replies: { [MERGE]: [409, { errors: ['required checks have not passed: build'] }] },
    });
    renderConsole('/console/needs-you');
    fireEvent.click((await card()).getByRole('button', { name: 'Merge pull request' }));
    await confirm();
    expect(await (await card()).findByText(/required checks have not passed: build/)).toBeTruthy();
  });

  it('is not offered when the server offered no merge option', async () => {
    serveApi({ inbox: offered(false) });
    renderConsole('/console/needs-you');
    const pr = await card();
    await pr.findByRole('link', { name: 'Open pull request' });
    expect(pr.queryByRole('button', { name: 'Merge pull request' })).toBeNull();
  });
});
