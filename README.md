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

Admin actions are a separate module, off by default. They file a request. Observe mode does not send, purge, or settle.

## Move mode

**Move mode** is the only way the app changes a broker, and it is off every time the app starts. Turn it on at the top of the window; a banner stays up while it is on. On the **Move** tab, drag messages (or a whole source, for the oldest N) from a dead-letter queue, queue or subscription onto a queue or topic, or use **Move to…** (Ctrl+M). Each drop checks send and receive rights, asks you to confirm, moves oldest first (session by session when the source uses sessions) keeping IDs, session, correlation and properties, and writes an audit line per message with no bodies or secrets. Within one namespace each message moves in one transaction; across namespaces it is at-least-once with a duplicate-detection warning. Only `Acornsoft.MessageExplorer.Operations` can mutate; the guard checks every other assembly. See [docs/design/move-mode.md](docs/design/move-mode.md).

## Layout

| Project | Role |
|---|---|
| `src/ME.Model` | CloudEvents envelope, observe-mode guard, provider matrix |
| `src/ME.Redaction` | Schema rules, pattern net, default deny, 16 KB cap |
| `src/ME.Correlation` | W3C `traceparent` and correlation tokens |
| `src/ME.Operations` | Move mode: the only assembly that sends, receives, locks or settles. Engine, audit log, in-memory broker, Service Bus transport |
| `src/ME.Brokers.Abstractions` | Broker-neutral provider contract: `IBrokerProvider`, `IEntityInventory`, capability flags, provider catalog |
| `src/ME.Adapters.ServiceBus` | Peek client with an explicit sequence cursor, and the read-only Service Bus provider and queue inventory |
| `src/ME.Adapters.EventHubs` | Read-only consumer, in-memory position |
| `src/ME.Adapters.EventGrid` | Refuses a read and points at the admin template |
| `src/ME.Admin` | Role gate, audit row, approval tickets, Event Grid template |
| `src/ME.Scenarios` | Seeded scenario parser, sandbox gate, kill switch |
| `src/ME.Mcp` | In-process tool policy: redaction, scopes, no self-approval |
| `src/ME.ReadTools` | Read-only sources and tools shared by `lfme`, `lfme-mcp` and the app: inventory, limit checks, dead-letter summary |
| `src/ME.Headless` | `lfme`: headless, read-only inventory and checks for containers and pipelines |
| `src/ME.McpServer` | `lfme-mcp`: read-only MCP server over stdio |
| `src/ME.Voice` | Grok Voice session (xAI realtime WebSocket) over the read-only tools, built-in voices, redaction |
| `plugins/cursor/lfme-readonly` | A Cursor plugin scaffold for `lfme-mcp` (not published) |
| `src/ME.Jobs` | Test jobs: job files, stdio JSON-RPC plugin host, in-memory broker, runner, JUnit |
| `src/ME.Jobs.Cli` | `me-jobs run <job>` and `me-jobs plugin-check` |
| `samples/plugins`, `samples/jobs` | Sample plugins (C# and Python) and sample jobs. Synthetic data only |
| `src/ME.App` | Avalonia shell: Accounts tab (one or more Entra accounts, namespace discovery), Connect panel (typed namespace or any connection string flavor), sortable Queues and Topics grids, and the Test jobs panel with a live run view |
| `src/ME.App.Desktop` | Desktop entry point |
| `src/Scenarios`, `tests/Scenarios` | Reserved for the GSL Reg E dispute library and runner (separate PR) |
| `tests/*` | One test project per core library, plus the shell (view models and headless UI) |

Borrowed ideas are credited in [docs/ideas-log.md](docs/ideas-log.md). No code is copied from those projects. Feature parity is tracked in [docs/parity-matrix.md](docs/parity-matrix.md), the plan in [docs/backlog.md](docs/backlog.md), and the test jobs design in [docs/design/test-jobs.md](docs/design/test-jobs.md).

## Accounts and namespace discovery

The **Accounts** tab opens first. Choose **Add account** and sign in with Microsoft Entra ID in your browser. Add as many accounts as you like. For each account, the app lists every tenant the account belongs to, every subscription in those tenants, and every Service Bus namespace in those subscriptions, through Azure Resource Manager GETs (`Azure.ResourceManager.ServiceBus`). A namespace shows up only when the account can read it in ARM (the Reader role is enough). The tree is Account > Subscription > Namespace. Double-click a namespace to open its queue and topic grids with that account.

- One account, tenant, or subscription that fails (an expired sign-in, a tenant that needs MFA, no access) shows its own error row. The others still load.
- **Sign out** keeps the account in the list, signed out. **Remove** forgets it.
- Sign-in uses `InteractiveBrowserCredential` with the system browser and tenant `organizations` (any work or school account), or the tenant you type. The client ID is the public Azure CLI application, `04b07795-8ddb-461a-bbee-02f9e1bf7b46`. Set `LFME_ENTRA_CLIENT_ID`, or `entraClientId` in `settings.json` in the config folder, to use another public client app.
- The app keeps each account's `AuthenticationRecord` (user name, tenant, account ID; no token) as an Entra login in `logins.json` in the config folder. Tokens go in the MSAL token cache named `lfme`, which is kept in the OS secure store (Keychain on macOS). On the next launch, accounts sign in silently.
- Config folder: `~/Library/Application Support/LunaFoundryMessageExplorer` on macOS, `~/.local/share/LunaFoundryMessageExplorer` on Linux, `%LOCALAPPDATA%\LunaFoundryMessageExplorer` on Windows. `LFME_CONFIG_DIR` overrides it.
- Limit: Remove and Sign out forget the account in the app. Azure.Identity has no call that deletes one account from the shared `lfme` token cache. To clear it on macOS, delete the `lfme` item in Keychain Access.

## Connection strings

One paste box takes every flavor and says which one it found:

| Flavor | Example shape | What opens |
|---|---|---|
| Namespace key | `Endpoint=sb://…/;…KeyName=…;…Key=…` | Queues and topics with counts |
| Entity key | the same plus `EntityPath=orders` | Only that entity. An entity-level key can't read namespace lists or counts, so the grid says so |
| SAS token | `SharedAccessSignature sr=…&sig=…&se=…&skn=…`, bare or as `SharedAccessSignature=` in a string | Namespace-wide token: queues and topics. Entity token: that entity only |
| Endpoint only | `Endpoint=sb://…/` | The signed-in Entra account you pick |
| Emulator | `…;UseDevelopmentEmulator=true` | The local Service Bus emulator |

The string is shown masked and cleared after connecting. Tick **Save** to keep it as a saved login: the secret goes only to the OS secure store (Keychain on macOS and iOS, Credential Manager on Windows, the Secret Service keyring on Linux) and the name and endpoint to `logins.json`. When there is no secure store, saving a string is turned off and says why; nothing falls back to a file. The masked form (keys and signatures as `***`) is the only form that reaches the UI or a log.

## Saved logins

The **Saved logins** tab keeps logins for every system in one list: Entra accounts, Service Bus and Event Hubs strings and namespaces, Kafka (SASL or mutual TLS), RabbitMQ, AWS SQS and SNS (access key or profile), and the emulator. Each row shows the system, name, kind and when it was last used. Add, Edit, Rename, Delete (with a confirm) and a read-only Test connection. Secrets are masked, can't be copied, and are never shown again. Logins that use Entra record which signed-in account they use. Version 0.6.0's saved strings and account files move in on first launch. How each platform stores secrets, and what has been proven on each, is in [docs/design/credential-vault.md](docs/design/credential-vault.md).

## Service Bus queue list

A typed namespace connects with Microsoft Entra ID: the signed-in account you pick, or `DefaultAzureCredential` when no account is signed in. Typed namespaces and connection strings read the data-plane management API (`GetQueuesRuntimePropertiesAsync` and `GetQueuesAsync`, paged), which needs a management read permission on the namespace. A namespace picked on the Accounts tab reads the same counts through ARM instead, so the Reader role is enough.

The no-mutation guard reads the compiled adapter IL. Service Bus send, receive, settle, create, update, and delete calls fail the build. ARM calls must be property reads or an allow-listed list or get; create, update, delete, key listing, and key regeneration fail the build.

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

## Headless (`lfme`)

`lfme` is the read-only command-line and container form: inventory and health checks (dead-letter, active and scheduled limits) for any queue, topic or subscription, with JSON reports and exit codes for pipelines. It never reads bodies and cannot change anything. See [headless](docs/headless.md), the container in `eng/container/`, and an [example GitHub Actions workflow](docs/examples/github-actions/servicebus-health.yml).

## MCP server (`lfme-mcp`)

`lfme-mcp` is a read-only MCP server over stdio with four tools: `list_entities`, `get_entity`, `check_limits` and `summarize_dead_letters`. It speaks MCP 2026-07-28 and the older `initialize` handshake. It reads counts only. A Cursor plugin scaffold, never published, is in `plugins/cursor/lfme-readonly`. See [MCP server](docs/mcp-server.md).

## Grok Voice

The Voice tab answers questions about the connected namespace out loud, push-to-talk, using the same four read-only tools as `lfme-mcp`. It can never move anything. The xAI key lives only in the vault (Saved logins > xAI). Message bodies are redacted by default, and the voice picker offers xAI's built-in voices only. See [Grok Voice](docs/voice.md).

## Environments

Azure public, US Government and China accounts; desktop builds for macOS, Windows and Linux (x64 and arm64); and an Android head. See [environments](docs/environments.md).

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
- MCP Streamable HTTP. The dispatcher enforces policy in process.
- Natural language beyond an allow-listed entity name and a minute window.
- Topology, trace, and dead-letter screens. The window is an observe-mode shell with Accounts, Queues, Topics, and Test jobs tabs.
- A Storage Queues adapter. The matrix records the peek verdict only.
- Web and non-Azure adapters. A platform audio backend for the Voice tab (questions are typed until one is added).
