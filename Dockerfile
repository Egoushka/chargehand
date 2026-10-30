# chargehand serve with the Claude Code worker runtime (ADR 0024). OpenCode is not in the image: a profile that uses it
# points opencode.url at a server the deployment runs next to this container.
# The operator console (ADR 0056) is built here and embedded in the server. Node and the npm packages stay in this stage: the image gets the built files only.
# The Node version must equal web/console/.nvmrc (CI checks it). Dependencies install from the lockfile, without install scripts.
FROM node:24.21.0-bookworm-slim@sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20 AS web
WORKDIR /web
COPY web/console/package.json web/console/package-lock.json web/console/.npmrc ./
RUN npm ci --ignore-scripts
COPY web/console/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=web /web/dist web/console/dist
RUN dotnet publish src/Chargehand.Cli -c Release -o /out

# The runner (`chargehand runner`, ADR 0039) starts containers through the docker CLI. The server never has the engine's socket, so the CLI is inert
# there. Version of the engine it talks to (the VPS runs 29.6.2); copied from the official image, by digest.
FROM docker:29.6.2-cli@sha256:be132a9f282288de4afaf63379dff75711fda0147c6b72a9df44e51841402144 AS dockercli

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# claude_code.version in the profile must be satisfied by this: an exact string, or `>=X.Y.Z` to take X.Y.Z and
# newer of major X without a matching profile change — prefer `>=` where this argument moves on its own schedule
# (the adapter refuses to run otherwise, ADR 0020).
ARG CLAUDE_CODE_VERSION=2.1.283
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git nodejs npm \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
WORKDIR /app
COPY --from=build /out bin/
COPY prompts prompts/
COPY presets presets/
# runs/: the run log (profile run_log, relative to /app). /srv/chargehand/work: worker clones (ADR 0023).
RUN mkdir -p runs /srv/chargehand/work && chown "$APP_UID" runs /srv/chargehand/work
USER $APP_UID
ENV CHARGEHAND_PROFILE=/config/profile.json DISABLE_AUTOUPDATER=1
EXPOSE 4300
ENTRYPOINT ["dotnet", "/app/bin/Chargehand.Cli.dll"]
CMD ["serve"]
