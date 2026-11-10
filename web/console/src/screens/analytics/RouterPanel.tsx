import { useQuery } from '@tanstack/react-query';
import { type RouterPool, routerQuery } from '../../api/router';
import { TightEmpty } from '../../charts/TightEmpty';
import { DASH, formatDateTime, formatUsd } from '../../lib/format';
import {
  Badge,
  Cell,
  ErrorState,
  HeadCell,
  Panel,
  Skeleton,
  Table,
  TableHead,
  TableRow,
  TableScroll,
} from '../../ui';

const POOL_COLS = 'minmax(0,1.4fr) 5rem 6rem 6rem 6rem 6rem minmax(0,1.2fr)';
const ROUTE_COLS = 'minmax(0,1fr) minmax(0,1fr) minmax(0,3fr)';

function Status({ pool }: { pool: RouterPool }) {
  if (!pool.has_room) return <Badge tone="danger" label={pool.why ?? 'no room'} />;
  if (pool.warning) return <Badge tone="warn" label={pool.warning} />;
  return <span>room</span>;
}

function PoolLine({ pool }: { pool: RouterPool }) {
  return (
    <TableRow>
      <Cell strong title={pool.models.join(', ')}>
        {pool.name}
      </Cell>
      <Cell mono>{pool.kind}</Cell>
      <Cell num>{formatUsd(pool.remaining_usd)}</Cell>
      <Cell num>{formatUsd(pool.room_usd)}</Cell>
      <Cell num>{formatUsd(pool.spent_today_usd)}</Cell>
      <Cell num>{formatUsd(pool.spent_period_usd)}</Cell>
      <Cell>
        <Status pool={pool} />
      </Cell>
    </TableRow>
  );
}

/** The budget router's pools in the order it drains them, and the tier each route resolves to. Reads `GET /v1/router` (owner only; off without a pools block). */
export function RouterPanel() {
  const router = useQuery(routerQuery());
  const title = 'Budget router';
  if (router.isPending)
    return (
      <Panel title={title} busy>
        <Skeleton width="100%" height={96} />
      </Panel>
    );
  if (router.isError)
    return (
      <Panel title={title}>
        <ErrorState
          title="Cannot read the budget router"
          call="GET /v1/router"
          detail={router.error.message}
          onRetry={() => void router.refetch()}
        />
      </Panel>
    );
  const body = router.data;
  if (!body.enabled)
    return (
      <Panel title={title}>
        <TightEmpty
          icon="info"
          title="Router off"
          text="The profile has no pools block, so every call runs on the model it names."
        />
      </Panel>
    );
  const refills = body.pools
    .filter((p) => p.refill)
    .map((p) => `${p.name} ${formatDateTime(p.refill)}`);
  return (
    <Panel title={title} aside={`pools in drain order · ${body.time_zone ?? 'UTC'} days`}>
      <div className="stack">
        <TableScroll label="Budget router pools">
          <Table cols={POOL_COLS} label="Budget router pools">
            <TableHead>
              <HeadCell>Pool</HeadCell>
              <HeadCell>Kind</HeadCell>
              <HeadCell num>Balance</HeadCell>
              <HeadCell num>Room today</HeadCell>
              <HeadCell num>Today</HeadCell>
              <HeadCell num>Period</HeadCell>
              <HeadCell>Status</HeadCell>
            </TableHead>
            {body.pools.map((pool) => (
              <PoolLine key={pool.name} pool={pool} />
            ))}
          </Table>
        </TableScroll>
        <TableScroll label="Budget router routes">
          <Table cols={ROUTE_COLS} label="Budget router routes">
            <TableHead>
              <HeadCell>Route</HeadCell>
              <HeadCell>Tier</HeadCell>
              <HeadCell>Models</HeadCell>
            </TableHead>
            {body.routes.map((route) => (
              <TableRow key={route.key}>
                <Cell mono strong>
                  {route.key}
                </Cell>
                <Cell>{route.tier}</Cell>
                <Cell mono>{route.models.join(', ') || DASH}</Cell>
              </TableRow>
            ))}
          </Table>
        </TableScroll>
        <div className="dim small">
          GET /v1/router · balance and room are dollars at API price; a limit only warns, a
          provider's refusal takes a pool out
          {refills.length > 0 ? ` · refills: ${refills.join(', ')}` : ''}
        </div>
      </div>
    </Panel>
  );
}
