import { fireEvent, render, screen, within } from '@testing-library/react';
import { Command } from 'cmdk';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { STATUS, type Status } from '../lib/status';
import {
  Badge,
  Banner,
  Button,
  buttonClass,
  CapMeter,
  Cell,
  Check,
  Chip,
  CommandItem,
  ConfirmDialog,
  Count,
  Dialog,
  Diff,
  DiffFile,
  DiffLine,
  EmptyState,
  ErrorState,
  Field,
  HeadCell,
  hiddenEdges,
  highlight,
  ICONS,
  Icon,
  IconButton,
  Input,
  KeyValue,
  Kpi,
  LogLine,
  LogStream,
  Menu,
  NavItem,
  Panel,
  revealSelected,
  Segmented,
  SegmentedMeter,
  SizeBadge,
  Skeleton,
  Sparkline,
  StatusPill,
  Switch,
  sparkPath,
  TabBar,
  TabBarItem,
  Table,
  TableHead,
  TableRow,
  TabPanel,
  Tabs,
  TaskNode,
  Toast,
  ToastHost,
  Tooltip,
  TooltipProvider,
} from '.';

const ALL_STATUSES = Object.keys(STATUS) as Status[];

describe('Button', () => {
  it('is a type=button button with its label, and the variant and size as classes', () => {
    render(<Button label="Start batch" variant="primary" size="lg" />);
    const button = screen.getByRole('button', { name: 'Start batch' });
    expect(button.getAttribute('type')).toBe('button');
    expect(button.className).toBe('ch-btn ch-btn--primary ch-btn--lg');
  });

  it('shows an icon before the label and a key hint after it', () => {
    render(<Button label="Open palette" icon="search" kbd="⌘K" />);
    const button = screen.getByRole('button', { name: /Open palette/ });
    expect(button.querySelector('.ch-i-search')?.getAttribute('aria-hidden')).toBe('true');
    expect(button.querySelector('kbd.ch-kbd')?.textContent).toBe('⌘K');
  });

  it('while loading keeps the label for assistive technology, swaps it for a spinner by class, and says it is busy', () => {
    render(<Button label="Validating" loading />);
    const button = screen.getByRole('button', { name: 'Validating' });
    expect(button.classList.contains('is-loading')).toBe(true);
    expect(button.getAttribute('aria-busy')).toBe('true');
  });

  it('runs onClick, and does nothing when disabled', () => {
    const onClick = vi.fn();
    const { rerender } = render(<Button label="Retry task" onClick={onClick} />);
    fireEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledTimes(1);
    rerender(<Button label="Retry task" onClick={onClick} disabled />);
    fireEvent.click(screen.getByRole('button'));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('gives a link the look of a button through buttonClass', () => {
    expect(buttonClass({ variant: 'danger-quiet', size: 'touch', block: true })).toBe(
      'ch-btn ch-btn--danger-quiet ch-btn--touch ch-btn--block',
    );
    expect(buttonClass()).toBe('ch-btn');
  });
});

describe('IconButton', () => {
  it('is named by its label, which is also its title', () => {
    render(<IconButton icon="copy" label="Copy run id" size="touch" outline />);
    const button = screen.getByRole('button', { name: 'Copy run id' });
    expect(button.getAttribute('title')).toBe('Copy run id');
    expect(button.className).toBe('ch-ibtn ch-ibtn--touch ch-ibtn--outline');
  });

  it('shows the on state of a toggle', () => {
    render(<IconButton icon="panel-left" label="Collapse the rail" pressed />);
    expect(screen.getByRole('button').getAttribute('aria-pressed')).toBe('true');
  });

  it('has an icon for every name it can be given', () => {
    expect(ICONS).toContain('check');
    expect(new Set(ICONS).size).toBe(ICONS.length);
    const { container } = render(<Icon name="alert-triangle" />);
    expect(container.querySelector('.ch-i.ch-i-alert-triangle')).not.toBeNull();
  });
});

describe('StatusPill', () => {
  it.each(ALL_STATUSES)(
    'shows %s as a word with its status attribute, so colour is never the only cue',
    (status) => {
      render(<StatusPill status={status} />);
      const pill = screen.getByText(STATUS[status].label);
      expect(pill.className).toBe('ch-pill');
      expect(pill.getAttribute('data-status')).toBe(status);
    },
  );

  it('can carry another word, as long as the status stays', () => {
    render(<StatusPill status="rate_limited" label="Paused until 13:36" />);
    expect(screen.getByText('Paused until 13:36').getAttribute('data-status')).toBe('rate_limited');
  });

  it('as a dot is a named image, because the glyph stands alone', () => {
    render(<StatusPill status="failed" dot />);
    const dot = screen.getByRole('img', { name: 'Failed' });
    expect(dot.getAttribute('title')).toBe('Failed');
    expect(dot.className).toContain('ch-pill--dot');
  });

  it('has a bare and a large variant', () => {
    const { rerender } = render(<StatusPill status="done" bare />);
    expect(screen.getByText('Done').className).toContain('ch-pill--bare');
    rerender(<StatusPill status="done" size="lg" />);
    expect(screen.getByText('Done').className).toContain('ch-pill--lg');
  });
});

describe('Badge', () => {
  it('gives the integrator policy its mode, and a flag its tone', () => {
    render(
      <>
        <Badge mode="ask" />
        <Badge label="test-integrity flagged" tone="warn" size="s" />
      </>,
    );
    expect(screen.getByText('ask').getAttribute('data-mode')).toBe('ask');
    const flag = screen.getByText('test-integrity flagged');
    expect(flag.getAttribute('data-tone')).toBe('warn');
    expect(flag.className).toContain('ch-badge--s');
  });

  it('shows a task size as S, M or L with its own class, and a count as a pill', () => {
    render(
      <>
        <SizeBadge size="L" />
        <Count value={3} tone="accent" />
      </>,
    );
    expect(screen.getByTitle('Size L').getAttribute('data-size')).toBe('L');
    expect(screen.getByText('3').getAttribute('data-tone')).toBe('accent');
  });
});

describe('CapMeter', () => {
  const meter = () => screen.getByRole('meter');

  it('stays neutral under 70 %, warns from 70 % and is dangerous from 90 %', () => {
    const { rerender } = render(<CapMeter label="Spend" value={38} cap={100} />);
    expect(meter().getAttribute('data-level')).toBeNull();
    rerender(<CapMeter label="Spend" value={70} cap={100} />);
    expect(meter().getAttribute('data-level')).toBe('warn');
    rerender(<CapMeter label="Spend" value={94} cap={100} />);
    expect(meter().getAttribute('data-level')).toBe('danger');
  });

  it('is a meter with its value and cap for assistive technology', () => {
    render(<CapMeter label="Spend today" value={9.4} cap={10} text="$9.40 of $10.00 · 94%" />);
    expect(meter().getAttribute('aria-valuenow')).toBe('9.4');
    expect(meter().getAttribute('aria-valuemax')).toBe('10');
    expect(meter().getAttribute('aria-valuetext')).toBe('$9.40 of $10.00 · 94%');
    expect(meter().getAttribute('aria-label')).toBe('Spend today');
    expect(screen.getByText('$9.40 of $10.00 · 94%')).toBeTruthy();
  });

  it('draws the fill by --p, with ticks where a budget is checkpointed and stopped', () => {
    const { container } = render(
      <CapMeter label="Budget" value={40} cap={100} ticks={[85, 100]} />,
    );
    expect(
      container.querySelector<HTMLElement>('.ch-meter__fill')?.style.getPropertyValue('--p'),
    ).toBe('40');
    const ticks = [...container.querySelectorAll<HTMLElement>('.ch-meter__tick')];
    expect(ticks.map((t) => t.style.getPropertyValue('--at'))).toEqual(['85', '100']);
  });

  it('says "no cap" on an empty track, never a full or empty bar', () => {
    const { container } = render(<CapMeter label="Spend" value={12.4} cap={null} />);
    expect(screen.getByText('no cap')).toBeTruthy();
    expect(meter().getAttribute('data-empty')).toBe('true');
    expect(container.querySelector('.ch-meter__fill')).toBeNull();
  });

  it('does not overflow its aria range when spend passes the cap', () => {
    render(<CapMeter label="Spend" value={12} cap={10} />);
    expect(meter().getAttribute('aria-valuemax')).toBe('12');
    expect(screen.getByText('100%')).toBeTruthy();
  });

  it('draws batch progress as one segment per state group, leaving out empty groups, and lists the counts', () => {
    const { container } = render(
      <SegmentedMeter
        label="2 done, 1 running"
        segments={[
          { n: 2, tone: 'ok' },
          { n: 1, tone: 'info', live: true },
          { n: 0, tone: 'danger' },
        ]}
      />,
    );
    const segments = [...container.querySelectorAll<HTMLElement>('.ch-meter__seg')];
    expect(
      segments.map((s) => [s.dataset.tone, s.style.getPropertyValue('--n'), s.dataset.live]),
    ).toEqual([
      ['ok', '2', undefined],
      ['info', '1', 'true'],
    ]);
    expect(screen.getByRole('img', { name: '2 done, 1 running' })).toBeTruthy();
  });
});

describe('Sparkline', () => {
  it('computes a line, an area closed at the bottom, and the last point', () => {
    const { line, area, last } = sparkPath([1, 3, 2], 96, 24);
    expect(line).toMatch(/^M2\.0 22\.0L48\.0 2\.0L94\.0 12\.0$/);
    expect(area.endsWith(`L${last[0].toFixed(1)} 24L2.0 24Z`)).toBe(true);
    expect(last).toEqual([94, 12]);
  });

  it('draws a single value in the middle, and an empty series as nothing', () => {
    expect(sparkPath([5], 96, 24).last[0]).toBe(48);
    expect(sparkPath([], 96, 24)).toEqual({ line: '', area: '', last: [0, 0] });
  });

  it('is an inline svg with a name, sized by its props, with a tone', () => {
    const { container } = render(
      <Sparkline values={[1, 2, 3]} label="Turns, latest 3" tone="ok" width={64} height={20} />,
    );
    const svg = screen.getByRole('img', { name: 'Turns, latest 3' });
    expect(svg.tagName.toLowerCase()).toBe('svg');
    expect(svg.getAttribute('viewBox')).toBe('0 0 64 20');
    expect(svg.getAttribute('data-tone')).toBe('ok');
    expect(container.querySelectorAll('path.line, path.area, circle.dot')).toHaveLength(3);
  });
});

describe('KeyValue', () => {
  it('is a definition list; ids and paths are monospace', () => {
    const { container } = render(
      <KeyValue
        ruled
        items={[
          ['Repository', 'payments-api'],
          ['Branch', 'chargehand/t3', true],
        ]}
      />,
    );
    expect(container.querySelector('dl')?.className).toBe('ch-kv ch-kv--ruled');
    expect(container.querySelectorAll('dt')).toHaveLength(2);
    expect(screen.getByText('chargehand/t3').className).toBe('ch-mono');
    expect(screen.getByText('payments-api').className).toBe('');
  });
});

describe('Tabs and Segmented', () => {
  function Harness() {
    const [value, setValue] = useState('tasks');
    return (
      <Tabs
        value={value}
        onValueChange={setValue}
        label="Batch"
        items={[
          { id: 'tasks', label: 'Tasks', count: 10 },
          { id: 'events', label: 'Events' },
        ]}
      >
        <TabPanel value="tasks">task list</TabPanel>
        <TabPanel value="events">event stream</TabPanel>
      </Tabs>
    );
  }

  it('is a tablist: the selected tab is marked, its panel shows, and a count sits in the tab', () => {
    render(<Harness />);
    expect(screen.getByRole('tablist', { name: 'Batch' }).className).toContain('ch-tabs');
    expect(screen.getByRole('tab', { name: /Tasks/ }).getAttribute('aria-selected')).toBe('true');
    expect(within(screen.getByRole('tab', { name: /Tasks/ })).getByText('10').className).toBe(
      'ch-count',
    );
    expect(screen.getByText('task list')).toBeTruthy();
    expect(screen.queryByText('event stream')).toBeNull();
  });

  it('changes tab with the keyboard', () => {
    render(<Harness />);
    const tasks = screen.getByRole('tab', { name: /Tasks/ });
    tasks.focus();
    fireEvent.keyDown(tasks, { key: 'ArrowRight' });
    fireEvent.focus(screen.getByRole('tab', { name: 'Events' }));
    expect(screen.getByRole('tab', { name: 'Events' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByText('event stream')).toBeTruthy();
  });

  it('segments are toggle buttons with aria-pressed, and report the choice', () => {
    const onChange = vi.fn();
    render(
      <Segmented
        label="Range"
        value="7d"
        onValueChange={onChange}
        items={[
          { id: '24h', label: '24 h' },
          { id: '7d', label: '7 d' },
        ]}
      />,
    );
    expect(screen.getByRole('group', { name: 'Range' }).className).toContain('ch-seg');
    expect(screen.getByRole('button', { name: '7 d' }).getAttribute('aria-pressed')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: '24 h' }));
    expect(onChange).toHaveBeenCalledWith('24h');
  });
});

describe('strips that scroll sideways', () => {
  const box = (scrollLeft: number, scrollWidth: number, clientWidth: number) => ({
    scrollLeft,
    scrollWidth,
    clientWidth,
  });

  it('names the ends that hide content', () => {
    expect(hiddenEdges(box(0, 300, 300))).toBe('');
    expect(hiddenEdges(box(0, 600, 300))).toBe('end');
    expect(hiddenEdges(box(150, 600, 300))).toBe('start end');
    expect(hiddenEdges(box(300, 600, 300))).toBe('start');
  });

  it('marks a tab strip that overflows, and updates the mark as it scrolls', () => {
    const width = vi.spyOn(HTMLElement.prototype, 'scrollWidth', 'get').mockReturnValue(600);
    const client = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(300);
    try {
      render(
        <Tabs
          value="a"
          onValueChange={() => {}}
          label="Wide"
          items={[
            { id: 'a', label: 'A' },
            { id: 'b', label: 'B' },
          ]}
        />,
      );
      const strip = screen.getByRole('tablist', { name: 'Wide' });
      expect(strip.dataset.edges).toBe('end');
      strip.scrollLeft = 300;
      fireEvent.scroll(strip);
      expect(strip.dataset.edges).toBe('start');
    } finally {
      width.mockRestore();
      client.mockRestore();
    }
  });

  it('scrolls the strip, not the page, to bring the selected item into view', () => {
    const strip = document.createElement('div');
    const item = document.createElement('button');
    item.setAttribute('aria-selected', 'true');
    strip.append(item);
    const rect = (left: number, right: number) => ({ left, right }) as DOMRect;
    strip.getBoundingClientRect = () => rect(0, 300);
    item.getBoundingClientRect = () => rect(340, 400);
    revealSelected(strip, '[aria-selected="true"]');
    expect(strip.scrollLeft).toBe(132);
    strip.scrollLeft = 200;
    item.getBoundingClientRect = () => rect(-50, 10);
    revealSelected(strip, '[aria-selected="true"]');
    expect(strip.scrollLeft).toBe(118);
  });
});

describe('Panel, Banner, Kpi, Chip', () => {
  it('a panel has a titled head with a count and an aside, and a body that can run edge to edge', () => {
    const { container } = render(
      <Panel
        title="Needs you"
        count={3}
        countTone="accent"
        aside={<a href="/x">Open inbox</a>}
        flush
        busy
      >
        body
      </Panel>,
    );
    expect(screen.getByRole('heading', { name: 'Needs you' }).className).toBe('ch-panel__title');
    expect(screen.getByText('3').getAttribute('data-tone')).toBe('accent');
    expect(screen.getByRole('link', { name: 'Open inbox' })).toBeTruthy();
    expect(container.querySelector('.ch-panel__body--flush')).not.toBeNull();
    expect(container.querySelector('section')?.getAttribute('aria-busy')).toBe('true');
  });

  it('a banner states a server-wide condition with a tone, one action and an alert role when it must be heard', () => {
    render(
      <Banner
        tone="danger"
        icon="alert-circle"
        title="Server is halted"
        role="alert"
        action={<Button label="Resume server" />}
      >
        Nothing starts until you resume.
      </Banner>,
    );
    const banner = screen.getByRole('alert');
    expect(banner.getAttribute('data-tone')).toBe('danger');
    expect(within(banner).getByRole('button', { name: 'Resume server' })).toBeTruthy();
    expect(within(banner).getByText('Server is halted').className).toBe('ch-banner__title');
  });

  it('a KPI tile is a label, a number and a sub-line', () => {
    const { container } = render(<Kpi label="Spend today" value="$26.14" sub="5.10M tokens" />);
    expect(container.querySelector('.ch-kpi__label')?.textContent).toBe('Spend today');
    expect(container.querySelector('.ch-kpi__value')?.textContent).toBe('$26.14');
    expect(container.querySelector('.ch-kpi__sub')?.textContent).toBe('5.10M tokens');
  });

  it('a chip removes its filter', () => {
    const onRemove = vi.fn();
    render(<Chip label="Repository" value="payments-api" onRemove={onRemove} />);
    fireEvent.click(screen.getByRole('button', { name: 'Remove filter Repository payments-api' }));
    expect(onRemove).toHaveBeenCalled();
  });
});

describe('form controls', () => {
  it('a field labels its control and ties the hint to it', () => {
    render(
      <Field label="Repository path" hint="Absolute path of the checkout.">
        {(control) => <Input {...control} mono />}
      </Field>,
    );
    const input = screen.getByLabelText('Repository path');
    expect(input.className).toBe('ch-input ch-input--mono');
    expect(input.getAttribute('aria-describedby')).toBe(
      screen.getByText('Absolute path of the checkout.').id,
    );
    expect(input.getAttribute('aria-invalid')).toBeNull();
  });

  it('an error replaces the hint, is announced, and marks the control invalid', () => {
    render(
      <Field label="Commit" hint="A hint" error="Not a commit.">
        {(control) => <Input {...control} />}
      </Field>,
    );
    const input = screen.getByLabelText('Commit');
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByRole('alert').textContent).toBe('Not a commit.');
    expect(screen.queryByText('A hint')).toBeNull();
    expect(input.getAttribute('aria-describedby')).toBe(screen.getByRole('alert').id);
  });

  it('a phone input is 44 px high and 16 px, by class', () => {
    render(<Field label="Goal">{(control) => <Input {...control} touch />}</Field>);
    expect(screen.getByLabelText('Goal').className).toContain('ch-input--touch');
  });

  it('a switch is a checkbox with the switch role and a label of its own, and a check is a checkbox', () => {
    const onChange = vi.fn();
    render(
      <>
        <label htmlFor="s">Batch finishes</label>
        <Switch id="s" checked onChange={onChange} />
        <label htmlFor="c">Select row</label>
        <Check id="c" />
      </>,
    );
    const sw = screen.getByRole('switch', { name: 'Batch finishes' });
    expect((sw as HTMLInputElement).checked).toBe(true);
    fireEvent.click(sw);
    expect(onChange).toHaveBeenCalled();
    expect(screen.getByRole('checkbox', { name: 'Select row' }).className).toBe('ch-check');
  });
});

describe('Empty, loading and error states', () => {
  it('an empty state says what is absent and why, and offers the one action', () => {
    render(
      <EmptyState
        title="Nothing needs you"
        text="No task is waiting for an answer."
        action={<Button label="Back to Overview" />}
      />,
    );
    expect(screen.getByRole('heading', { name: 'Nothing needs you' })).toBeTruthy();
    expect(screen.getByText('No task is waiting for an answer.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to Overview' })).toBeTruthy();
  });

  it('a skeleton line is hidden from assistive technology and sized by its props', () => {
    const { container } = render(<Skeleton width="40%" height={28} />);
    const line = container.querySelector<HTMLElement>('.ch-skel');
    expect(line?.getAttribute('aria-hidden')).toBe('true');
    expect(line?.style.width).toBe('40%');
    expect(line?.style.height).toBe('28px');
  });

  it('an error names the failing call and status, and retries', () => {
    const onRetry = vi.fn();
    render(
      <ErrorState
        title="Cannot reach the server"
        call="GET /v1/overview"
        detail="HTTP 503"
        age="Showing the last data, from 13:14."
        onRetry={onRetry}
      />,
    );
    const alert = screen.getByRole('alert');
    expect(within(alert).getByText('GET /v1/overview').className).toContain('mono');
    expect(alert.textContent).toContain('HTTP 503');
    expect(alert.textContent).toContain('13:14');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalled();
  });
});

