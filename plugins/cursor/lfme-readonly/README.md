# lfme-readonly: a Cursor plugin scaffold (not published)

A local plugin that gives an agent the read-only `lfme-mcp` server and a short skill for using it. It is a
scaffold to copy and adapt; it is not on any marketplace and this repository does not publish it.

## Use it locally

1. Build the server: `dotnet publish src/ME.McpServer -c Release -o ~/.local/lfme-mcp`, and put
   `~/.local/lfme-mcp` on your `PATH` (the binary is `lfme-mcp`).
2. Copy this folder to `~/.cursor/plugins/local/lfme-readonly/` and reload Cursor.
3. Ask: "Which queues have dead letters?" The default `mcp.json` uses `--synthetic`, a made-up namespace
   with `orders`, `payments` and `orders-events`.

## Point it at a namespace

Edit `mcp.json` and replace `--synthetic` with one source, the same as the headless `lfme`:

```json
{ "command": "lfme-mcp", "args": ["--resource-id", "/subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001"] }
```

Sign-in is non-interactive Microsoft Entra ID (Azure CLI, environment or managed identity). Through
`--resource-id` the Reader role is enough. A connection string is read only from an environment variable
(`--connection-string-env VAR`), never from `mcp.json`; use a Listen-only key.

## What it cannot do

Every tool reads counts. There is no tool that sends, receives, settles, moves, creates or deletes, the
server refuses unknown tool names, and the compiled server and its shared read library are scanned in the
tests for any call that could. Tool annotations say `readOnlyHint: true`, but the guarantee is the code, not
the hint.

License: MPL-2.0 (see `LICENSE`).
