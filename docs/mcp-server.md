# MCP server (`lfme-mcp`)

`lfme-mcp` gives an agent read-only access to a Service Bus namespace: names and message counts, nothing
else. It is a stdio server, one JSON-RPC message per line, and logs only to stderr.

```bash
dotnet run --project src/ME.McpServer -- --synthetic
```

## Sources

Exactly one, the same as [`lfme`](headless.md):

| Option | Reads | Least privilege |
| --- | --- | --- |
| `--synthetic` | a made-up namespace: `orders`, `payments`, `orders-events` | none |
| `--resource-id <ARM id>` | counts through Azure Resource Manager | Reader on the namespace |
| `--namespace <name-or-host>` | the namespace's management API | a management read permission |
| `--connection-string-env <VAR>` | a connection string from an environment variable | a Listen-only key |

`--cloud public|usgov|china` picks the Azure cloud for `--resource-id` and `--namespace`. Entra sign-in is
non-interactive (workload identity, managed identity, environment, Azure CLI). A secret on the command line
(`--connection-string`, `--sas`, `--key`) is refused without being echoed.

## Tools

| Tool | Arguments | Returns |
| --- | --- | --- |
| `list_entities` | `entity?`, `kind?` (queue, topic, subscription) | every entity with active, dead-letter and scheduled counts |
| `get_entity` | `path` (`orders`, `orders-events/subscriptions/billing`) | one entity, or a tool error if it does not exist |
| `check_limits` | `maxDeadLetter?`, `maxActive?`, `maxScheduled?`, `entities?` | entities over a limit |
| `summarize_dead_letters` | none | entities with dead letters, most first, and the total |

Each result has a text block for people and voices and `structuredContent` with the same data. Every tool is
annotated `readOnlyHint: true`, `destructiveHint: false`, `idempotentHint: true`, `openWorldHint: false`.
Annotations are hints, so the guarantee is in the code: the tools come from `src/ME.ReadTools`, which has no
send, receive, settle, move, create or delete, an unknown tool name is refused, and the compiled server and
library are scanned for any such call in the tests. The server does not reference the Operations (move mode),
Admin or in-process `ME.Mcp` policy assemblies.

## Protocol

- **MCP 2026-07-28 (stateless).** Each request carries `_meta` `io.modelcontextprotocol/protocolVersion` and
  `io.modelcontextprotocol/clientCapabilities`. `server/discover` returns the supported version, the `tools`
  capability, instructions and cache hints. Another version gets `UnsupportedProtocolVersionError` (-32022)
  with `data.supported` and `data.requested`. Results carry `io.modelcontextprotocol/serverInfo` in `_meta`.
- **Older clients (2025-11-25, 2025-06-18).** `initialize` selects the handshake for the rest of the process,
  and requests without `_meta` are then served under that revision. An unknown older version is offered
  2025-11-25.
- No resources, prompts or sampling: those methods return "method not found".

Sources: [Versioning and compatibility, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning),
[stdio transport, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio),
[schema, 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/schema).

## Cursor plugin scaffold

`plugins/cursor/lfme-readonly` is a local plugin: a manifest, an `mcp.json` that runs `lfme-mcp --synthetic`,
and a short `service-bus-health` skill. It is not published to any marketplace. To try it, put `lfme-mcp` on
your `PATH` and copy the folder to `~/.cursor/plugins/local/lfme-readonly/`. See its README.

## Limits

- stdio only; no Streamable HTTP transport.
- Counts only. No message bodies, properties or peeks, and no changes of any kind.
- Each tool call reads the whole namespace inventory again; for a large namespace that is one list call per
  topic.
- `notifications/cancelled` is accepted and ignored: calls are short reads.
