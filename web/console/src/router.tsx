import { QueryClient } from '@tanstack/react-query';
import {
  createRootRoute,
  createRoute,
  createRouter,
  type RouterHistory,
} from '@tanstack/react-router';
import { ApiError, Unauthorized } from './api/client';
import { Placeholder } from './screens/Placeholder';
import { RunDetail } from './screens/RunDetail';
import { Runs } from './screens/Runs';
import { Root } from './shell/Root';
import { SCREENS } from './shell/screens';

const blurb = (to: (typeof SCREENS)[number]['to']) => {
  const screen = SCREENS.find((s) => s.to === to);
  return screen ? { title: screen.label, blurb: screen.blurb } : { title: to, blurb: '' };
};

const rootRoute = createRootRoute({
  component: Root,
  notFoundComponent: () => <Placeholder title="Not found" blurb="There is no such screen." />,
});

function child<const TPath extends string>(path: TPath, component: () => React.JSX.Element) {
  return createRoute({ getParentRoute: () => rootRoute, path, component });
}

const routeTree = rootRoute.addChildren([
  child('/', () => <Placeholder {...blurb('/')} />),
  child('runs', Runs),
  child('runs/$runId', RunDetail),
  child('needs-you', () => <Placeholder {...blurb('/needs-you')} />),
  child('new', () => <Placeholder {...blurb('/new')} />),
  child('analytics', () => <Placeholder {...blurb('/analytics')} />),
  child('learning', () => <Placeholder {...blurb('/learning')} />),
  child('intake', () => <Placeholder {...blurb('/intake')} />),
  child('repositories', () => <Placeholder {...blurb('/repositories')} />),
  child('accounts', () => <Placeholder {...blurb('/accounts')} />),
  child('system', () => <Placeholder {...blurb('/system')} />),
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
