import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { accountsQuery } from '../../api/accounts';
import type { AccountBody } from '../../api/common';
import { useDailyWarning, useSetDailyWarning } from '../../api/daily-warning';
import { useRunApproval, useSetRunApproval } from '../../api/run-approval';
import { formatDateTime, formatTime, formatUsd } from '../../lib/format';
import { useIsPhone } from '../../shell/useIsPhone';
import {
  Badge,
  Banner,
  Button,
  CapMeter,
  Cell,
  cssVars,
  EmptyState,
  ErrorState,
  Field,
  HeadCell,
  Icon,
  Input,
  KeyValue,
  Panel,
  StatusPill,
  Switch,
  Table,
  TableHead,
  TableRow,
} from '../../ui';
import { isForbidden, useOwnerGate } from '../admin/gate';
import { OwnerOnly } from '../admin/OwnerOnly';
import { Later, lastSeen, NotRecorded, PanelSkeleton, Planned, ScrollBox } from '../admin/parts';
import {
  capsRows,
  credentialView,
  firstSelection,
  forecastView,
  keyViews,
  presenceText,
  repositoriesText,
  runsText,
  windowMeter,
} from './model';
import './accounts.css';

const COLS = '96px 72px minmax(96px,1fr) minmax(144px,1.1fr) minmax(144px,1.1fr) 136px 156px 52px';

/** Who may call the server, what they may run and spend, and whose model credential pays. Read-only: the profile is the place to change any of it today. */
export function Accounts() {
  const gate = useOwnerGate();
  const accounts = useQuery({ ...accountsQuery(), enabled: gate.allowed });
  const [picked, setPicked] = useState<string>();
  const phone = useIsPhone();
  const data = accounts.data;

  if (gate.denied || isForbidden(accounts.error))
    return <OwnerOnly title="Accounts and keys" route="GET /v1/accounts" account={gate.name} />;

  const list = data?.accounts ?? [];
  const selected =
    list.find((a) => a.name === (picked ?? firstSelection(list))) ?? list[0] ?? undefined;
  return (
    <div className="screen">
      <div className="screen__head">
        <div>
          <h1 className="screen__title">Accounts and keys</h1>
          <p className="screen__sub">
            Who may call the server, what they may run, what they may spend, and whose model
            credential pays. Read-only: edits are made in the profile today.
          </p>
        </div>
        <div className="screen__actions">
          <Planned>
            <Button label="Add account" icon="plus" disabled />
          </Planned>
        </div>
      </div>

      {accounts.isError ? (
        <ErrorState
          title="Cannot read the accounts"
          call="GET /v1/accounts"
          detail={accounts.error.message}
          age={data ? lastSeen(accounts.dataUpdatedAt) : undefined}
          onRetry={() => void accounts.refetch()}
        />
      ) : null}

      {!data && !accounts.isError ? (
        <PanelSkeleton title="Accounts" lines={[30, 100, 98, 96]} />
      ) : null}

      {data ? (
        <>
          <Panel title="Accounts" count={list.length} flush>
            {phone ? (
              <ul className="list-reset">
                {list.map((account) => (
                  <AccountCard
                    key={account.name}
                    account={account}
                    selected={account.name === selected?.name}
                    onPick={() => setPicked(account.name)}
                  />
                ))}
              </ul>
            ) : (
              <ScrollBox label="Accounts">
                <Table cols={COLS} label="Accounts">
                  <TableHead>
                    <HeadCell>Account</HeadCell>
                    <HeadCell>Role</HeadCell>
                    <HeadCell>Repositories</HeadCell>
                    <HeadCell>Spend today of cap</HeadCell>
                    <HeadCell>Spend this month</HeadCell>
                    <HeadCell>Runs</HeadCell>
                    <HeadCell>Model credential</HeadCell>
                    <HeadCell>Edit</HeadCell>
                  </TableHead>
                  {list.map((account) => (
                    <AccountRow
                      key={account.name}
                      account={account}
                      selected={account.name === selected?.name}
                      onPick={() => setPicked(account.name)}
                    />
                  ))}
                </Table>
              </ScrollBox>
            )}
            {list.length <= 1 ? (
              <EmptyState
                icon="key"
                title="Only the owner exists"
                text={
                  <>
                    Declare callers in <span className="mono">http.accounts</span>, then make a key
                    with <span className="mono">chargehand accounts add &lt;account&gt;</span>. A
                    key is shown once and never again.
                  </>
                }
              />
            ) : null}
          </Panel>

          {selected ? (
            <div className="g2e">
              <div className="stack">
                <KeysPanel account={selected} />
              </div>
              <div className="stack">
                <ForecastPanel account={selected} />
                <CredentialPanel account={selected} />
                <LimitsPanel account={selected} />
              </div>
            </div>
          ) : null}
        </>
      ) : null}

      {data ? <NotRecorded lines={data.not_recorded} /> : null}
    </div>
  );
}

