import { QueryClient } from '@tanstack/react-query';
import {
  createRootRoute,
  createRoute,
  createRouter,
  type RouterHistory,
} from '@tanstack/react-router';
import { ApiError, Unauthorized } from './api/client';
import { Accounts } from './screens/accounts/Accounts';
import { Intake } from './screens/intake/Intake';
import { NeedsYou } from './screens/needs-you/NeedsYou';
import { Overview } from './screens/overview/Overview';
import { Placeholder } from './screens/Placeholder';
import { Repositories } from './screens/repositories/Repositories';
import { RunDetail } from './screens/run/RunDetail';
import { Runs } from './screens/runs/Runs';
import { Start } from './screens/start/Start';
import { System } from './screens/system/System';
import { Root } from './shell/Root';
import { SCREENS, type ScreenPath } from './shell/screens';

const placeholder = (to: ScreenPath) => () => {
  const screen = SCREENS.find((s) => s.to === to);
  return <Placeholder title={screen?.label ?? to} blurb={screen?.blurb ?? ''} />;
};

const rootRoute = createRootRoute({
  component: Root,
  notFoundComponent: () => <Placeholder title="Not found" blurb="There is no such screen." />,
});

function child<const TPath extends string>(path: TPath, component: () => React.JSX.Element) {
  return createRoute({ getParentRoute: () => rootRoute, path, component });
}

// A screen lives in src/screens/<name>/. To build one of the placeholders, replace its line with the screen's component.
const routeTree = rootRoute.addChildren([
  child('/', Overview),
  child('runs', Runs),
  child('runs/$runId', RunDetail),
  child('needs-you', NeedsYou),
  child('new', Start),
  child('analytics', placeholder('/analytics')),
  child('learning', placeholder('/learning')),
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
