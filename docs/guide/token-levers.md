# Token levers

Issue #780 asks which choices beyond structure save tokens without costing answers. `chargehand eval levers` measures
that over the eval cells in [evals/cells.json](../../evals/cells.json), the same items Prompt CI gates on.

```bash
dotnet run --project src/Chargehand.Cli -- eval levers [--levers evals/levers.json] [--cells cheap/worker,review/worker] [--pass 0.7]
```

Each lever in [evals/levers.json](../../evals/levers.json) is applied to a copy of this checkout's `prompts/` and
`presets/` under `runs/levers/<name>/<lever>/`. Every item of a cell then runs under the base and under the lever, back
to back, as `eval gate` pairs them. A lever that changes none of a cell's files is listed as skipped, with no run. Intake
reports no usage, so it is left out unless named with `--cells`.

## The levers

| id | kind | what it changes |
|---|---|---|
| `terse-worker-prompt` | `prompt` (prompt language) | `core/worker` in telegraphic English ([evals/levers/worker-terse.md](../../evals/levers/worker-terse.md)): the same rules and result shape in about a third fewer characters |
| `effort-low` | `effort` (output and thinking budget) | every node kind's model gets the `#low` variant: OpenCode's reasoning variant, Claude Code's `--effort low` |
| `compact-at-30k` | `compaction` (context trimming) | `trigger_tokens` 30000 where a preset compacts (60000 or 100000 today) |

A new lever is one more entry: `prompt` with `block` and `file`, `effort` with `value` (a lower-case word), or
`compaction` with `value` (tokens).

## The report

One row per cell and lever, so per preset: items, tokens of each arm (input, output and cache), the token change, pass
rate of each arm (the share of items at quality `--pass` or above, 0.7 by default), the mean quality change and the
cell's gate verdict with its T and C. `keep` is yes when the lever saves tokens and the gate passes it.

## Which levers to keep

The rule: keep a lever on a preset when its row says `keep: yes` on at least the cell's `min_items`; drop it where the
gate blocks, whatever it saves. A lever kept goes into the preset or prompt by an ordinary pull request, which Prompt CI
gates again.

**No measured run is recorded yet**: items live in the maintainer's Langfuse datasets, and the run needs a model
server. Until the first run is entered in [benchmarks.md](../benchmarks.md), no lever is named as worth keeping. What
is expected, not measured: `effort-low` saves output tokens on the presets with no critic; `compact-at-30k` saves input
only on long sessions, such as `code` (`driven` has no compaction setting); `terse-worker-prompt` saves little per call, since the block
is cached after the first call, and risks completeness.
