import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryHistory, RouterProvider } from '@tanstack/react-router';
import { render } from '@testing-library/react';
import { setKey } from '../api/key';
import { createAppRouter } from '../router';
import { LOGIN } from './fixtures';

/** Opens the console at a path with the router, a fresh cache that does not retry, and (by default) a signed-in key. */
export function renderConsole(path: string, options: { key?: string | null } = {}) {
  if (options.key !== null) setKey(options.key ?? LOGIN);
  const router = createAppRouter({
    basepath: '/console',
    history: createMemoryHistory({ initialEntries: [path] }),
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return {
    client,
    router,
    ...render(
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>,
    ),
  };
}
