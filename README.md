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
| `src/ME.Adapters.ServiceBus` | Peek client with an explicit sequence cursor |
| `src/ME.Adapters.EventHubs` | Read-only consumer, in-memory position |
| `src/ME.Adapters.EventGrid` | Refuses a read and points at the admin template |
| `src/ME.Admin` | Role gate, audit row, approval tickets, Event Grid template |
| `src/ME.Scenarios` | Seeded scenario parser, sandbox gate, kill switch |
| `src/ME.Mcp` | In-process tool policy: redaction, scopes, no self-approval |
| `src/ME.App` | Avalonia shell. Admin controls are hidden in observe mode |
| `src/ME.App.Desktop` | Desktop entry point |
| `tests/*` | One test project per core library, plus the shell |

Borrowed ideas are credited in [docs/ideas-log.md](docs/ideas-log.md). No code is copied from those projects.

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
- Scenario execution. The runner returns a plan and sends nothing.
- MCP Streamable HTTP and Entra sign-in. The dispatcher enforces policy in process.
- Natural language beyond an allow-listed entity name and a minute window.
- Topology, trace, and dead-letter screens. The window is an observe-mode shell.
- A Storage Queues adapter. The matrix records the peek verdict only.
- Web, voice, and non-Azure adapters.
