# The base image of ADR 0078 (driven.environment: "mise"): Debian, git, make, a C toolchain, curl, unzip, Python 3 and the mise binary, and no SDK. The toolchain a repository pins is
# installed into its home volume (/home/session) by an install helper that runs this same image. Build from the repository root:
#   docker build -f images/session-mise/Dockerfile -t chargehand-session-mise .
# This step holds the base only; the driver and Claude Code layers come with the CI-as-verifier step.
FROM debian:13-slim
ARG MISE_VERSION=2026.10.17
ARG MISE_SHA256_X64=fe6dd17e68e09682b31d2e7bb7bb755d22636ac1af5d94621e300f2ca9e9fe8a
ARG MISE_SHA256_ARM64=e90904144ba4377c5fbe385f429830282c68f3b4f9c690bf6c5a2ae9b74a809b
ARG TARGETARCH
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git make build-essential unzip xz-utils libicu-dev python3 python3-pip python3-venv \
    && case "${TARGETARCH:-$(dpkg --print-architecture)}" in \
         amd64) arch=x64 sha="$MISE_SHA256_X64" ;; \
         arm64) arch=arm64 sha="$MISE_SHA256_ARM64" ;; \
         *) echo "no mise build for this architecture" >&2; exit 1 ;; \
       esac \
    && curl -fsSLo /usr/local/bin/mise "https://github.com/jdx/mise/releases/download/v${MISE_VERSION}/mise-v${MISE_VERSION}-linux-${arch}" \
    && echo "${sha}  /usr/local/bin/mise" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/mise \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home --home-dir /home/session --shell /bin/bash session \
    && mkdir -p /work /out && chown 10001:10001 /work /out
# The shims directory is on PATH: a tool the install helper put on the home volume resolves in a session without activation. The template sets MISE_DATA_DIR to the same place.
ENV HOME=/home/session MISE_DATA_DIR=/home/session/.local/share/mise PATH=/home/session/.local/share/mise/shims:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin DISABLE_AUTOUPDATER=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
USER 10001
WORKDIR /work
