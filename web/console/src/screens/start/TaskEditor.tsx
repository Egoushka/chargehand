import { formatUsd } from '../../lib/format';
import {
  Button,
  EmptyState,
  Field,
  IconButton,
  Input,
  Segmented,
  Select,
  Textarea,
} from '../../ui';
import {
  type Issue,
  issuesAt,
  moveTask,
  removeTask,
  renameTask,
  SIZES,
  type TaskDraft,
  type TaskEstimate,
  wouldCycle,
} from './model';

type SizeChoice = 'auto' | (typeof SIZES)[number];

export interface TaskEditorProps {
  tasks: TaskDraft[];
  /** One per task, in the same order. */
  estimates: TaskEstimate[];
  issues: Issue[];
  /** Show the problems that only mean "not filled in yet". */
  showLate: boolean;
  phone: boolean;
  onChange: (tasks: TaskDraft[]) => void;
  onAdd: () => void;
  onImport: () => void;
}

/** The batch's tasks as rows: id, size, optional budget, a multi-line goal, and what each waits for. Each problem shows at its field. */
export function TaskEditor({
  tasks,
  estimates,
  issues,
  showLate,
  phone,
  onChange,
  onAdd,
  onImport,
}: TaskEditorProps) {
  if (tasks.length === 0)
    return (
      <EmptyState
        icon="list"
        title="No tasks yet"
        text="A batch is a list of tasks. Add one by hand, or paste a list of goals."
        action={
          <div className="row">
            <Button label="Add task" icon="plus" onClick={onAdd} />
            <Button label="Import tasks" icon="download" onClick={onImport} />
          </div>
        }
      />
    );
  const patch = (index: number, change: Partial<TaskDraft>) =>
    onChange(tasks.map((t, i) => (i === index ? { ...t, ...change } : t)));

  return (
    <ol className="taskedit-list list-reset" aria-label="Tasks">
      {tasks.map((task, index) => {
        const at = (field: string) => issuesAt(issues, `tasks.${index}.${field}`, showLate);
        const first = (field: string) => at(field).find((i) => i.level === 'error')?.message;
        const name = task.id.trim() === '' ? `task ${index + 1}` : task.id;
        const estimate = estimates[index];
        const others = tasks.filter((t, i) => i !== index && t.id.trim() !== '');
        const optionIds = [...new Set(others.map((t) => t.id))].filter(
          (id) => !task.dependsOn.includes(id),
        );
        const sizeChoice: SizeChoice = (SIZES as readonly string[]).includes(task.size)
          ? (task.size as SizeChoice)
          : 'auto';
        return (
          <li key={task.key} className="taskedit">
            <div className="taskedit__top">
              <Field label="Task id" error={first('id')}>
                {(control) => (
                  <Input
                    {...control}
                    mono
                    value={task.id}
                    autoComplete="off"
                    spellCheck={false}
                    data-field={`tasks.${index}.id`}
                    onChange={(e) => onChange(renameTask(tasks, index, e.target.value))}
                  />
                )}
              </Field>
              <div className="ch-field">
                <span className="ch-label">Size</span>
                <Segmented<SizeChoice>
                  label={`Size of ${name}`}
                  touch={phone}
                  value={sizeChoice}
                  onValueChange={(next) => patch(index, { size: next === 'auto' ? '' : next })}
                  items={[
                    { id: 'auto', label: 'Auto' },
                    { id: 'S', label: 'S' },
                    { id: 'M', label: 'M' },
                    { id: 'L', label: 'L' },
                  ]}
                />
                {first('size') ? (
                  <div className="ch-error" role="alert">
                    {first('size')}
                  </div>
                ) : null}
              </div>
              <Field
                label="Budget, USD (optional)"
                error={first('budget')}
                hint="Blank: from history"
              >
                {(control) => (
                  <Input
                    {...control}
                    mono
                    inputMode="decimal"
                    value={task.budget}
                    autoComplete="off"
                    data-field={`tasks.${index}.budget`}
                    onChange={(e) => patch(index, { budget: e.target.value })}
                  />
                )}
              </Field>
              <div className="taskedit__tools">
                <IconButton
                  icon="chevron-up"
                  label={`Move ${name} up`}
                  disabled={index === 0}
                  onClick={() => onChange(moveTask(tasks, index, -1))}
                />
                <IconButton
                  icon="chevron-down"
                  label={`Move ${name} down`}
                  disabled={index === tasks.length - 1}
                  onClick={() => onChange(moveTask(tasks, index, 1))}
                />
                <IconButton
                  icon="trash"
                  label={`Remove ${name}`}
                  onClick={() => onChange(removeTask(tasks, index))}
                />
              </div>
            </div>
            <Field label="Goal" error={first('goal')}>
              {(control) => (
                <Textarea
                  {...control}
                  rows={3}
                  value={task.goal}
                  placeholder={
                    task.extra.ref !== undefined || task.extra.continue_from !== undefined
                      ? 'Taken from the task source (ref) or the earlier task (continue_from)'
                      : 'What the change should do, in a few sentences.'
                  }
                  data-field={`tasks.${index}.goal`}
                  onChange={(e) => patch(index, { goal: e.target.value })}
                />
              )}
            </Field>
            <div className="taskedit__deps">
              <span className="ch-label" id={`deps-${task.key}`}>
                Depends on
              </span>
              {/* biome-ignore lint/a11y/useSemanticElements: a fieldset would add its own border and legend layout to a row of chips */}
              <div className="row row--wrap" aria-labelledby={`deps-${task.key}`} role="group">
                {task.dependsOn.length === 0 ? <span className="dim small">none</span> : null}
                {task.dependsOn.map((dep) => (
                  <span key={dep} className="ch-chip">
                    {dep}
                    <IconButton
                      icon="x"
                      size="sm"
                      label={`Remove dependency ${dep} from ${name}`}
                      onClick={() =>
                        patch(index, { dependsOn: task.dependsOn.filter((d) => d !== dep) })
                      }
                    />
                  </span>
                ))}
                <Select
                  value=""
                  aria-label={`Add a dependency to ${name}`}
                  disabled={optionIds.length === 0}
                  data-field={`tasks.${index}.depends_on`}
                  onChange={(e) => {
                    if (e.target.value)
                      patch(index, { dependsOn: [...task.dependsOn, e.target.value] });
                  }}
                >
                  <option value="">
                    {optionIds.length === 0 && others.length === 0
                      ? 'No other task'
                      : 'Add dependency'}
                  </option>
                  {optionIds.map((id) => (
                    <option key={id} value={id} disabled={wouldCycle(tasks, task.id, id)}>
                      {wouldCycle(tasks, task.id, id) ? `${id} (would make a cycle)` : id}
                    </option>
                  ))}
                </Select>
              </div>
              {at('depends_on').map((issue) => (
                <div key={issue.message} className="ch-error" role="alert">
                  {issue.message}
                </div>
              ))}
            </div>
            {estimate ? (
              <p className="taskedit__estimate dim small">
                {`Budget ${formatUsd(estimate.usd, estimate.source === 'repository' || estimate.source === 'all')} · ${estimate.label}${estimate.sizeAssumed ? '; no size set, so M is assumed' : ''}`}
              </p>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}
