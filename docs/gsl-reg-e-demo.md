# GSL Reg E demo

A walkthrough of the `gsl-reg-e-dispute` scenario against the GSL dev queue. Gotham Savings and Loan (GSL) is a
fictional first customer context. Every message is synthetic and says so in its body and its `synthetic=true`
property. There are no real people, accounts, or cases.

## What it shows

1. **Simulation.** The runner sends 15 seeded Reg E deadline messages: 6 standard, 3 new-account, 3 point-of-sale or
   foreign, and 3 deliberately malformed (no dispute id, inconsistent day counts, a `MM/dd/yyyy` notice date).
2. **The real consumer.** The GSL Function App (`regEDisputeConsumer`) reads the queue. Valid messages are logged as
   `RegEDisputeTracked` and completed. Invalid ones are dead-lettered with a reason the explorer can group by.
3. **Counts.** Active and dead-letter counts are polled until the queue settles.
4. **Dead-letter processing.** The runner peeks the dead-letter subqueue (no lock), shows each reason and
   description, and previews a fix. Resubmit is a separate command and needs both `--yes` and `--send`.

## Before you start

| Need | Why |
|---|---|
| `az login` as a user in the tenant | Every `lfme` command except `--help` checks Microsoft Entra sign-in first (`DefaultAzureCredential`, Azure CLI works). Keys, SAS, connection strings, and skip-auth variables are refused. There is no HTTP listener and no local emulator mode. |
| **Azure Service Bus Data Sender** and **Data Receiver** on the namespace | Send, peek, and dead-letter resubmit are data-plane calls. |
| **Reader** on the resource group | Tags, `disableLocalAuth`, and queue counts are read through Azure Resource Manager. |
| The subscription id in `LFME_ALLOWED_SUBSCRIPTIONS` | The sandbox gate fails closed. Real subscription ids stay out of the repository. |

The gate also checks that the namespace is on `src/ME.Scenarios/Sandbox/allowed-namespaces.txt`, has
`disableLocalAuth=true`, and is tagged `env=test`, or `env=dev` together with `dataClass=synthetic`. `dataClass=synthetic` with `env=prod` (or any other env) is refused.

```bash
export LFME_SUBSCRIPTION=<subscription id>
export LFME_RESOURCE_GROUP=rg-gsl-dev-eastus-001
export LFME_ALLOWED_SUBSCRIPTIONS=$LFME_SUBSCRIPTION
dotnet build src/ME.Cli -c Release
alias lfme='dotnet src/ME.Cli/bin/Release/net8.0/lfme.dll'
```

## Walkthrough

```bash
lfme scenario list
lfme scenario run gsl-reg-e-dispute                    # dry run: gate + plan + counts, sends nothing
lfme scenario run gsl-reg-e-dispute --run-id demo-1 --send  # send, settle, show dead-letters, preview fixes
lfme dlq peek gsl-reg-e-dispute --run demo-1           # that run's dead-letters, grouped by reason, with fix verdicts
lfme dlq resubmit gsl-reg-e-dispute --run demo-1       # preview only
lfme dlq resubmit gsl-reg-e-dispute --run demo-1 --yes --send  # resubmit the fixable ones
```

Expected result of a run: 12 consumed, 3 dead-lettered (`MissingDisputeId`, `InconsistentDeadlines`,
`SchemaViolation`), and "Every message ended where the scenario expected." After `--yes --send`, the first two come back
fixed and are consumed. The bad date stays: the fixer never guesses a date or a flag.

`lfme dlq peek` requires `--run` and only shows dead-letters from that run, so descriptions and correlation ids from other messages are not printed.
`lfme dlq resubmit --sequence 1,2,3` can resubmit listed sequence numbers, but still needs `--yes` and `--send`. Resubmit never takes the whole queue.

## What a fix does

| Dead-letter reason | Fix |
|---|---|
| `MissingDisputeId` | Stamp a synthetic id `GSL-DSP-9nnnnn` derived from the sequence number, in the body and `correlationId`. |
| `InvalidDisputeId`, `DisputeIdMismatch` | Same: a replacement synthetic id. |
| `InconsistentDeadlines` | Recompute day counts and due dates from `noticeDate` and the two flags (12 CFR 1005.11(c)). |
| `SchemaViolation` with a readable date and flags | Rewrite to the camelCase contract. |
| Unreadable `noticeDate`, missing flag, or non-JSON body | Not fixable. A person has to decide. |
| `MaxDeliveryCountExceeded` | Same rules on the body. A clean body is resubmitted as is. |

A resubmitted message gets a new id (`<original>-r1`) and the properties `lfmeResubmitOf` and `lfmeFix`. It is sent
before the dead-letter is completed, so a crash in between leaves a duplicate, never a loss.

## Options

| Option | Default | Notes |
|---|---|---|
| `--run-id` | `r<UTC timestamp>` | Letters, digits, and dashes. Stamped as `lfmeRunId`. |
| `--send` | off | Opt-in live send for `scenario run`, and for resubmit together with `--yes`. Without it, the command is a dry run or a preview. |
| `--settle-seconds` | 90 | How long to wait for the consumer. |
| `--tcp` | off | AMQP on 5671. The default is WebSockets on 443. |
| `LFME_USE_MANAGED_IDENTITY=1` | off | Lets `DefaultAzureCredential` try managed identity. Off by default because the probe can stall off Azure. |

Exit codes: 0 ok, 1 blocked by the gate or an outcome mismatch, 2 usage error.