describe('Table', () => {
  it('is a table of rows and cells, the columns set once on the table as --cols', () => {
    render(
      <Table cols="84px 1fr 80px" label="Runs">
        <TableHead>
          <HeadCell>Run</HeadCell>
          <HeadCell>Repository</HeadCell>
          <HeadCell num>Cost</HeadCell>
        </TableHead>
        <TableRow selected cursor>
          <Cell mono>1fdf01</Cell>
          <Cell>payments-api</Cell>
          <Cell num>$0.50</Cell>
        </TableRow>
        <TableRow dim fresh>
          <Cell mono>77bd0a</Cell>
          <Cell>web-shop</Cell>
          <Cell num>—</Cell>
        </TableRow>
      </Table>,
    );
    const table = screen.getByRole('table', { name: 'Runs' });
    expect(table.style.getPropertyValue('--cols')).toBe('84px 1fr 80px');
    const rows = within(table).getAllByRole('row');
    expect(rows).toHaveLength(3);
    expect(rows[0]?.className).toBe('ch-tr ch-tr--head');
    expect(rows[1]?.getAttribute('aria-selected')).toBe('true');
    expect(rows[1]?.getAttribute('data-cursor')).toBe('true');
    expect(rows[2]?.className).toBe('ch-tr ch-tr--dim is-new');
    expect(within(table).getAllByRole('columnheader')).toHaveLength(3);
    expect(screen.getByText('$0.50').className).toBe('ch-td ch-td--num');
    expect(screen.getByText('1fdf01').className).toBe('ch-td ch-td--id');
  });
});

