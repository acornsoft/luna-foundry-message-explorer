# Design: synthetic test jobs for system testing

Status: **design only**. Nothing in this document is built in the queue-list slice. The build is planned as a **third PR**, after PR #1 (skeleton) and the queue-list slice. Gotham Savings and Loan (GSL) is the first customer context.

## 1. Goal

Run **system tests** of a message-driven application without the real outside systems it depends on.

A system test checks the whole application under test end to end: it receives messages, does its work, and emits messages or rows. The application's outside dependencies (an ERP, a card processor, a notice printer, a core banking host) are often not available in a test environment, or are slow, costly, or shared. A **plugin** in this design is a **synthetic stand-in** for one such outside system. It mimics the system's behavior on the wire, without implementing the system.

This is not integration testing of the stand-in. The stand-in is a test double. The application under test is the real thing.

Ground rules:

- Synthetic data only. No production data, no customer names, no real account numbers.
- Bodies are redacted with the existing `ME.Redaction` pipeline **before** anything is saved: run records, replays, logs, and JUnit output.
- Jobs run against any broker through `IBrokerProvider` (see `src/ME.Brokers.Abstractions`). Service Bus is first. Kafka and AWS SQS/SNS follow when their providers exist.
- A job never targets a namespace that is not on the sandbox allow list (`src/ME.Scenarios/Sandbox`).

## 2. Plugin model

### 2.1 Language-neutral, out of process

A plugin is a **separate process** that speaks a small, versioned protocol. Any language works: Python, C#, Go, Rust, Java, Node, and so on. The runner owns the broker connection. The plugin never gets broker credentials. It only sees messages the runner hands it and returns messages and verdicts for the runner to act on.

- **Default transport:** JSON-RPC 2.0 over stdio. One JSON object per line (newline-delimited). Logs go to stderr, never stdout.
- **Optional transport:** gRPC over a local socket, for plugins that want streaming and typed stubs. Same message set, same semantics.
- **Optional in-process .NET host:** for speed, a .NET plugin can implement the same contract as an interface and load in-process. It is the same contract with no process boundary. It is never the only way to write a plugin.

Inline scripts: there is an **extension point, marked future**, for small inline expressions inside a job file. No language is chosen. Until it exists, all logic lives in plugins.

### 2.2 Four roles

| Role | What it does | Example |
|---|---|---|
| **Generator** | Emits messages over a timeline. | Customers opening Reg E disputes over a simulated business day. |
| **Simulator** | Subscribes to a queue or topic and responds the way the outside system would: replies, follow-on events, configurable delays, failures, and error codes. Keeps state across a run. | An ERP stand-in that confirms orders and later ships them. |
| **Assertion** | States expected behavior and checks it against what was observed. | "Every dispute gets a provisional credit decision within 10 business days of simulated time." |
| **Observer** | Watches one outcome sink and reports what happened. One observer plugin per sink type. | A reply queue, a topic subscription, or a SQL table. |

A plugin may declare more than one role in its handshake.

**Simulator state.** State lives **per run**. It resets at the start of every run and is derived from the pinned seed, so two runs with the same job, plugin versions, and seed produce the same simulator decisions. Example: an order moves `created`, then `confirmed`, then `shipped`, and the simulator remembers where each order is.

### 2.3 Live view: expected versus actual

The run view shows two columns side by side:

- **Expected**: what Assertion plugins said should happen, keyed by correlation ID.
- **Actual**: what Observer plugins saw, keyed by the same correlation ID.

Rows link on the correlation ID (`correlationId`, plus W3C `traceparent` when present, from `ME.Correlation`). A row is green when they match, red on a mismatch, and amber while still waiting inside its deadline.

## 3. Protocol (version 1)

All messages are JSON-RPC 2.0. Times are simulated-clock ISO 8601 UTC. Bodies are JSON objects. The runner redacts before it records anything.

### 3.1 Handshake

