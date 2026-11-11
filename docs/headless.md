<!-- SPDX-License-Identifier: MPL-2.0 -->
# Headless: `lfme` in a container or a pipeline

`lfme` is Message Explorer without a window. It lists queues, topics and subscriptions with their message
counts and checks them against limits. It works for any queue or topic.

It is **read-only**. It never reads a message body, and it cannot send, receive, settle, move, create or
delete anything. The compiled tool is scanned in the tests for any of those calls, and it does not
reference the one assembly (`Operations`) that can move messages.

```text
lfme inventory <source> [--format table|json] [--out <file>] [--summary <file>]
lfme check     <source> [--max-dead-letter <n>] [--max-active <n>] [--max-scheduled <n>]
                        [--entity <name>]... [--format table|json] [--out <file>] [--summary <file>]
```

| Source | Sign-in | Permission needed |
| --- | --- | --- |
| `--resource-id <ARM id>` | Microsoft Entra ID | **Reader** on the namespace (counts come from Azure Resource Manager) |
| `--namespace <name>` | Microsoft Entra ID | a management read permission on the namespace (its own management API) |
| `--connection-string-env <VAR>` | connection string or SAS token in env var `VAR` | a **Listen**-only key is enough |
| `--synthetic` | none | none: made-up `orders`, `payments`, `orders-events` |

- Entra sign-in uses, in order: workload identity (`AZURE_FEDERATED_TOKEN_FILE`), managed identity,
  environment (`AZURE_CLIENT_ID`, ...) and the Azure CLI. `--cloud public|usgov|china` picks the cloud.
- Secrets are never taken on the command line (`--connection-string` is refused and its value is never
  echoed). Output and reports never contain the key.
- Exit codes: `0` ok, `2` broker or sign-in error, `3` a check failed, `64` bad arguments.
- `--summary` appends a Markdown table, for example to `$GITHUB_STEP_SUMMARY`.

## Container

```bash
docker build -f eng/container/Dockerfile -t lfme:local .
docker run --rm --read-only lfme:local check --synthetic --max-dead-letter 0   # exits 3: payments has 3
```

The image is built on the chiseled .NET runtime (no shell, no package manager) and runs as a non-root user.
Nothing secret is baked in. It is not published to a registry; build it from source.

## Business scenarios

- **Dead-letter alarm.** An hourly scheduled job fails, and so notifies the team, when any queue or
  subscription has dead-lettered messages. See the example below.
- **Release gate.** Before a deploy, check that `orders` and `payments` are drained (`--max-active 0`)
  and nothing is dead-lettered, without giving the pipeline any right to touch messages.
- **Backlog report.** A nightly `inventory --format json --out report.json` kept as a build artifact
  shows backlog trends per queue, topic and subscription.
- **Least-privilege audits.** Auditors and support staff get counts with the Reader role only.

## Example GitHub Actions workflow

[`docs/examples/github-actions/servicebus-health.yml`](examples/github-actions/servicebus-health.yml) is an
example to copy into your own repository. It is not run here. It signs in with GitHub OpenID Connect and
Entra workload identity federation (no stored secret), builds the container, runs
`check --resource-id ... --max-dead-letter 0` on a read-only file system, adds the table to the job page,
and keeps the JSON report. The job's token can only read the repository.

## Limits

- Counts and status only. Peeking at messages is not offered headless.
- Service Bus only for now; the other broker families are planned in the app first.
- The example checks out `develop`; pin it to a release tag or commit once one exists.
- The example has not been run against a real namespace; CI runs the container against synthetic data.
