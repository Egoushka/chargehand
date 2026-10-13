import { queryOptions, useQuery } from '@tanstack/react-query';
import { getJson } from './client';
import type { ContractMeta, HostBody } from './common';

/** A secret item is shown as set or not, never by name or value. */
export interface ProfileHttp {
  port: number;
  listen: string;
  allowed_hosts: string[];
  max_concurrent_runs: number;
  ui: boolean;
  api_key_set: boolean;
  accounts: string[];
}

export interface ProfileClaudeCode {
  version: string;
  binary: string;
  credential: 'api_key' | 'oauth_token' | 'login';
  gateway: boolean;
}

export interface ProfileOpenCode {
  version: string;
  host: string;
}

/** A runner service the profile lists: its name and address as scheme, host and port, never its key. */
export interface ProfileRunner {
  name: string;
  host?: string | null;
  max_sessions?: number | null;
  platform: string;
  weight: number;
}

/** The CI fixer's limits; `attempts`, `turns`, `minutes` and `wait` are the values in force, the `max_*` and `wait_minutes` ones what the profile sets. */
export interface DrivenCiFix {
  max_attempts?: number | null;
  max_turns?: number | null;
  max_minutes?: number | null;
  wait_minutes?: number | null;
  attempts: number;
  turns: number;
  minutes: number;
  wait: number;
}

export interface ProfileIntegrator {
  app_configured: boolean;
  repositories: Record<string, string>;
  always_ask: string[];
  ci_fix: DrivenCiFix;
}

/** The preset's `driven` limits, as the profile sets them; a key left out keeps the preset's value. */
export interface DrivenLimits {
  max_minutes?: number | null;
  no_progress_minutes?: number | null;
  max_turns?: number | null;
  max_tokens?: number | null;
  memory_mb?: number | null;
  cpus?: number | null;
  pids?: number | null;
  max_continuations?: number | null;
}

export interface DrivenGovernorHistory {
  min_tasks: number;
  window_days: number;
  percentile: number;
}

/** The governor's settings for every session; a threshold left out keeps the session governor's default. */
export interface DrivenGovernor {
  mode: 'off' | 'observe' | 'enforce';
  /** The flat budget per size class (`S`, `M`, `L`) in the dollar equivalent. */
  budgets?: Record<string, number> | null;
  repeat_calls?: number | null;
  repeat_failures?: number | null;
  failing_calls?: number | null;
  probe_commands?: number | null;
  no_growth_turns?: number | null;
  rewarn_turns?: number | null;
  context_tokens?: number | null;
  checkpoint_at?: number | null;
  probe_patterns?: string[] | null;
  wait_patterns?: string[] | null;
  history?: DrivenGovernorHistory | null;
}

/** When the host has room for another session. */
export interface DrivenAdmission {
  /** Memory left for everything else on the host. */
  reserve_mb: number;
  max_load_per_cpu?: number | null;
  default_request_mb: number;
  max_memory_pressure: number;
}

export interface DrivenPlanning {
  enabled: boolean;
  preset: string;
}

export interface DrivenLearning {
  retro: boolean;
  retro_preset: string;
  pr_days: number;
  pr_check_minutes: number;
}

export interface ProfileDriven {
  enabled: boolean;
  max_parallel: number;
  max_parallel_total: number;
  max_session_calls: number;
  images: string[];
  network_allow: string[];
  runners: ProfileRunner[];
  push_secret_set: boolean;
  limits?: DrivenLimits | null;
  governor?: DrivenGovernor | null;
  admission?: DrivenAdmission | null;
  integrator?: ProfileIntegrator | null;
  planning?: DrivenPlanning | null;
  learning?: DrivenLearning | null;
  forge_kind?: string | null;
  forge_host?: string | null;
  service_repositories: string[];
  task_source_set: boolean;
}

export interface ProfileAutomation {
  repositories: string[];
  /** How many authors are allowed, not their names. */
  allowed_authors: number;
  label: string;
  mention: string;
  poll_seconds: number;
  max_concurrent: number;
  max_starts_per_day: number;
  max_review_rounds: number;
}

/** The profile as the owner may see it: settings only. Secret items, URL paths and queries, headers, environment values, commands and key files are left out. */
export interface ProfileView {
  schema: string;
  runtime: string;
  default_preset: string;
  intake_model?: string | null;
  worker_root: string;
  repository_roots: string[];
  run_cap_usd: number;
  max_parallel_nodes: number;
  support_check: boolean;
  signing: boolean;
  telemetry: boolean;
  memory: string[];
  mcp_servers: string[];
  models: Record<string, string>;
  http?: ProfileHttp | null;
  claude_code?: ProfileClaudeCode | null;
  open_code?: ProfileOpenCode | null;
  driven?: ProfileDriven | null;
  automation?: ProfileAutomation | null;
}

export interface PresetBody {
  name: string;
  version?: string | null;
  allowed_actions: string[];
  driven: boolean;
  approval: boolean;
  error?: string | null;
}

/** `session`, `egress`, `service`, `workspace` (prep and transfer helpers) or `other`. */
export interface ContainerBody {
  name: string;
  kind: 'session' | 'egress' | 'service' | 'workspace' | 'other';
  run_id?: string | null;
  batch_run_id?: string | null;
  task_id?: string | null;
  actor?: string | null;
}

export interface RunnerBody {
  configured: boolean;
  host?: string | null;
  /** The runner answered (or the local engine did); null when driven sessions are off. */
  reachable?: boolean | null;
  error?: string | null;
  elapsed_ms?: number | null;
  container_count?: number | null;
  containers: ContainerBody[];
  networks: string[];
  volumes: string[];
  host_snapshot?: HostBody | null;
}

export interface ImageBody {
  reference: string;
  name: string;
  digest?: string | null;
  pinned: boolean;
}

/** `system/v1`: version, the redacted profile, presets, the runner and its live containers, and the pinned images. Owner only. */
export interface SystemBody extends ContractMeta {
  contract_version: 'system/v1';
  version: string;
  started_at: string;
  halted: boolean;
  unfinished: number;
  profile?: ProfileView | null;
  presets: PresetBody[];
  runner: RunnerBody;
  images: ImageBody[];
}

export const systemQuery = () =>
  queryOptions({
    queryKey: ['system'],
    queryFn: ({ signal }) => getJson<SystemBody>('/v1/system', signal),
    refetchInterval: 60_000,
  });

export const useSystem = () => useQuery(systemQuery());
