import { useQuery } from '@tanstack/react-query';
import { ApiError } from '../../api/client';
import { meQuery } from '../../api/queries';

/** The server answers an account key `403` on the routes about the server itself (intake, accounts, system). */
export const isForbidden = (error: unknown): boolean =>
  error instanceof ApiError && error.status === 403;

export interface OwnerGate {
  /** Who is signed in is known to be an account: the owner-only call is not made. */
  denied: boolean;
  /** The call may be made: the key is the owner's, or `GET /v1/accounts/me` failed and the route itself decides. */
  allowed: boolean;
  /** The name of the account that is signed in, once known. */
  name?: string;
}

/**
 * Whether an owner-only screen may call its route. The shell already reads `/v1/accounts/me`, so an account never sends the call
 * that would be refused; if `me` cannot be read, the route is tried and a `403` is handled as well.
 */
export function useOwnerGate(): OwnerGate {
  const me = useQuery(meQuery());
  return {
    denied: me.data?.owner === false,
    allowed: me.data?.owner === true || me.isError,
    name: me.data?.name,
  };
}
