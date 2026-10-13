# The session image with a JDK and Gradle, for repositories that build with Gradle or Maven (ADR 0048): the base session image plus the tools, nothing removed. Build it after the base image,
# from the repository root, naming the base by the digest the profile pins:
#   docker build -f images/session-jdk/Dockerfile --build-arg BASE=<name@sha256:...> -t chargehand-session-jdk .
# List the result in driven.images and under driven.services.images (gradle, maven), and in the runner's --images.
ARG BASE
FROM ${BASE}
ARG JDK_PACKAGE=openjdk-21-jdk-headless
# Gradle from services.gradle.org, checked against its published SHA-256 (the distribution's gradle is too old for current Android and Kotlin builds).
ARG GRADLE_VERSION=9.8.1
ARG GRADLE_SHA256=dce76f55f8e251a3a1f130eb120f30b3d271de2b76c9b0729d316b5a1b6dc01f
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends "${JDK_PACKAGE}" maven unzip \
    && curl -fsSLo /tmp/gradle.zip "https://services.gradle.org/distributions/gradle-${GRADLE_VERSION}-bin.zip" \
    && echo "${GRADLE_SHA256}  /tmp/gradle.zip" | sha256sum -c - \
    && unzip -q /tmp/gradle.zip -d /opt \
    && ln -s "/opt/gradle-${GRADLE_VERSION}/bin/gradle" /usr/local/bin/gradle \
    && rm /tmp/gradle.zip \
    && rm -rf /var/lib/apt/lists/*
# Without a shared cache (driven.services.caches) Gradle and Maven restore into the workspace volume, like NuGet: the tmpfs at /home/session is too small for a dependency cache. The workspace helper keeps both directories out of the snapshot commit.
ENV GRADLE_USER_HOME=/work/.gradle MAVEN_OPTS=-Dmaven.repo.local=/work/.m2/repository
# Gradle's home for the probe is a scratch directory: the image's /work stays empty, so a new volume mounted there is owned by the session user.
RUN GRADLE_USER_HOME=/tmp/gradle-probe dotnet /opt/chargehand/Chargehand.Cli.dll session --probe /opt/chargehand/environment.json && rm -rf /tmp/gradle-probe
USER 10001
