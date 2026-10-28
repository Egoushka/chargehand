import { useState } from 'react';
import { type InboxItem, type InboxOption, useDismiss, useOption } from '../../api/inbox';
import { shortId } from '../../lib/format';
import { safeHttpUrl } from '../../lib/url';
import { useToast } from '../../shell/toasts';
import { Button, buttonClass, IconButton } from '../../ui';

/** The address an `open` option may go to: a console page of this site, or an http(s) page; nothing else. */
function openUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  if (url.startsWith('/console/') && !url.includes('..')) return url;
  return safeHttpUrl(url);
}

/**
 * A card's options (ADR 0085), each with its effect beside it: the button sends the server call, copies the command or opens the page.
 * `answer` is left out: the card's answer box is that option.
 */
export function Options({ item, label }: { item: InboxItem; label: string }) {
  const options = (item.options ?? []).filter((o) => o.id !== 'answer');
  if (options.length === 0) return null;
  return (
    <ul className="options list-reset" aria-label={`What you can do about ${label}`}>
      {options.map((option) => (
        <li key={option.id} className="option">
          <OptionAction item={item} option={option} label={label} />
          <span className="option__effect">{option.effect}</span>
        </li>
      ))}
    </ul>
  );
}

function OptionAction({
  item,
  option,
  label,
}: {
  item: InboxItem;
  option: InboxOption;
  label: string;
}) {
  if (option.id === 'dismiss') return <DismissOption item={item} option={option} label={label} />;
  if (option.command) return <CommandOption option={option} />;
  if (option.call) return <CallOption option={option} label={label} />;
  const url = openUrl(option.url);
  if (!url) return null;
  const external = !url.startsWith('/console/');
  return (
    <a
      href={url}
      className={buttonClass({ variant: option.id === 'open' && external ? 'primary' : undefined })}
      {...(external ? { target: '_blank', rel: 'noreferrer noopener' } : {})}
    >
      {option.label}
    </a>
  );
}

/** Dismiss and its Undo, the same as the card's button before options. */
function DismissOption({
  item,
  option,
  label,
}: {
  item: InboxItem;
  option: InboxOption;
  label: string;
}) {
  const toast = useToast();
  const dismiss = useDismiss();
  return (
    <Button
      variant="quiet"
      label={option.label}
      aria-label={`${option.label} ${label}`}
      loading={dismiss.isPending}
      onClick={() =>
        dismiss.mutate(
          { ids: [item.id] },
          {
            onSuccess: () =>
              toast.push({
                tone: 'ok',
                icon: 'check',
                title: `Dismissed ${label}`,
                actions: [{ label: 'Undo', onSelect: () => void dismiss.restore([item.id]) }],
              }),
            onError: (error) =>
              toast.push({
                tone: 'danger',
                icon: 'alert-triangle',
                title: 'Not dismissed',
                body: error.message,
              }),
          },
        )
      }
    />
  );
}

/** An option the server carries out: retry, undo, and the policy choices. */
function CallOption({ option, label }: { option: InboxOption; label: string }) {
  const toast = useToast();
  const send = useOption();
  return (
    <Button
      variant={option.id === 'retry' ? 'primary' : undefined}
      label={option.label}
      aria-label={`${option.label} ${label}`}
      loading={send.isPending}
      onClick={() =>
        send.mutate(option, {
          onSuccess: (reply) => {
            const run = typeof reply.run_id === 'string' ? reply.run_id : null;
            toast.push({
              tone: 'ok',
              icon: 'check',
              title: run ? `${option.label}: started ${shortId(run)}` : `${option.label}: done`,
            });
          },
          onError: (error) =>
            toast.push({
              tone: 'danger',
              icon: 'alert-triangle',
              title: `${option.label} refused`,
              body: error.message,
            }),
        })
      }
    />
  );
}

/** An option a person carries out: the command shows, and copies with one click. */
function CommandOption({ option }: { option: InboxOption }) {
  const [open, setOpen] = useState(false);
  const toast = useToast();
  const command = option.command ?? '';
  return (
    <>
      <Button label={option.label} onClick={() => setOpen((v) => !v)} aria-expanded={open} />
      {open ? (
        <span className="row">
          <code className="mono">{command}</code>
          <IconButton
            icon="copy"
            size="sm"
            label={`Copy the ${option.label.toLowerCase()} command`}
            onClick={() =>
              navigator.clipboard.writeText(command).then(
                () => toast.push({ tone: 'ok', icon: 'check', title: 'Command copied' }),
                () => toast.push({ tone: 'warn', icon: 'alert-triangle', title: 'Cannot copy' }),
              )
            }
          />
        </span>
      ) : null}
    </>
  );
}