describe('LogLine and DiffLine', () => {
  it('a log line has time, kind, task and detail, with the kind as an attribute and a problem tint', () => {
    const { container } = render(
      <LogStream label="Events" tail="following the tail">
        <LogLine
          time="12:51:09"
          kind="governor"
          task="t3"
          detail={'warn: spend 85%\nmore'}
          tone="warn"
          isNew
        />
      </LogStream>,
    );
    const stream = screen.getByRole('log', { name: 'Events' });
    expect(stream.getAttribute('aria-live')).toBe('off');
    const line = container.querySelector('.ch-log');
    expect(line?.getAttribute('data-kind')).toBe('governor');
    expect(line?.getAttribute('data-tone')).toBe('warn');
    expect(line?.classList.contains('is-new')).toBe(true);
    expect([...container.querySelectorAll('.ch-log > span')].map((s) => s.textContent)).toEqual([
      '12:51:09',
      'governor',
      't3',
      'warn: spend 85%\nmore',
    ]);
    expect(screen.getByText('following the tail')).toBeTruthy();
  });

  it('a diff line always carries its marker, so colour is not the only cue', () => {
    const { container } = render(
      <Diff label="Changes">
        <DiffFile
          path="src/Cache.cs"
          added={4}
          removed={1}
          flags={<Badge label="test file" size="s" />}
        />
        <DiffLine kind="add" newNo={12} text="var ttl = Read();" />
        <DiffLine kind="del" oldNo={11} text="var ttl = 60;" />
        <DiffLine oldNo={10} newNo={10} text="context" />
        <DiffLine kind="fold" text="Diff cut at 32 KiB; fetch the branch for the rest" />
      </Diff>,
    );
    const marks = [...container.querySelectorAll('.ch-dl__m')].map((m) => m.textContent);
    expect(marks).toEqual(['+', '−', '', '']);
    expect([...container.querySelectorAll('.ch-dl')].map((l) => l.getAttribute('data-k'))).toEqual([
      'add',
      'del',
      null,
      'fold',
    ]);
    expect(screen.getByTitle('src/Cache.cs').textContent).toBe('src/Cache.cs');
    expect(container.querySelector('.ch-diff__stat')?.textContent).toBe('+4−1');
  });
});