The runner starts the plugin and sends `initialize`. The plugin answers with what it is and what it can do. If the major protocol versions differ, the runner stops the plugin and fails the job with a clear message.

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{
  "protocol":"lfme-plugin/1.0",
  "runId":"run-2026-11-10-0001",
  "seed":424242,
  "clockStart":"2026-03-02T09:00:00Z",
  "config":{"failureRate":0.05},
  "variables":{"region":"synthetic-east"}
}}
```

```json
{"jsonrpc":"2.0","id":1,"result":{
  "protocol":"lfme-plugin/1.0",
  "name":"erp-standin",
  "version":"0.3.1",
  "roles":["simulator"],
  "capabilities":{"subscribe":["orders.created"],"emits":["orders.confirmed","orders.shipped","orders.rejected"],"stateful":true}
}}
```

### 3.2 Messages

| Method | Direction | Roles | Purpose |
|---|---|---|---|
| `initialize` | runner to plugin | all | Handshake. Seed, config, variables, clock start. |
| `tick` | runner to plugin | generator, simulator | Advance the simulated clock to `now`. Returns messages due by then, as a batch. Simulators return delayed follow-on events here. |
| `nextBatch` | runner to plugin | generator | Pull the next batch of up to `max` messages, for count-driven jobs with no timeline. |
| `deliver` | runner to plugin | simulator | A message arrived on a subscribed queue or topic. Returns zero or more replies, each with an optional delay, or an error outcome. |
| `expect` | runner to plugin | assertion | Given a sent message, return expectations: sink, match rule, deadline. |
| `observe` | runner to plugin | observer | Poll or stream from the sink the plugin owns. Returns observed events with correlation IDs. |
| `assert` | runner to plugin | assertion | Given expectations and observations for one correlation ID, return pass, fail, or pending with a reason. |
| `event` | plugin to runner | all | Notification (no `id`). Progress, warnings, state changes for the live view. |
| `shutdown` | runner to plugin | all | Flush and exit. The plugin must exit within 5 seconds or the runner kills it. |

A `deliver` result from a simulator:

```json
{"jsonrpc":"2.0","id":17,"result":{
  "replies":[
    {"to":"orders.confirmed","delayMs":0,"correlationId":"ord-0007","body":{"orderRef":"ord-0007","state":"confirmed"}},
    {"to":"orders.shipped","delayMs":7200000,"correlationId":"ord-0007","body":{"orderRef":"ord-0007","state":"shipped"}}
  ]
}}
```

An injected failure:

```json
{"jsonrpc":"2.0","id":18,"result":{
  "replies":[{"to":"orders.rejected","delayMs":0,"correlationId":"ord-0008","body":{"orderRef":"ord-0008","state":"rejected","code":"CREDIT_HOLD"}}]
}}
```

## 4. Job files

Jobs are plain files kept in git, reviewed like code. YAML or JSON with the same schema.

```
jobs/
  gsl-reg-e/
    collection.yaml
    environments/
      local.yaml
      sandbox.yaml
    jobs/
      business-day.job.yaml
      dead-letter-replay.job.yaml
  order-app/
    collection.yaml
    environments/local.yaml
    jobs/day-in-the-life.job.yaml
```

### 4.1 Collection, environment, variables

```yaml
# collection.yaml
collection: gsl-reg-e
description: Synthetic Reg E dispute flows for Gotham Savings and Loan (GSL).
defaults:
  environment: local
plugins:
  dispute-generator: { version: "1.2.0", command: ["python3", "plugins/dispute_generator.py"] }
  provisional-credit-assertions: { version: "1.0.4", command: ["dotnet", "plugins/ProvisionalCredit.dll"] }
  queue-observer: { version: "0.9.0", builtin: true }
```

```yaml
# environments/local.yaml
provider: azure-service-bus
endpoint: "{{ env.ME_SB_NAMESPACE }}"     # host name only; sign-in is Entra
variables:
  intakeQueue: gsl-disputes-intake
  decisionsTopic: gsl-dispute-decisions
```

Variables resolve in this order: job, then environment, then collection. `{{ env.NAME }}` reads a process environment variable at run time. A secret can never be written into a job or environment file; the license check rejects key patterns.

### 4.2 Job

```yaml
# jobs/business-day.job.yaml
job: business-day
seed: 424242                 # pinned, so reruns are deterministic
clock: { start: "2026-03-02T09:00:00Z", speed: 600 }   # 10 simulated minutes per real second
target:
  send: "{{ intakeQueue }}"
plugins:
  - use: dispute-generator@1.2.0
    role: generator
    config:
      timeline: business-day
      count: 200
      rows: data/disputes.csv    # data-driven rows, synthetic
  - use: provisional-credit-assertions@1.0.4
    role: assertion
  - use: queue-observer@0.9.0
    role: observer
    config: { sink: "{{ decisionsTopic }}", subscription: lfme-observer }
template:                      # used when a generator returns only fields, not a full body
  contentType: application/json
  body:
    disputeRef: "{{ row.ref }}"
    amount: "{{ row.amount }}"
    reason: "{{ row.reason }}"
