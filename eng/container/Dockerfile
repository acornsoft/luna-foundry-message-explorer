# SPDX-License-Identifier: MPL-2.0
# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.
#
# lfme, the headless and read-only Message Explorer, in a small container.
#
#   docker build -f eng/container/Dockerfile -t lfme:local .
#   docker run --rm --read-only lfme:local check --synthetic --max-dead-letter 0
#
# The runtime image is "chiseled": no shell, no package manager, and it runs as a non-root user.
# Nothing secret is baked in. Pass a connection string only as an environment variable
# (--connection-string-env), or sign in with workload identity / managed identity (--namespace).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props LICENSE ./
COPY src/ src/
RUN dotnet publish src/ME.Headless/ME.Headless.csproj -c Release -o /out --nologo -p:DebugType=none

FROM mcr.microsoft.com/dotnet/runtime:8.0-noble-chiseled
WORKDIR /app
COPY --from=build /out/ ./
COPY --from=build /src/LICENSE ./LICENSE
LABEL org.opencontainers.image.title="lfme" \
      org.opencontainers.image.description="Read-only Service Bus inventory and health checks (Message Explorer, headless)" \
      org.opencontainers.image.licenses="MPL-2.0"
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/lfme.dll"]
CMD ["--help"]