describe('TaskNode', () => {
  it('shows size, id, a status that is named, the goal, progress and spend; selecting it reports', () => {
    const onSelect = vi.fn();
    const { container } = render(
      <TaskNode
        id="t3-memory-ttl"
        goal="Add a TTL to the cache"
        size="M"
        status="question"
        progress={40}
        meta="turn 12"
        cost="≈ $0.90"
        selected
        onSelect={onSelect}
      />,
    );
    const node = screen.getByRole('button');
    expect(node.getAttribute('data-status')).toBe('question');
    expect(node.getAttribute('aria-pressed')).toBe('true');
    expect(screen.getByRole('img', { name: 'Question' })).toBeTruthy();
    expect(screen.getByTitle('t3-memory-ttl')).toBeTruthy();
    expect(
      container.querySelector<HTMLElement>('.ch-node__bar > i')?.style.getPropertyValue('--p'),
    ).toBe('40');
    fireEvent.click(node);
    fireEvent.keyDown(node, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledTimes(2);
  });
});

describe('Navigation', () => {
  it('a nav item is a link; the current one is marked, a count joins its name, and a plain click stays on the page', () => {
    const onNavigate = vi.fn();
    render(
      <NavItem
        icon="inbox"
        label="Needs you"
        href="/console/needs-you"
        current
        count={3}
        tone="accent"
        onNavigate={onNavigate}
      />,
    );
    const link = screen.getByRole('link', { name: 'Needs you, 3 waiting' });
    expect(link.getAttribute('aria-current')).toBe('page');
    expect(link.getAttribute('href')).toBe('/console/needs-you');
    expect(link.querySelector('.ch-count')?.getAttribute('data-tone')).toBe('accent');
    fireEvent.click(link);
    expect(onNavigate).toHaveBeenCalledTimes(1);
    fireEvent.click(link, { ctrlKey: true });
    expect(onNavigate).toHaveBeenCalledTimes(1); // left to the browser: a new tab
  });

  it('the tab bar has links and a button, and counts', () => {
    render(
      <TabBar label="Main">
        <TabBarItem icon="grid" label="Overview" href="/console/" current />
        <TabBarItem icon="inbox" label="Needs you" href="/console/needs-you" count={2} />
        <TabBarItem icon="menu" label="More" />
      </TabBar>,
    );
    expect(screen.getByRole('navigation', { name: 'Main' }).className).toBe('ch-tabbar');
    expect(screen.getByRole('link', { name: 'Overview' }).getAttribute('aria-current')).toBe(
      'page',
    );
    expect(screen.getByRole('link', { name: 'Needs you, 2 waiting' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'More' })).toBeTruthy();
  });
});

describe('CommandItem', () => {
  it('shows the match in <mark>, the meta and the keys, and runs on select', () => {
    expect(highlight('Needs you', 'ds')).toBeTruthy();
    const onSelect = vi.fn();
    render(
      <Command label="Palette">
        <Command.List>
          <CommandItem
            label="Needs you"
            meta="3 waiting"
            keys={['g', 'n']}
            icon="inbox"
            query="eed"
            onSelect={onSelect}
          />
        </Command.List>
      </Command>,
    );
    const option = screen.getByRole('option');
    expect(option.className).toBe('ch-cmd');
    expect(option.querySelector('mark')?.textContent).toBe('eed');
    expect(option.querySelector('.ch-cmd__meta')?.textContent).toBe('3 waiting');
    expect([...option.querySelectorAll('kbd')].map((k) => k.textContent)).toEqual(['g', 'n']);
    fireEvent.click(option);
    expect(onSelect).toHaveBeenCalled();
  });

  it('leaves a label with no match as it is', () => {
    const { container } = render(<span>{highlight('Runs', 'zzz')}</span>);
    expect(container.textContent).toBe('Runs');
    expect(container.querySelector('mark')).toBeNull();
  });
});

describe('Dialog', () => {
  it('opens as a modal dialog with its title as its name, and closes on Esc', () => {
    const onOpenChange = vi.fn();
    render(
      <Dialog open onOpenChange={onOpenChange} title="Keyboard shortcuts" hideTitle>
        <button type="button">first</button>
      </Dialog>,
    );
    const dialog = screen.getByRole('dialog', {
      name: 'Keyboard shortcuts',
      hidden: true,
    }) as HTMLDialogElement;
    expect(dialog.open).toBe(true);
    dialog.close(); // what Esc does in a browser
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it('renders nothing while closed', () => {
    render(
      <Dialog open={false} onOpenChange={() => {}} title="Closed">
        x
      </Dialog>,
    );
    expect(screen.queryByText('x')).toBeNull();
  });

  it('a confirm dialog says its effect, offers a way out, and asks before acting', () => {
    const onConfirm = vi.fn();
    const onOpenChange = vi.fn();
    render(
      <ConfirmDialog
        open
        onOpenChange={onOpenChange}
        title="Halt the server?"
        text="This cancels every run. Nothing is pushed."
        keepLabel="Keep running"
        confirmLabel="Halt server"
        onConfirm={onConfirm}
      />,
    );
    const dialog = screen.getByRole('alertdialog', { name: 'Halt the server?', hidden: true });
    expect(within(dialog).getByText('This cancels every run. Nothing is pushed.')).toBeTruthy();
    expect(onConfirm).not.toHaveBeenCalled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep running', hidden: true }));
    expect(onOpenChange).toHaveBeenCalledWith(false);
    const confirm = within(dialog).getByRole('button', { name: 'Halt server', hidden: true });
    expect(confirm.className).toContain('ch-btn--danger');
    fireEvent.click(confirm);
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });
});

describe('Toast', () => {
  it('shows the title, one line and the actions of a notice; the first action resolves it', () => {
    const onSelect = vi.fn();
    const onDismiss = vi.fn();
    render(
      <ToastHost label="Notices">
        <Toast
          tone="accent"
          icon="help"
          title="t3 asked a question"
          body="Should the TTL be read from the environment?"
          actions={[
            { label: 'Answer', onSelect },
            { label: 'Later', onSelect: () => {} },
          ]}
          onDismiss={onDismiss}
        />
      </ToastHost>,
    );
    const toast = screen.getByText('t3 asked a question').closest('.ch-toast') as HTMLElement;
    expect(toast.getAttribute('role')).toBe('status');
    expect(toast.getAttribute('data-tone')).toBe('accent');
    expect(within(toast).getByText('Should the TTL be read from the environment?')).toBeTruthy();
    const answer = within(toast).getByRole('button', { name: 'Answer' });
    expect(answer.className).toContain('ch-btn--primary');
    fireEvent.click(answer);
    expect(onSelect).toHaveBeenCalled();
    fireEvent.click(within(toast).getByRole('button', { name: 'Dismiss' }));
    expect(onDismiss).toHaveBeenCalled();
  });

  it('announces a failure as an alert, and a failure keeps its primary look off the action', () => {
    render(
      <ToastHost label="Notices">
        <Toast
          tone="danger"
          icon="alert-circle"
          title="t1 failed"
          body="verification_failed"
          actions={[{ label: 'Open', onSelect: () => {} }]}
          onDismiss={() => {}}
        />
      </ToastHost>,
    );
    expect(screen.getByText('t1 failed').closest('.ch-toast')?.getAttribute('role')).toBe('alert');
    expect(screen.getByRole('button', { name: 'Open' }).className).not.toContain('primary');
  });
});

describe('Menu and Tooltip', () => {
  it('a menu opens under its button, runs an item, and does not lock the page', () => {
    const onSelect = vi.fn();
    render(
      <Menu
        trigger={<IconButton icon="user" label="Account" />}
        header={<b>owner</b>}
        items={[{ id: 'out', label: 'Sign out', icon: 'lock', onSelect }]}
      />,
    );
    const trigger = screen.getByRole('button', { name: 'Account' });
    fireEvent.keyDown(trigger, { key: 'Enter' });
    const item = screen.getByRole('menuitem', { name: 'Sign out' });
    expect(screen.getByText('owner')).toBeTruthy();
    expect(document.body.getAttribute('data-scroll-locked')).toBeNull();
    fireEvent.click(item);
    expect(onSelect).toHaveBeenCalled();
  });

  it('a tooltip leaves the control as it is when disabled', () => {
    render(
      <TooltipProvider>
        <Tooltip content="Overview" disabled>
          <button type="button">Overview</button>
        </Tooltip>
      </TooltipProvider>,
    );
    expect(screen.getByRole('button', { name: 'Overview' })).toBeTruthy();
  });
});
