# Luna Foundry Message Explorer

Luna Foundry Message Explorer is an Acornsoft desktop tool for watching a message flow without changing it. The working name is Acornsoft Message Explorer. The product name is not final.

The first customer context is Gotham Savings and Loan (GSL). GSL is not the product. This repository holds the product skeleton: a .NET 8 core, an Avalonia desktop head, and tests that gate merges.

## License

Source in this repository is [Mozilla Public License 2.0](LICENSE). Each source file carries an `SPDX-License-Identifier: MPL-2.0` header. `eng/check-license.sh` fails the build when the license text or a header is missing.

## Synthetic data only

Bodies in tests and scenario files are synthetic. Do not put real customer data, connection strings, or keys in this repository. The redaction suite is a build gate, not a compliance claim. Health-shaped values are out of scope for a production profile until a separate review says otherwise. The canary still scrubs diagnosis-code shapes so a planted value fails the build.

## What observe mode means

Read mode is named **Observe**.

- Service Bus observe mode peeks. The caller passes `fromSequenceNumber`. A call returns at most 100 messages. Peek does not renew a lock, defer a message, or settle one (complete, abandon, dead-letter).
- Event Hubs observe mode reads with `EventHubConsumerClient`-style forward reads on the shared group `cg-message-explorer`. Position stays in memory. There is no checkpoint store and no owner epoch.
- Event Grid has no read API. A diagnostic subscription is a write. The Bicep file under `src/ME.Admin/Templates` is an owner-applied template. The explorer does not create the subscription.

Admin actions are a separate module, off by default. They file a request. This skeleton does not send, purge, or settle.

## Layout

| Project | Role |
|---|---|
| `src/ME.Model` | CloudEvents envelope, observe-mode guard, provider matrix |
| `src/ME.Redaction` | Schema rules, pattern net, default deny, 16 KB cap |
| `src/ME.Correlation` | W3C `traceparent` and correlation tokens |
| `src/ME.Brokers.Abstractions` | Broker-neutral provider contract: `IBrokerProvider`, `IEntityInventory`, capability flags, provider catalog |
| `src/ME.Adapters.ServiceBus` | Peek client with an explicit sequence cursor, and the read-only Service Bus provider and queue inventory |
| `src/ME.Adapters.EventHubs` | Read-only consumer, in-memory position |
| `src/ME.Adapters.EventGrid` | Refuses a read and points at the admin template |
| `src/ME.Admin` | Role gate, audit row, approval tickets, Event Grid template |
| `src/ME.Scenarios` | Seeded scenario parser, sandbox gate, kill switch |
| `src/ME.Mcp` | In-process tool policy: redaction, scopes, no self-approval |
| `src/ME.Jobs` | Test jobs: job files, stdio JSON-RPC plugin host, in-memory broker, runner, JUnit |
| `src/ME.Jobs.Cli` | `me-jobs run <job>` and `me-jobs plugin-check` |
| `samples/plugins`, `samples/jobs` | Sample plugins (C# and Python) and sample jobs. Synthetic data only |
| `src/ME.App` | Avalonia shell: Connect panel (Entra or in-memory connection string), sortable Queues and Topics grids, and the Test jobs panel with a live run view |
| `src/ME.App.Desktop` | Desktop entry point |
| `src/Scenarios`, `tests/Scenarios` | Reserved for the GSL Reg E dispute library and runner (separate PR) |
| `tests/*` | One test project per core library, plus the shell (view models and headless UI) |

Borrowed ideas are credited in [docs/ideas-log.md](docs/ideas-log.md). No code is copied from those projects. Feature parity is tracked in [docs/parity-matrix.md](docs/parity-matrix.md), the plan in [docs/backlog.md](docs/backlog.md), and the test jobs design in [docs/design/test-jobs.md](docs/design/test-jobs.md).

## Service Bus queue list

Connect with Microsoft Entra ID (namespace host name plus `DefaultAzureCredential`) or with a connection string typed at runtime. The connection string is held in memory, shown masked, and cleared after connecting. The queue list reads the management plane only (`GetQueuesRuntimePropertiesAsync` and `GetQueuesAsync`, paged). Listing needs a management read permission on the namespace.

The **Topics** tab lists topics with subscription counts. Pick a topic to list its subscriptions with active, dead-letter, and transfer dead-letter counts; non-zero dead-letter cells are highlighted.

Opt-in integration test: set `ME_SB_NAMESPACE` to a namespace you can read, then run `dotnet test`. CI does not set it, so the test is skipped there.

## Test jobs

Synthetic system tests against an in-memory broker. Plugins are separate processes in any language, speaking JSON-RPC over stdio. See [docs/design/test-jobs.md](docs/design/test-jobs.md).

```bash
dotnet build MessageExplorer.sln -c Release
dotnet src/ME.Jobs.Cli/bin/Release/net8.0/me-jobs.dll run samples/jobs/erp-standin/jobs/erp-day.job.yaml --junit out/erp-day.xml
```

Test jobs never send to a real broker. Only `provider: in-memory` is accepted.

### In the app

The **Test jobs** tab picks a collection folder, a job, an environment, and a pace, shows the pinned plugins and the seed, and has Run and Cancel. The live view lists expected versus actual per correlation ID with a status (pending, passed, failed, dead-lettered, late), the hop timeline, latency, plugin event counters, a status filter, a final summary, and Export JUnit.

Startup options (all optional):

```bash
dotnet src/ME.App.Desktop/bin/Release/net8.0/Acornsoft.MessageExplorer.Desktop.dll \
  --collection samples/jobs/crew-stock --pace 600 --run --synthetic-demo
```

- `--collection <folder>` opens a collection on the Test jobs tab. `--run` starts the default job.
- `--pace <n>` sets simulated seconds per real second (0 runs as fast as possible).
- `--tab queues|topics|jobs` picks the tab.
- `--exit-after-run <file>` writes the finished run report to the file and quits (used by the macOS smoke check).
- `--synthetic-demo` (or `LFME_SYNTHETIC_DEMO=1`) adds an in-memory provider with made-up queues, topics, and subscriptions, and connects to it. It is off by default.

## macOS

A DMG with a self-contained app for Apple Silicon and for Intel Macs: `eng/package-macos.sh`. See [docs/install-macos.md](docs/install-macos.md) for install, first launch, and signing.

## Build and test

```bash
dotnet build MessageExplorer.sln
dotnet test MessageExplorer.sln
bash eng/check-license.sh
```

GitHub Actions runs the same three steps on Ubuntu. A redaction failure or a side-effect failure fails the test run, which fails CI.

The desktop head builds with the solution. CI does not launch the window.

## Not in this skeleton

- Azure SDK clients. Adapters talk to interfaces. A production peek transport is not wired, and no cloud call is made.
- The Event Grid template is not deployed.
- Machine-learning PII detection. The local net is schema rules, a pattern scrubber, and an echo scrub of values those rules already removed.
- Scenario execution. The scenario runner returns a plan and sends nothing. (Test jobs run against the in-memory broker only.)
- MCP Streamable HTTP and Entra sign-in. The dispatcher enforces policy in process.
- Natural language beyond an allow-listed entity name and a minute window.
- Topology, trace, and dead-letter screens. The window is an observe-mode shell with Queues, Topics, and Test jobs tabs.
- A Storage Queues adapter. The matrix records the peek verdict only.
- Web, voice, and non-Azure adapters.
