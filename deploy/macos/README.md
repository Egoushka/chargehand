# chargehand runner on a Mac

Scripts that run `chargehand runner` on a Mac as a launchd login agent, with OrbStack as the engine behind a socket proxy ([ADR 0058](../../docs/adr/0058-several-runners.md)). The walkthrough, what is different from a runner in a container, and the checks are in
[docs/guide/runners.md](../../docs/guide/runners.md#a-mac-as-a-runner).

| file | what |
|---|---|
| `install-runner.sh` | `init`, `render`, `install [--dry-run]`, `status`, `drain`, `undrain`, `uninstall [--proxy]` |
| `run-runner.sh` | what launchd runs: checks the configuration, waits for the private address, the socket proxy and the Keychain item, then starts the runner. `--print` checks and prints the command without starting anything |
| `runner.conf.example` | the settings (no secret): copy to `~/.chargehand/runner.conf` |
| `com.chargehand.runner.plist.in` | the agent, with `__HOME__` filled in by `install-runner.sh` |
| `update.sh` | `[--dry-run]`: updates the build under `~/.chargehand/app` that the runner and the local server use. Refuses a dirty or non-`main` checkout, publishes to a new directory and swaps a symlink into it, restarts the Mac agent, and rolls back unless `/v1/health` answers after the restart |

Nothing here installs or starts anything until `install` or `update.sh` (without `--dry-run`) is run; `render` and `install --dry-run` change nothing.