rate: { perMinute: 30 }        # cap, applied by the runner
deadline: PT15M                # real time
```

A job **pins** each plugin by name and version, and pins the seed. The runner refuses a plugin whose handshake reports a different version.

### 4.3 Replay

- **Record**: every run writes a redacted run record (sent messages, simulator replies, observations, verdicts, timings) to `runs/<runId>/`. Records stay out of git by default.
- **Replay as a job**: `lfme jobs record-to-job runs/<runId>` turns a run record into a job file whose generator is the built-in `replay` plugin. Rerunning it sends the same messages in the same order on the same simulated timeline.
- **Dead-letter replay**: a job can name a dead-letter subqueue as its source. The runner peeks (never receives) the dead-lettered messages, redacts them, and replays them as synthetic copies to the target. Resubmitting the originals is an admin action with approval, not a test job.

## 5. Interfaces (sketch)

```csharp
public interface IJobRunner
{
    Task<RunResult> RunAsync(JobDefinition job, RunOptions options, IProgress<RunEvent> progress, CancellationToken cancellationToken);
}

public interface IPluginHost : IAsyncDisposable
{
    PluginInfo Info { get; }   // from the handshake: name, version, roles, capabilities
    Task<IReadOnlyList<OutboundMessage>> TickAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutboundMessage>> NextBatchAsync(int max, CancellationToken cancellationToken);
    Task<DeliverResult> DeliverAsync(InboundMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<Expectation>> ExpectAsync(OutboundMessage sent, CancellationToken cancellationToken);
    Task<IReadOnlyList<Observation>> ObserveAsync(CancellationToken cancellationToken);
    Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<Expectation> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken);
}

public interface IReplayStore
{
    Task SaveAsync(RunRecord redactedRecord, CancellationToken cancellationToken);
    Task<RunRecord> LoadAsync(string runId, CancellationToken cancellationToken);
    Task<JobDefinition> ToJobAsync(string runId, CancellationToken cancellationToken);
}
```

`IPluginHost` has two implementations: `StdioJsonRpcPluginHost` (default) and `InProcessPluginHost` (optional, .NET only). A gRPC host may follow. `IReplayStore.SaveAsync` accepts only records that have passed the redaction pipeline; the type carries a redaction profile ID and the store rejects records without one.

The runner sends through `IBrokerProvider`. Sending is a write, so test jobs run only in the admin module, only against allow-listed sandbox namespaces, and never in observe mode.

## 6. Minimal plugins

### 6.1 Python generator (stdio JSON-RPC)

```python
import json, random, sys

rng = None

