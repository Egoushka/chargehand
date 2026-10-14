import { QueryClient } from '@tanstack/react-query';
import {
  createRootRoute,
  createRoute,
  createRouter,
  lazyRouteComponent,
  type RouterHistory,
} from '@tanstack/react-router';
import { ApiError, Unauthorized } from './api/client';
import { Accounts } from './screens/accounts/Accounts';
import { Analytics } from './screens/analytics/Analytics';
import { isTab, type TabId } from './screens/batch/tabs';
import { Intake } from './screens/intake/Intake';
import { Learning } from './screens/learning/Learning';
import { NeedsYou } from './screens/needs-you/NeedsYou';
import { Overview } from './screens/overview/Overview';
import { Placeholder } from './screens/Placeholder';
import { Repositories } from './screens/repositories/Repositories';
import { Runs } from './screens/runs/Runs';
import { Start } from './screens/start/Start';
import { System } from './screens/system/System';
import { Root } from './shell/Root';

const rootRoute = createRootRoute({
  component: Root,
  notFoundComponent: () => <Placeholder title="Not found" blurb="There is no such screen." />,
});

function child<const TPath extends string>(path: TPath, component: () => React.JSX.Element) {
  return createRoute({ getParentRoute: () => rootRoute, path, component });
}

// A batch, a task and a research result are the heavy screens (a graph, a diff with syntax colour): their code loads when one is opened.
// `tab` is the batch's section and `task` the task whose drawer is open; both are in the address, so a link opens the same view.
const runRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'runs/$runId',
  validateSearch: (search: Record<string, unknown>): { tab?: TabId; task?: string } => ({
    tab: isTab(search.tab) ? search.tab : undefined,
    task: typeof search.task === 'string' && search.task !== '' ? search.task : undefined,
  }),
  component: lazyRouteComponent(() => import('./screens/run/RunPage'), 'RunPage'),
});

const taskRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: 'runs/$runId/tasks/$taskId',
  component: lazyRouteComponent(() => import('./screens/run/TaskPage'), 'TaskPage'),
});

// A screen lives in src/screens/<name>/; its route is one line here.
const routeTree = rootRoute.addChildren([
  child('/', Overview),
  child('runs', Runs),
  runRoute,
  taskRoute,
  child('needs-you', NeedsYou),
  child('new', Start),
  child('analytics', Analytics),
  child('learning', Learning),
  child('intake', Intake),
  child('repositories', Repositories),
  child('accounts', Accounts),
  child('system', System),
]);

/** The server serves the console at the base path Vite was built with (/console/). */
const basepath = import.meta.env.BASE_URL.replace(/\/$/, '');

export function createAppRouter(options: { history?: RouterHistory; basepath?: string } = {}) {
  return createRouter({
    routeTree,
    basepath: options.basepath ?? basepath,
    history: options.history,
  });
}

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // A refused key or a client error will not get better by asking again.
        retry: (failures, error) =>
          !(error instanceof Unauthorized) &&
          !(error instanceof ApiError && error.status < 500) &&
          failures < 2,
      },
    },
  });
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
