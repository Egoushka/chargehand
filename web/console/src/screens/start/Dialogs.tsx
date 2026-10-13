import { useState } from 'react';
import { formatDateTime } from '../../lib/format';
import {
  Button,
  Dialog,
  DialogClose,
  EmptyState,
  Field,
  IconButton,
  Input,
  Textarea,
} from '../../ui';
import { type Draft, MAX_TASKS, parseImport, type TaskDraft } from './model';
import { deleteTemplate, loadTemplates, MAX_NAME, saveTemplate, type Template } from './templates';

function Head({ title }: { title: string }) {
  return (
    <div className="ch-panel__head">
      <span className="ch-panel__title" aria-hidden="true">
        {title}
      </span>
      <div className="ch-panel__aside">
        <DialogClose />
      </div>
    </div>
  );
}

export interface ImportDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  existingIds: string[];
  onAdd: (tasks: TaskDraft[]) => void;
}

/** Tasks from pasted text: one per line, or a JSON list. GitHub issues are not fetched; the server has no route that lists them. */
export function ImportDialog({ open, onOpenChange, existingIds, onAdd }: ImportDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Import tasks" hideTitle wide>
      <Head title="Import tasks" />
      <ImportBody
        existingIds={existingIds}
        onCancel={() => onOpenChange(false)}
        onAdd={(tasks) => {
          onAdd(tasks);
          onOpenChange(false);
        }}
      />
    </Dialog>
  );
}

function ImportBody({
  existingIds,
  onCancel,
  onAdd,
}: {
  existingIds: string[];
  onCancel: () => void;
  onAdd: (tasks: TaskDraft[]) => void;
}) {
  const [text, setText] = useState('');
  const room = MAX_TASKS - existingIds.length;
  const found = parseImport(text, existingIds, room);
  return (
    <div className="dialog__body stack stack--tight">
      <Field
        label="Tasks"
        error={found.error}
        hint={
          <>
            One task per line; a leading <span className="mono">-</span> or{' '}
            <span className="mono">1.</span> is dropped, and <span className="mono">[S]</span>,{' '}
            <span className="mono">(L)</span> or <span className="mono">M:</span> before the goal
            sets its size (M otherwise). Or paste a JSON list of goals or of task objects (
            <span className="mono">id</span>, <span className="mono">goal</span>,{' '}
            <span className="mono">size</span>, <span className="mono">budget_usd</span>,{' '}
            <span className="mono">depends_on</span>).
          </>
        }
      >
        {(control) => (
          <Textarea
            {...control}
            rows={8}
            mono
            value={text}
            spellCheck={false}
            data-autofocus
            placeholder={
              '[S] Let intake watch a repository by an alias\n[M] Report peak memory per container'
            }
            onChange={(e) => setText(e.target.value)}
          />
        )}
      </Field>
      <p className="dim small" role="status">
        {found.tasks.length === 0
          ? 'No tasks found yet.'
          : `${found.tasks.length} ${found.tasks.length === 1 ? 'task' : 'tasks'} found.`}
        {found.skipped > 0
          ? ` ${found.skipped} more would pass the limit of ${MAX_TASKS} tasks and are left out.`
          : ''}
      </p>
      <p className="dim small">
        GitHub issues cannot be imported: the server has no route that lists them. Paste their
        titles here.
      </p>
      <div className="row row--end">
        <Button label="Cancel" onClick={onCancel} />
        <Button
          variant="primary"
          label={`Add ${found.tasks.length} ${found.tasks.length === 1 ? 'task' : 'tasks'}`}
          disabled={found.tasks.length === 0}
          onClick={() => onAdd(found.tasks)}
        />
      </div>
    </div>
  );
}

export interface TemplatesDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  draft: Draft;
  onLoad: (template: Template) => void;
}

/** Named requests kept in this browser: save the form as one, load one into the form, delete one. */
export function TemplatesDialog({ open, onOpenChange, draft, onLoad }: TemplatesDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Templates" hideTitle wide>
      <Head title="Templates" />
      <TemplatesBody
        draft={draft}
        onLoad={(template) => {
          onLoad(template);
          onOpenChange(false);
        }}
      />
    </Dialog>
  );
}

function TemplatesBody({ draft, onLoad }: { draft: Draft; onLoad: (template: Template) => void }) {
  const [templates, setTemplates] = useState<Template[]>(loadTemplates);
  const [name, setName] = useState('');
  const [failed, setFailed] = useState(false);

  function save() {
    const next = saveTemplate(name, draft);
    setFailed(next === null);
    if (next) {
      setTemplates(next);
      setName('');
    }
  }

  return (
    <div className="dialog__body stack stack--tight">
      <form
        className="start-save"
        onSubmit={(e) => {
          e.preventDefault();
          save();
        }}
      >
        <Field
          label="Save the form as a template"
          error={
            failed ? 'This browser would not keep it: site storage is blocked or full.' : undefined
          }
          hint="Keeps the label, repository, tasks and options; not the commit."
        >
          {(control) => (
            <Input
              {...control}
              value={name}
              maxLength={MAX_NAME}
              placeholder="Nightly backlog"
              data-autofocus
              onChange={(e) => setName(e.target.value)}
            />
          )}
        </Field>
        <Button type="submit" label="Save template" icon="check" disabled={name.trim() === ''} />
      </form>
      {templates.length === 0 ? (
        <EmptyState
          icon="layers"
          title="No templates yet"
          text="Saved templates stay in this browser only."
        />
      ) : (
        <ul className="start-templates list-reset" aria-label="Saved templates">
          {templates.map((template) => (
            <li key={template.name} className="li start-template">
              <div className="li__main">
                <span className="li__title b">{template.name}</span>
                <span className="li__meta">Saved {formatDateTime(template.savedAt)}</span>
              </div>
              <Button label="Load" size="sm" onClick={() => onLoad(template)} />
              <IconButton
                icon="trash"
                size="sm"
                label={`Delete template ${template.name}`}
                onClick={() => {
                  const next = deleteTemplate(template.name);
                  setFailed(next === null);
                  if (next) setTemplates(next);
                }}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