def reply(id_, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": id_, "result": result}) + "\n")
    sys.stdout.flush()

for line in sys.stdin:
    msg = json.loads(line)
    method, params, id_ = msg["method"], msg.get("params", {}), msg.get("id")
    if method == "initialize":
        rng = random.Random(params["seed"])
        reply(id_, {"protocol": "lfme-plugin/1.0", "name": "hello-generator",
                    "version": "0.1.0", "roles": ["generator"], "capabilities": {}})
    elif method == "nextBatch":
        batch = [{"correlationId": f"syn-{rng.randrange(10**6):06d}",
                  "body": {"amount": rng.randrange(100, 5000) / 100}}
                 for _ in range(params["max"])]
        reply(id_, {"messages": batch})
    elif method == "shutdown":
        reply(id_, {})
        break
```

### 6.2 C# simulator (stdio JSON-RPC)

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

Random? rng = null;
double failureRate = 0;
var stdout = Console.Out;

while (Console.ReadLine() is { } line)
{
    var msg = JsonNode.Parse(line)!;
    var id = msg["id"]?.DeepClone();
    var p = msg["params"];
    switch ((string?)msg["method"])
    {
        case "initialize":
            rng = new Random((int)p!["seed"]!);
            failureRate = (double?)p["config"]?["failureRate"] ?? 0;
            Reply(id, new JsonObject { ["protocol"] = "lfme-plugin/1.0", ["name"] = "hello-simulator", ["version"] = "0.1.0", ["roles"] = new JsonArray("simulator") });
            break;
        case "deliver":
            var cid = (string)p!["message"]!["correlationId"]!;
            var state = rng!.NextDouble() < failureRate ? "rejected" : "confirmed";
            Reply(id, new JsonObject { ["replies"] = new JsonArray(new JsonObject { ["to"] = $"orders.{state}", ["delayMs"] = 0, ["correlationId"] = cid, ["body"] = new JsonObject { ["state"] = state } }) });
            break;
        case "shutdown":
            Reply(id, new JsonObject());
            return;
    }
}

void Reply(JsonNode? id, JsonNode result) =>
    stdout.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString());
```

## 7. Headless CLI runner

```
lfme jobs run jobs/gsl-reg-e/jobs/business-day.job.yaml --env local --junit out/junit.xml
lfme jobs replay runs/run-2026-11-10-0001 --env local
lfme jobs record-to-job runs/run-2026-11-10-0001 > jobs/gsl-reg-e/jobs/replay-0001.job.yaml
```

- Exit code 0 when every assertion passes, 1 on any failure, 2 on a setup error (bad file, plugin version mismatch, namespace not allow-listed).
- `--junit` writes JUnit XML: one test suite per job, one test case per correlation ID, failure text from the assertion reason (redacted).
- The CLI and the desktop app share `IJobRunner`. CI runs the CLI against a local broker container or a sandbox namespace.

## 8. iOS

Confirmed for .NET: `System.Diagnostics.Process.Start` is marked `[UnsupportedOSPlatform("ios")]` and `[UnsupportedOSPlatform("tvos")]` (Mac Catalyst is supported). In the runtime, `ProcessUtils.PlatformSupportsProcessStartAndKill` is false on iOS (not Mac Catalyst) and tvOS, and the call throws `PlatformNotSupportedException`.

- https://learn.microsoft.com/dotnet/api/system.diagnostics.process.start?view=net-8.0 (attributes on every `Start` overload)
- https://source.dot.net/System.Diagnostics.Process/System/Diagnostics/ProcessUtils.cs.html (`PlatformSupportsProcessStartAndKill`)
- https://github.com/dotnet/runtime/issues/47910 (annotating APIs that throw on iOS and tvOS, including `Process.Start`)

Apple: App Review Guideline 2.5.2 says apps "should be self-contained in their bundles" and may not "download, install, or execute code which introduces or changes features or functionality of the app" (https://developer.apple.com/app-store/review/guidelines/#software-requirements). A user-supplied plugin process would conflict with that rule.

**Unverified:** I did not find an Apple document that states outright that the iOS sandbox blocks `fork` or `posix_spawn`. Runtime maintainers say so in https://github.com/dotnet/runtime/issues/126097, but that is not an Apple source. Treat the kernel-level claim as unverified.

**Design decision:** there is no built-in scripting and no local plugin process on iOS. An iOS build of the app runs test jobs only by driving a **remote runner** (desktop or CI host) over the same protocol, and shows the expected-versus-actual view from the events it streams back.

## 9. Sample collections

Synthetic data only in every sample.

### 9.1 GSL Reg E dispute (first)

- **Generator**: customers open Reg E disputes over a simulated business day, from data-driven rows (amount, reason, channel).
- **Simulator**: a card-network stand-in that answers a chargeback request with an acknowledgement, then a resolution after a configurable delay.
- **Assertion**: provisional credit decision within the configured window of simulated time; a notice event for every decision.
- **Observer**: the decisions topic and the notices queue.
- The GSL Reg E dispute class library and console runner arrive in their own PR (`src/Scenarios/`). This collection will call that library's runner as a plugin.

### 9.2 Order app: a day in the life (second)

- **Generator**: shoppers browse, add to cart, order, pay, wait for shipping, and sometimes return, spread across a simulated day.
- **Simulators**: a payment stand-in (authorize, decline, timeout) and the ERP stand-in below.
- **Assertions**: every paid order reaches `shipped` or `rejected`; every return produces a refund event; no order is charged twice.
- **Observers**: the order-events topic, the refunds queue, and an orders table through a SQL observer plugin.

### 9.3 ERP stand-in (short example)

A Simulator that stands in for an ERP-like system. It uses our own message names and fields, not any vendor's.

1. Subscribes to `orders.created`.
2. On each message, replies at once with `orders.confirmed`.
3. After a configurable delay (default 2 simulated hours), emits `orders.shipped`.
4. Failure injection: `failureRate: 0.05` replies `orders.rejected` with code `CREDIT_HOLD` instead, for about 5% of orders. Which orders fail is decided by the seeded random source, so a rerun fails the same orders.
5. Keeps per-run state: `created`, then `confirmed`, then `shipped` (or `rejected`). A second `orders.created` for the same order reference returns `DUPLICATE` instead of a second confirmation.

```yaml
- use: erp-standin@0.3.1
  role: simulator
  config:
    subscribe: orders.created
    shipDelay: PT2H
    failureRate: 0.05
    failureCode: CREDIT_HOLD
```

## 10. Open questions

- Simulated clock versus real broker timestamps: the live view shows both.
- Back-pressure when a simulator is slower than the generator: the runner queues by correlation ID and warns.
- Plugin packaging: a local folder path in the collection file first; a signed package feed later.
