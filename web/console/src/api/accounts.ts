import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { AccountBody, ContractMeta } from './common';

/** `accounts/v1`: the owner and every account the profile declares, with caps, spend against them, and the state of their keys and credentials. Owner only. */
export interface AccountsBody extends ContractMeta {
  contract_version: 'accounts/v1';
  accounts: AccountBody[];
}

export const accountsQuery = () =>
  queryOptions({
    queryKey: ['accounts'],
    queryFn: ({ signal }) => getJson<AccountsBody>('/v1/accounts', signal),
  });

export const useAccounts = () => useQuery(accountsQuery());