const roleBadge = (account: AccountBody) => (
  <Badge
    size="s"
    tone={account.owner ? 'accent' : undefined}
    label={account.owner ? 'owner' : 'account'}
  />
);

function SpendMeters({ account, window }: { account: AccountBody; window: 'day' | 'month' }) {
  const meter = windowMeter(account, window);
  return (
    <CapMeter
      label={`${account.name}, spend ${window === 'day' ? 'today' : 'this month'}`}
      head={meter.head}
      value={meter.value}
      cap={meter.cap}
      text={meter.text}
      level={meter.level}
    />
  );
}

function CredentialPill({ account }: { account: AccountBody }) {
  const view = credentialView(account);
  return <StatusPill status={view.status} label={view.label} bare />;
}

function AccountRow({
  account,
  selected,
  onPick,
}: {
  account: AccountBody;
  selected: boolean;
  onPick: () => void;
}) {
  const repos = repositoriesText(account);
  return (
    <TableRow selected={selected} onClick={onPick} className="account-row">
      <Cell strong>
        <button type="button" className="link link--plain" aria-pressed={selected} onClick={onPick}>
          {account.name}
        </button>
      </Cell>
      <Cell>{roleBadge(account)}</Cell>
      <Cell className="dim account-repos" title={repos.title}>
        {repos.text}
      </Cell>
      <Cell>
        <SpendMeters account={account} window="day" />
      </Cell>
      <Cell>
        <SpendMeters account={account} window="month" />
      </Cell>
      <Cell className="dim">{runsText(account)}</Cell>
      <Cell className="wrap">
        <CredentialPill account={account} />
      </Cell>
      <Cell>
        <Later />
      </Cell>
    </TableRow>
  );
}

function AccountCard({
  account,
  selected,
  onPick,
}: {
  account: AccountBody;
  selected: boolean;
  onPick: () => void;
}) {
  const repos = repositoriesText(account);
  return (
    // biome-ignore lint/a11y/useKeyWithClickEvents: the name inside is the button that takes the keyboard; the row is the mouse's larger target
    <li
      className="li li--click account-card"
      data-selected={selected ? 'true' : undefined}
      style={cssVars({ '--cols': 'minmax(0,1fr)' })}
      onClick={onPick}
    >
      <div className="row row--wrap">
        <button
          type="button"
          className="link link--plain b"
          aria-pressed={selected}
          onClick={onPick}
        >
          {account.name}
        </button>
        {roleBadge(account)}
        <span className="spacer" />
        <CredentialPill account={account} />
      </div>
      <SpendMeters account={account} window="day" />
      <div className="li__meta wrap">
        {runsText(account)} · {repos.text}
      </div>
    </li>
  );
}

