# chargehand with Docker Compose

`compose.yaml` runs the release images: the server, and with `--profile driven` the runner behind a docker socket proxy.
The walkthrough (prerequisites, `chargehand init`, `.env`, `up`, `doctor`, a first run, a first driven batch) is
[docs/guide/install.md](../../docs/guide/install.md).

| file | what |
|---|---|
| `compose.yaml` | server (Core), runner and socket proxy (profile `driven`) |
| `.env.example` | names of the variables compose reads; copy to `.env`, which is gitignored |
| `profile.core.json` | chargehand profile for Core only: no runner, no engine socket |
| `profile.driven.json` | chargehand profile for Core + driven |
| `pin-digests.sh` | `./pin-digests.sh <version>` pins the server and session images by digest in `.env` and `profile.json` |

```bash
cp .env.example .env && cp profile.core.json profile.json   # fill in .env
docker compose up -d
```
