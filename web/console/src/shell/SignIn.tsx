import { useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useState } from 'react';
import { ApiError, call, problems, Unauthorized } from '../api/client';
import { setKey } from '../api/key';
import type { Me } from '../api/types';
import { Brand, Button, Field, Input } from '../ui';

/** Asks for an API key and checks it with GET /v1/accounts/me. A key the server refuses is not stored. */
export function SignIn({ notice }: { notice: string | null }) {
  const queryClient = useQueryClient();
  const [value, setValue] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const key = value.trim();
    if (!key) return;
    setBusy(true);
    setError(null);
    try {
      const reply = await call<Me>('/v1/accounts/me', { key });
      if (!reply.ok) throw new ApiError(reply.status, problems(reply));
      queryClient.setQueryData(['me'], reply.data);
      setValue('');
      setKey(key);
    } catch (e) {
      setError(
        e instanceof Unauthorized
          ? 'The server rejected that key.'
          : e instanceof Error
            ? e.message
            : String(e),
      );
    } finally {
      setBusy(false);
    }
  }

  const message = error ?? notice;
  return (
    <main className="signin">
      <Brand />
      <h1>Sign in</h1>
      <p className="dim">
        Paste an API key. It stays in this tab (sessionStorage) until you sign out or close it, and
        goes only to this server.
      </p>
      <form onSubmit={submit} className="stack stack--tight">
        <Field label="API key" error={message ?? undefined}>
          {(control) => (
            <Input
              {...control}
              type="password"
              autoComplete="off"
              required
              large
              placeholder="chk1.… or the server key"
              value={value}
              onChange={(e) => setValue(e.target.value)}
            />
          )}
        </Field>
        <div className="row">
          <Button type="submit" variant="primary" size="lg" label="Sign in" loading={busy} />
        </div>
      </form>
    </main>
  );
}