function KeysPanel({ account }: { account: AccountBody }) {
  const keys = keyViews(account.keys);
  return (
    <Panel
      title={`Keys of ${account.name}`}
      count={keys.length}
      flush
      aside={
        <Planned>
          <Button label="New key" size="sm" icon="plus" disabled />
        </Planned>
      }
    >
      {keys.length === 0 ? (
        <EmptyState
          icon="key"
          title="No stored key"
          text={
            account.owner
              ? 'The owner’s key is the profile’s api_key_secret. It is not a stored key, so it is not listed.'
              : 'This account has no key: nobody can call the server as it. Make one with chargehand accounts add.'
          }
        />
      ) : (
        <ul className="list-reset">
          {keys.map((key) => (
            <li
              key={key.id}
              className="li"
              style={cssVars({ '--cols': '16px minmax(0,1fr) auto' })}
            >
              <Icon name="key" />
              <div className="li__main">
                <div className="b">{key.label}</div>
                <div className="li__meta mono" title={key.id}>
                  {key.id}
                </div>
              </div>
              <div className="account-key-when">
                {key.revoked ? (
                  <span title={formatDateTime(key.revoked)}>
                    <StatusPill
                      status="cancelled"
                      bare
                      label={`Revoked ${formatTime(key.revoked)}`}
                    />
                  </span>
                ) : (
                  <span className="dim small" title={formatDateTime(key.created)}>
                    created {formatTime(key.created)}
                  </span>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      <p className="pad dim small sep-top">
        A key is shown once when it is made. This page never shows a key’s value, salt or hash; a
        key’s last use is not recorded. Revoke one with{' '}
        <span className="mono">chargehand accounts revoke</span>.
      </p>
    </Panel>
  );
}

function CredentialPanel({ account }: { account: AccountBody }) {
  const view = credentialView(account);
  return (
    <Panel title="Model credential">
      <div className="stack stack--tight">
        <KeyValue
          stacked
          items={[
            ['Source', <StatusPill key="s" status={view.status} label={view.label} bare />],
            ['Secret item', presenceText(view)],
            ['What it means', view.note],
            [
              'allow_server_credential',
              view.allowServer == null ? (
                <span key="allow" className="dim">
                  {account.owner
                    ? 'not an owner setting'
                    : 'not reported for an account with its own key'}
                </span>
              ) : (
                <span key="allow" className="row">
                  <Switch
                    aria-label="allow_server_credential"
                    checked={view.allowServer}
                    disabled
                    readOnly
                  />
                  <Later />
                </span>
              ),
            ],
          ]}
        />
        <p className="dim small">
          An account with no model block and allow_server_credential false is refused at admission
          with 403 model_credential_missing. Presence only: a credential’s value is never shown.
        </p>
      </div>
    </Panel>
  );
}

function LimitsPanel({ account }: { account: AccountBody }) {
  return (
    <Panel title={`Limits of ${account.name}`}>
      <div className="stack stack--tight">
        <KeyValue stacked items={capsRows(account)} />
        <DailyWarningField />
        <RunApprovalField />
      </div>
    </Panel>
  );
}

/** The server-wide soft daily dollar limit (ADR 0091): it warns and never refuses a run. Empty switches the warning off. */
function DailyWarningField() {
  const warning = useDailyWarning();
  const save = useSetDailyWarning();
  const [text, setText] = useState<string>();
  const data = warning.data;
  if (!data) return null;
  const shown = text ?? (data.limit_usd == null ? '' : String(data.limit_usd));
  const parsed = shown.trim() === '' ? null : Number(shown);
  const invalid = parsed !== null && !(Number.isFinite(parsed) && parsed > 0);
  const unchanged = !invalid && parsed === data.limit_usd;
  return (
    <form
      className="stack stack--tight"
      onSubmit={(e) => {
        e.preventDefault();
        if (invalid) return;
        save.mutate(parsed, { onSuccess: () => setText(undefined) });
      }}
    >
      <Field
        label="Daily spend warning, all accounts (USD per UTC day)"
        hint={`Today ${formatUsd(data.spent_usd)}${data.limit_usd == null ? ', warning off' : ` of ${formatUsd(data.limit_usd)}`}. Only warns; runs are never refused. Empty switches it off; the default is ${formatUsd(data.default_usd)}.`}
        error={
          invalid
            ? 'Enter a number above 0, or leave it empty to switch the warning off.'
            : save.error?.message
        }
      >
        {(control) => (
          <Input
            {...control}
            type="number"
            inputMode="decimal"
            min="0"
            step="any"
            value={shown}
            onChange={(e) => setText(e.target.value)}
          />
        )}
      </Field>
      <div>
        <Button
          type="submit"
          label="Save warning limit"
          size="sm"
          loading={save.isPending}
          disabled={invalid || unchanged}
        />
      </div>
    </form>
  );
}

/** The server-wide per-run approval threshold (ADR 0092): a run that may spend more waits for the owner in the inbox. Empty switches it off. */
function RunApprovalField() {
  const approval = useRunApproval();
  const save = useSetRunApproval();
  const [text, setText] = useState<string>();
  const data = approval.data;
  if (!data) return null;
  const shown = text ?? (data.limit_usd == null ? '' : String(data.limit_usd));
  const parsed = shown.trim() === '' ? null : Number(shown);
  const invalid = parsed !== null && !(Number.isFinite(parsed) && parsed > 0);
  const unchanged = !invalid && parsed === data.limit_usd;
  return (
    <form
      className="stack stack--tight"
      onSubmit={(e) => {
        e.preventDefault();
        if (invalid) return;
        save.mutate(parsed, { onSuccess: () => setText(undefined) });
      }}
    >
      <Field
        label="Approval threshold per run (USD)"
        hint={`${data.limit_usd == null ? 'No run is asked about' : `A run that may spend more than ${formatUsd(data.limit_usd)} waits for you in the inbox before it starts`}. Priced runs only: a subscription run never asks. Empty switches it off; the default is ${formatUsd(data.default_usd)}.${data.pending > 0 ? ` ${data.pending} waiting now.` : ''}`}
        error={
          invalid
            ? 'Enter a number above 0, or leave it empty to ask about no run.'
            : save.error?.message
        }
      >
        {(control) => (
          <Input
            {...control}
            type="number"
            inputMode="decimal"
            min="0"
            step="any"
            value={shown}
            onChange={(e) => setText(e.target.value)}
          />
        )}
      </Field>
      <div>
        <Button
          type="submit"
          label="Save approval threshold"
          size="sm"
          loading={save.isPending}
          disabled={invalid || unchanged}
        />
      </div>
    </form>
  );
}

/** The month's spend at the trailing 7-day rate against max_usd_per_month (CHARGEHAND-216, ADR 0076). */
function ForecastPanel({ account }: { account: AccountBody }) {
  const view = forecastView(account);
  if (!view) return null;
  return (
    <Panel title="Month forecast" aside={<span className="dim">at the last 7 days’ rate</span>}>
      <div className="stack stack--tight">
        <CapMeter
          label={`${account.name}, spend projected by the end of the month`}
          head="Projected by the end of the month"
          value={view.projected}
          cap={view.cap}
          text={view.text}
          level={view.over ? 'danger' : undefined}
        />
        <p className="dim small">{view.rate}</p>
        {view.over && view.reached ? (
          <Banner tone="danger" icon="alert-triangle" title="The forecast passes the monthly cap">
            At this rate the cap is reached {formatDateTime(view.reached)}; admission refuses the
            account’s runs from then until the month ends. A person’s own run is never held by the
            forecast.
          </Banner>
        ) : view.cap == null ? (
          <p className="dim small">No max_usd_per_month: there is no cap to pass.</p>
        ) : null}
        {view.held ? (
          <Banner tone="warn" icon="pause" title="Automated starts are held">
            {view.automation}
          </Banner>
        ) : view.automation ? (
          <p className="dim small">{view.automation}</p>
        ) : null}
        <p className="dim small">
          Priced usage only: a run on a subscription adds nothing to the forecast.
        </p>
      </div>
    </Panel>
  );
}
